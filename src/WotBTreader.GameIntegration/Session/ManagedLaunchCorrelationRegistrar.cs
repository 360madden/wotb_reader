using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Logs;

namespace WotBTreader.GameIntegration.Session;

/// <summary>
/// Builds a ManagedGameLaunchContext from the preparation and suspended child
/// process lease. It verifies the child identity, captures every active lifecycle
/// source cursor, and creates the context that the coordinator later uses for evidence
/// correlation. This registrar deliberately does not call RecordManagedLaunch —
/// the caller owns that composition step.
/// </summary>
internal interface IManagedLaunchCorrelationRegistrar
{
    OperationResult<ManagedGameLaunchContext> Register(
        ManagedLaunchPreparation preparation,
        SuspendedGameProcessLease suspendedLease,
        BattleSessionId? battleSessionId = null);
}

internal sealed class ManagedLaunchCorrelationRegistrar
    : IManagedLaunchCorrelationRegistrar
{
    public OperationResult<ManagedGameLaunchContext> Register(
        ManagedLaunchPreparation preparation,
        SuspendedGameProcessLease suspendedLease,
        BattleSessionId? battleSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(suspendedLease);
        if (suspendedLease.ProcessId <= 0)
        {
            return Failure("game.launch.correlation_invalid_pid");
        }

        if (suspendedLease.CreationTimeUtcTicks <= 0)
        {
            return Failure("game.launch.correlation_invalid_creation_time");
        }

        if (string.IsNullOrWhiteSpace(suspendedLease.VerifiedExecutablePath)
            || !string.Equals(
                suspendedLease.VerifiedExecutablePath,
                preparation.TrustedIdentity.Identity.ExecutablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure("game.launch.correlation_executable_mismatch");
        }

        if (suspendedLease.HandedOff)
        {
            return Failure("game.launch.correlation_already_handed_off");
        }

        ManagedReplayArtifactLease? artifactLease = suspendedLease.ArtifactLease;
        if (artifactLease is null)
        {
            return Failure("game.launch.correlation_artifact_unavailable");
        }

        LifecycleFeedBaseline baseline = preparation.LifecycleBaseline;
        if (baseline.Health != LifecycleFeedHealth.Healthy)
        {
            return Failure("game.launch.correlation_unhealthy_lifecycle");
        }

        if (baseline.CapturedAtUtc <= DateTimeOffset.MinValue)
        {
            return Failure("game.launch.correlation_invalid_baseline_time");
        }

        ArgumentNullException.ThrowIfNull(baseline.Sources);
        HashSet<string> sourceIds = new(StringComparer.Ordinal);
        foreach (LifecycleSourceCursor source in baseline.Sources)
        {
            if (source.SourceId.Value is not { Length: > 0 })
            {
                return Failure("game.launch.correlation_invalid_source");
            }

            if (source.Generation <= 0 || source.LastByteOffset < 0)
            {
                return Failure("game.launch.correlation_invalid_generation");
            }

            if (!sourceIds.Add(source.SourceId.Value))
            {
                return Failure("game.launch.correlation_duplicate_source");
            }
        }

        if (baseline.Sequence < 0)
        {
            return Failure("game.launch.correlation_invalid_sequence");
        }

        var context = new ManagedGameLaunchContext(
            preparation.LaunchCorrelation,
            preparation.TrustedIdentity.Identity,
            suspendedLease.ProcessId,
            suspendedLease.CreationTimeUtcTicks,
            baseline.Sources,
            baseline.Sequence,
            baseline.CapturedAtUtc,
            artifactLease.SourceArtifactId,
            artifactLease.Sha256,
            battleSessionId);

        return OperationResult.Success(context);
    }

    private static OperationResult<ManagedGameLaunchContext> Failure(string code) =>
        OperationResult.Failure<ManagedGameLaunchContext>(
            new ApplicationError(
                code,
                "The managed launch correlation could not be registered.",
                Retryable: false));

    public override string ToString() => nameof(ManagedLaunchCorrelationRegistrar);
}
