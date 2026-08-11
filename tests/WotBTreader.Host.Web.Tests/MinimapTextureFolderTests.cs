using WotBTreader.Host.Web.Services;

namespace WotBTreader.Host.Web.Tests;

/// <summary>
/// Pins <see cref="MinimapTextureService.MapMinimapFolder"/>'s resolution
/// contract. Known gap (2026-08-11): decoded map IDs are numeric arena
/// identities (Oasis Palms = "11", Dead Rail = "7"), which pass through
/// unchanged and therefore never match the name-based minimap folders in
/// the install — so the HUD's map texture fails closed for real replays
/// until an arena-id → folder mapping exists.
/// </summary>
[TestClass]
public sealed class MinimapTextureFolderTests
{
    [TestMethod]
    public void MapMinimapFolder_StripsNumericPrefixAndTwoLetterSuffix()
    {
        Assert.AreEqual("desert_train", MinimapTextureService.MapMinimapFolder("02_desert_train_dt"));
        Assert.AreEqual("canal", MinimapTextureService.MapMinimapFolder("01_canal_ca"));
    }

    [TestMethod]
    public void MapMinimapFolder_NumericVariantSuffixIsStrippedToo()
    {
        // Second gap pinned: variant folders (desert_train_02) are also
        // unreachable — the numeric "_02" segment is stripped, so the id
        // resolves to the base name instead of the variant folder.
        Assert.AreEqual("desert_train", MinimapTextureService.MapMinimapFolder("01_desert_train_02_dt"));
    }

    [TestMethod]
    public void MapMinimapFolder_NumericArenaIdPassesThroughUnchanged()
    {
        // The gap pinned: a numeric arena identity resolves to itself, so it
        // can never match a name-based folder ("11", "7", ... ∉ folders).
        Assert.AreEqual("11", MinimapTextureService.MapMinimapFolder("11"));
        Assert.AreEqual("7", MinimapTextureService.MapMinimapFolder("7"));
    }
}
