using WotBTreader.Application.Results;

namespace WotBTreader.GameIntegration.Session;

/// <summary>
/// Resumes the suspended child thread after the caller has handed off the
/// executable and artifact leases and registered the correlation context.
/// </summary>
internal interface IThreadResumePlatform
{
    OperationResult<ThreadResumeOutcome> Resume(SafeThreadHandle threadHandle);
}

internal sealed record ThreadResumeOutcome(int PreviousSuspendCount);

internal sealed class WindowsThreadResumePlatform : IThreadResumePlatform
{
    public OperationResult<ThreadResumeOutcome> Resume(SafeThreadHandle threadHandle)
    {
        ArgumentNullException.ThrowIfNull(threadHandle);

        if (threadHandle.IsInvalid || threadHandle.IsClosed)
        {
            return Failure("game.launch.resume_invalid_handle");
        }

        if (!OperatingSystem.IsWindows())
        {
            return Failure("game.launch.resume_platform_unsupported");
        }

        uint previousCount = NativeMethods.ResumeThread(threadHandle);
        if (previousCount == uint.MaxValue)
        {
            return Failure("game.launch.resume_failed");
        }

        return OperationResult.Success(
            new ThreadResumeOutcome(checked((int)previousCount)));
    }

    private static OperationResult<ThreadResumeOutcome> Failure(string code) =>
        OperationResult.Failure<ThreadResumeOutcome>(
            new ApplicationError(
                code,
                "The child thread could not be resumed.",
                Retryable: false));

    public override string ToString() => nameof(WindowsThreadResumePlatform);
}
