using System.Globalization;
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
    /// by the view model. The minimap panel (god-view dots over the map
    /// texture) is drawn pinned to the bottom-right corner, and the kill feed
    /// to the bottom-left; each is skipped when it has no entries. The
    /// <paramref name="minimapImage"/> is the current map's texture, aligned
    /// to the same normalized coordinate space as the dots.
    /// </summary>
    public void Render(
        IReadOnlyList<BeaconItem> beacons,
        IReadOnlyList<PipItem> pips,
        IReadOnlyList<NameplateItem> items,
        IReadOnlyList<MinimapItem> minimap,
        IReadOnlyList<MinimapBeaconItem> minimapBeacons,
        double? cameraX,
        double? cameraZ,
        double? cameraYawRadians,
        IReadOnlyList<KillItem> killFeed,
        IReadOnlyList<ScoreboardItem> scoreboard,
        ImageSource? minimapImage,
        double? playbackProgress,
        string? playbackLabel,
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

        if (minimap.Count > 0 || minimapBeacons.Count > 0)
        {
            HudCanvas.Children.Add(
                BuildMinimap(minimap, minimapBeacons, cameraX, cameraZ, cameraYawRadians, viewportWidth, viewportHeight, minimapImage));
        }

        if (killFeed.Count > 0)
        {
            HudCanvas.Children.Add(BuildKillFeed(killFeed));
        }

        if (scoreboard.Count > 0)
        {
            HudCanvas.Children.Add(BuildScoreboard(scoreboard));
        }

        if (playbackProgress is not null)
        {
            HudCanvas.Children.Add(BuildPlaybackBar(playbackProgress.Value, playbackLabel, viewportWidth));
        }
    }

    /// <summary>
    /// Builds the scoreboard panel: every roster tank's cumulative damage
    /// dealt and kills at the current frame time, pinned to the top-right
    /// corner, sorted by the view model (damage dealt, highest first). Rows
    /// are team-colored (blue/red), greyed when the tank is destroyed.
    /// </summary>
    private static Canvas BuildScoreboard(IReadOnlyList<ScoreboardItem> scoreboard)
    {
        const double margin = 12;
        const double rowHeight = 16;
        const double panelWidth = 292;
        const double maxRows = 14;

        var panel = new Canvas
        {
            Background = CreateBrush("#80101820"),
        };
        Canvas.SetRight(panel, margin);
        Canvas.SetTop(panel, margin);

        int shown = 0;
        foreach (ScoreboardItem row in scoreboard)
        {
            if (shown >= maxRows)
            {
                break;
            }

            Brush brush = row.Alive
                ? (row.TeamNumber == 1 ? Team1Brush : row.TeamNumber == 2 ? Team2Brush : NeutralBrush)
                : DeadBrush;

            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(6, 1, 6, 1),
            };
            line.Children.Add(new TextBlock
            {
                Text = row.PlayerName,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = brush,
                Width = 128,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            line.Children.Add(new TextBlock
            {
                Text = row.DamageDealt.ToString(CultureInfo.InvariantCulture),
                FontSize = 11,
                Foreground = brush,
                Width = 52,
                TextAlignment = TextAlignment.Right,
            });
            line.Children.Add(new TextBlock
            {
                Text = row.DamageTaken.ToString(CultureInfo.InvariantCulture),
                FontSize = 11,
                Foreground = brush,
                Width = 52,
                TextAlignment = TextAlignment.Right,
            });
            line.Children.Add(new TextBlock
            {
                Text = row.Kills.ToString(CultureInfo.InvariantCulture),
                FontSize = 11,
                Foreground = brush,
                Width = 32,
                TextAlignment = TextAlignment.Right,
            });

            Canvas.SetTop(line, shown * rowHeight);
            panel.Children.Add(line);
            shown++;
        }

        panel.Width = panelWidth;
        panel.Height = shown * rowHeight + 4;
        return panel;
    }

    /// <summary>
    /// Builds the playback progress bar: a thin track pinned to the bottom-
    /// centre of the overlay with a filled portion proportional to playback
    /// progress and a small time label above the left end. Only drawn while a
    /// session is selected and its duration is known.
    /// </summary>
    private static Canvas BuildPlaybackBar(
        double progress,
        string? label,
        double viewportWidth)
    {
        const double barWidth = 320;
        const double barHeight = 3;
        const double margin = 12;
        const double gap = 40;
        double trackWidth = Math.Min(barWidth, Math.Max(0, viewportWidth - (2 * margin) - gap));
        double left = (viewportWidth - trackWidth) / 2.0;
        double bottom = margin;
        double fillWidth = PlaybackFillWidth(trackWidth, progress);

        var panel = new Canvas
        {
            Width = trackWidth,
            Height = barHeight,
        };
        Canvas.SetLeft(panel, left);
        Canvas.SetBottom(panel, bottom);

        // Track.
        panel.Children.Add(new Rectangle
        {
            Width = trackWidth,
            Height = barHeight,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = CreateBrush("#66101820"),
        });

        // Fill.
        if (fillWidth > 0)
        {
            panel.Children.Add(new Rectangle
            {
                Width = fillWidth,
                Height = barHeight,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = CreateBrush("#CCFFFFFF"),
                HorizontalAlignment = HorizontalAlignment.Left,
            });
        }

        if (!string.IsNullOrWhiteSpace(label))
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = CreateBrush("#BFFFFFFF"),
                Margin = new Thickness(0, -16, 0, 0),
            };
            panel.Children.Add(text);
        }

        return panel;
    }

    /// <summary>
    /// Computes the progress bar fill width for a given track width and
    /// progress fraction, for unit tests (no WPF rendering required). Clamps
    /// progress to 0..1 so the fill never overflows the track.
    /// </summary>
    public static double PlaybackFillWidth(double trackWidth, double progress) =>
        Math.Clamp(progress, 0, 1) * trackWidth;

    /// <summary>
    /// Formats a playback time label "m:ss / m:ss" for the HUD progress bar,
    /// for unit tests. Null when the duration is unknown or non-positive.
    /// </summary>
    public static string? FormatPlaybackLabel(double currentSeconds, double totalSeconds)
    {
        if (!double.IsFinite(currentSeconds) || !double.IsFinite(totalSeconds) || totalSeconds <= 0)
        {
            return null;
        }

        return $"{FormatClock(currentSeconds)} / {FormatClock(totalSeconds)}";
    }

    private static string FormatClock(double seconds) =>
        $"{TimeSpan.FromSeconds(Math.Max(0, seconds)):m\\:ss}";

    /// <summary>
    /// Formats the compact nameplate totals line (damage dealt + kills),
    /// invariant-culture numbers, for unit tests. "0 dmg · 0 kills" when
    /// the tank has no stats evidence.
    /// </summary>
    public static string NameplateTotalsLabel(long damageDealt, long kills) =>
        $"{damageDealt.ToString(CultureInfo.InvariantCulture)} dmg · {kills.ToString(CultureInfo.InvariantCulture)} kills";

    /// <summary>
    /// Builds the kill-feed panel: the most recent entries as a stacked list
    /// pinned to the bottom-left corner, "Killer → Victim" with a time tag,
    /// newest first (the view model already orders it). Environmental kills
    /// render the victim with an em-dash killer label.
    /// </summary>
    private static Canvas BuildKillFeed(IReadOnlyList<KillItem> killFeed)
    {
        const int maxEntries = 8;
        const double margin = 12;
        const double entryHeight = 18;

        var panel = new Canvas
        {
            Background = CreateBrush("#80101820"),
        };
        Canvas.SetLeft(panel, margin);
        Canvas.SetBottom(panel, margin);

        int shown = 0;
        foreach (KillItem kill in killFeed)
        {
            if (shown >= maxEntries)
            {
                break;
            }

            var text = new TextBlock
            {
                Text = $"{kill.KillerLabel} → {kill.VictimLabel}  {kill.ReplayTimeSeconds:F0}s",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateBrush("#E8E8E8"),
                Margin = new Thickness(6, 1, 6, 1),
            };
            Canvas.SetTop(text, shown * entryHeight);
            panel.Children.Add(text);
            shown++;
        }

        panel.Width = 240;
        panel.Height = shown * entryHeight + 6;
        return panel;
    }

    /// <summary>
    /// Builds the god-view minimap panel: a fixed-size square pinned to the
    /// bottom-right corner, with the map texture as background, one dot per
    /// tank at its normalized position (team-colored; grey when destroyed)
    /// and a white ring for the camera. The camera marker is only drawn when
    /// the viewpoint position is known. The texture is stretched to the panel
    /// so dots in normalized coordinates align with terrain features; a
    /// non-square boundary therefore distorts the texture rather than the
    /// dots (dot alignment is the invariant that matters). Pure layout math is
    /// unit-tested via <see cref="MinimapMath"/>, <see cref="MinimapDotRect"/>
    /// and <see cref="MinimapImageRect"/>.
    /// </summary>
    private static Canvas BuildMinimap(
        IReadOnlyList<MinimapItem> minimap,
        IReadOnlyList<MinimapBeaconItem> minimapBeacons,
        double? cameraX,
        double? cameraZ,
        double? cameraYawRadians,
        double viewportWidth,
        double viewportHeight,
        ImageSource? minimapImage)
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

        // Map texture under the dots: stretched to the panel square, matching
        // the 0..1 normalized coordinate space the dots and beacons share.
        if (minimapImage is not null)
        {
            panel.Children.Add(new Image
            {
                Source = minimapImage,
                Width = panelSize,
                Height = panelSize,
                Opacity = 0.55,
                Stretch = Stretch.Fill,
            });
        }

        // Beacons next (under the tank dots): small diamonds in their own
        // marker color, so POIs read even where they overlap tanks.
        foreach (MinimapBeaconItem beacon in minimapBeacons)
        {
            Brush markerBrush = CreateBrush(beacon.Color);
            var diamond = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(0, -5),
                    new Point(5, 0),
                    new Point(0, 5),
                    new Point(-5, 0),
                },
                Fill = markerBrush,
                Stroke = CreateBrush("#CC000000"),
                StrokeThickness = 1,
                RenderTransform = new TranslateTransform(
                    beacon.NormalizedX * panelSize,
                    beacon.NormalizedZ * panelSize),
            };
            panel.Children.Add(diamond);
        }

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

            // Camera facing tick: a short line from the ring toward the
            // viewpoint's facing direction. Yaw convention (packet): 0 faces
            // +Z, +pi/2 faces +X; the minimap maps world X to panel right and
            // world Z to panel down, so the panel delta is (sin yaw, cos yaw)
            // scaled by the tick length.
            if (cameraYawRadians is double yaw && double.IsFinite(yaw))
            {
                const double tickLength = 14;
                double cx = cameraX.Value * panelSize;
                double cz = cameraZ.Value * panelSize;
                double px = Math.Sin(yaw) * tickLength;
                double pz = Math.Cos(yaw) * tickLength;

                var tick = new Polygon
                {
                    Points = new PointCollection
                    {
                        new Point(cx + pz * 0.35, cz - px * 0.35),
                        new Point(cx + px, cz + pz),
                        new Point(cx - pz * 0.35, cz + px * 0.35),
                    },
                    Fill = CreateBrush("#FFFFFFFF"),
                };
                panel.Children.Add(tick);
            }
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

    /// <summary>
    /// Computes the minimap texture image rect for a given panel size, for
    /// unit tests (no WPF rendering required). Mirrors the layout used by
    /// <see cref="BuildMinimap"/>: the texture fills the panel so normalized
    /// dot coordinates align with terrain features.
    /// </summary>
    public static Rect MinimapImageRect(double panelSize) =>
        new(0, 0, panelSize, panelSize);

    /// <summary>
    /// Computes the camera facing tick's apex (panel coordinates) for a given
    /// camera position, yaw and tick length, for unit tests (no WPF rendering
    /// required). Yaw uses the packet convention (0 faces +Z, +π/2 faces +X)
    /// mapped to panel pixels: world X → panel right, world Z → panel down.
    /// </summary>
    public static Point CameraTickApex(
        double cameraX,
        double cameraZ,
        double yawRadians,
        double panelSize,
        double tickLength) =>
        new(
            cameraX * panelSize + Math.Sin(yawRadians) * tickLength,
            cameraZ * panelSize + Math.Cos(yawRadians) * tickLength);

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

        // Totals line: cumulative damage dealt + kills at the frame time.
        // Kept under the distance so the plate height stays stable.
        root.Children.Add(new TextBlock
        {
            Text = NameplateTotalsLabel(item.DamageDealt, item.Kills),
            FontSize = 8,
            Foreground = CreateBrush("#99FFFFFF"),
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
