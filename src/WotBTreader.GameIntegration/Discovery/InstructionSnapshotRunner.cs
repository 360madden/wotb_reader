using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Session;

namespace WotBTreader.GameIntegration.Discovery;

internal sealed record InstructionSnapshotExecutionRequest(
    int ProcessId,
    long ProcessStartIdentity,
    string CanonicalExecutablePath,
    string ProductVersion,
    ContentHash ExecutableSha256,
    int DurationMilliseconds,
    int MaxHits);

internal sealed record InstructionSnapshotRunnerOutcome(
    bool IsSuccess,
    InstructionSnapshotResult? Result,
    ApplicationError? Error,
    bool CleanupProven);

internal interface IInstructionSnapshotRunner
{
    ValueTask<InstructionSnapshotRunnerOutcome> RunAsync(
        InstructionSnapshotExecutionRequest request,
        CancellationToken cancellationToken);
}

internal sealed class WindowsInstructionSnapshotRunner(GameIntegrationOptions options)
    : IInstructionSnapshotRunner
{
    private const int MaximumResultBytes = 64 * 1024;
    private const int HelperShutdownGraceMilliseconds = 3_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    private readonly GameIntegrationOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<InstructionSnapshotRunnerOutcome> RunAsync(
        InstructionSnapshotExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Failure("discover.instruction_snapshot.unsupported", cleanupProven: true);
        }

        if (!InstructionSnapshotTargetPolicy.TryResolve(
            request.ProductVersion,
            request.ExecutableSha256,
            out InstructionSnapshotTargetPlan? target))
        {
            return Failure("discover.instruction_snapshot.target_unsupported", cleanupProven: true);
        }

        string? helperPath = _options.InstructionSnapshotHelperPath;
        string? expectedHelperSha256 = _options.InstructionSnapshotHelperSha256;
        if (string.IsNullOrWhiteSpace(helperPath)
            || string.IsNullOrWhiteSpace(expectedHelperSha256)
            || !File.Exists(helperPath))
        {
            return Failure("discover.instruction_snapshot.helper_unavailable", cleanupProven: true);
        }

        await using PinnedInstructionSnapshotHelper? helperLease =
            await PinnedInstructionSnapshotHelper
                .AcquireAsync(helperPath, expectedHelperSha256, cancellationToken)
                .ConfigureAwait(false);
        if (helperLease is null)
        {
            return Failure("discover.instruction_snapshot.helper_identity_mismatch", cleanupProven: true);
        }

        string? coordinatorPath = Environment.ProcessPath;
        string coordinatorAssemblyPath = Path.Combine(
            AppContext.BaseDirectory,
            "WotBTreader.Host.Web.dll");
        if (string.IsNullOrWhiteSpace(coordinatorPath)
            || !string.Equals(
                Path.GetFileName(coordinatorPath),
                "WotBTreader.Host.Web.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure("discover.instruction_snapshot.coordinator_untrusted", cleanupProven: true);
        }

        var fingerprintReader = new WindowsExecutableFingerprintReader();
        WindowsExecutableFingerprint? coordinatorFingerprint =
            await fingerprintReader.ReadAsync(coordinatorPath, cancellationToken).ConfigureAwait(false);
        WindowsExecutableFingerprint? coordinatorAssemblyFingerprint =
            await fingerprintReader
                .ReadAsync(coordinatorAssemblyPath, cancellationToken)
                .ConfigureAwait(false);
        if (coordinatorFingerprint is null || coordinatorAssemblyFingerprint is null)
        {
            return Failure("discover.instruction_snapshot.coordinator_untrusted", cleanupProven: true);
        }

        using Process coordinatorProcess = Process.GetCurrentProcess();
        long coordinatorStartIdentity = coordinatorProcess.StartTime
            .ToUniversalTime()
            .ToFileTimeUtc();

        HelperPlan helperPlan = new()
        {
            CoordinatorProcessId = coordinatorProcess.Id,
            CoordinatorProcessStartIdentity = coordinatorStartIdentity,
            CoordinatorCanonicalExecutablePath = coordinatorFingerprint.CanonicalPath,
            CoordinatorExecutableSha256 = coordinatorFingerprint.Sha256.Value,
            CoordinatorManagedAssemblyPath = coordinatorAssemblyFingerprint.CanonicalPath,
            CoordinatorManagedAssemblySha256 = coordinatorAssemblyFingerprint.Sha256.Value,
            ProcessId = request.ProcessId,
            ProcessStartIdentity = request.ProcessStartIdentity,
            CanonicalExecutablePath = request.CanonicalExecutablePath,
            ProductVersion = request.ProductVersion,
            ExecutableSha256 = request.ExecutableSha256.Value,
            ModuleName = target!.ModuleName,
            Rva = target.Rva,
            ExpectedInstructionHex = target.ExpectedInstructionHex,
            CaptureKind = target.CaptureKind,
            EntityIdDisplacement = target.EntityIdDisplacement,
            DurationMilliseconds = request.DurationMilliseconds,
            MaxHits = request.MaxHits,
            MinimumObjectSampleIntervalMilliseconds = target.MinimumObjectSampleIntervalMilliseconds,
        };

        using AnonymousPipeServerStream planPipe = new(
            PipeDirection.Out,
            HandleInheritability.Inheritable);
        using AnonymousPipeServerStream resultPipe = new(
            PipeDirection.In,
            HandleInheritability.Inheritable);
        using AnonymousPipeServerStream cancelPipe = new(
            PipeDirection.Out,
            HandleInheritability.Inheritable);
        using Process helper = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = helperLease.CanonicalPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            },
        };
        helper.StartInfo.ArgumentList.Add("--execute-object-snapshot");
        helper.StartInfo.ArgumentList.Add("-PlanPipe");
        helper.StartInfo.ArgumentList.Add(planPipe.GetClientHandleAsString());
        helper.StartInfo.ArgumentList.Add("-ResultPipe");
        helper.StartInfo.ArgumentList.Add(resultPipe.GetClientHandleAsString());
        helper.StartInfo.ArgumentList.Add("-CancelPipe");
        helper.StartInfo.ArgumentList.Add(cancelPipe.GetClientHandleAsString());

        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                cancelPipe.WriteByte(1);
                cancelPipe.Flush();
            }
            catch
            {
                // Closing the helper or pipe wins the race with cancellation.
            }
        });
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!helper.Start())
            {
                return Failure("discover.instruction_snapshot.helper_start_failed", cleanupProven: true);
            }
        }
        catch
        {
            return Failure("discover.instruction_snapshot.helper_start_failed", cleanupProven: true);
        }

        if (!helperLease.VerifyStartedProcess(helper))
        {
            bool stopped = await TryKillAndWaitAsync(helper).ConfigureAwait(false);
            return Failure("discover.instruction_snapshot.helper_identity_mismatch", stopped);
        }

        planPipe.DisposeLocalCopyOfClientHandle();
        resultPipe.DisposeLocalCopyOfClientHandle();
        cancelPipe.DisposeLocalCopyOfClientHandle();

        try
        {
            await JsonSerializer.SerializeAsync(planPipe, helperPlan, JsonOptions, CancellationToken.None)
                .ConfigureAwait(false);
            await planPipe.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            planPipe.Close();
        }
        catch
        {
            _ = await TryKillAndWaitAsync(helper).ConfigureAwait(false);

            return Failure("discover.instruction_snapshot.helper_pipe_failed", cleanupProven: false);
        }

        Task<byte[]> resultRead = ReadBoundedAsync(resultPipe, MaximumResultBytes);
        Task exitTask = helper.WaitForExitAsync(CancellationToken.None);
        Task timeoutTask = Task.Delay(
            request.DurationMilliseconds + HelperShutdownGraceMilliseconds,
            CancellationToken.None);
        if (await Task.WhenAny(exitTask, timeoutTask).ConfigureAwait(false) != exitTask)
        {
            _ = await TryKillAndWaitAsync(helper).ConfigureAwait(false);

            return Failure("discover.instruction_snapshot.helper_timeout", cleanupProven: false);
        }

        byte[] resultBytes;
        try
        {
            resultBytes = await resultRead.ConfigureAwait(false);
        }
        catch
        {
            return Failure("discover.instruction_snapshot.invalid_result", cleanupProven: false);
        }

        HelperReport? report;
        try
        {
            report = JsonSerializer.Deserialize<HelperReport>(resultBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return Failure("discover.instruction_snapshot.invalid_result", cleanupProven: false);
        }

        if (report is null
            || !string.Equals(report.Schema, "wotbtreader.execute-object-snapshot.v2", StringComparison.Ordinal)
            || !string.Equals(report.Mode, "execute-object-snapshot", StringComparison.Ordinal)
            || report.Hits is null
            || report.Hits.Count > request.MaxHits
            || report.ExitCode != helper.ExitCode
            || !report.CleanupProven
            || !report.Detached
            || !report.CoordinatorIdentityPinned
            || !report.DebuggerExitTerminatesTarget
            || !string.Equals(report.CaptureKind, target.CaptureKind, StringComparison.Ordinal)
            || !string.Equals(report.ObjectRegister, "esi", StringComparison.Ordinal)
            || !string.Equals(report.VectorRegister, "eax", StringComparison.Ordinal)
            || report.EntityIdDisplacement != target.EntityIdDisplacement)
        {
            return Failure(
                report?.CleanupProven == true
                    ? InstructionSnapshotDiagnosticPolicy.Project(report.Diagnostics)
                    : "discover.instruction_snapshot.cleanup_unproven",
                report?.CleanupProven == true && report.Detached);
        }

        if (report.ExitCode != 0
            || report.Target is null
            || !report.Target.InstructionMatched
            || !report.Target.ExecutableImageSectionProven)
        {
            return Failure(
                InstructionSnapshotDiagnosticPolicy.Project(report.Diagnostics),
                cleanupProven: true);
        }

        List<InstructionSnapshotHit> hits = new(report.Hits.Count);
        Dictionary<uint, string> objectKeys = [];
        foreach (HelperHit hit in report.Hits)
        {
            if (hit.Vector is null
                || hit.ObjectAddress == 0
                || hit.ReplayEntityIdReadOk != hit.ReplayEntityId.HasValue)
            {
                return Failure("discover.instruction_snapshot.invalid_result", cleanupProven: true);
            }

            if (!objectKeys.TryGetValue(hit.ObjectAddress, out string? objectKey))
            {
                objectKey = $"object-{objectKeys.Count + 1:D2}";
                objectKeys.Add(hit.ObjectAddress, objectKey);
            }

            hits.Add(new InstructionSnapshotHit(
                hit.Sequence,
                objectKey,
                hit.Utc,
                hit.ReplayEntityIdReadOk,
                hit.ReplayEntityId,
                hit.Vector.ReadOk,
                hit.Vector.Finite,
                hit.Vector.X,
                hit.Vector.Y,
                hit.Vector.Z,
                hit.SameDebugEvent && hit.DebugEventProcessSuspended,
                hit.SingleRead12Bytes,
                ObjectRegisterCaptured: true,
                hit.HardwareAtomicReadProven,
                hit.SameDecodedClockProven,
                hit.ViewpointIdentityProven,
                hit.StableRootProven));
        }

        return new InstructionSnapshotRunnerOutcome(
            IsSuccess: true,
            new InstructionSnapshotResult(
                report.StartedUtc,
                report.FinishedUtc,
                report.Status,
                target.ModuleName,
                target.Rva,
                report.Target.InstructionMatched,
                report.CleanupProven && report.Detached,
                report.Truncated,
                hits),
            Error: null,
            CleanupProven: true);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes)
    {
        using MemoryStream buffer = new();
        byte[] block = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(block, CancellationToken.None).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException("helper_result_too_large");
            }

            buffer.Write(block, 0, read);
        }

        return buffer.ToArray();
    }

    private static InstructionSnapshotRunnerOutcome Failure(string code, bool cleanupProven) =>
        new(
            IsSuccess: false,
            Result: null,
            new ApplicationError(code, "The instruction snapshot capture was not accepted."),
            cleanupProven);

    private static async ValueTask<bool> TryKillAndWaitAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The exact helper may have exited before containment won the race.
        }

        try
        {
            Task exit = process.WaitForExitAsync(CancellationToken.None);
            return await Task.WhenAny(
                    exit,
                    Task.Delay(HelperShutdownGraceMilliseconds, CancellationToken.None))
                .ConfigureAwait(false) == exit;
        }
        catch
        {
            return false;
        }
    }

    private sealed record HelperPlan
    {
        public int CoordinatorProcessId { get; init; }
        public long CoordinatorProcessStartIdentity { get; init; }
        public string CoordinatorCanonicalExecutablePath { get; init; } = string.Empty;
        public string CoordinatorExecutableSha256 { get; init; } = string.Empty;
        public string CoordinatorManagedAssemblyPath { get; init; } = string.Empty;
        public string CoordinatorManagedAssemblySha256 { get; init; } = string.Empty;
        public int ProcessId { get; init; }
        public long ProcessStartIdentity { get; init; }
        public string CanonicalExecutablePath { get; init; } = string.Empty;
        public string ProductVersion { get; init; } = string.Empty;
        public string ExecutableSha256 { get; init; } = string.Empty;
        public string ModuleName { get; init; } = string.Empty;
        public uint Rva { get; init; }
        public string ExpectedInstructionHex { get; init; } = string.Empty;
        public string CaptureKind { get; init; } = string.Empty;
        public int EntityIdDisplacement { get; init; }
        public int DurationMilliseconds { get; init; }
        public int MaxHits { get; init; }
        public int MinimumObjectSampleIntervalMilliseconds { get; init; }
    }

    private sealed record HelperReport
    {
        public string Schema { get; init; } = string.Empty;
        public string Mode { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public int ExitCode { get; init; }
        public DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset FinishedUtc { get; init; }
        public HelperTarget? Target { get; init; }
        public bool CleanupProven { get; init; }
        public bool Detached { get; init; }
        public bool CoordinatorIdentityPinned { get; init; }
        public bool DebuggerExitTerminatesTarget { get; init; }
        public bool Truncated { get; init; }
        public string CaptureKind { get; init; } = string.Empty;
        public string ObjectRegister { get; init; } = string.Empty;
        public string VectorRegister { get; init; } = string.Empty;
        public int EntityIdDisplacement { get; init; }
        public List<HelperHit> Hits { get; init; } = [];
        public List<string> Diagnostics { get; init; } = [];
    }

    private sealed record HelperTarget
    {
        public bool InstructionMatched { get; init; }
        public bool ExecutableImageSectionProven { get; init; }
    }

    private sealed record HelperHit
    {
        public int Sequence { get; init; }
        public uint ObjectAddress { get; init; }
        public DateTimeOffset Utc { get; init; }
        public bool ReplayEntityIdReadOk { get; init; }
        public int? ReplayEntityId { get; init; }
        public HelperVector? Vector { get; init; }
        public bool SameDebugEvent { get; init; }
        public bool DebugEventProcessSuspended { get; init; }
        public bool SingleRead12Bytes { get; init; }
        public bool HardwareAtomicReadProven { get; init; }
        public bool SameDecodedClockProven { get; init; }
        public bool ViewpointIdentityProven { get; init; }
        public bool StableRootProven { get; init; }
    }

    private sealed record HelperVector
    {
        public bool ReadOk { get; init; }
        public bool Finite { get; init; }
        public float? X { get; init; }
        public float? Y { get; init; }
        public float? Z { get; init; }
    }
}

internal sealed class PinnedInstructionSnapshotHelper : IAsyncDisposable
{
    private const int MaximumPathCharacters = 32_768;
    private WindowsTrustedExecutableLaunchLease? _lease;

    private PinnedInstructionSnapshotHelper(WindowsTrustedExecutableLaunchLease lease)
    {
        _lease = lease;
        CanonicalPath = lease.CanonicalExecutablePath;
    }

    internal string CanonicalPath { get; }

    internal static async ValueTask<PinnedInstructionSnapshotHelper?> AcquireAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        WindowsExecutableFingerprint? fingerprint = await new WindowsExecutableFingerprintReader()
            .ReadAsync(path, cancellationToken)
            .ConfigureAwait(false);
        if (fingerprint is null
            || !string.Equals(
                fingerprint.Sha256.Value,
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? directory = Path.GetDirectoryName(fingerprint.CanonicalPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var identity = new TrustedGameExecutableIdentity(
            new InstalledGameIdentity(
                fingerprint.CanonicalPath,
                fingerprint.ProductVersion,
                fingerprint.Sha256,
                directory,
                []),
            fingerprint.FileIdentity);
        OperationResult<WindowsTrustedExecutableLaunchLease> leaseResult =
            await WindowsTrustedExecutableLaunchLease
                .AcquireAsync(identity, cancellationToken)
                .ConfigureAwait(false);
        return leaseResult.IsSuccess
            ? new PinnedInstructionSnapshotHelper(leaseResult.Value!)
            : null;
    }

    internal bool VerifyStartedProcess(Process process)
    {
        WindowsTrustedExecutableLaunchLease? lease = _lease;
        if (lease is null || process.Id <= 0 || process.HasExited)
        {
            return false;
        }

        try
        {
            using SafeProcessHandle processHandle = NativeMethods.OpenProcess(
                NativeMethods.ProcessQueryLimitedInformation,
                bInheritHandle: false,
                checked((uint)process.Id));
            if (processHandle.IsInvalid)
            {
                return false;
            }

            char[] pathBuffer = new char[MaximumPathCharacters];
            uint pathLength = checked((uint)pathBuffer.Length);
            if (!NativeMethods.QueryFullProcessImageNameW(
                    processHandle,
                    dwFlags: 0,
                    pathBuffer,
                    ref pathLength)
                || pathLength == 0)
            {
                return false;
            }

            string observedPath = Path.GetFullPath(
                new string(pathBuffer, 0, checked((int)pathLength)));
            if (!string.Equals(observedPath, CanonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using SafeFileHandle observedFile = File.OpenHandle(
                observedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.SequentialScan);
            return NativeMethods.GetFileInformationByHandle(
                    observedFile,
                    out NativeFileInformation observedInformation)
                && (observedInformation.FileAttributes & FileAttributes.ReparsePoint) == 0
                && observedInformation.VolumeSerialNumber
                    == lease.ExecutableIdentity.FileIdentity.VolumeSerialNumber
                && observedInformation.FileIndex
                    == lease.ExecutableIdentity.FileIdentity.FileIndex;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        WindowsTrustedExecutableLaunchLease? lease = Interlocked.Exchange(ref _lease, null);
        if (lease is not null)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed record InstructionSnapshotTargetPlan(
    string ModuleName,
    uint Rva,
    string ExpectedInstructionHex,
    string CaptureKind,
    int EntityIdDisplacement,
    int MinimumObjectSampleIntervalMilliseconds);

internal static class InstructionSnapshotTargetPolicy
{
    private const string SupportedVersion = "11.19.0.10";
    private const string SupportedExecutableSha256 =
        "1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d";

    internal static bool TryResolve(
        string productVersion,
        ContentHash executableSha256,
        out InstructionSnapshotTargetPlan? plan)
    {
        if (string.Equals(productVersion, SupportedVersion, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                executableSha256.Value,
                SupportedExecutableSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            plan = new InstructionSnapshotTargetPlan(
                "wotblitz.exe",
                0x022FA78D,
                "F30F7E00",
                "type10-entity-position",
                0x1c,
                750);
            return true;
        }

        plan = null;
        return false;
    }
}

internal static class InstructionSnapshotDiagnosticPolicy
{
    private const string GenericFailure = "discover.instruction_snapshot.helper_failed";

    private static readonly HashSet<string> AllowedCodes = new(StringComparer.Ordinal)
    {
        "cancelled_before_validation",
        "plan_bounds_invalid",
        "synthetic_target_invalid",
        "coordinator_identity_invalid",
        "coordinator_parent_mismatch",
        "coordinator_start_mismatch",
        "coordinator_path_unavailable",
        "coordinator_path_mismatch",
        "coordinator_hash_mismatch",
        "coordinator_assembly_hash_mismatch",
        "coordinator_hash_unavailable",
        "production_plan_invalid",
        "cancelled_before_open",
        "open_process_failed",
        "process_start_identity_mismatch",
        "process_path_query_failed",
        "process_path_mismatch",
        "process_version_mismatch",
        "process_hash_mismatch",
        "cancelled_before_attach",
        "debug_attach_failed",
        "initial_debug_event_wait_failed",
        "cancelled_before_arm",
        "initial_debug_event_identity_mismatch",
        "initial_debug_event_continue_failed",
        "initial_debug_event_timeout",
        "module_identity_ambiguous",
        "target_rva_out_of_range",
        "target_fingerprint_invalid",
        "stopped_instruction_mismatch",
        "thread_snapshot_failed",
        "thread_snapshot_empty",
        "cancelled_during_arm",
        "cancelled_before_thread_arm",
        "thread_bound_or_target_invalid",
        "thread_open_failed",
        "thread_context_read_failed",
        "thread_dr0_occupied",
        "cancelled_before_thread_context_write",
        "thread_context_arm_failed",
        "wait_for_debug_event_failed",
        "continue_debug_event_failed",
        "hit_thread_open_failed",
        "hit_context_read_failed",
        "hit_context_update_failed",
        "cleanup_restore_failed",
        "cleanup_event_still_pending",
        "cleanup_debug_break_failed",
        "cleanup_thread_restore_failed",
        "cleanup_continue_failed",
        "restore_thread_open_failed",
        "restore_thread_context_failed",
        "restore_thread_verify_failed",
        "debug_detach_failed",
    };

    internal static string Project(IReadOnlyList<string>? diagnostics)
    {
        if (diagnostics is null)
        {
            return GenericFailure;
        }

        foreach (string diagnostic in diagnostics)
        {
            if (AllowedCodes.Contains(diagnostic))
            {
                return $"{GenericFailure}.{diagnostic}";
            }
        }

        return GenericFailure;
    }
}
