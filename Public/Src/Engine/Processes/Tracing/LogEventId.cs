﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using BuildXL.Utilities.Instrumentation.Common;

namespace BuildXL.Processes.Tracing
{
    // disable warning regarding 'missing XML comments on public API'. We don't need docs for these values
#pragma warning disable 1591

    /// <summary>
    /// Defines event IDs corresponding to events />
    /// </summary>
    public enum LogEventId
    {
        None = 0,

        PipProcessDisallowedFileAccess = 9,
        PipProcessFileAccess = 10,
        PipProcessStartFailed = 11,
        PipProcessFinished = 12,
        PipProcessFinishedFailed = 13,
        PipProcessFileNotFound = 14,
        PipProcessTookTooLongWarning = 15,
        PipProcessTookTooLongError = 16,
        PipProcessStandardOutput = 17,
        PipProcessStandardError = 18,
        ReadWriteFileAccessConvertedToReadMessage = 19,
        PipProcessDisallowedTempFileAccess = 20,
        ReadWriteFileAccessConvertedToReadWarning = 21,
        PipProcessFileAccessTableEntry = 22,
        PipInvalidDetoursDebugFlag1 = 23,
        PipInvalidDetoursDebugFlag2 = 24,

        PipProcessFinishedDetourFailures = 26,
        PipProcessCommandLineTooLong = 32,
        PipProcessInvalidWarningRegex = 39,
        PipProcessChildrenSurvivedError = 41,
        PipProcessChildrenSurvivedKilled = 42,
        PipProcessChildrenSurvivedTooMany = 43,
        PipProcessMissingExpectedOutputOnCleanExit = 44,
        PipProcessWroteToStandardErrorOnCleanExit = 45,
        PipProcessOutputPreparationFailed = 46,
        PipProcessOutputPreparationToBeRetriedInVM = 47,
        PipProcessPreserveOutputDirectoryFailedToMakeFilePrivate = 53,
        PipProcessPreserveOutputDirectorySkipMakeFilesPrivate = 54,

#pragma warning disable 618
        CancellationRequested = SharedLogEventId.CancellationRequested,
        PipProcessError = SharedLogEventId.PipProcessError,
        PipProcessWarning = SharedLogEventId.PipProcessWarning,
#pragma warning restore 618
        PipProcessOutput = 66,

        PipProcessResponseFileCreationFailed = 74,

        PipProcessStartExternalTool = 78,
        PipProcessFinishedExternalTool = 79,
        PipProcessStartExternalVm = 80,
        PipProcessFinishedExternalVm = 81,
        RetryStartPipDueToErrorPartialCopyDuringDetours = 85,

        PipProcessStandardInputException = 86,

        PipProcessInvalidErrorRegex = 89,
        PipProcessChangeAffectedInputsWrittenFileCreationFailed = 90,

        PipProcessNeedsExecuteExternalButExecuteInternal = 92,
        LogPhaseDuration = 93,

        PipProcessDisallowedFileAccessAllowlistedCacheable = 264,
        PipProcessDisallowedFileAccessAllowlistedNonCacheable = 269,
        FileAccessAllowlistFailedToParsePath = 274,
        CannotProbeOutputUnderSharedOpaque = 275,

        //// Reserved = 306,
        //// Reserved = 307,
        PipFailSymlinkCreation = 308,
        //// Reserved = 309,

        PipProcessMessageParsingError = 311,

        PipExitedUncleanly = 314,
        PipStandardIOFailed = 316,

        PipRetryDueToExitedWithAzureWatsonExitCode = 317,
        PipFinishedWithSomeProcessExitedWithAzureWatsonExitCode = 319,

        DuplicateWindowsEnvironmentVariableEncountered = 336,

        PipProcessDisallowedNtCreateFileAccessWarning = 480,

        PipProcessExpectedMissingOutputs = 504,

        //// Additional Process Isolation
        PipProcessIgnoringPathWithWildcardsFileAccess = 800,
        PipProcessIgnoringPathOfSpecialDeviceFileAccess = 801,
        PipProcessFailedToParsePathOfFileAccess = 802,
        Process = 803,

        SharedOpaqueOutputsDeletedLazily = 874,
        CannotReadSidebandFileError = 875,
        CannotReadSidebandFileWarning = 876,
        CannotDeleteSharedOpaqueOutputFile = 877,
        ResumeOrSuspendProcessError = 878,
        ResumeOrSuspendException = 879,
        // 880 is used
        UnexpectedCondition = 881,

        DetouredProcessAccessViolationException = 882,

        // Temp files/directory cleanup
        PipTempDirectoryCleanupWarning = 2201,
        PipTempDirectorySetupWarning = 2203,
        PipTempSymlinkRedirectionError = 2205,
        PipTempSymlinkRedirection = 2206,

        PipFailedToCreateDumpFile = 2210,

        PipOutputNotAccessed = 2603,

        LogInternalDetoursErrorFileNotEmpty = 2919,
        LogGettingInternalDetoursErrorFile = 2920,

        LogMessageCountSemaphoreExists = 2923,
        LogFailedToCreateDirectoryForInternalDetoursFailureFile = 2925,
        LogMismatchedDetoursVerboseCount = 2927,
        LogDetoursMaxHeapSize = 2928,
        // Moved to BuildXL.Native
        // MoreBytesWrittenThanBufferSize = 2930,

        //DominoProcessesStart = 4400,
        PipProcessUncacheableAllowlistNotAllowedInDistributedBuilds = 4401,
        //DominoProcessesEnd = 4499,

        //// Detours
        BrokeredDetoursInjectionFailed = 10100,
        LogDetoursDebugMessage = 10101,
        LogAppleSandboxPolicyGenerated = 10102,
        LogMacKextFailure = 10103,
        LinuxSandboxReportedStaticallyLinkedBinary = 10104,
        PTraceRunnerError = 10105,
        PTraceSandboxLaunchedForPip = 10106,
        ReportArgsMismatch = 10107,
        ReceivedReportFromUnknownPid = 10108,

        FailedToCreateHardlinkOnMerge = 12209,
        DoubleWriteAllowedDueToPolicy = 12210,
        DisallowedDoubleWriteOnMerge = 12211,

        DumpSurvivingPipProcessChildrenStatus = 12213,
        ExistenceAssertionUnderOutputDirectoryFailed = 12214,

        PipProcessStartRemoteExecution = 12502,
        PipProcessFinishedRemoteExecution = 12503,

        SandboxedProcessResultLogOutputTimeout = 12514,

        //// Special tool errors
        PipProcessToolErrorDueToHandleToFileBeingUsed = 14300,
    }
}
