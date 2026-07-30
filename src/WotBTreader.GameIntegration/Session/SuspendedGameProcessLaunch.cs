using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WotBTreader.Application.Results;

namespace WotBTreader.GameIntegration.Session;

/// <summary>
/// Platform abstraction for suspended game process creation and verification.
/// </summary>
internal interface ISuspendedProcessPlatform
{
    ValueTask<OperationResult<SuspendedGameProcessLease>> CreateAsync(
        WindowsTrustedExecutableLaunchLease executableLease,
        ManagedReplayArtifactLease artifactLease,
        CancellationToken cancellationToken);
}

/// <summary>
/// Lease representing a suspended game process with verified identity.
/// Owns the child process/thread handles and the input leases.
/// </summary>
internal sealed class SuspendedGameProcessLease : IAsyncDisposable
{
    private SafeProcessHandle? _processHandle;
    private SafeThreadHandle? _threadHandle;
    private WindowsTrustedExecutableLaunchLease? _executableLease;
    private ManagedReplayArtifactLease? _artifactLease;
    private bool _disposed;
    private bool _handedOff;

    internal SuspendedGameProcessLease(
        int processId,
        long creationTimeUtcTicks,
        string verifiedExecutablePath,
        SafeProcessHandle processHandle,
        SafeThreadHandle threadHandle,
        WindowsTrustedExecutableLaunchLease executableLease,
        ManagedReplayArtifactLease artifactLease)
    {
        ProcessId = processId;
        CreationTimeUtcTicks = creationTimeUtcTicks;
        VerifiedExecutablePath = verifiedExecutablePath;
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        _executableLease = executableLease;
        _artifactLease = artifactLease;
    }

    internal int ProcessId { get; }
    internal long CreationTimeUtcTicks { get; }
    internal string VerifiedExecutablePath { get; }

    internal WindowsTrustedExecutableLaunchLease? ExecutableLease => _executableLease;
    internal ManagedReplayArtifactLease? ArtifactLease => _artifactLease;

    internal SafeProcessHandle? ProcessHandle => _processHandle;
    internal SafeThreadHandle? ThreadHandle => _threadHandle;

    internal bool HandedOff => _handedOff;

    /// <summary>
    /// Transfers ownership of the leases to the caller (for the resume unit).
    /// After this call, disposal will not terminate the child process.
    /// </summary>
    internal (WindowsTrustedExecutableLaunchLease Executable, ManagedReplayArtifactLease Artifact) HandOffLeases()
    {
        if (_handedOff)
        {
            throw new InvalidOperationException("Leases already handed off");
        }
        _handedOff = true;
        var exe = _executableLease!;
        var artifact = _artifactLease!;
        _executableLease = null;
        _artifactLease = null;
        return (exe, artifact);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (!_handedOff)
        {
            TerminateChildProcess();
        }

        _processHandle?.Dispose();
        _processHandle = null;
        _threadHandle?.Dispose();
        _threadHandle = null;

        if (_executableLease is not null)
        {
            await _executableLease.DisposeAsync().ConfigureAwait(false);
            _executableLease = null;
        }
        if (_artifactLease is not null)
        {
            await _artifactLease.DisposeAsync().ConfigureAwait(false);
            _artifactLease = null;
        }
    }

    private void TerminateChildProcess()
    {
        if (_processHandle is not null && !_processHandle.IsInvalid)
        {
            try
            {
                NativeMethods.TerminateProcess(_processHandle, 1);
                _ = NativeMethods.WaitForSingleObject(_processHandle, 5000);
            }
            catch
            {
                // Best effort - the handle will be closed regardless
            }
        }
    }

    public override string ToString() => nameof(SuspendedGameProcessLease);

    internal static OperationResult<SuspendedGameProcessLease> Failure(
        string? code = null,
        string? message = null) =>
        OperationResult.Failure<SuspendedGameProcessLease>(
            new ApplicationError(
                code ?? "game.launch.process_creation_failed",
                message ?? "Failed to create or verify the suspended game process",
                Retryable: true));
}

/// <summary>
/// Windows implementation of suspended process creation using CreateProcessW.
/// </summary>
internal sealed class WindowsSuspendedProcessPlatform : ISuspendedProcessPlatform
{
    private const int CreateSuspended = 0x0000_0004;
    private const int CreateUnicodeEnvironment = 0x0000_0400;
    private const int StartfUseShowWindow = 0x0000_0001;
    private const int SwHide = 0;

    public async ValueTask<OperationResult<SuspendedGameProcessLease>> CreateAsync(
        WindowsTrustedExecutableLaunchLease executableLease,
        ManagedReplayArtifactLease artifactLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executableLease);
        ArgumentNullException.ThrowIfNull(artifactLease);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return SuspendedGameProcessLease.Failure(
                "game.launch.platform_unsupported",
                "Suspended process creation is only supported on Windows.");
        }

        SafeProcessHandle? processHandle = null;
        SafeThreadHandle? threadHandle = null;
        SafeProcessHandle? reducedProcessHandle = null;
        SafeThreadHandle? reducedThreadHandle = null;

        try
        {
            string executablePath = executableLease.CanonicalExecutablePath;
            string replayPath = artifactLease.StagingPath;
            string? workingDirectory = Path.GetDirectoryName(executablePath);

            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                return SuspendedGameProcessLease.Failure(
                    "game.launch.invalid_working_directory",
                    "Could not determine working directory from executable path.");
            }

            // Command line: "executable" "replayPath"
            string commandLine = $"\"{executablePath}\" \"{replayPath}\"";

            var startupInfo = new StartupInfoEx
            {
                cb = Marshal.SizeOf<StartupInfoEx>(),
                dwFlags = StartfUseShowWindow,
                wShowWindow = SwHide
            };

            var processInfo = new ProcessInformation();

            bool created = NativeMethods.CreateProcessW(
                executablePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateSuspended | CreateUnicodeEnvironment,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out processInfo);

            if (!created)
            {
                int error = Marshal.GetLastWin32Error();
                return SuspendedGameProcessLease.Failure(
                    "game.launch.create_process_failed",
                    $"CreateProcessW failed with error {error}.");
            }

            processHandle = new SafeProcessHandle(processInfo.hProcess, ownsHandle: true);
            threadHandle = new SafeThreadHandle(processInfo.hThread, ownsHandle: true);

            // Reduce handle rights immediately - drop from all-access to least privilege
            if (!NativeMethods.DuplicateHandle(
                    NativeMethods.GetCurrentProcess(),
                    processHandle,
                    NativeMethods.GetCurrentProcess(),
                    out reducedProcessHandle,
                    NativeMethods.ProcessQueryLimitedInformation | NativeMethods.ProcessTerminate | NativeMethods.Synchronize,
                    false,
                    0))
            {
                return SuspendedGameProcessLease.Failure(
                    "game.launch.handle_reduction_failed",
                    "Failed to reduce process handle rights.");
            }
            processHandle.Dispose();
            processHandle = null;

            if (!NativeMethods.DuplicateHandle(
                    NativeMethods.GetCurrentProcess(),
                    threadHandle,
                    NativeMethods.GetCurrentProcess(),
                    out reducedThreadHandle,
                    NativeMethods.ThreadSuspendResume | NativeMethods.ThreadQueryLimitedInformation,
                    false,
                    0))
            {
                return SuspendedGameProcessLease.Failure(
                    "game.launch.handle_reduction_failed",
                    "Failed to reduce thread handle rights.");
            }
            threadHandle.Dispose();
            threadHandle = null;

            // Verify child identity before any resume
            cancellationToken.ThrowIfCancellationRequested();

            int childPid = NativeMethods.GetProcessId(reducedProcessHandle!);
            if (childPid == 0)
            {
                return SuspendedGameProcessLease.Failure(
                    "game.launch.child_pid_failed",
                    "Failed to retrieve child process ID.");
            }

            if (!NativeMethods.GetProcessTimes(
                    reducedProcessHandle,
                    out NativeFileTime creationFileTime,
                    out _,
                    out _,
                    out _))
            {
                return SuspendedGameProcessLease.Failure(
                    "game.launch.child_creation_time_failed",
                    "Failed to retrieve child process creation time.");
            }

            long creationTime = creationFileTime.ToInt64();

            // Query child executable path and verify against lease
            uint pathCapacity = 32_768;
            char[] pathBuffer = new char[pathCapacity];
            uint pathLength = pathCapacity;
            if (!NativeMethods.QueryFullProcessImageNameW(
                    reducedProcessHandle,
                    0,
                    pathBuffer,
                    ref pathLength)
                || pathLength >= pathBuffer.Length)
            {
                return SuspendedGameProcessLease.Failure(
                    "game.launch.child_exe_query_failed",
                    "Failed to query child executable path.");
            }

            string childExePath = new(pathBuffer, 0, checked((int)pathLength));
            if (!string.Equals(
                    childExePath,
                    executableLease.CanonicalExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                NativeMethods.TerminateProcess(reducedProcessHandle, 1);
                _ = NativeMethods.WaitForSingleObject(reducedProcessHandle, 5000);
                return SuspendedGameProcessLease.Failure(
                    "game.launch.child_exe_mismatch",
                    "Child executable path does not match trusted executable.");
            }

            // Revalidate executable file identity via the lease's pinned handle
            if (!NativeMethods.GetFileInformationByHandle(
                    executableLease.ExecutableHandle,
                    out NativeFileInformation revalidatedInfo)
                || (revalidatedInfo.FileAttributes & FileAttributes.ReparsePoint) != 0
                || revalidatedInfo.VolumeSerialNumber != executableLease.ExecutableIdentity.FileIdentity.VolumeSerialNumber
                || revalidatedInfo.FileIndex != executableLease.ExecutableIdentity.FileIdentity.FileIndex)
            {
                NativeMethods.TerminateProcess(reducedProcessHandle, 1);
                _ = NativeMethods.WaitForSingleObject(reducedProcessHandle, 5000);
                return SuspendedGameProcessLease.Failure(
                    "game.launch.child_identity_mismatch",
                    "Child executable file identity does not match trusted identity.");
            }

            var lease = new SuspendedGameProcessLease(
                childPid,
                creationTime,
                childExePath,
                reducedProcessHandle!,
                reducedThreadHandle!,
                executableLease,
                artifactLease);

            reducedProcessHandle = null;
            reducedThreadHandle = null;

            return OperationResult.Success(lease);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or DirectoryNotFoundException
                or UnauthorizedAccessException
                or IOException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or System.Security.Cryptography.CryptographicException
                or OverflowException)
        {
            return SuspendedGameProcessLease.Failure();
        }
        finally
        {
            reducedProcessHandle?.Dispose();
            reducedThreadHandle?.Dispose();
            processHandle?.Dispose();
            threadHandle?.Dispose();
        }
    }
}
