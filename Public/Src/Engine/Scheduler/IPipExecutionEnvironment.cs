// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using BuildXL.Engine.Cache;
using BuildXL.Engine.Cache.Artifacts;
using BuildXL.Ipc.Common;
using BuildXL.Ipc.Interfaces;
using BuildXL.Native.IO;
using BuildXL.Pips;
using BuildXL.Pips.Graph;
using BuildXL.Pips.Operations;
using BuildXL.Plugin;
using BuildXL.ProcessPipExecutor;
using BuildXL.Processes.Remoting;
using BuildXL.Processes.VmCommandProxy;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;

namespace BuildXL.Scheduler
{
    /// <summary>
    /// Represents the environment in which a pip can execute in an incremental manner.
    /// The environment is required to provide hashes of statically-known inputs.
    /// An executing pip is required to report the hashes of produced (or up to date / cached) outputs.
    /// </summary>
    public interface IPipExecutionEnvironment
    {
        /// <summary>
        /// Context used for executing pips.
        /// </summary>
        [NotNull]
        PipExecutionContext Context { get; }

        /// <summary>
        /// Pip table holding all known pips.
        /// </summary>
        [NotNull]
        PipTable PipTable { get; }

        /// <summary>
        /// Gets the state/context required for pip execution
        /// </summary>
        [NotNull]
        PipExecutionState State { get; }

        /// <summary>
        /// Counters for pips executed in this environment. These counters include aggregate pip and caching performance information.
        /// </summary>
        [NotNull]
        CounterCollection<PipExecutorCounter> Counters { get; }

        /// <summary>
        /// The Configuration.
        /// </summary>
        /// <remarks>
        /// Ideally this is only ISandBoxConfiguration, but have to expose a larger config object for now due to existing tangling.
        /// </remarks>
        [NotNull]
        IConfiguration Configuration { get; }

        /// <summary>
        /// Gets the root mappings for the build
        /// </summary>
        [NotNull]
        IReadOnlyDictionary<string, string> RootMappings { get; }

        /// <summary>
        /// Analyzer which should be provided file monitoring violations for further analysis.
        /// </summary>
        /// <remarks>
        /// File monitoring violation analyzer is needed both for correctness and for reliability.
        /// Without this analyzer, builds can be incorrect and can also lead to catastrophic failure due to contract violation.
        /// </remarks>
        [NotNull]
        IFileMonitoringViolationAnalyzer FileMonitoringViolationAnalyzer { get; }

        /// <summary>
        /// BuildXL cache layer. This provides several facets:
        /// - Cache of artifact content, used to address artifact content by hash.
        /// - Cache of prior pip executions (either one or two phases).
        /// </summary>
        [NotNull]
        EngineCache Cache { get; }

        /// <summary>
        /// Representation of local disks, allowing storage and retrieval of content at particular paths.
        /// This store is responsible for tracking changes to paths that are accessed (including remembering
        /// their hashes to avoid re-hashing or re-materializing them).
        /// </summary>
        [NotNull]
        LocalDiskContentStore LocalDiskContentStore { get; }

        /// <summary>
        /// The PipGraph
        /// </summary>
        [NotNull]
        IPipGraphFileSystemView PipGraphView { get; }

        /// <summary>
        /// Computes content-based fingerprints given a pip.
        /// </summary>
        [NotNull]
        PipContentFingerprinter ContentFingerprinter { get; }

        /// <summary>
        /// Indicates if outputs should be materialized in background rather than inline
        /// </summary>
        bool MaterializeOutputsInBackground { get; }

        /// <summary>
        /// Returns a Boolean indicating if the scheduler has received a request for cancellation.
        /// </summary>
        bool IsTerminating { get; }

        /// <summary>
        /// CancellationToken which is set when scheduler has received a request for quick termination.
        /// </summary>
        CancellationToken SchedulerCancellationToken { get; }

        /// <summary>
        /// Returns whether the directory artifact represents a source sealed directory. If that's the case, returns the patterns and type of
        /// the source sealed.
        /// </summary>
        bool IsSourceSealedDirectory(DirectoryArtifact directoryArtifact, out bool allDirectories, out ReadOnlyArray<StringId> patterns);

        /// <summary>
        /// Returns the directory kind of the given directory artifact
        /// </summary>
        SealDirectoryKind GetSealDirectoryKind(DirectoryArtifact directoryArtifact);

        /// <summary>
        /// Records that tool warnings occurred.
        /// </summary>
        void ReportWarnings(bool fromCache, int count);

        /// <summary>
        /// Reports that a pip had had a cache descriptor hit.
        /// </summary>
        void ReportCacheDescriptorHit([NotNull]string sourceCache);

        /// <summary>
        /// Whether the execution environment should treat this pip as an artificial cache miss.
        /// </summary>
        bool ShouldHaveArtificialMiss([NotNull]Pip pip);

        /// <summary>
        /// Gets the priority of the given pip
        /// </summary>
        /// <param name="pipId">the pip id</param>
        /// <returns>the priority of the pip</returns>
        int GetPipPriority(PipId pipId);

        /// <summary>
        /// All processes that specify <paramref name="pipId"/> as <see cref="Process.ServicePipDependencies"/>.
        /// </summary>
        IEnumerable<Pip> GetServicePipClients(PipId pipId);

        /// <summary>
        /// Directory translator.
        /// </summary>
        DirectoryTranslator DirectoryTranslator { get; }

        /// <summary>
        /// Renderer used for converting <see cref="PipData"/> to string (<see cref="PipData.ToString(PipFragmentRenderer)"/>).
        /// </summary>
        [NotNull]
        PipFragmentRenderer PipFragmentRenderer { get; }

        /// <summary>
        /// IpcProvider, used by PipExecutor to execute Ipc pips.
        /// </summary>
        [NotNull]
        IIpcProvider IpcProvider { get; }

        /// <summary>
        /// PluginManager, used to load and manage plugins.
        /// </summary>
        [NotNull]
        PluginManager PluginManager { get; }

        /// <summary>
        /// Kernel connection, needed to instrument sandboxed processes / pips on macOS
        /// </summary>
        BuildXL.Processes.ISandboxConnection SandboxConnection { get; }

        /// <summary>
        /// Sets the maximum number of external processes run concurrently so far.
        /// </summary>
        void SetMaxExternalProcessRan();

        /// <summary>
        /// Checks if file is rewritten.
        /// </summary>
        bool IsFileRewritten(FileArtifact file);

        /// <summary>
        /// Checks if a handle for the specified file should be created with sequential scan.
        /// </summary>
        bool ShouldCreateHandleWithSequentialScan(FileArtifact file);

        /// <summary>
        /// VM initializer.
        /// </summary>
        VmInitializer VmInitializer { get; }

        /// <summary>
        /// Remoting process manager.
        /// </summary>
        IRemoteProcessManager RemoteProcessManager { get; }

        /// <summary>
        /// Temp directory cleaner
        /// </summary>
        ITempCleaner TempCleaner { get; }

        /// <summary>
        /// Retruns the declared producer pip of <paramref name="artifact"/>, if one exists.
        /// </summary>
        bool TryGetProducerPip(in FileOrDirectoryArtifact artifact, out PipId producer);

        /// <summary>
        /// Returns whether pip <paramref name="to"/> is reachable from pip <paramref name="from"/>
        /// </summary>
        bool IsReachableFrom(PipId from, PipId to);

        /// <summary>
        /// Resolves file accesses containing symlinked paths
        /// </summary>
        [AllowNull] ReparsePointResolver ReparsePointAccessResolver { get; }

        /// <summary>
        /// Indicates if inputs are lazily materialized via MaterializeInput step.
        /// </summary>
        bool InputsLazilyMaterialized { get; }

        /// <summary>
        /// The set of <see cref="ISandboxConfiguration.GlobalUnsafeUntrackedScopes"/> translated with <see cref="DirectoryTranslator"/>
        /// </summary>
        IReadOnlySet<AbsolutePath> TranslatedGlobalUnsafeUntrackedScopes { get; }

        /// <summary>
        /// Test hooks for scheduler.
        /// </summary>
        SchedulerTestHooks SchedulerTestHooks { get; }
    }

    /// <summary>
    /// Extension methodss for <see cref="IPipExecutionEnvironment"/>.
    /// </summary>
    public static class PipExecutionEnvironmentExtensions
    {
        /// <summary>
        /// Creates a default renderer that can be used for <see cref="IPipExecutionEnvironment.PipFragmentRenderer"/>.
        /// </summary>
        public static PipFragmentRenderer CreatePipFragmentRenderer(this IPipExecutionEnvironment env)
        {
            return new PipFragmentRenderer(env.Context.PathTable, mId => env.IpcProvider.LoadAndRenderMoniker(mId), env.ContentFingerprinter.ContentHashLookupFunction);
        }
    }
}
