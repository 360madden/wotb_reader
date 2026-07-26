using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Discovery;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class GameInstallationDiscoveryTests
{
    [TestMethod]
    public async Task DiscoverAsync_NoCandidate_ReturnsStableAbsenceError()
    {
        using TemporaryDirectory temporary = new();
        GameInstallationDiscovery discovery = new(
            new GameIntegrationOptions
            {
                GameInstallRoots = [temporary.Path],
                UseDefaultDiscoveryRoots = false,
            },
            new StubIdentityReader("11.18.0.7"),
            NullLogger<GameInstallationDiscovery>.Instance);

        var result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("game.discovery.not_found", result.Error!.Code);
    }

    [TestMethod]
    public async Task DiscoverAsync_ValidCandidate_ReturnsExactIdentityAndDlcPrecedence()
    {
        using TemporaryDirectory temporary = new();
        string firstGameRoot = temporary.CreateDirectory("missing-game");
        string gameRoot = temporary.CreateDirectory("game");
        Directory.CreateDirectory(System.IO.Path.Combine(gameRoot, "Data"));
        await File.WriteAllBytesAsync(
            System.IO.Path.Combine(gameRoot, "wotblitz.exe"),
            "not-a-real-pe-test-seam"u8.ToArray());

        string userRoot = temporary.CreateDirectory("user");
        string packsRoot = Directory.CreateDirectory(
            System.IO.Path.Combine(userRoot, "packs")).FullName;
        ContentHash executableHash = DvplTestData.HashOf(0xab);
        GameInstallationDiscovery discovery = new(
            new GameIntegrationOptions
            {
                GameInstallRoots = [firstGameRoot, gameRoot],
                UserDataRoots = [userRoot],
                UseDefaultDiscoveryRoots = false,
            },
            new StubIdentityReader("11.18.0.7", executableHash),
            NullLogger<GameInstallationDiscovery>.Instance);

        var result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("11.18.0.7", result.Value!.ProductVersion);
        Assert.AreEqual(executableHash, result.Value.ExecutableSha256);
        Assert.AreEqual(System.IO.Path.Combine(gameRoot, "Data"), result.Value.ResourceRoot);
        CollectionAssert.AreEqual(new[] { packsRoot }, result.Value.DlcRoots.ToArray());
    }

    private sealed class StubIdentityReader(
        string productVersion,
        ContentHash? hash = null) : IGameExecutableIdentityReader
    {
        public ValueTask<OperationResult<GameExecutableIdentity>> ReadAsync(
            string executablePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                OperationResult.Success(
                    new GameExecutableIdentity(
                        productVersion,
                        hash ?? DvplTestData.HashOf(0xcd))));
        }
    }
}
