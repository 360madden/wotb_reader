using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Session;

internal enum GameProcessObservationStatus
{
    Unsupported,
    Absent,
    Available,
    Ambiguous,
    QueryFailed,
}

internal sealed record GameWindowCandidate(
    long WindowHandle,
    int ProcessId);

internal sealed record GameWindowEnumerationResult(
    IReadOnlyList<GameWindowCandidate> Candidates,
    bool IsComplete);

internal sealed record ExecutableFileIdentity(
    uint VolumeSerialNumber,
    ulong FileIndex);

internal sealed record ObservedGameProcessIdentity(
    int ProcessId,
    long ProcessStartIdentity,
    long WindowHandle,
    string CanonicalExecutablePath,
    ExecutableFileIdentity FileIdentity,
    string ProductVersion,
    ContentHash ExecutableSha256);

internal sealed record GameProcessObservationResult(
    GameProcessObservationStatus Status,
    ObservedGameProcessIdentity? Identity);

internal interface IGameProcessIdentityObserver
{
    ValueTask<GameProcessObservationResult> ObserveAsync(
        CancellationToken cancellationToken);

    ValueTask<GameProcessObservationResult> ObserveAsync(
        int expectedProcessId,
        CancellationToken cancellationToken) =>
        ObserveAsync(cancellationToken);
}

internal interface IGameProcessQuerySession : IDisposable
{
    int ProcessId { get; }

    long ProcessStartIdentity { get; }

    bool IsAlive { get; }

    string CanonicalExecutablePath { get; }

    ExecutableFileIdentity FileIdentity { get; }

    string ProductVersion { get; }

    ContentHash ExecutableSha256 { get; }
}

internal interface IGameProcessQueryPlatform
{
    bool IsSupported { get; }

    GameWindowEnumerationResult EnumerateEligibleGameWindows(
        int? expectedProcessId = null);

    ValueTask<IGameProcessQuerySession?> OpenQuerySessionAsync(
        GameWindowCandidate candidate,
        uint desiredAccess,
        CancellationToken cancellationToken);

    bool IsWindowStillEligible(GameWindowCandidate candidate);
}

/// <summary>
/// Collects process identity using a short-lived query-only handle. This
/// observer is deliberately disconnected from offline authorization.
/// </summary>
internal sealed class GameProcessIdentityObserver(
    IGameProcessQueryPlatform platform)
    : IGameProcessIdentityObserver
{
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    private readonly IGameProcessQueryPlatform _platform =
        platform ?? throw new ArgumentNullException(nameof(platform));

    public async ValueTask<GameProcessObservationResult> ObserveAsync(
        CancellationToken cancellationToken) =>
        await ObserveCoreAsync(
            expectedProcessId: null,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<GameProcessObservationResult> ObserveAsync(
        int expectedProcessId,
        CancellationToken cancellationToken) =>
        await ObserveCoreAsync(
            expectedProcessId > 0 ? expectedProcessId : null,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<GameProcessObservationResult> ObserveCoreAsync(
        int? expectedProcessId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_platform.IsSupported)
        {
            return Result(GameProcessObservationStatus.Unsupported);
        }

        GameWindowEnumerationResult enumeration =
            _platform.EnumerateEligibleGameWindows(expectedProcessId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!enumeration.IsComplete)
        {
            return Result(GameProcessObservationStatus.Ambiguous);
        }

        IReadOnlyList<GameWindowCandidate> candidates = enumeration.Candidates;
        if (candidates.Count == 0)
        {
            return Result(GameProcessObservationStatus.Absent);
        }

        if (candidates.Count != 1)
        {
            return Result(GameProcessObservationStatus.Ambiguous);
        }

        GameWindowCandidate candidate = candidates[0];
        IGameProcessQuerySession? session = await _platform
            .OpenQuerySessionAsync(
                candidate,
                ProcessQueryLimitedInformation,
                cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return Result(GameProcessObservationStatus.QueryFailed);
        }

        using (session)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!session.IsAlive
                || session.ProcessId != candidate.ProcessId
                || session.ProcessStartIdentity <= 0
                || string.IsNullOrWhiteSpace(session.CanonicalExecutablePath)
                || string.IsNullOrWhiteSpace(session.ProductVersion)
                || !_platform.IsWindowStillEligible(candidate)
                || !session.IsAlive)
            {
                return Result(GameProcessObservationStatus.QueryFailed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new GameProcessObservationResult(
                GameProcessObservationStatus.Available,
                new ObservedGameProcessIdentity(
                    session.ProcessId,
                    session.ProcessStartIdentity,
                    candidate.WindowHandle,
                    session.CanonicalExecutablePath,
                    session.FileIdentity,
                    session.ProductVersion,
                    session.ExecutableSha256));
        }
    }

    private static GameProcessObservationResult Result(
        GameProcessObservationStatus status) =>
        new(status, Identity: null);
}
