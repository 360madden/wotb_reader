using WotBTreader.Application.Results;
using WotBTreader.GameIntegration.Logs;

namespace WotBTreader.GameIntegration.Session;

/// <summary>
/// Builds a ManagedGameLaunchContext from the preparation and suspended child
/// process lease. It verifies the child identity, selects the primary lifecycle
/// source, and creates the context that the coordinator later uses for evidence
/// correlation. This registrar deliberately does not call RecordManagedLaunch —
/// the caller owns that composition step.
/// </summary>
internal interface IManagedLaunchCorrelationRegistrar
{
    OperationResult<ManagedGameLaunchContext> Register(
        ManagedLaunchPreparation preparation,
        SuspendedGameProcessLease suspendedLease);
}

internal sealed class ManagedLaunchCorrelationRegistrar
    : IManagedLaunchCorrelationRegistrar
{
    public OperationResult<ManagedGameLaunchContext> Register(
        ManagedLaunchPreparation preparation,
        SuspendedGameProcessLease suspendedLease)
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

        LifecycleFeedBaseline baseline = preparation.LifecycleBaseline;
        if (baseline.Health != LifecycleFeedHealth.Healthy)
        {
            return Failure("game.launch.correlation_unhealthy_lifecycle");
        }

        if (baseline.Sources is not { Count: > 0 })
        {
            return Failure("game.launch.correlation_no_lifecycle_source");
        }

        LifecycleSourceCursor primarySource = baseline.Sources[0];
        if (primarySource.SourceId.Value is not { Length: > 0 })
        {
            return Failure("game.launch.correlation_invalid_source");
        }

        if (primarySource.Generation <= 0)
        {
            return Failure("game.launch.correlation_invalid_generation");
        }

        if (baseline.Sequence < 0)
        {
            return Failure("game.launch.correlation_invalid_sequence");
        }

        var context = new ManagedGameLaunchContext(
            preparation.LaunchCorrelation,
            preparation.TrustedIdentity.Identity,
            primarySource.SourceId.Value,
            primarySource.Generation,
            baseline.Sequence);

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
