using WotBTreader.Application.Replay;
using WotBTreader.Core;
using WotBTreader.Host.Cli.Rendering;

namespace WotBTreader.Host.Cli.Tests.Rendering;

[TestClass]
public sealed class FrameRasterizerTests
{
    private const int Width = 320;
    private const int Height = 240;

    [TestMethod]
    public void Render_BackgroundAndCrosshairAreDrawn()
    {
        byte[] rgba = FrameRasterizer.Render(EmptyProjection(), Width, Height);

        AssertPixel(rgba, 5, 5, 12, 14, 20);            // background
        AssertPixel(rgba, Width / 2, Height / 2, 40, 44, 56); // crosshair
    }

    [TestMethod]
    public void Render_NameplateDrawsPanelBorderHpBarAndLabel()
    {
        var tank = new ProjectedTank(
            EntityId: 7,
            PlayerName: "Alpha",
            TankName: null,
            ClanTag: null,
            TeamNumber: 2,
            HpFraction: 0.5,
            Alive: true,
            DistanceMeters: 100,
            WorldX: 0, WorldZ: 100,
            ScreenX: 160, ScreenY: 120,
            Depth: 100,
            InViewport: true,
            ScreenHeadingDegrees: null,
            DamageDealt: 0, DamageTaken: 0, Kills: 0);

        byte[] rgba = FrameRasterizer.Render(Projection(tanks: [tank]), Width, Height);

        // Panel interior is the dark fill; border pixels are team-2 red.
        AssertPixel(rgba, 160, 120 - 11, 20, 24, 34);          // fill
        AssertPixel(rgba, 160 - 55, 120 - 1, 255, 90, 90);     // left border
        AssertPixel(rgba, 160 - 55 + 2, 120 - 2, 255, 90, 90); // HP bar (50% → 53px)
        // Label "ALPHA" above the panel; the first glyph row's second
        // column is set ('A' row 0 = 0x0E = 0b01110). Label x = 160 - 29/2,
        // top = panel top - 5 (glyph) - 2 (gap).
        AssertPixel(rgba, 160 - 14 + 1, 120 - 22 - 5 - 2, 255, 90, 90);
    }

    [TestMethod]
    public void Render_DeadTankNameplateIsGrey()
    {
        var tank = new ProjectedTank(
            EntityId: 9,
            PlayerName: "Wreck",
            TankName: null,
            ClanTag: null,
            TeamNumber: 1,
            HpFraction: 0.0,
            Alive: false,
            DistanceMeters: 50,
            WorldX: 0, WorldZ: 50,
            ScreenX: 80, ScreenY: 60,
            Depth: 50,
            InViewport: true,
            ScreenHeadingDegrees: null,
            DamageDealt: 0, DamageTaken: 0, Kills: 0);

        byte[] rgba = FrameRasterizer.Render(Projection(tanks: [tank]), Width, Height);

        AssertPixel(rgba, 80 - 55, 60 - 1, 120, 120, 120);
    }

    [TestMethod]
    public void Render_BeaconDrawsMarkerAndLabel()
    {
        var beacon = new ProjectedBeacon(
            Name: "CAP",
            Color: "#00FF00",
            DistanceMeters: 200,
            WorldX: 0, WorldZ: 200,
            ScreenX: 240, ScreenY: 100,
            Depth: 200,
            InViewport: true);

        byte[] rgba = FrameRasterizer.Render(Projection(beacons: [beacon]), Width, Height);

        AssertPixel(rgba, 240, 100, 0, 255, 0);                 // marker center
        AssertPixel(rgba, 240 + 4, 100, 0, 255, 0);             // marker right tip
        // Label "CAP" above the marker ('C' row 0 = 0x0E, second column);
        // top = marker top - 5 (glyph) - 2 (gap).
        AssertPixel(rgba, 240 - 8 + 1, 100 - 4 - 5 - 2, 0, 255, 0);
    }

    [TestMethod]
    public void Render_MinimapInsetDrawsTankBeaconAndCamera()
    {
        // Boundary 0..1000 both axes; panel at top-right (left 128, top 12
        // for a 320px-wide viewport). Tanks/beacons project via normalized
        // world coords: tank at (250,250) → (173,57), beacon at (750,750) →
        // (260,144), camera at (500,500) → (217,101).
        var boundary = new MapBoundary("maps/test", MinX: 0, MaxX: 1000, MinZ: 0, MaxZ: 1000);
        var tank = new ProjectedTank(
            EntityId: 2,
            PlayerName: "Hidden",
            TankName: null,
            ClanTag: null,
            TeamNumber: 2,
            HpFraction: 1.0,
            Alive: true,
            DistanceMeters: 100,
            WorldX: 250, WorldZ: 250,
            ScreenX: null, ScreenY: null,
            Depth: null,
            InViewport: false,
            ScreenHeadingDegrees: null,
            DamageDealt: 0, DamageTaken: 0, Kills: 0);
        var beacon = new ProjectedBeacon(
            Name: "CAP",
            Color: "#00FF88",
            DistanceMeters: 200,
            WorldX: 750, WorldZ: 750,
            ScreenX: null, ScreenY: null,
            Depth: null,
            InViewport: false);
        var projection = new OverlayFrameProjection(
            ReplayTime: TimeSpan.FromSeconds(5),
            CameraX: 500, CameraY: 0, CameraZ: 500,
            CameraYawRadians: 0.5, CameraPitchRadians: -0.1,
            Tanks: [tank],
            Beacons: [beacon],
            Pips: [],
            Kills: []);

        byte[] rgba = FrameRasterizer.Render(projection, Width, Height, boundary);

        AssertPixel(rgba, 173, 57, 255, 90, 90);   // team-2 tank dot
        AssertPixel(rgba, 260, 144, 0, 255, 136);  // beacon dot
        AssertPixel(rgba, 217, 101, 230, 230, 230); // camera crosshair
        AssertPixel(rgba, 100, 20, 12, 14, 20);    // untouched background
    }

    [TestMethod]
    public void Render_DegenerateBoundarySkipsMinimap()
    {
        var boundary = new MapBoundary("maps/test", MinX: 0, MaxX: 0, MinZ: 0, MaxZ: 0);

        byte[] rgba = FrameRasterizer.Render(EmptyProjection(), Width, Height, boundary);

        // The panel area (top-right) stays background — no inset is drawn.
        AssertPixel(rgba, Width - MinimapMidX(), MinimapMidY(), 12, 14, 20);
    }

    private static int MinimapMidX() => Width - 180 - 12 + 90;
    private static int MinimapMidY() => 12 + 90;

    [TestMethod]
    public void Render_OffscreenProjectionIsSkipped()
    {
        var tank = new ProjectedTank(
            EntityId: 1,
            PlayerName: null,
            TankName: "Behind",
            ClanTag: null,
            TeamNumber: null,
            HpFraction: 1.0,
            Alive: true,
            DistanceMeters: 10,
            WorldX: 0, WorldZ: -10,
            ScreenX: null, ScreenY: null,
            Depth: -5,
            InViewport: false,
            ScreenHeadingDegrees: null,
            DamageDealt: 0, DamageTaken: 0, Kills: 0);

        byte[] rgba = FrameRasterizer.Render(Projection(tanks: [tank]), Width, Height);

        AssertPixel(rgba, 5, 5, 12, 14, 20); // untouched background
    }

    private static OverlayFrameProjection Projection(
        IReadOnlyList<ProjectedTank>? tanks = null,
        IReadOnlyList<ProjectedBeacon>? beacons = null) => new(
        ReplayTime: TimeSpan.FromSeconds(5),
        CameraX: 0, CameraY: 0, CameraZ: 0,
        CameraYawRadians: 0.5, CameraPitchRadians: -0.1,
        Tanks: tanks ?? [],
        Beacons: beacons ?? [],
        Pips: [],
        Kills: []);

    private static OverlayFrameProjection EmptyProjection() => Projection();

    private static void AssertPixel(
        byte[] rgba, int x, int y, byte r, byte g, byte b)
    {
        int offset = (y * Width + x) * 4;
        Assert.AreEqual(r, rgba[offset], $"R at ({x},{y})");
        Assert.AreEqual(g, rgba[offset + 1], $"G at ({x},{y})");
        Assert.AreEqual(b, rgba[offset + 2], $"B at ({x},{y})");
    }
}
