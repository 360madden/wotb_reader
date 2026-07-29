using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Discovery;
using WotBTreader.GameIntegration.Session;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class TrustedGameIdentityProviderTests
{
    [TestMethod]
    public async Task GetAsync_ProjectsFreshPinnedFingerprintAndPreservesDataRoots()
    {
        InstalledGameIdentity discovered = CreateIdentity();
        var discovery = new StubDiscovery(OperationResult.Success(discovered));
        var reader = new StubReader(
            new WindowsExecutableFingerprint(
                @"C:\Canonical\wotblitz.exe",
                new ExecutableFileIdentity(7, 11),
                "2.3.4",
                Hash("B")));
        var provider = new TrustedGameIdentityProvider(discovery, reader);

        OperationResult<TrustedGameExecutableIdentity> result =
            await provider.GetAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(@"C:\Canonical\wotblitz.exe", result.Value!.Identity.ExecutablePath);
        Assert.AreEqual("2.3.4", result.Value.Identity.ProductVersion);
        Assert.AreEqual(Hash("B"), result.Value.Identity.ExecutableSha256);
        Assert.AreEqual(discovered.ResourceRoot, result.Value.Identity.ResourceRoot);
        CollectionAssert.AreEqual(discovered.DlcRoots.ToArray(), result.Value.Identity.DlcRoots.ToArray());
        Assert.AreEqual(new ExecutableFileIdentity(7, 11), result.Value.FileIdentity);
        Assert.AreEqual(discovered.ExecutablePath, reader.ReceivedPath);
    }

    [TestMethod]
    public async Task GetAsync_DoesNotFingerprintWhenDiscoveryFails()
    {
        var discovery = new StubDiscovery(
            OperationResult.Failure<InstalledGameIdentity>(
                new ApplicationError("game.discovery.not_found", "Not found.")));
        var reader = new StubReader(null);
        var provider = new TrustedGameIdentityProvider(discovery, reader);

        OperationResult<TrustedGameExecutableIdentity> result =
            await provider.GetAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("game.discovery.not_found", result.Error!.Code);
        Assert.IsNull(reader.ReceivedPath);
    }

    [TestMethod]
    public async Task GetAsync_FingerprintFailureReturnsNoPartialIdentity()
    {
        var reader = new StubReader(null);
        var provider = new TrustedGameIdentityProvider(
            new StubDiscovery(OperationResult.Success(CreateIdentity())),
            reader);

        OperationResult<TrustedGameExecutableIdentity> result =
            await provider.GetAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("game.identity.fingerprint_failed", result.Error!.Code);
        Assert.IsNull(result.Value);
    }

    [TestMethod]
    public async Task GetAsync_CancellationAfterDiscoveryDoesNotCallReader()
    {
        using var cancellation = new CancellationTokenSource();
        var discovery = new CancelingDiscovery(
            OperationResult.Success(CreateIdentity()),
            cancellation);
        var reader = new StubReader(null);
        var provider = new TrustedGameIdentityProvider(discovery, reader);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await provider.GetAsync(cancellation.Token));

        Assert.IsNull(reader.ReceivedPath);
    }

    private static InstalledGameIdentity CreateIdentity() => new(
        @"C:\Discovered\wotblitz.exe",
        "1.0.0",
        Hash("A"),
        @"C:\Discovered\Data",
        [@"C:\User\packs"]);

    private static ContentHash Hash(string value) => new(value.PadLeft(64, '0'));

    private sealed class StubDiscovery(OperationResult<InstalledGameIdentity> result)
        : IGameInstallationDiscovery
    {
        public ValueTask<OperationResult<InstalledGameIdentity>> DiscoverAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    private sealed class StubReader(WindowsExecutableFingerprint? fingerprint)
        : IWindowsExecutableFingerprintReader
    {
        public string? ReceivedPath { get; private set; }

        public ValueTask<WindowsExecutableFingerprint?> ReadAsync(
            string executablePath,
            CancellationToken cancellationToken)
        {
            ReceivedPath = executablePath;
            return ValueTask.FromResult(fingerprint);
        }
    }

    private sealed class CancelingDiscovery(
        OperationResult<InstalledGameIdentity> result,
        CancellationTokenSource cancellation)
        : IGameInstallationDiscovery
    {
        public ValueTask<OperationResult<InstalledGameIdentity>> DiscoverAsync(
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
