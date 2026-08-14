using SkiaSharp;
using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.GameIntegration;
using WotBTreader.GameIntegration.Discovery;
using WotBTreader.GameIntegration.Dvpl;
using WotBTreader.GameIntegration.Metadata;
using WotBTreader.Host.Web.Services;

namespace WotBTreader.Host.Web.Tests;

/// <summary>
/// Pins <see cref="MinimapTextureService.MapMinimapFolder"/>'s installed
/// scene-path to minimap-folder contract. Numeric arena IDs are resolved by
/// the service through <c>IInstalledGameMetadataProvider</c> before this pure
/// normalizer runs.
/// </summary>
[TestClass]
public sealed class MinimapTextureFolderTests
{
    [TestMethod]
    public void MapMinimapFolder_StripsNumericPrefixAndTwoLetterSuffix()
    {
        Assert.AreEqual("desert_train", MinimapTextureService.MapMinimapFolder("02_desert_train_dt"));
        Assert.AreEqual("canal", MinimapTextureService.MapMinimapFolder("01_canal_ca"));
        Assert.AreEqual(
            "karelia",
            MinimapTextureService.MapMinimapFolder(
                "17_karelia_ka/17_karelia_ka.sc2"));
    }

    [TestMethod]
    public void MapMinimapFolder_NumericVariantSuffixIsPreserved()
    {
        Assert.AreEqual(
            "desert_train_02",
            MinimapTextureService.MapMinimapFolder("01_desert_train_02_dt"));
    }

    [TestMethod]
    public void MapMinimapFolder_NumericArenaIdAndInvalidComponentsFailClosed()
    {
        Assert.IsNull(MinimapTextureService.MapMinimapFolder("11"));
        Assert.IsNull(MinimapTextureService.MapMinimapFolder(".."));
        Assert.IsNull(MinimapTextureService.MapMinimapFolder("map:name"));
    }

    [TestMethod]
    public async Task GetMinimapPngAsync_NumericArenaIdUsesMetadataScenePath()
    {
        string resourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"wotbtreader-minimap-{Guid.NewGuid():N}");
        string minimapFolder = Path.Combine(
            resourceRoot,
            "Gfx", "UI", "BattleScreenHUD", "minimap", "desert_train_02");
        string expectedPath = Path.Combine(
            minimapFolder,
            "MiniMapSmall.packed.webp.dvpl");

        Directory.CreateDirectory(minimapFolder);
        await File.WriteAllBytesAsync(expectedPath, [0]);

        try
        {
            ContentHash executableHash = HashOf('a');
            GameMetadataContext context = new(
                new InstalledGameIdentity(
                    Path.Combine(resourceRoot, "wotblitz.exe"),
                    "11.19.0.10",
                    executableHash,
                    resourceRoot,
                    DlcRoots: []),
                ProviderVersion: "test",
                SourceSetHash: HashOf('b'),
                LoadedAtUtc: DateTimeOffset.UnixEpoch);
            MapMetadata metadata = new(
                MapId: "desert_train",
                DisplayName: "Desert Train",
                WorldMinX: null,
                WorldMaxX: null,
                WorldMinZ: null,
                WorldMaxZ: null,
                GameVersion: "11.19.0.10",
                SourceHash: HashOf('c'),
                SceneResourcePath:
                    "01_desert_train_02_dt/01_desert_train_02_dt.sc2");
            StubMetadataProvider metadataProvider = new(context, metadata);
            StubDvplReader dvplReader = new(CreateWebpBytes());
            using MinimapTextureService service = new(metadataProvider, dvplReader);

            byte[]? png = await service.GetMinimapPngAsync(
                "11",
                CancellationToken.None);

            Assert.IsNotNull(png);
            CollectionAssert.AreEqual(
                new byte[] { 0x89, 0x50, 0x4e, 0x47 },
                png[..4]);
            Assert.AreEqual("11", metadataProvider.RequestedMapId);
            Assert.AreEqual(expectedPath, dvplReader.RequestedPath);
        }
        finally
        {
            Directory.Delete(resourceRoot, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("LocalGame")]
    public async Task GetMinimapPngAsync_WhenExplicitlyOptedIn_ResolvesNumericArenaFromInstall()
    {
        string? gameRoot = Environment.GetEnvironmentVariable("WOTB_TREADER_GAME_ROOT");
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            Assert.Inconclusive(
                "Set WOTB_TREADER_GAME_ROOT to opt in to read-only installed-game validation.");
            return;
        }

        GameIntegrationOptions options = new()
        {
            GameInstallRoots = [gameRoot],
            UseDefaultDiscoveryRoots = false,
        };
        GameInstallationDiscovery discovery = new(
            options,
            NullLogger<GameInstallationDiscovery>.Instance);
        DvplReader dvplReader = new(options);
        InstalledGameMetadataProvider metadataProvider = new(
            discovery,
            dvplReader,
            options,
            NullLogger<InstalledGameMetadataProvider>.Instance);
        using MinimapTextureService service = new(metadataProvider, dvplReader);

        byte[]? png = await service.GetMinimapPngAsync("1", CancellationToken.None);

        Assert.IsNotNull(png);
        CollectionAssert.AreEqual(
            new byte[] { 0x89, 0x50, 0x4e, 0x47 },
            png[..4]);
    }

    private static byte[] CreateWebpBytes()
    {
        using SKBitmap bitmap = new(1, 1);
        bitmap.Erase(SKColors.Red);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Webp, quality: 100);
        return encoded.ToArray();
    }

    private static ContentHash HashOf(char value) => new(new string(value, 64));

    private sealed class StubMetadataProvider(
        GameMetadataContext context,
        MapMetadata metadata) : IInstalledGameMetadataProvider
    {
        public string? RequestedMapId { get; private set; }

        public ValueTask<OperationResult<GameMetadataContext>> ProbeAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Success(context));

        public ValueTask<OperationResult<VehicleMetadata>> ResolveVehicleAsync(
            GameMetadataContext metadataContext,
            int compactDescriptor,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Failure<VehicleMetadata>(
                new ApplicationError("test.not_supported", "not supported")));

        public ValueTask<OperationResult<MapMetadata>> ResolveMapAsync(
            GameMetadataContext metadataContext,
            string mapId,
            CancellationToken cancellationToken)
        {
            RequestedMapId = mapId;
            return ValueTask.FromResult(OperationResult.Success(metadata));
        }
    }

    private sealed class StubDvplReader(byte[] webpBytes) : IDvplReader
    {
        public string? RequestedPath { get; private set; }

        public ValueTask<OperationResult<DvplPayload>> ReadAsync(
            string path,
            CancellationToken cancellationToken)
        {
            RequestedPath = path;
            DvplFooter footer = new(
                webpBytes.Length,
                webpBytes.Length,
                StoredPayloadCrc32: 0,
                DvplCompressionMode.None);
            return ValueTask.FromResult(OperationResult.Success(new DvplPayload(
                webpBytes,
                footer,
                HashOf('d'),
                HashOf('e'))));
        }
    }
}
