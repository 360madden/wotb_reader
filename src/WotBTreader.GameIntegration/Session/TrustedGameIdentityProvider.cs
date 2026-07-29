using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.GameIntegration.Discovery;

namespace WotBTreader.GameIntegration.Session;

/// <summary>
/// A freshly re-fingerprinted installation identity. The retained file identifier
/// is for later local correlation only and is not exposed outside GameIntegration.
/// </summary>
internal sealed record TrustedGameExecutableIdentity(
    InstalledGameIdentity Identity,
    ExecutableFileIdentity FileIdentity);

internal interface ITrustedGameIdentityProvider
{
    ValueTask<OperationResult<TrustedGameExecutableIdentity>> GetAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Re-fingerprints discovery output through a replacement-resistant file handle.
/// This component deliberately grants no session authorization.
/// </summary>
internal sealed class TrustedGameIdentityProvider(
    IGameInstallationDiscovery discovery,
    IWindowsExecutableFingerprintReader fingerprintReader)
    : ITrustedGameIdentityProvider
{
    private readonly IGameInstallationDiscovery _discovery =
        discovery ?? throw new ArgumentNullException(nameof(discovery));
    private readonly IWindowsExecutableFingerprintReader _fingerprintReader =
        fingerprintReader ?? throw new ArgumentNullException(nameof(fingerprintReader));

    public async ValueTask<OperationResult<TrustedGameExecutableIdentity>> GetAsync(
        CancellationToken cancellationToken)
    {
        OperationResult<InstalledGameIdentity> discovered = await _discovery
            .DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!discovered.IsSuccess)
        {
            return OperationResult.Failure<TrustedGameExecutableIdentity>(
                discovered.Error!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        InstalledGameIdentity installation = discovered.Value!;
        WindowsExecutableFingerprint? fingerprint = await _fingerprintReader
            .ReadAsync(installation.ExecutablePath, cancellationToken)
            .ConfigureAwait(false);
        if (fingerprint is null)
        {
            return OperationResult.Failure<TrustedGameExecutableIdentity>(
                new ApplicationError(
                    "game.identity.fingerprint_failed",
                    "The discovered game executable could not be re-fingerprinted consistently.",
                    Retryable: true));
        }

        return OperationResult.Success(
            new TrustedGameExecutableIdentity(
                new InstalledGameIdentity(
                    fingerprint.CanonicalPath,
                    fingerprint.ProductVersion,
                    fingerprint.Sha256,
                    installation.ResourceRoot,
                    installation.DlcRoots),
                fingerprint.FileIdentity));
    }
}
