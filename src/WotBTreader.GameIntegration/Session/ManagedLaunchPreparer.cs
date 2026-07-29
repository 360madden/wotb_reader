using System.Security.Cryptography;
using WotBTreader.Application.Results;
using WotBTreader.GameIntegration.Logs;

namespace WotBTreader.GameIntegration.Session;

/// <summary>Internal launch inputs captured as one fresh preparation operation.</summary>
internal sealed class ManagedLaunchPreparation
{
    public ManagedLaunchPreparation(
        TrustedGameExecutableIdentity trustedIdentity,
        string launchCorrelation,
        LifecycleFeedBaseline lifecycleBaseline)
    {
        TrustedIdentity = trustedIdentity ?? throw new ArgumentNullException(nameof(trustedIdentity));
        if (!LaunchCorrelationGenerator.IsValid(launchCorrelation))
        {
            throw new ArgumentException(
                "A launch correlation must be a 32-byte unpadded base64url value.",
                nameof(launchCorrelation));
        }

        LaunchCorrelation = launchCorrelation;
        LifecycleBaseline = lifecycleBaseline ?? throw new ArgumentNullException(nameof(lifecycleBaseline));
    }

    public TrustedGameExecutableIdentity TrustedIdentity { get; }

    public string LaunchCorrelation { get; }

    public LifecycleFeedBaseline LifecycleBaseline { get; }

    public override string ToString() => nameof(ManagedLaunchPreparation);
}

internal interface IManagedLaunchPreparer
{
    ValueTask<OperationResult<ManagedLaunchPreparation>> PrepareAsync(
        CancellationToken cancellationToken);
}

internal interface ILaunchCorrelationGenerator
{
    OperationResult<string> Generate();
}

/// <summary>Creates opaque, adapter-owned correlations without caller input.</summary>
internal sealed class LaunchCorrelationGenerator : ILaunchCorrelationGenerator
{
    private const int CorrelationByteLength = 32;
    private const int EncodedCorrelationLength = 43;

    public OperationResult<string> Generate()
    {
        try
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(CorrelationByteLength);
            return OperationResult.Success(
                Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        }
        catch (CryptographicException)
        {
            return Failure();
        }
    }

    internal static bool IsValid(string? value) =>
        value is { Length: EncodedCorrelationLength }
        && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static OperationResult<string> Failure() =>
        OperationResult.Failure<string>(
            new ApplicationError(
                "game.launch.correlation_failed",
                "A launch correlation could not be generated.",
                Retryable: true));
}

/// <summary>
/// Captures the immutable, adapter-owned inputs required before a managed launch.
/// It intentionally does not select a lifecycle source or record a launch.
/// </summary>
internal sealed class ManagedLaunchPreparer(
    ITrustedGameIdentityProvider trustedIdentityProvider,
    IBlitzReplayLifecycleFeed lifecycleFeed,
    ILaunchCorrelationGenerator correlationGenerator)
    : IManagedLaunchPreparer
{
    private readonly ITrustedGameIdentityProvider _trustedIdentityProvider =
        trustedIdentityProvider ?? throw new ArgumentNullException(nameof(trustedIdentityProvider));
    private readonly IBlitzReplayLifecycleFeed _lifecycleFeed =
        lifecycleFeed ?? throw new ArgumentNullException(nameof(lifecycleFeed));
    private readonly ILaunchCorrelationGenerator _correlationGenerator =
        correlationGenerator ?? throw new ArgumentNullException(nameof(correlationGenerator));

    public async ValueTask<OperationResult<ManagedLaunchPreparation>> PrepareAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        OperationResult<TrustedGameExecutableIdentity> identityResult = await _trustedIdentityProvider
            .GetAsync(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!identityResult.IsSuccess)
        {
            return OperationResult.Failure<ManagedLaunchPreparation>(identityResult.Error!);
        }

        OperationResult<string> correlationResult;
        try
        {
            correlationResult = _correlationGenerator.Generate();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CorrelationFailure();
        }

        if (!correlationResult.IsSuccess
            || !LaunchCorrelationGenerator.IsValid(correlationResult.Value))
        {
            return CorrelationFailure();
        }

        cancellationToken.ThrowIfCancellationRequested();
        LifecycleFeedBaseline baseline = await _lifecycleFeed
            .CaptureReconciledBaselineAsync(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (baseline.Health != LifecycleFeedHealth.Healthy)
        {
            return OperationResult.Failure<ManagedLaunchPreparation>(
                new ApplicationError(
                    "game.launch.lifecycle_unhealthy",
                    "The replay lifecycle feed is not healthy.",
                    Retryable: true));
        }

        return OperationResult.Success(
            new ManagedLaunchPreparation(
                identityResult.Value!,
                correlationResult.Value!,
                baseline));
    }

    private static OperationResult<ManagedLaunchPreparation> CorrelationFailure() =>
        OperationResult.Failure<ManagedLaunchPreparation>(
            new ApplicationError(
                "game.launch.correlation_failed",
                "A launch correlation could not be generated.",
                Retryable: true));
}
