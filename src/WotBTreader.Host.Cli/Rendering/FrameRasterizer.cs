using WotBTreader.ApiContracts;
using WotBTreader.Application.Replay;
using WotBTreader.Core;

namespace WotBTreader.Host.Cli.Rendering;

/// <summary>
/// Rasterizes an <see cref="OverlayFrameProjection"/> into an RGBA buffer for
/// the offline PNG preview: a dark viewport with a center crosshair, beacon
/// diamond markers + labels, event pips, and nameplate rects + labels. The
/// schematic mirrors the overlay's draw order (beacons under pips under
/// nameplates) so layout overlaps and occlusion are visible without running
/// the game. Pure pixel math — no anti-aliasing, no external dependencies —
/// deterministic for tests.
/// </summary>
public static class FrameRasterizer
{
    private const int NameplateWidth = 110;
    private const int NameplateHeight = 22;
    private const int BeaconRadius = 4;
    private const int PipSize = 5;
    private const int MinimapSize = 180;
    private const int MinimapMargin = 12;
    private const int MinimapDot = 3;

    private static readonly byte[] Background = [12, 14, 20, 255];
    private static readonly byte[] Crosshair = [40, 44, 56, 255];
    private static readonly byte[] Team1 = [90, 150, 255, 255];
    private static readonly byte[] Team2 = [255, 90, 90, 255];
    private static readonly byte[] Neutral = [200, 200, 200, 255];
    private static readonly byte[] Dead = [120, 120, 120, 255];
    private static readonly byte[] PipColor = [255, 180, 60, 255];
    private static readonly byte[] PanelFill = [20, 24, 34, 255];
    private static readonly byte[] LabelDefault = [230, 230, 230, 255];

    /// <summary>Renders the projection into a width×height RGBA buffer
    /// (row-major, 4 bytes per pixel, origin top-left). When
    /// <paramref name="boundary"/> is a non-degenerate map boundary, a
    /// god-view minimap inset is drawn top-right: team-colored tank dots,
    /// beacon dots, and a camera crosshair normalized within the boundary.
    /// </summary>
    public static byte[] Render(
        OverlayFrameProjection projection,
        int width,
        int height,
        MapBoundary? boundary = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Viewport dimensions must be positive.");
        }

        byte[] rgba = new byte[width * height * 4];
        Fill(rgba, width, height, Background);

        DrawCrosshair(rgba, width, height);

        if (IsUsableBoundary(boundary))
        {
            DrawMinimap(rgba, width, height, projection, boundary!);
        }

        foreach (ProjectedBeacon beacon in projection.Beacons)
        {
            DrawBeacon(rgba, width, height, beacon);
        }

        foreach (ProjectedPip pip in projection.Pips)
        {
            DrawPip(rgba, width, height, pip);
        }

        foreach (ProjectedTank tank in projection.Tanks)
        {
            DrawNameplate(rgba, width, height, tank);
        }

        return rgba;
    }

    /// <summary>Contact-sheet grid columns: as square as possible.</summary>
    public static int ContactSheetColumns(int frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameCount, 1);
        return (int)Math.Ceiling(Math.Sqrt(frameCount));
    }

    /// <summary>Contact-sheet grid rows for the column count.</summary>
    public static int ContactSheetRows(int frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameCount, 1);
        return (int)Math.Ceiling((double)frameCount / ContactSheetColumns(frameCount));
    }

    /// <summary>Contact-sheet pixel width (cells + margins + gutters).</summary>
    public static int ContactSheetWidth(int frameCount, int cellWidth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellWidth, 1);
        int columns = ContactSheetColumns(frameCount);
        return ContactSheetMargin * 2 + columns * cellWidth + ContactSheetGutter * (columns - 1);
    }

    /// <summary>Contact-sheet pixel height (cells + margins + gutters).</summary>
    public static int ContactSheetHeight(int frameCount, int cellHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellHeight, 1);
        int rows = ContactSheetRows(frameCount);
        return ContactSheetMargin * 2 + rows * cellHeight + ContactSheetGutter * (rows - 1);
    }

    /// <summary>Renders evenly spaced frame projections into one contact-sheet
    /// RGBA buffer (dark background, cells tiled left-to-right, top-to-bottom
    /// with a gutter, each cell rendered with the shared boundary).</summary>
    public static byte[] RenderContactSheet(
        IReadOnlyList<OverlayFrameProjection> projections,
        MapBoundary? boundary = null,
        int cellWidth = 640,
        int cellHeight = 360)
    {
        ArgumentNullException.ThrowIfNull(projections);
        if (projections.Count == 0)
        {
            throw new ArgumentException("At least one projection is required.", nameof(projections));
        }

        int columns = ContactSheetColumns(projections.Count);
        int rows = ContactSheetRows(projections.Count);
        int width = ContactSheetWidth(projections.Count, cellWidth);
        int height = ContactSheetHeight(projections.Count, cellHeight);
        byte[] sheet = new byte[width * height * 4];
        Fill(sheet, width, height, Background);

        for (int index = 0; index < projections.Count; index++)
        {
            byte[] cell = Render(projections[index], cellWidth, cellHeight, boundary);
            int column = index % columns;
            int row = index / columns;
            int dstX = ContactSheetMargin + column * (cellWidth + ContactSheetGutter);
            int dstY = ContactSheetMargin + row * (cellHeight + ContactSheetGutter);
            for (int y = 0; y < cellHeight; y++)
            {
                Array.Copy(
                    cell, y * cellWidth * 4,
                    sheet, ((dstY + y) * width + dstX) * 4,
                    cellWidth * 4);
            }

            // Replay-time label in the cell's top-left corner (the actual
            // frame time, not the requested sample time).
            DrawText(sheet, width, height, dstX + 2, dstY + 2,
                $"{projections[index].ReplayTime.TotalSeconds:0}s", LabelDefault);
        }

        return sheet;
    }

    private const int ContactSheetMargin = 16;
    private const int ContactSheetGutter = 8;

    private static bool IsUsableBoundary(MapBoundary? boundary) =>
        boundary is not null
        && boundary.MaxX > boundary.MinX
        && boundary.MaxZ > boundary.MinZ;

    /// <summary>God-view minimap inset: tanks (team color, grey when dead),
    /// beacons (their color), and the camera (white crosshair). World X maps
    /// to the panel's left→right, world Z to top→bottom.</summary>
    private static void DrawMinimap(
        byte[] rgba, int width, int height, OverlayFrameProjection projection, MapBoundary boundary)
    {
        int left = width - MinimapSize - MinimapMargin;
        int top = MinimapMargin;
        for (int dy = 0; dy < MinimapSize; dy++)
        {
            for (int dx = 0; dx < MinimapSize; dx++)
            {
                bool border = dx == 0 || dy == 0 || dx == MinimapSize - 1 || dy == MinimapSize - 1;
                SetPixel(rgba, width, height, left + dx, top + dy,
                    border ? Crosshair : PanelFill);
            }
        }

        foreach (ProjectedTank tank in projection.Tanks)
        {
            byte[] color = !tank.Alive
                ? Dead
                : tank.TeamNumber == 1 ? Team1 : tank.TeamNumber == 2 ? Team2 : Neutral;
            DrawDot(rgba, width, height, left, top, boundary, tank.WorldX, tank.WorldZ, color);
        }

        foreach (ProjectedBeacon beacon in projection.Beacons)
        {
            DrawDot(rgba, width, height, left, top, boundary, beacon.WorldX, beacon.WorldZ,
                ParseColor(beacon.Color, LabelDefault));
        }

        if (projection.CameraX is double cameraX && projection.CameraZ is double cameraZ)
        {
            (int cx, int cy) = Normalize(boundary, left, top, cameraX, cameraZ);
            for (int dx = -2; dx <= 2; dx++)
            {
                SetPixel(rgba, width, height, cx + dx, cy, LabelDefault);
            }

            for (int dy = -2; dy <= 2; dy++)
            {
                SetPixel(rgba, width, height, cx, cy + dy, LabelDefault);
            }
        }
    }

    private static void DrawDot(
        byte[] rgba, int width, int height, int panelLeft, int panelTop,
        MapBoundary boundary, double worldX, double worldZ, byte[] color)
    {
        (int x, int y) = Normalize(boundary, panelLeft, panelTop, worldX, worldZ);
        for (int dy = 0; dy < MinimapDot; dy++)
        {
            for (int dx = 0; dx < MinimapDot; dx++)
            {
                SetPixel(rgba, width, height, x + dx, y + dy, color);
            }
        }
    }

    private static (int X, int Y) Normalize(
        MapBoundary boundary, int panelLeft, int panelTop, double worldX, double worldZ)
    {
        // Shared world→minimap contract (same as the HUD's MinimapMath):
        // u from west→east, v from north (min-z)→south; clamped + 1px inset
        // because the schematic panel is fixed-size.
        (double U, double V) normalized = MinimapNormalizer.Normalize(
            worldX, worldZ, boundary.MinX, boundary.MaxX, boundary.MinZ, boundary.MaxZ)!.Value;
        int x = panelLeft + 1 + (int)Math.Round(Math.Clamp(normalized.U, 0, 1) * (MinimapSize - 2 - MinimapDot));
        int y = panelTop + 1 + (int)Math.Round(Math.Clamp(normalized.V, 0, 1) * (MinimapSize - 2 - MinimapDot));
        return (x, y);
    }

    private static void DrawCrosshair(byte[] rgba, int width, int height)
    {
        int cx = width / 2;
        int cy = height / 2;
        for (int x = cx - 8; x <= cx + 8; x++)
        {
            SetPixel(rgba, width, height, x, cy, Crosshair);
        }

        for (int y = cy - 8; y <= cy + 8; y++)
        {
            SetPixel(rgba, width, height, cx, y, Crosshair);
        }
    }

    private static void DrawBeacon(byte[] rgba, int width, int height, ProjectedBeacon beacon)
    {
        if (beacon.ScreenX is not double x || beacon.ScreenY is not double y)
        {
            return;
        }

        int xi = (int)Math.Round(x);
        int yi = (int)Math.Round(y);
        byte[] color = ParseColor(beacon.Color, LabelDefault);

        // Diamond marker, then the label above it.
        for (int dy = -BeaconRadius; dy <= BeaconRadius; dy++)
        {
            int halfWidth = BeaconRadius - Math.Abs(dy);
            for (int dx = -halfWidth; dx <= halfWidth; dx++)
            {
                SetPixel(rgba, width, height, xi + dx, yi + dy, color);
            }
        }

        DrawText(rgba, width, height, xi - (beacon.Name.Length * (BitmapFont5x7.GlyphWidth + 1) - 1) / 2,
            yi - BeaconRadius - BitmapFont5x7.GlyphHeight - 2, beacon.Name, color);
    }

    private static void DrawPip(byte[] rgba, int width, int height, ProjectedPip pip)
    {
        int xi = (int)Math.Round(pip.ScreenX);
        int yi = (int)Math.Round(pip.ScreenY);
        for (int dy = 0; dy < PipSize; dy++)
        {
            for (int dx = 0; dx < PipSize; dx++)
            {
                SetPixel(rgba, width, height, xi - PipSize / 2 + dx, yi - PipSize / 2 + dy, PipColor);
            }
        }
    }

    private static void DrawNameplate(byte[] rgba, int width, int height, ProjectedTank tank)
    {
        if (tank.ScreenX is not double x || tank.ScreenY is not double y)
        {
            return;
        }

        int xi = (int)Math.Round(x);
        int yi = (int)Math.Round(y);
        int left = xi - NameplateWidth / 2;
        int top = yi - NameplateHeight;
        byte[] color = !tank.Alive
            ? Dead
            : tank.TeamNumber == 1 ? Team1 : tank.TeamNumber == 2 ? Team2 : Neutral;

        // Panel with a 1px colored border.
        for (int dy = 0; dy < NameplateHeight; dy++)
        {
            for (int dx = 0; dx < NameplateWidth; dx++)
            {
                bool border = dx == 0 || dy == 0 || dx == NameplateWidth - 1 || dy == NameplateHeight - 1;
                SetPixel(rgba, width, height, left + dx, top + dy,
                    border ? color : PanelFill);
            }
        }

        // HP bar along the bottom edge, inset 2px.
        int barWidth = (int)Math.Round((NameplateWidth - 4) * Math.Clamp(tank.HpFraction, 0.0, 1.0));
        for (int dx = 0; dx < barWidth; dx++)
        {
            SetPixel(rgba, width, height, left + 2 + dx, top + NameplateHeight - 2, color);
        }

        string label = string.IsNullOrWhiteSpace(tank.PlayerName)
            ? tank.TankName ?? $"TANK {tank.EntityId}"
            : tank.PlayerName;
        DrawText(rgba, width, height,
            xi - (label.Length * (BitmapFont5x7.GlyphWidth + 1) - 1) / 2,
            top - BitmapFont5x7.GlyphHeight - 2, label, color);
    }

    /// <summary>Draws a line of text (uppercase via the 5x7 font, 1px advance)
    /// with its left-top at (x, y). Unknown characters render as blank.</summary>
    private static void DrawText(
        byte[] rgba, int width, int height, int x, int y, string text, byte[] color)
    {
        int cursor = x;
        foreach (char character in text)
        {
            for (int row = 0; row < BitmapFont5x7.GlyphHeight; row++)
            {
                for (int column = 0; column < BitmapFont5x7.GlyphWidth; column++)
                {
                    if (BitmapFont5x7.HasPixel(character, column, row))
                    {
                        SetPixel(rgba, width, height, cursor + column, y + row, color);
                    }
                }
            }

            cursor += BitmapFont5x7.GlyphWidth + 1;
        }
    }

    private static byte[] ParseColor(string? hex, byte[] fallback)
    {
        if (hex is null || hex.Length < 7 || hex[0] != '#')
        {
            return fallback;
        }

        try
        {
            return
            [
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16),
                255,
            ];
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static void Fill(byte[] rgba, int width, int height, byte[] color)
    {
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = color[0];
            rgba[i + 1] = color[1];
            rgba[i + 2] = color[2];
            rgba[i + 3] = color[3];
        }
    }

    private static void SetPixel(byte[] rgba, int width, int height, int x, int y, byte[] color)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        int offset = (y * width + x) * 4;
        rgba[offset] = color[0];
        rgba[offset + 1] = color[1];
        rgba[offset + 2] = color[2];
        rgba[offset + 3] = color[3];
    }
}
