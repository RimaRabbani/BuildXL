// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Interop;
using BuildXL.Interop.Unix;
using BuildXL.Native.IO;
using BuildXL.Utilities.Collections;
using BuildXL.Processes.Tracing;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.ParallelAlgorithms;
using Microsoft.Win32.SafeHandles;
using static BuildXL.Interop.Unix.Sandbox;

namespace BuildXL.Processes
{
    /// <summary>
    /// A connection that communicates with a sandbox via a FIFO (a.k.a., named pipe).
    ///
    /// A separate FIFO is used for each pip.  The sandbox is injected into the pip
    /// by virtue of setting the LD_PRELOAD environment variable to point to a native
    /// dynamic library where all the system call interposing is implemented.
    /// </summary>
    public sealed class SandboxConnectionLinuxDetours : ISandboxConnection
    {
        // Used to signal that no active processes were seen through the FIFO. The value -21 is arbitrary,
        // it could be any negative value, as it will be read in place of a value representing
        // a length, which could take any positive value.
        // This value means that we *may* have reached the end of reports since no active processes are around. We may
        // still have reports to be processed containing start process reports.
        private const int NoActiveProcessesSentinel = -21;
        
        // This value means that we actually reached the end of the reports: no reports need to be processed and we got to
        // 0 active processes
        private const int EndOfReportsSentinel = -22;

        private static readonly string s_detoursLibFile = SandboxedProcessUnix.EnsureDeploymentFile("libDetours.so");
        private static readonly string s_auditLibFile = SandboxedProcessUnix.EnsureDeploymentFile("libBxlAudit.so");

        /// <summary>
        /// Environment variable containing the path to the file access manifest to be read by the detoured process.
        /// </summary>
        public static readonly string BuildXLFamPathEnvVarName = "__BUILDXL_FAM_PATH";

        /// <summary>
        /// Environment variable containing the PID for the ptracerunner process to trace when it is launched.
        /// </summary>
        public static readonly string BuildXLTracedProcessPid = "__BUILDXL_TRACED_PID";

        /// <summary>
        /// Environment variable containing the path to a ptraced process for the ptracerunner process to use.
        /// </summary>
        public static readonly string BuildXLTracedProcessPath = "__BUILDXL_TRACED_PATH";

        internal sealed class PathCacheRecord
        {
            internal RequestedAccess RequestedAccess { get; set; }

            internal RequestedAccess GetClosure(RequestedAccess access)
            {
                var result = RequestedAccess.None;

                // Read implies Probe
                if (access.HasFlag(RequestedAccess.Read))
                {
                    result |= RequestedAccess.Probe;
                }

                // Write implies Read and Probe
                if (access.HasFlag(RequestedAccess.Write))
                {
                    result |= RequestedAccess.Read | RequestedAccess.Probe;
                }

                return result;
            }

            internal bool CheckCacheHitAndUpdate(RequestedAccess access)
            {
                // if all flags in 'access' are already present --> cache hit
                bool isCacheHit = (RequestedAccess & access) == access;
                if (!isCacheHit)
                {
                    RequestedAccess |= GetClosure(access);
                }
                return isCacheHit;
            }
        }

        internal sealed class Info : IDisposable
        {
            /// <summary>
            /// Encapsulates both a background thread that is processing incoming messages, and an action block that is processing said messages.
            /// </summary>
            /// <remarks>
            /// The intention is for report processors use the same backing <see cref="SandboxConnectionLinuxDetours.Info"/> to ultimately process the incoming messages 
            /// (using <see cref="Info.ProcessBytes(ValueTuple{ReportProcessor, PooledObjectWrapper{byte[]}, int})"/>), because all the messages are associated to the same sandbox,
            /// but use different FIFOs (and thus consuming threads and processing action blocks). 
            /// We expose a <see cref="JoinReceivingThread"/> method to join the receiving thread, the <see cref="Completion"/> property of the message-processing action block and 
            /// a <see cref="Complete"/> to stop receiving access reports.
            /// </remarks>
            internal sealed class ReportProcessor
            {
                internal readonly Info Info;
                private readonly Thread m_workerThread;
                private readonly ActionBlockSlim<(ReportProcessor reportProcessor, PooledObjectWrapper<byte[]> wrapper, int length)> m_processingBlock;
                private int m_completeAccessReportProcessingCounter;
                private readonly string m_fifoName;
                private readonly Lazy<SafeFileHandle> m_fifoWriteHandle;
                
                // We use these two to synchronize sending a sentinel via the write handle and disposing the read handle. Trying to write to a FIFO with no read handles open
                // produces an error (a broken pipe)
                private bool m_readHandleDisposed = false;
                internal object ReadHandleLock { get; } = new();
                
                /// <summary>
                /// Whether the read handle has been disposed
                /// </summary>
                internal bool IsReadHandleDisposed() => m_readHandleDisposed;

                public ReportProcessor(Info info, string fifoName, Lazy<SafeFileHandle> fifoHandle)
                {
                    Info = info;
                    m_fifoName = fifoName;
                    m_fifoWriteHandle = fifoHandle;

                    m_workerThread = new Thread(() => StartReceivingAccessReports(m_fifoName, fifoHandle))
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.Highest
                    };

                    m_processingBlock = ActionBlockSlim.Create<(ReportProcessor reportProcessor, PooledObjectWrapper<byte[]> wrapper, int length)>(degreeOfParallelism: 1,
                        Info.ProcessBytes,
                        singleProducedConstrained: true // Only m_workerThread posts to the action block
                    );

                    IsPrimaryFifoProcessor = m_fifoWriteHandle == info.m_lazyWriteHandle;
                }

                internal void Start() => m_workerThread.Start();

                internal void Complete()
                {
                    LogDebug($"Complete action requested for {GetFifoName()}");

                    // Send the end of report sentinel, so we can exit the report loop
                    Info.WriteSentinel(m_fifoWriteHandle, s_endOfReportsSentinelAsBytes);
                }

                internal string GetFifoName() => m_fifoName;

                internal bool IsPrimaryFifoProcessor { get; }

                internal void CompleteAccessReportProcessing()
                {
                    var cnt = Interlocked.Increment(ref m_completeAccessReportProcessingCounter);
                    if (cnt > 1)
                    {
                        return; // already completed
                    }

                    m_processingBlock.Complete();
                }

                /// <nodoc />
                internal Task Completion => m_processingBlock.Completion;

                /// <nodoc />
                internal void JoinReceivingThread() => m_workerThread.Join();

                private static int Read(SafeFileHandle handle, byte[] buffer, int offset, int length)
                {
                    Contract.Requires(buffer.Length >= offset + length);
                    int totalRead = 0;
                    while (totalRead < length)
                    {
                        var numRead = IO.Read(handle, buffer, offset + totalRead, length - totalRead);
                        if (numRead <= 0)
                        {
                            return numRead;
                        }
                        totalRead += numRead;
                    }

                    return totalRead;
                }

                private void LogDebug(string s) => Info.Process.LogDebug(s);

#if NETCOREAPP
                private void LogDebug([InterpolatedStringHandlerArgument("")] DebugMessageInterpolatedStringHandler builder) => Info.Process.LogDebug(builder);
#endif
                private void LogError(string s) => Info.LogError(s);

                /// <summary>
                /// The method backing the <see cref="m_workerThread"/> thread.
                /// </summary>
                /// <remarks>
                /// The way we deal with the decision about when to stop reading messages from the FIFO deserves some details:
                /// * Messages are read from the FIFO and posted to an action block <see cref="m_processingBlock"/>, which processes them async.
                /// * A write handle <see cref="m_lazyWriteHandle"/> is kept open to avoid reaching EOF if other writers (running tools) happen to close the FIFO
                /// * The potential end of the receive loop is triggered by removing the last active process from <see cref="m_activeProcesses"/>. This
                ///   can happen because the <see cref="m_activeProcessesChecker"/> detected than an active process is no longer alive or because 
                ///   a process exited report is seen. When this case is reached a special message <see cref="NoActiveProcessesSentinel"/> is sent from this
                ///   same loop. Sentinels are just special messages used for synchronization purposes.
                /// * Sending <see cref="NoActiveProcessesSentinel"/> *may* result in ending this processing loop: when <see cref="NoActiveProcessesSentinel"/> is sent, 
                ///   other messages may still be on the processing pipe of <see cref="m_processingBlock"/> (e.g. observe that the active process checker runs in a separate 
                ///   thread, and the point in time when the sentinel is sent is not synchronized with the point in time when we processed all reports). When we get to 
                ///   processing the sentinel and if 'start process' reports had arrived, we just ignore the sentinel and keep processing messages. We will eventually
                ///   reach again 0 processes and the sentinel will be sent another time.
                /// * If <see cref="NoActiveProcessesSentinel"/> arrives and we see 0 active processes, we can safely exit the loop. In this case we send another
                ///   sentinel <see cref="EndOfReportsSentinel"/>. Instead of this we could just close <see cref="m_lazyWriteHandle"/> and let the receiving loop reach an EOF,
                ///   but this proved to be slow in some cases (since it is likely depending on some GC process). So instead we send a sentinel. The loop can safely exit when we
                ///   see this since no pending messages can be left to be processed (we saw the <see cref="NoActiveProcessesSentinel"/> on the other end of the pipe at the 
                ///   same pipe there were 0 active processes).
                /// * A note on the secondary FIFO: the processing loop for the secondary FIFO (used to communicate ptrace specific messages) goes through the same flow, with the 
                ///   caveat that we only initiate the tear down process of the seconday FIFO once the decide to exit the primary FIFO via <see cref="EndOfReportsSentinel"/>. The reason
                ///   is that we want the secondary FIFO to be alive throughout the lifetime of the first FIFO. 
                /// </remarks>
                private void StartReceivingAccessReports(string fifoName, Lazy<SafeFileHandle> fifoHandle)
                {
                    // opening FIFO for reading (blocks until there is at least one writer connected)
                    LogDebug($"Opening FIFO '{fifoName}' for reading");

                    var readHandle = IO.Open(fifoName, IO.OpenFlags.O_RDONLY, 0);
                    try
                    {
                        if (readHandle.IsInvalid)
                        {
                            LogError($"Opening FIFO {fifoName} for reading failed.");
                            return;
                        }

                        // make sure that m_lazyWriteHandle has been created
                        Analysis.IgnoreResult(fifoHandle.Value);

                        byte[] messageLengthBytes = new byte[sizeof(int)];
                        while (true)
                        {
                            // read length
                            var numRead = Read(readHandle, messageLengthBytes, 0, messageLengthBytes.Length);
                            if (numRead == 0) // EOF
                            {
                                // We don't expect EOF before reading the EndOfReportsSentinel (see below)
                                LogError("Exiting 'receive reports' loop on EOF without observing the end of reports sentinel value.");
                                break;
                            }

                            if (numRead < 0) // error
                            {
                                LogError($"Read from FIFO {fifoName} failed with return value {numRead}.");
                                break;
                            }

                            // decode length
                            int messageLength = BitConverter.ToInt32(messageLengthBytes, startIndex: 0);

                            // The process tree we know about so far has completed. We might still
                            // have 'process start' reports to be processed, so we just send this sentinel and let the processing block decide.
                            if (messageLength == NoActiveProcessesSentinel)
                            {
                                m_processingBlock.Post((this, ByteArrayPool.GetInstance(0), NoActiveProcessesSentinel), throwOnFullOrComplete: true);
                                continue;
                            }

                            // We processed all pending messages in the processing block and didn't see any active processes, we can exit the loop
                            if (messageLength == EndOfReportsSentinel)
                            {
                                LogDebug($"End of reports sentinel arrived on FIFO {fifoName}. Exiting 'receive reports' loop.");

                                // The primary FIFO has no more reports. Terminate the secondary FIFO.
                                if (IsPrimaryFifoProcessor && !string.IsNullOrEmpty(Info.SecondaryFifoPath))
                                {
                                    Info.WriteSentinel(Info.m_lazySecondaryFifoWriteHandle, s_noActiveProcessesSentinelAsBytes);

                                    LogDebug("NoProcessesSentinel sent to secondary FIFO");
                                }

                                break;
                            }

                            // read a message of that length
                            PooledObjectWrapper<byte[]> messageBytes = ByteArrayPool.GetInstance(messageLength);
                            numRead = Read(readHandle, messageBytes.Instance, 0, messageLength);
                            if (numRead < messageLength)
                            {
                                LogError($"Read from FIFO {fifoName} failed: read only {numRead} out of {messageLength} bytes.");
                                messageBytes.Dispose();
                                break;
                            }

                            // Add message to processing queue
                            try
                            {
                                m_processingBlock.Post((this, messageBytes, messageLength), throwOnFullOrComplete: true);
                            }
                            catch (Exception e)
                            {
                                Analysis.IgnoreException("Will error and exit on LogError");
                                LogError($"Could not post message to the processing block for {fifoName}. Exception details: {e}");
                                break;
                            }
                        }

                        LogDebug($"Completed receiving access reports for fifo '{fifoName}'");
                    }
                    finally
                    {
                        // Synchronize the disposal to make sure we don't try to send a sentinel (e.g. the active process checker seeing 0 processes)
                        // while disposing the read handle
                        lock (ReadHandleLock)
                        {
                            LogDebug($"Disposing read handle for fifo '{fifoName}'");
                            m_readHandleDisposed = true;
                            readHandle.Dispose();
                        }
                    }

                    CompleteAccessReportProcessing();
                }
            }

            internal SandboxedProcessUnix Process { get; }
            internal string ReportsFifoPath { get; }
            /// <summary>
            /// This fifo is used to communication between the process and BuildXL for non-file access related messages.
            /// We use a second pipe here because it is possible for the reports pipe to drain slow due to a large number of messages.
            /// </summary>
            internal string SecondaryFifoPath { get; }
            internal string FamPath { get; }

            private readonly Sandbox.ManagedFailureCallback m_failureCallback;
            private readonly Dictionary<string, PathCacheRecord> m_pathCache; // TODO: use AbsolutePath instead of string
            private readonly bool m_isInTestMode;

            /// <remarks>
            /// This dictionary is accessed both from the report processor threads as well as the thread
            /// backing <see cref="m_activeProcessesChecker"/>, hence it must be thread-safe.
            ///
            /// Implementation detail: ConcurrentDictionary is used to implement a thread-safe set (because no
            /// ConcurrentSet class exists).  Therefore, the values in this dictionary are completely ignored;
            /// the keys represent the set of currently active process IDs.
            /// </remarks>
            private readonly ConcurrentDictionary<int, byte> m_activeProcesses;

            private readonly CancellableTimedAction m_activeProcessesChecker;
            private readonly Lazy<SafeFileHandle> m_lazyWriteHandle;
            private readonly Lazy<SafeFileHandle> m_lazySecondaryFifoWriteHandle;

            private readonly ReportProcessor m_reportProcessor;
            private readonly ReportProcessor m_secondaryReportProcessor;
            private static readonly TimeSpan s_activeProcessesCheckerInterval = TimeSpan.FromSeconds(1);

            // These are just the byte representations of the sentinel values, so we don't need to compute them over and over
            private static readonly byte[] s_noActiveProcessesSentinelAsBytes = BitConverter.GetBytes(NoActiveProcessesSentinel);
            private static readonly byte[] s_endOfReportsSentinelAsBytes = BitConverter.GetBytes(EndOfReportsSentinel);

            private static ArrayPool<byte> ByteArrayPool { get; } = new ArrayPool<byte>(4096);

            private ReportProcessor GetReportProcessorFor(Lazy<SafeFileHandle> lazyWriteHandle) => lazyWriteHandle == m_lazyWriteHandle ? m_reportProcessor : m_secondaryReportProcessor;

            internal Info(Sandbox.ManagedFailureCallback failureCallback, SandboxedProcessUnix process, string reportsFifoPath, string secondaryFifoPath, string famPath, bool isInTestMode)
            {
                m_isInTestMode = isInTestMode;
                m_failureCallback = failureCallback;
                Process = process;
                ReportsFifoPath = reportsFifoPath;
                SecondaryFifoPath = secondaryFifoPath;
                FamPath = famPath;

                m_pathCache = new Dictionary<string, PathCacheRecord>();
                m_activeProcesses = new ConcurrentDictionary<int, byte>();
                m_activeProcessesChecker = new CancellableTimedAction(
                    CheckActiveProcesses,
                    intervalMs: Math.Min((int)process.ChildProcessTimeout.TotalMilliseconds, (int)s_activeProcessesCheckerInterval.TotalMilliseconds));

                // create a write handle (used to keep the fifo open, i.e.,
                // the 'read' syscall won't receive EOF until we close this writer
                m_lazyWriteHandle = GetLazyWriteHandle(ReportsFifoPath);

                // will start a background thread for reading from the FIFO
                m_reportProcessor = new ReportProcessor(this, ReportsFifoPath, m_lazyWriteHandle);

                // Second thread for reading the secondary FIFO
                // The secondary pipe is used here to allow for messages that are higher priority (such as ptrace notifications)
                // to be delivered back to the managed layer faster if the fifo used for file access reports is congested.
                Task secondaryCompletion = Task.CompletedTask;
                if (!string.IsNullOrEmpty(SecondaryFifoPath))
                {
                    m_lazySecondaryFifoWriteHandle = GetLazyWriteHandle(SecondaryFifoPath);
                    m_secondaryReportProcessor = new ReportProcessor(this, SecondaryFifoPath, m_lazySecondaryFifoWriteHandle);
                    secondaryCompletion = m_secondaryReportProcessor.Completion;
                }

                // Post the process tree completion after we process all reports
                Task.WhenAll(m_reportProcessor.Completion, secondaryCompletion).ContinueWith(t =>
                {
                    LogDebug("Posting OpProcessTreeCompleted message");
                    Process.PostAccessReport(new AccessReport
                    {
                        Operation = FileOperation.OpProcessTreeCompleted,
                        PathOrPipStats = AccessReport.EncodePath("")
                    });
                });
            }

            private Lazy<SafeFileHandle> GetLazyWriteHandle(string path)
            {
                return new Lazy<SafeFileHandle>(() =>
                {
                    LogDebug($"Opening FIFO '{path}' for writing");
                    return IO.Open(path, IO.OpenFlags.O_WRONLY, 0);
                });
            }

            /// <summary>
            /// Starts receiving access reports
            /// </summary>
            internal void Start()
            {
                m_reportProcessor.Start();
                m_secondaryReportProcessor?.Start();
            }

            private void CheckActiveProcesses()
            {
                foreach (var pid in m_activeProcesses.Keys)
                {
                    if (!Dispatch.IsProcessAlive(pid))
                    {
                        LogDebug($"CheckActiveProcesses. Removing {pid}.");
                        RemovePid(pid);
                    }
                }
            }

            /// <summary>
            /// Request to stop receiving access reports. 
            /// Any currently pending reports will be processed asynchronously.
            /// </summary>
            internal void RequestStop()
            {
                LogDebug($"RequestStop: closing the write handle for FIFO '{ReportsFifoPath}'");

                m_lazyWriteHandle.Value.Close();
                m_lazyWriteHandle.Value.Dispose();

                if (!string.IsNullOrEmpty(SecondaryFifoPath))
                {
                    m_lazySecondaryFifoWriteHandle.Value.Close();
                    m_lazySecondaryFifoWriteHandle.Value.Dispose();
                }

                m_activeProcessesChecker.Cancel();
            }

            private void WriteSentinel(Lazy<SafeFileHandle> writeHandle, byte[] sentinelBytes)
            {
                var reportProcessor = GetReportProcessorFor(writeHandle);

                // If the read or write handles are already closed, no need to send a sentinel
                if (reportProcessor.IsReadHandleDisposed() || writeHandle.Value.IsClosed || writeHandle.Value.IsInvalid)
                {
                    return;
                }
                
                // Synchronize sending the sentinel so we don't dispose the read handle without coordination
                // Without any read handle open, writing to the FIFO causes an broken pipe error
                lock (reportProcessor.ReadHandleLock)
                {
                    if (reportProcessor.IsReadHandleDisposed())
                    {
                        return;
                    }

                    // Observe this will be atomic because the length of an int is less than PIPE_BUF
                    var bytesWritten = Write(writeHandle.Value, sentinelBytes, 0, sentinelBytes.Length);
                    if (bytesWritten < 0) // error
                    {
                        string win32Message = new Win32Exception(Marshal.GetLastWin32Error()).Message;

                        StackTrace stackTrace = new StackTrace();

                        LogError($"Cannot write sentinel {BitConverter.ToInt32(sentinelBytes, 0)} to {reportProcessor.GetFifoName()}. Error: {win32Message}. {stackTrace}");

                        // Dispose the handle so we avoid a potential hang in the process reports loop
                        writeHandle.Value.Dispose();
                    }
                }
            }

            private static int Write(SafeFileHandle handle, byte[] buffer, int offset, int length)
            {
                Contract.Requires(buffer.Length >= offset + length);
                int totalWrite = 0;
                while (totalWrite < length)
                {
                    var numWrite = IO.Write(handle, buffer, offset + totalWrite, length - totalWrite);
                    if (numWrite <= 0)
                    {
                        return numWrite;
                    }
                    totalWrite += numWrite;
                }

                return totalWrite;
            }

            /// <summary>Adds <paramref name="pid" /> to the set of active processes</summary>
            internal void AddPid(int pid)
            {
                bool added = m_activeProcesses.TryAdd(pid, 1);
                LogDebug($"AddPid({pid}) :: added: {added}; size: {m_activeProcesses.Count}");
            }

            /// <summary>
            /// Removes <paramref name="pid" /> from the set of active processes.
            /// </summary>
            internal void RemovePid(int pid)
            {
                bool removed = m_activeProcesses.TryRemove(pid, out var _);
                LogDebug($"RemovePid({pid}) :: removed: {removed}; size: {m_activeProcesses.Count}");
                if (removed && m_activeProcesses.IsEmpty)
                {
                    LogDebug($"Removed {pid} and the active count is 0. Sending sentinel on primary FIFO");
                    // We just reached 0 active processes. Notify this through the FIFO so we can check on the other end
                    // whether this means we are done processing reports. There might be reports still to be processed, including start process reports,
                    // so pushing this sentinel makes sure we process all pending reports before reaching a decision
                    
                    // We only send the sentinel on the primary FIFO. The secondary one will be terminated once we decide the primary can terminate.
                    WriteSentinel(m_lazyWriteHandle, s_noActiveProcessesSentinelAsBytes);
                }
                else if (removed && pid == Process.ProcessId)
                {
                    LogDebug($"Root process {pid} was removed. Starting the active process checker.");

                    // We just removed the root process and there are still active processes left
                    //   => start periodically checking if they are still alive, because we don't
                    //      have a reliable mechanism for receiving those events straight from the
                    //      child processes (e.g., if they crash, we might not hear about it)
                    //
                    // Observe also that we do have a reliable mechanism for detecting when the
                    // root process exits (even if it crashes): see NotifyRootProcessExited below,
                    // which is guaranteed to be called by SandboxedProcessUnix.
                    m_activeProcessesChecker.Start();
                }
            }

            internal PathCacheRecord GetOrCreateCacheRecord(string path)
            {
                PathCacheRecord cacheRecord;
                if (!m_pathCache.TryGetValue(path, out cacheRecord))
                {
                    cacheRecord = new PathCacheRecord()
                    {
                        RequestedAccess = RequestedAccess.None
                    };
                    m_pathCache[path] = cacheRecord;
                }

                return cacheRecord;
            }

            internal void LogError(string message)
            {
                message = $"{message} (errno: {Marshal.GetLastWin32Error()})";
                Process.LogProcessState("[ERROR]: " + message);
                m_failureCallback?.Invoke(1, message);
            }

            internal void LogDebug(string s) => Process.LogDebug(s);

#if NETCOREAPP
            private void LogDebug([InterpolatedStringHandlerArgument("")] DebugMessageInterpolatedStringHandler builder) => Process.LogDebug(builder);
#endif

            /// <nodoc />
            public void Dispose()
            {
                RequestStop();

                try
                {
                    m_activeProcessesChecker.Join();
                }
                catch (ThreadStateException)
                {
                    // The active process checker is only started once the main thread exits and child processes are still active. So we can start disposing the connection
                    // without that being the case
                }

                m_pathCache.Clear();
                m_activeProcesses.Clear();
                Analysis.IgnoreResult(FileUtilities.TryDeleteFile(ReportsFifoPath, retryOnFailure: false));
                Analysis.IgnoreResult(FileUtilities.TryDeleteFile(FamPath, retryOnFailure: false));
                if (m_isInTestMode)
                {
                    // The worker thread should complete in all but most extreme cases.  One such extreme case
                    // is when the underlying filesystems crashes or shuts down completely (which is possible,
                    // especially if that's a custom-implemented filesystem running in user space).  When that
                    // happens, some write handled to the created FIFO may remain open, so the 'read' call in
                    // 'StartReceivingAccessReports' may remain stuck forever.
                    LogDebug("Waiting for the worker thread to complete");
                    m_reportProcessor.JoinReceivingThread();
                    m_secondaryReportProcessor?.JoinReceivingThread();
                }
            }

            /// <summary>
            /// This method is backing the message processors action block.
            /// </summary>
            private void ProcessBytes((ReportProcessor processor, PooledObjectWrapper<byte[]> wrapper, int length) item)
            {
                using (item.wrapper)
                {
                    // This means the active process checker detected that no processes were running. But we need to make sure we still have no active processes. There is a race between
                    // that count reaching 0 and a potential new create process report being processed. Since the create report is reported on the parent process (and as well on the child), if this race
                    // happened the create process report should have bumped the active process count.
                    if (item.length == NoActiveProcessesSentinel)
                    {
                        if (m_activeProcesses.IsEmpty)
                        {
                            LogDebug($"NoActiveProcessesSentinel received for fifo {item.processor.GetFifoName()} and 0 active processes found. Requesting completion to the report processor.");
                            item.processor.Complete();
                        }
                        else
                        {
                            // In this case we just ignore the message. The sentinel will be sent again once we reach 0
                            // active processes
                            LogDebug($"NoActiveProcessesSentinel received for fifo {item.processor.GetFifoName()} but {m_activeProcesses.Count} processes were detected. This means new start process reports arrived afterwards. The sentinel is ignored.");
                        }

                        return;
                    }

                    Contract.Assert(item.length > 0, "No other sentinel but the one above should be posted");

                    var messageStr = s_encoding.GetString(item.wrapper.Instance, index: 0, count: item.length);
                    var message = messageStr.AsSpan().TrimEnd('\n');

                    // parse the message, consuming the span field by field. The format is:
                    //  "%s|%d|%d|%d|%d|%d|%d|%d|%s\n", __progname, getpid(), access, status, explicitLogging, err, opcode, isDirectory, reportPath
                    var restOfMessage = message;
                    _ = nextField(restOfMessage, out restOfMessage);  // ignore progname
                    var pid = AssertInt(nextField(restOfMessage, out restOfMessage));
                    var access = (RequestedAccess)AssertInt(nextField(restOfMessage, out restOfMessage));
                    var status = AssertInt(nextField(restOfMessage, out restOfMessage));
                    var explicitlogging = AssertInt(nextField(restOfMessage, out restOfMessage));
                    var err = AssertInt(nextField(restOfMessage, out restOfMessage));
                    var opCode = AssertInt(nextField(restOfMessage, out restOfMessage));
                    var isDirectory = AssertInt(nextField(restOfMessage, out restOfMessage));
                    var path = nextField(restOfMessage, out restOfMessage);
                    Contract.Assert(restOfMessage.IsEmpty);  // We should have reached the end of the message

                    // ignore accesses to libDetours.so, because we injected that library
                    if (path.SequenceEqual(s_detoursLibFile.AsSpan()))
                    {
                        return;
                    }

                    var report = new AccessReport
                    {
                        Pid = (int)pid,
                        PipId = Process.PipId,
                        RequestedAccess = (uint)access,
                        Status = status,
                        ExplicitLogging = explicitlogging,
                        Error = err,
                        Operation = (FileOperation)opCode,
                        PathOrPipStats = s_encoding.GetBytes(path.ToArray()),
                        IsDirectory = isDirectory,
                    };

                    // update active processes
                    if (report.Operation == FileOperation.OpProcessStart)
                    {
                        // We should never get process start messages in the secondary FIFO. We run the risk of having exited the primary FIFO already,
                        // and never sending the sentinel to the secondary one.
                        Contract.Assert(item.processor.IsPrimaryFifoProcessor, "Process start messages can only arrive to the primary FIFO");

                        LogDebug($"Received FileOperation.OpProcessStart for pid {report.Pid})");
                        AddPid(report.Pid);
                    }
                    else if (report.Operation == FileOperation.OpProcessExit)
                    {
                        LogDebug($"Received FileOperation.OpProcessExit for pid {report.Pid})");
                        RemovePid(report.Pid);
                    }
                    else
                    {
                        // We don't want to check the path cache for statically linked processes
                        // because we rely on this report to start the ptrace sandbox.
                        // OpProcessCommandLine can also be skipped because its not a path, and shouldn't be cached.
                        if (report.Operation != FileOperation.OpStaticallyLinkedProcess && report.Operation != FileOperation.OpProcessCommandLine)
                        {
                            var pathStr = path.ToString();
                            // check the path cache (only when the message is not about process tree)                        
                            if (GetOrCreateCacheRecord(pathStr).CheckCacheHitAndUpdate(access))
                            {
                                LogDebug($"Cache hit for access report: ({pathStr}, {access})");
                                return;
                            }
                        }
                    }

                    // post the AccessReport
                    Process.PostAccessReport(report);
                }

                // Reads next field of the serialized message, i.e. split on the first | and return both parts
                static ReadOnlySpan<char> nextField(ReadOnlySpan<char> message, out ReadOnlySpan<char> rest)
                {
                    for (int i = 0; i < message.Length; i++)
                    {
                        if (message[i] == '|')
                        {
                            rest = i + 1 == message.Length ? ReadOnlySpan<char>.Empty : message.Slice(i+1); // Defend against | being the last character, although we don't expect this
                            return message.Slice(0, i);
                        }
                    }

                    rest = ReadOnlySpan<char>.Empty;
                    return message;
                }
            }

            private uint AssertInt(ReadOnlySpan<char> str)
            {
#if NETCOREAPP
                if (uint.TryParse(str, out uint result))
#else // .NET 472 - no ReadOnlySpan<char> overloads. We don't really care about perf for .NET472 here
                if (uint.TryParse(str.ToString(), out uint result))
#endif
                {
                    return result;
                }
                else
                {
                    LogError($"Could not parse int from '{str.ToString()}'");
                    return 0;
                }
            }
        }

        /// <inheritdoc />
        public SandboxKind Kind => SandboxKind.LinuxDetours;

        /// <inheritdoc />
        /// <remarks>Unimportant</remarks>
        public ulong MinReportQueueEnqueueTime => (ulong)DateTime.UtcNow.Ticks;

        /// <inheritdoc />
        public bool IsInTestMode { get; }

        /// <summary>
        /// Name of PTrace runner file.
        /// </summary>
        public const string PTraceRunnerFileName = "ptracerunner";

        private readonly ConcurrentDictionary<long, Info> m_pipProcesses = new();

        private readonly ManagedFailureCallback m_failureCallback;

        private static readonly Encoding s_encoding = Encoding.UTF8;

        /// <inheritdoc />
        /// <remarks>Unimportant</remarks>
        public TimeSpan CurrentDrought => TimeSpan.FromSeconds(0);

        /// <nodoc />
        public SandboxConnectionLinuxDetours(ManagedFailureCallback failureCallback = null, bool isInTestMode = false)
        {
            m_failureCallback = failureCallback;
            IsInTestMode = isInTestMode;
            Native.Processes.ProcessUtilities.SetNativeConfiguration(SandboxConnection.IsInDebugMode);
        }

        /// <inheritdoc />
        public void ReleaseResources()
        {
        }

        /// <summary>
        /// Disposes the sandbox kernel extension connection and release the resources in the interop layer, when running tests this can be skipped
        /// </summary>
        public void Dispose()
        {
            ReleaseResources();
        }

        /// <inheritdoc />
        public bool NotifyUsage(uint cpuUsage, uint availableRamMB)
        {
            return true;
        }

        /// <inheritdoc />
        public IEnumerable<(string, string)> AdditionalEnvVarsToSet(SandboxedProcessInfo info, string uniqueName)
        {
            var detoursLibPath = info.RootJailInfo.CopyToRootJailIfNeeded(s_detoursLibFile);
            (_, _, string famPath) = GetPaths(info.RootJailInfo, uniqueName);

            yield return ("__BUILDXL_ROOT_PID", "1"); // CODESYNC: Public/Src/Sandbox/Linux/common.h (temp solution for breakaway processes)
            yield return (BuildXLFamPathEnvVarName, info.RootJailInfo.ToPathInsideRootJail(famPath));
            yield return ("__BUILDXL_DETOURS_PATH", detoursLibPath);

            if (info.RootJailInfo?.DisableSandboxing != true)
            {
                yield return ("LD_PRELOAD", detoursLibPath + ":" + info.EnvironmentVariables.TryGetValue("LD_PRELOAD", string.Empty));
            }

            // Auditing is disabled by default. LD_AUDIT is able to observe the dependencies on system-level libraries that LD_PRELOAD misses. These libraries
            // may include libgcc, libc, libpthread and libDetours itself. System libraries are typically not part of the fingerprint since their behavior is unlikely
            // to change (a similar effect can be achieved by enabling 'dependsOnCurrentHosOSDirectories' on DScript). In particular, that libDetours itself is detected could
            // be problematic since any change in the sandbox version will imply a cache miss.
            // In addition to that, LD_AUDIT is known to be expensive from a perf standpoint (perf analysis on some JS customers showed a 2X degradation in sandboxing overhead
            // on e2e builds when LD_AUDIT is on).
            if (info.RootJailInfo?.DisableAuditing == false)
            {
                yield return ("LD_AUDIT", info.RootJailInfo.CopyToRootJailIfNeeded(s_auditLibFile) + ":" + info.EnvironmentVariables.TryGetValue("LD_AUDIT", string.Empty));
            }
        }

        /// <summary>
        /// Returns the paths for the FIFO and FAM based on the unique name for a pip.
        /// </summary>
        public static (string fifo, string secondaryFifo, string fam) GetPaths(RootJailInfo? rootJailInfo, string uniqueName)
        {
            string rootDir = rootJailInfo?.RootJail ?? Path.GetTempPath();
            string fifoPath = Path.Combine(rootDir, $"bxl_{uniqueName}.fifo");
            // CODESYNC: Public/Src/Sandbox/Linux/bxl_observer.cpp
            string secondaryFifoPath = Path.Combine(rootDir, $"bxl_{uniqueName}.fifo2");
            string famPath = Path.ChangeExtension(fifoPath, ".fam");
            return (fifo: fifoPath, secondaryFifo: secondaryFifoPath, fam: famPath);
        }

        /// <inheritdoc />
        public bool NotifyPipStarted(SandoxedProcessLogAction sandboxedProcessLogAction, FileAccessManifest fam, SandboxedProcessUnix process) => true;

        /// <inheritdoc />
        public void NotifyPipReady(SandoxedProcessLogAction sandboxedProcessLogAction, FileAccessManifest fam, SandboxedProcessUnix process, Task reportCompletion)
        {
            Contract.Requires(!process.Started);
            Contract.Requires(process.PipId != 0);

            (string fifoPath, string secondaryFifoPath, string famPath) = GetPaths(process.RootJailInfo, process.UniqueName);
            
            if (IsInTestMode)
            {
                fam.EnableLinuxSandboxLogging = true;
            }

            // serialize FAM
            using (var wrapper = Pools.MemoryStreamPool.GetInstance())
            {
                var debugFlags = true;
                ArraySegment<byte> manifestBytes = fam.GetPayloadBytes(
                    sandboxedProcessLogAction,
                    new FileAccessSetup { DllNameX64 = string.Empty, DllNameX86 = string.Empty, ReportPath = process.ToPathInsideRootJail(fifoPath) },
                    wrapper.Instance,
                    timeoutMins: 10, // don't care
                    debugFlagsMatch: ref debugFlags);

                Contract.Assert(manifestBytes.Offset == 0);
                File.WriteAllBytes(famPath, manifestBytes.ToArray());
            }

            process.LogDebug($"Saved FAM to '{famPath}'");

            // create a FIFO (named pipe)
            createNewFifo(fifoPath);

            // Secondary fifo is only used by the ptrace sandbox for now
            if (fam.EnableLinuxPTraceSandbox)
            {
                createNewFifo(secondaryFifoPath);
            }
            else
            {
                secondaryFifoPath = string.Empty;
            }

            // create and save info for this pip
            var info = new Info(m_failureCallback, process, fifoPath, secondaryFifoPath, famPath, IsInTestMode);
            if (!m_pipProcesses.TryAdd(process.PipId, info))
            {
                throw new BuildXLException($"Process with PidId {process.PipId} already exists");
            }

            // Make sure we dispose the process info after report processing is completed
            reportCompletion.ContinueWith(t => info.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.RunContinuationsAsynchronously);

            info.Start();

            void createNewFifo(string fifo)
            {
                Analysis.IgnoreResult(FileUtilities.TryDeleteFile(fifo, retryOnFailure: false));
                if (IO.MkFifo(fifo, IO.FilePermissions.S_IRWXU) != 0)
                {
                    throw new BuildXLException($"Creating FIFO {fifo} failed. (errno: {Marshal.GetLastWin32Error()})");
                }

                process.LogDebug($"Created FIFO at '{fifo}'");
            }
        }

        /// <inheritdoc />
        public void NotifyPipProcessTerminated(long pipId, int processId)
        {
            if (m_pipProcesses.TryGetValue(pipId, out var info))
            {
                info.Process.LogDebug($"NotifyPipProcessTerminated. Removing pid {processId}");
                info.RemovePid(processId);
            }
        }

        /// <inheritdoc />
        public void NotifyRootProcessExited(long pipId, SandboxedProcessUnix process)
        {
            if (m_pipProcesses.TryGetValue(pipId, out var info))
            {
                info.Process.LogDebug($"NotifyRootProcessExited. Removing pid {process.ProcessId}");
                info.RemovePid(process.ProcessId);
            }
        }

        /// <inheritdoc />
        public bool NotifyPipFinished(long pipId, SandboxedProcessUnix process) => m_pipProcesses.TryRemove(pipId, out _);
    }
}
