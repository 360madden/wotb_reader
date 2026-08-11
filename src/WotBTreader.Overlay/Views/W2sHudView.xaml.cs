using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay.Views;

/// <summary>
/// Renders world-to-screen nameplates over the game window. Each item is
/// anchored at the tank's projected viewport pixel (top-left origin, same
/// space as the overlay window, which is resized to exactly match the game
/// client rect) with the label and HP bar drawn just above the point.
/// Team coloring follows the game's ally/enemy convention (blue/red); the
/// HP bar is green→red by fraction, greyed when the tank is destroyed.
/// </summary>
public sealed partial class W2sHudView : UserControl
{
    private const double NameplateWidth = 64;
    private const double NameplateHeight = 20;
    private const double HpBarWidth = 56;
    private const double HpBarHeight = 4;
    private const double LabelFontSize = 11;
    private const double AnchorGap = 6;
    private const double BeaconDotRadius = 5;
    private const double BeaconLabelFontSize = 10;
    private const double HeadingArrowLength = 10;
    private const double HeadingArrowHalfWidth = 5;

    private static readonly Brush Team1Brush = CreateBrush("#33A2FF");
    private static readonly Brush Team2Brush = CreateBrush("#FF5A5A");
    private static readonly Brush NeutralBrush = CreateBrush("#CCCCCC");
    private static readonly Brush DeadBrush = CreateBrush("#666666");
    private static readonly Brush HpGoodBrush = CreateBrush("#00E066");
    private static readonly Brush HpMidBrush = CreateBrush("#E0B000");
    private static readonly Brush HpLowBrush = CreateBrush("#E03030");
    private static readonly Brush LabelBackground = CreateBrush("#B0101018");

    public W2sHudView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Computes the screen-space anchor rectangle for one nameplate: the
    /// point the tank projects to, with the plate centred horizontally and
    /// lifted <see cref="AnchorGap"/> above it, clamped so the plate stays
    /// fully inside the viewport. Pure — unit-tested without WPF rendering.
    /// </summary>
    public static Rect AnchorRect(
        double screenX,
        double screenY,
        double viewportWidth,
        double viewportHeight)
    {
        double left = screenX - NameplateWidth / 2.0;
        double top = screenY - NameplateHeight - AnchorGap;
        left = Math.Clamp(left, 0, Math.Max(0, viewportWidth - NameplateWidth));
        top = Math.Clamp(top, 0, Math.Max(0, viewportHeight - NameplateHeight));
        return new Rect(left, top, NameplateWidth, NameplateHeight);
    }    /// <summary>
    /// Replaces the HUD contents with the given beacons (drawn first, under
    /// the nameplates), pips (drawn next, floating above the nameplates), and
    /// nameplates. All lists are already filtered to in-viewport projections
    /// by the view model. The minimap panel (god-view dots) is drawn last,
    /// pinned to the bottom-right corner; it is skipped when no tank has a
    /// normalized position or the camera position is unknown.
    /// </summary>
    public void Render(
        IReadOnlyList<BeaconItem> beacons,
        IReadOnlyList<PipItem> pips,
        IReadOnlyList<NameplateItem> items,
        IReadOnlyList<MinimapItem> minimap,
        double? cameraX,
        double? cameraZ,
        double viewportWidth,
        double viewportHeight)
    {
        HudCanvas.Children.Clear();
        foreach (BeaconItem beacon in beacons)
        {
            HudCanvas.Children.Add(BuildBeacon(beacon));
        }

        foreach (PipItem pip in pips)
        {
            HudCanvas.Children.Add(BuildPip(pip));
        }

        foreach (NameplateItem item in items)
        {
            HudCanvas.Children.Add(BuildNameplate(item, viewportWidth, viewportHeight));
        }

        if (minimap.Count > 0)
        {
            HudCanvas.Children.Add(BuildMinimap(minimap, cameraX, cameraZ, viewportWidth, viewportHeight));
        }
    }

    /// <summary>
    /// Builds the god-view minimap panel: a fixed-size square pinned to the
    /// bottom-right corner, with one dot per tank at its normalized position
    /// (team-colored; grey when destroyed) and a white ring for the camera.
    /// The camera marker is only drawn when the viewpoint position is known.
    /// Pure layout math is unit-tested via <see cref="MinimapMath"/> and
    /// <see cref="MinimapDotRect"/>.
    /// </summary>
    private static Canvas BuildMinimap(
        IReadOnlyList<MinimapItem> minimap,
        double? cameraX,
        double? cameraZ,
        double viewportWidth,
        double viewportHeight)
    {
        const double panelSize = 150;
        const double margin = 12;
        const double dotRadius = 4;

        var panel = new Canvas
        {
            Width = panelSize,
            Height = panelSize,
            Background = CreateBrush("#80101820"),
        };
        Canvas.SetRight(panel, margin);
        Canvas.SetBottom(panel, margin);

        foreach (MinimapItem item in minimap)
        {
            var dot = new Ellipse
            {
                Width = dotRadius * 2,
                Height = dotRadius * 2,
                Fill = item.Alive
                    ? (item.TeamNumber == 1 ? Team1Brush : item.TeamNumber == 2 ? Team2Brush : NeutralBrush)
                    : DeadBrush,
            };
            Canvas.SetLeft(dot, item.NormalizedX * panelSize - dotRadius);
            Canvas.SetTop(dot, item.NormalizedZ * panelSize - dotRadius);
            panel.Children.Add(dot);
        }

        if (cameraX is not null && cameraZ is not null)
        {
            var ring = new Ellipse
            {
                Width = dotRadius * 2 + 3,
                Height = dotRadius * 2 + 3,
                Stroke = CreateBrush("#FFFFFF"),
                StrokeThickness = 1.5,
                Fill = null,
            };
            Canvas.SetLeft(ring, cameraX.Value * panelSize - (dotRadius + 1.5));
            Canvas.SetTop(ring, cameraZ.Value * panelSize - (dotRadius + 1.5));
            panel.Children.Add(ring);
        }

        return panel;
    }

    /// <summary>
    /// Computes the minimap dot anchor rect for a normalized position, for
    /// unit tests (no WPF rendering required). Mirrors the layout used by
    /// <see cref="BuildMinimap"/>.
    /// </summary>
    public static Rect MinimapDotRect(double normalizedX, double normalizedZ, double panelSize, double dotRadius) =>
        new(normalizedX * panelSize - dotRadius, normalizedZ * panelSize - dotRadius, dotRadius * 2, dotRadius * 2);

    private static Canvas BuildBeacon(BeaconItem beacon)
    {
        Brush markerBrush = CreateBrush(beacon.Color);

        var root = new Canvas();
        Canvas.SetLeft(root, beacon.ScreenX);
        Canvas.SetTop(root, beacon.ScreenY);

        // Pin: a filled circle with a dark outline, centered on the anchor.
        root.Children.Add(new Ellipse
        {
            Width = BeaconDotRadius * 2,
            Height = BeaconDotRadius * 2,
            Fill = markerBrush,
            Stroke = CreateBrush("#CC000000"),
            StrokeThickness = 1,
            RenderTransform = new TranslateTransform(-BeaconDotRadius, -BeaconDotRadius),
        });

        // Label above the pin.
        var label = new Border
        {
            Background = CreateBrush("#B0101018"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3, 0, 3, 0),
            Margin = new Thickness(0, -2, 0, 0),
            Child = new TextBlock
            {
                Text = beacon.Name,
                FontSize = BeaconLabelFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = markerBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 140,
            },
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(label, -label.DesiredSize.Width / 2.0);
        Canvas.SetTop(label, -BeaconDotRadius - 16);
        root.Children.Add(label);

        return root;
    }

    private static StackPanel BuildNameplate(
        NameplateItem item,
        double viewportWidth,
        double viewportHeight)
    {
        Rect rect = AnchorRect(item.ScreenX, item.ScreenY, viewportWidth, viewportHeight);

        var root = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Width = NameplateWidth,
        };
        Canvas.SetLeft(root, rect.Left);
        Canvas.SetTop(root, rect.Top);

        // Facing arrow: a screen-space heading (0 = away from the viewer)
        // drawn above the label, rotated to the tank's hull direction. No
        // arrow when the heading is unknown (no packet rotation evidence or
        // a facing that projects to a single pixel).
        if (item.ScreenHeadingDegrees is double heading && double.IsFinite(heading))
        {
            root.Children.Add(BuildHeadingArrow(heading));
        }

        var label = new Border
        {
            Background = LabelBackground,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
        };
        var text = new TextBlock
        {
            Text = item.Label,
            FontSize = LabelFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = item.Alive
                ? (item.TeamNumber == 1 ? Team1Brush : item.TeamNumber == 2 ? Team2Brush : NeutralBrush)
                : DeadBrush,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = NameplateWidth,
        };
        label.Child = text;
        root.Children.Add(label);

        // HP bar: fraction-scaled fill, color by health.
        var hpTrack = new Border
        {
            Width = HpBarWidth,
            Height = HpBarHeight,
            Background = CreateBrush("#55000000"),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness((NameplateWidth - HpBarWidth) / 2.0, 2, 0, 0),
            Child = new Border
            {
                Width = Math.Clamp(HpBarWidth * Math.Clamp(item.HpFraction, 0, 1), 0, HpBarWidth),
                Height = HpBarHeight,
                Background = item.Alive
                    ? HpColor(item.HpFraction)
                    : DeadBrush,
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };
        root.Children.Add(hpTrack);

        // Distance label under the bar.
        root.Children.Add(new TextBlock
        {
            Text = $"{item.DistanceMeters:F0} m",
            FontSize = 9,
            Foreground = CreateBrush("#AAFFFFFF"),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0),
        });

        return root;
    }

    private static Border BuildPip(PipItem pip)
    {
        // Damage pips read "+N", death pips read "\u2716" (a dark skull-like
        // marker); both float just above the affected tank's anchor.
        bool isDamage = string.Equals(pip.Kind, "Damage", StringComparison.Ordinal);
        string text = isDamage ? $"+{pip.Damage}" : "\u2716";
        Brush brush = isDamage
            ? CreateBrush("#FFB000")
            : CreateBrush("#FF5A5A");

        var pipBorder = new Border
        {
            Background = CreateBrush("#D0101018"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0, 4, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = brush,
            },
        };
        pipBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(pipBorder, pip.ScreenX - pipBorder.DesiredSize.Width / 2.0);
        Canvas.SetTop(pipBorder, pip.ScreenY - pipBorder.DesiredSize.Height - 4);
        return pipBorder;
    }

    private static Canvas BuildHeadingArrow(double headingDegrees)
    {
        // Arrow drawn pointing UP (away from the viewer) at 0 degrees;
        // RotateTransform turns it to the tank's screen-space hull heading
        // (positive = clockwise, matching the packet's yaw convention).
        var canvas = new Canvas
        {
            Width = NameplateWidth,
            Height = HeadingArrowLength + 2,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(headingDegrees),
        };
        var arrow = new Polygon
        {
            Points = new PointCollection
            {
                new Point(NameplateWidth / 2.0, 0),
                new Point(NameplateWidth / 2.0 - HeadingArrowHalfWidth, HeadingArrowLength),
                new Point(NameplateWidth / 2.0 + HeadingArrowHalfWidth, HeadingArrowLength),
            },
            Fill = NeutralBrush,
            Stroke = CreateBrush("#CC000000"),
            StrokeThickness = 0.75,
        };
        canvas.Children.Add(arrow);
        return canvas;
    }

    private static Brush HpColor(double fraction) =>
        fraction > 0.5 ? HpGoodBrush : fraction > 0.25 ? HpMidBrush : HpLowBrush;

    private static SolidColorBrush CreateBrush(string hex)
    {
        Color color = (Color)ColorConverter.ConvertFromString(hex);
        return new SolidColorBrush(color);
    }
}
