﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Threading;
using BuildXL.Pips;
using BuildXL.Pips.Graph;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;

namespace BuildXL.Scheduler.Tracing
{
    /// <summary>
    /// Logging target for events triggered when a Pip fails.
    /// </summary>
    public sealed class DumpPipLiteExecutionLogTarget : ExecutionLogTargetBase
    {
        /// <summary>
        /// Used to hydrate pips from <see cref="PipId"/>s.
        /// </summary>
        private readonly PipTable m_pipTable;

        /// <summary>
        /// Execution context pointer for path table, symbol table, and string table.
        /// </summary>
        private readonly PipExecutionContext m_pipExecutionContext;

        /// <summary>
        /// Context for logging methods.
        /// </summary>
        private readonly LoggingContext m_loggingContext;

        /// <summary>
        /// Pip graph used to extract information from failing pip.
        /// </summary>
        private readonly PipGraph m_pipGraph;

        /// <summary>
        /// Path to the folder containing the failed pip logs - LogFolder/FailedPips.
        /// </summary>
        /// <remarks> This path is only created on the first Analyze call, if the analyzer is not called then it will not be created. </remarks>
        private readonly AbsolutePath m_logPath;

        /// <summary>
        /// Indicates whether the log path was already created.
        /// </summary>
        private bool m_logPathCreated;

        /// <summary>
        /// The maximum amount of log files that should be generated by this analyzer per run
        /// </summary>
        private readonly int m_maxLogFiles;

        /// <summary>
        /// The number of log files that have already been generated by this analyzer
        /// </summary>
        private int m_numLogFilesGenerated;

        /// <summary>
        /// If an error occured while logging, this will be set to true, and future logging requests will be skipped.
        /// </summary>
        private bool m_loggingErrorOccured;

        /// <summary>
        /// Holds data collected by this execution log target until it is ready to be dumped.
        /// </summary>
        private readonly ConcurrentDictionary<PipId, ProcessExecutionMonitoringReportedEventData?> m_dynamicDataDictionary;

        /// <summary>
        /// Indicates whether any data other than just static pip data should be dumped.
        /// </summary>
        private readonly bool m_shouldDumpDynamicData;

        /// <summary>
        /// Intialize execution log target and create log directory.
        /// </summary>
        /// <remarks>
        /// If log directory creation fails, then runtime failed pip analysis will be disabled for this build.
        /// </remarks>
        public DumpPipLiteExecutionLogTarget(PipExecutionContext context, 
                                             PipTable pipTable, 
                                             LoggingContext loggingContext,
                                             IConfiguration configuration,
                                             PipGraph graph)
        {
            m_pipTable = pipTable;
            m_pipExecutionContext = context;
            m_loggingContext = loggingContext;
            m_pipGraph = graph;
            m_logPath = configuration.Logging.LogsDirectory.Combine(context.PathTable, "FailedPips"); // This path has not been created yet
            m_logPathCreated = false;
            m_loggingErrorOccured = false;
            m_maxLogFiles = configuration.Logging.DumpFailedPipsLogLimit.GetValueOrDefault();
            m_numLogFilesGenerated = 0;
            m_dynamicDataDictionary = new ConcurrentDictionary<PipId, ProcessExecutionMonitoringReportedEventData?>();
            m_shouldDumpDynamicData = configuration.Logging.DumpFailedPipsWithDynamicData.HasValue &&
                                      configuration.Logging.DumpFailedPipsWithDynamicData.Value &&
                                      ((configuration.Sandbox.UnsafeSandboxConfiguration.MonitorFileAccesses && 
                                      configuration.Sandbox.LogObservedFileAccesses) ||
                                      configuration.Sandbox.LogProcesses);

            if (!m_shouldDumpDynamicData &&
                configuration.Logging.DumpFailedPipsWithDynamicData.HasValue &&
                configuration.Logging.DumpFailedPipsWithDynamicData.Value)
            {
                Logger.Log.DumpPipLiteSettingsMismatch(m_loggingContext);
            }
        }

        /// <inheritdoc/>
        public override bool CanHandleWorkerEvents => true;

        /// <inheritdoc/>
        public override IExecutionLogTarget CreateWorkerTarget(uint workerId) => this;

        /// <summary>
        /// Hooks into the log target for pip execution performance data which will be called
        /// when a pip fails. This will then dump relevant information on the failing pip
        /// to a JSON file specified under <see cref="m_logPath"/>.
        /// The maximum number of logs generated can be specified using the 
        /// /DumpFailedPipsLogCount parameter.
        /// </summary>
        /// <remarks>
        /// If an error occurs while serializing/dumping the specified pip,
        /// then this analyzer will be disabled for the remainder of this build and
        /// a warning will be logged with more details.
        /// </remarks>
        public override void PipExecutionPerformance(PipExecutionPerformanceEventData data)
        {
            // Get the number of file access violations for this pip from ProcessPipExecutionPerformance if pip is a process pip
            var fileAccessViolationCount = 0;
            var processPerformance = data.ExecutionPerformance as ProcessPipExecutionPerformance;
            if (m_pipTable.GetPipType(data.PipId) == PipType.Process && processPerformance != null)
            {
                fileAccessViolationCount = processPerformance.FileMonitoringViolations.Total;
            }

            if ((data.ExecutionPerformance.ExecutionLevel == PipExecutionLevel.Failed || fileAccessViolationCount > 0) && !m_loggingErrorOccured)
            {
                var currentNumLogFiles = Interlocked.Increment(ref m_numLogFilesGenerated);

                if (currentNumLogFiles <= m_maxLogFiles)
                {
                    var pip = m_pipTable.HydratePip(data.PipId, PipQueryContext.DumpPipLiteAnalyzer);
                    ProcessExecutionMonitoringReportedEventData? dynamicData = null;

                    if (m_shouldDumpDynamicData)
                    {
                        m_dynamicDataDictionary.TryRemove(data.PipId, out dynamicData);
                    }

                    DumpPip(pip, dynamicData, currentNumLogFiles);
                }
            }
            else
            {
                // If m_loggingErrorOccured is set to true, then no data is being added to the dictionary anyway, so there is no need to remove
                if (!m_loggingErrorOccured && m_shouldDumpDynamicData)
                {
                    // The pip is not in a failed state, so it does not need to be dumped.
                    m_dynamicDataDictionary.TryRemove(data.PipId, out _);
                }
            }
        }

        /// <summary>
        /// This event will get data about reported file accesses and reported processes.
        /// The data will be added to <see cref="m_dynamicDataDictionary"/>, and will be dumped if the pip fails
        /// in the PipExecutionPerformance event.
        /// </summary>
        /// <param name="data"></param>
        public override void ProcessExecutionMonitoringReported(ProcessExecutionMonitoringReportedEventData data)
        {
            if (m_shouldDumpDynamicData && !m_loggingErrorOccured && m_numLogFilesGenerated < m_maxLogFiles)
            {
                m_dynamicDataDictionary.TryAdd(data.PipId, data);
            }
        }

        /// <summary>
        /// Ensures that a log path is created, calls <see cref="o:DumpPipLiteAnalysisUtilities.DumpPip"/>, and updates
        /// the state of the analyzer if it runs into an error.
        /// </summary>
        /// <param name="pip">Pip to be dumped.</param>
        /// <param name="dynamicData"> Dynamic data to be dumped.</param>
        /// <param name="currentNumLogFiles">The current number of dump pip lite files that have been created.</param>
        private void DumpPip(Pip pip, ProcessExecutionMonitoringReportedEventData? dynamicData, int currentNumLogFiles)
        {
            var dumpPipResult = false;

            if (!m_logPathCreated)
            {
                // A log entry should have been generated already if this fails
                m_logPathCreated = DumpPipLiteAnalysisUtilities.CreateLoggingDirectory(m_logPath.ToString(m_pipExecutionContext.PathTable), m_loggingContext);
            }

            if (m_logPathCreated)
            {
                // A log entry should have been generated already if this fails
                dumpPipResult = DumpPipLiteAnalysisUtilities.DumpPip(pip,
                                                                     dynamicData,
                                                                     m_logPath.ToString(m_pipExecutionContext.PathTable),
                                                                     m_pipExecutionContext.PathTable,
                                                                     m_pipExecutionContext.StringTable,
                                                                     m_pipExecutionContext.SymbolTable,
                                                                     m_pipGraph,
                                                                     m_loggingContext);
            }

            if (!(m_logPathCreated && dumpPipResult))
            {
                // This failure was already logged in DumpPipLiteAnalysisUtilies
                m_loggingErrorOccured = true;
            }

            if (currentNumLogFiles >= m_maxLogFiles)
            {
                // Log limit reached, log this once
                Logger.Log.RuntimeDumpPipLiteLogLimitReached(m_loggingContext, m_maxLogFiles);
            }
        }
    }
}
