using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
///
/// Visual language: dark "glass" panels with a single-pixel light border and
/// a team-color accent, so plates stay readable over arbitrary game footage
/// without obscuring it. All shared brushes are frozen at static-init time so
/// the per-frame rebuild allocates only layout objects, not paint resources.
/// </summary>
public sealed partial class W2sHudView : UserControl
{
    // AnchorRect's width/height/gap are the plate's unit-test contract; keep
    // them in sync with the geometry asserted in W2sHudViewTests.
    private const double NameplateWidth = 96;
    private const double NameplateHeight = 22;
    private const double AnchorGap = 6;
    private const double HpBarWidth = 84;
    private const double HpBarHeight = 5;
    private const double LabelFontSize = 12;
    private const double MetaFontSize = 8.5;
    private const double BeaconDotRadius = 5;
    private const double BeaconLabelFontSize = 10;
    private const double HeadingArrowLength = 10;
    private const double HeadingArrowHalfWidth = 5;

    // Combat-pip rise-and-fade tuning: a pip rises 14px and fades out over
    // ~16 rendered frames (≈ 0.8s at the 20 fps playback tick).
    internal const double PipRisePixels = 14;
    internal const int PipAnimationFrames = 16;

    // HP damage-ghost tuning: after a hit the pale "recent damage" bar lags
    // above the live fill and eases down toward it over ~15 frames (≈ 0.75s).
    internal const double HpGhostEaseRate = 0.35;
    internal const double HpGhostSnapThreshold = 0.005;

    // Kill-feed entry animation: the newest entry slides in from the left and
    // fades up over ~8 frames (≈ 0.4s).
    internal const double FeedSlidePixels = 8;
    internal const int FeedAnimationFrames = 8;

    // Pen-badge pulse tuning: a verdict change pops the badge by ~8% and it
    // eases back to full size over ~10 frames (≈ 0.5s).
    internal const double PenPulseOvershoot = 0.08;
    internal const int PenPulseFrames = 10;

    // Low-HP nameplate pulse tuning: a killable target's HP-bar edge glows
    // red, pulsing between full and min intensity over ~24 frames (≈ 1.2s).
    internal const double LowHpThreshold = 0.25;
    internal const double LowHpPulseMinAlpha = 0.35;
    internal const int LowHpPulsePeriodFrames = 24;

    // ── Team / state palette ────────────────────────────────
    private static readonly Brush Team1Brush = Solid("#4FA8FF");
    private static readonly Brush Team2Brush = Solid("#FF6B6B");
    private static readonly Brush NeutralBrush = Solid("#C8D0DA");
    private static readonly Brush DeadBrush = Solid("#6F7887");

    // ── Health palette (vertical gradients read as lit glass) ──
    private static readonly Brush HpGoodBrush = VerticalGradient("#5BEFAA", "#1BC46A");
    private static readonly Brush HpMidBrush = VerticalGradient("#FFDD80", "#E9A93C");
    private static readonly Brush HpLowBrush = VerticalGradient("#FF8585", "#E6355C");

    // ── Glass / ink palette ─────────────────────────────────
    private static readonly Brush PanelGlass = VerticalGradient("#F0141B28", "#C60A0D17");
    private static readonly Brush PanelBorderBrush = Solid("#3DFFFFFF");
    private static readonly Brush HpTrackBrush = Solid("#66000000");
    private static readonly Brush HpTrackBorderBrush = Solid("#2EFFFFFF");
    private static readonly Brush InkBrush = Solid("#F4F7FA");
    private static readonly Brush MutedBrush = Solid("#A9B4C0");
    private static readonly Brush FaintBrush = Solid("#7C8794");
    private static readonly Brush OutlineBrush = Solid("#CC000000");

    // ── Verdict / pip / playback accents ────────────────────
    private static readonly Brush PenVerdictBrush = Solid("#2BE67D");
    private static readonly Brush MarginalVerdictBrush = Solid("#F5C34B");
    private static readonly Brush NoPenVerdictBrush = Solid("#FF5252");
    private static readonly Brush PipDamageBrush = Solid("#FFC24B");
    private static readonly Brush PipKillBrush = Solid("#FF6B6B");
    private static readonly Brush HpGhostBrush = Solid("#A6FFFFFF");
    private static readonly Brush LowHpGlowBrush = Solid("#FF5252");
    private static readonly Brush PlaybackFillBrush = Solid("#CCFFFFFF");
    private static readonly Brush MinimapVignette = RadialVignette();

    /// <summary>
    /// Pip age in rendered frames, keyed by (entity, kind, damage) so a hit or
    /// death popup animates across consecutive frames even though the canvas
    /// is cleared and rebuilt every frame. Pips absent from the current frame
    /// drop out of the map, so a later identical hit restarts its pop.
    /// </summary>
    private Dictionary<(long EntityId, string Kind, int Damage), int> _pipAges = new();

    /// <summary>Lagging HP fill fraction per tank, for the damage-ghost trail.</summary>
    private Dictionary<long, double> _hpGhosts = new();

    /// <summary>Frames-since-low-HP per tank, for the killable-target edge pulse.</summary>
    private Dictionary<long, int> _lowHpAges = new();

    /// <summary>Frames-since-arrival per kill-feed victim, for the slide-in.</summary>
    private Dictionary<long, int> _killAges = new();

    /// <summary>Last rendered pen-badge verdict text, to detect verdict changes.</summary>
    private string? _lastPenStateKey;

    /// <summary>Frames since the last pen verdict change (<see cref="int.MaxValue"/> = settled).</summary>
    private int _penPulseAge = int.MaxValue;

    /// <summary>True while the pointer is dragging the playback bar scrub region.</summary>
    private bool _scrubbing;

    /// <summary>
    /// Raised while the user drags (or clicks) the in-HUD playback bar, with
    /// the requested position as a 0..1 fraction of the timeline. The owner
    /// (MainWindow) maps it to a view-model scrub.
    /// </summary>
    public event Action<double>? PlaybackScrubRequested;

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
    }

    /// <summary>
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
        IReadOnlyList<OwnMarkerItem> ownMarkers,
        IReadOnlyList<MinimapItem> minimap,
        IReadOnlyList<MinimapBeaconItem> minimapBeacons,
        double? cameraX,
        double? cameraZ,
        double? cameraYawRadians,
        IReadOnlyList<KillItem> killFeed,
        IReadOnlyList<ScoreboardItem> scoreboard,
        PenBadgeItem? penBadge,
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

        Dictionary<(long EntityId, string Kind, int Damage), int> nextPipAges = new();
        foreach (PipItem pip in pips)
        {
            var key = (pip.EntityId, pip.Kind, pip.Damage);
            int age = _pipAges.TryGetValue(key, out int previous) ? previous + 1 : 0;
            nextPipAges[key] = age;
            HudCanvas.Children.Add(BuildPip(pip, age));
        }

        _pipAges = nextPipAges;

        // Per-entity HP damage-ghost state: the pale trail behind the live
        // fill eases down toward the current fraction after each hit, then
        // snaps forward on heal/regen. Rebuilt per frame so tanks that leave
        // the viewport drop out and restart at their live value.
        Dictionary<long, double> nextHpGhosts = new();
        foreach (NameplateItem item in items)
        {
            double target = double.IsFinite(item.HpFraction)
                ? Math.Clamp(item.HpFraction, 0, 1)
                : 0;
            double ghost = _hpGhosts.TryGetValue(item.EntityId, out double previous)
                ? HpGhostEase(previous, target)
                : target;
            nextHpGhosts[item.EntityId] = ghost;
        }

        _hpGhosts = nextHpGhosts;

        // Per-entity low-HP state: an alive tank in the killable band pulses
        // its HP-bar edge; leaving the band or viewport drops it out so the
        // pulse restarts cleanly. Keyed by entity id, rebuilt per frame.
        Dictionary<long, int> nextLowHpAges = new();
        foreach (NameplateItem item in items)
        {
            double hp = double.IsFinite(item.HpFraction)
                ? Math.Clamp(item.HpFraction, 0, 1)
                : 0;
            if (item.Alive && hp > 0 && hp <= LowHpThreshold)
            {
                nextLowHpAges[item.EntityId] =
                    _lowHpAges.TryGetValue(item.EntityId, out int previous) ? previous + 1 : 0;
            }
        }

        _lowHpAges = nextLowHpAges;

        foreach (NameplateItem item in items)
        {
            double? lowHpPulseAlpha = nextLowHpAges.TryGetValue(item.EntityId, out int lowHpAge)
                ? LowHpPulseAlpha(lowHpAge, LowHpPulsePeriodFrames)
                : null;
            HudCanvas.Children.Add(
                BuildNameplate(item, nextHpGhosts[item.EntityId], lowHpPulseAlpha, viewportWidth, viewportHeight));
        }

        foreach (OwnMarkerItem marker in ownMarkers)
        {
            HudCanvas.Children.Add(BuildOwnMarker(marker));
        }

        if (minimap.Count > 0 || minimapBeacons.Count > 0)
        {
            HudCanvas.Children.Add(
                BuildMinimap(minimap, minimapBeacons, cameraX, cameraZ, cameraYawRadians, viewportWidth, viewportHeight, minimapImage));
        }

        // Per-entry kill age so the newest kill slides in and fades up across
        // consecutive frames while older entries stay settled. Rebuilt each
        // frame (empty when the feed is empty) so the map stays bounded.
        Dictionary<long, int> nextKillAges = new();
        foreach (KillItem kill in killFeed)
        {
            int age = _killAges.TryGetValue(kill.VictimEntityId, out int previous) ? previous + 1 : 0;
            nextKillAges[kill.VictimEntityId] = age;
        }

        _killAges = nextKillAges;

        if (killFeed.Count > 0)
        {
            HudCanvas.Children.Add(BuildKillFeed(killFeed, nextKillAges));
        }

        if (scoreboard.Count > 0)
        {
            HudCanvas.Children.Add(BuildScoreboard(scoreboard));
        }

        if (penBadge is not null)
        {
            NameplateItem? aimed = null;
            foreach (NameplateItem item in items)
            {
                if (item.EntityId == penBadge.AimedEntityId)
                {
                    aimed = item;
                    break;
                }
            }

            // Pulse the badge whenever its verdict text changes (a real pen
            // state change), so the readout visibly reacts to the shot you
            // can or cannot take. The badge also re-pulses on reappearance.
            string stateKey = PenBadgeLabel(
                penBadge.Band,
                penBadge.EffectiveArmorMm,
                penBadge.PenetrationMmAtRange,
                penBadge.Ricochet,
                penBadge.Shell,
                penBadge.Face);
            if (!string.Equals(stateKey, _lastPenStateKey, StringComparison.Ordinal))
            {
                _lastPenStateKey = stateKey;
                _penPulseAge = 0;
            }
            else if (_penPulseAge < int.MaxValue)
            {
                _penPulseAge++;
            }

            HudCanvas.Children.Add(BuildPenBadge(penBadge, aimed, _penPulseAge, viewportWidth, viewportHeight));
        }
        else
        {
            _lastPenStateKey = null;
            _penPulseAge = int.MaxValue;
        }

        if (playbackProgress is not null)
        {
            HudCanvas.Children.Add(BuildPlaybackBar(playbackProgress.Value, playbackLabel, viewportWidth));
            PositionPlaybackScrubRegion(viewportWidth, viewportHeight);
        }
        else
        {
            PlaybackScrubRegion.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Builds the scoreboard panel: every roster tank's cumulative damage
    /// dealt, damage taken and kills at the current frame time, pinned to the
    /// top-right corner, sorted by the view model (damage dealt, highest
    /// first). Rows are team-colored (blue/red), greyed and struck through
    /// when the tank is destroyed. A header row labels the numeric columns.
    /// </summary>
    private static Border BuildScoreboard(IReadOnlyList<ScoreboardItem> scoreboard)
    {
        const double margin = 12;
        const double rowHeight = 16;
        const double headerHeight = 18;
        const double panelWidth = 300;
        const int maxRows = 14;

        var root = new Border
        {
            Width = panelWidth,
            Background = PanelGlass,
            BorderBrush = PanelBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4, 4, 4, 6),
        };
        var stack = new StackPanel();
        root.Child = stack;

        // Header row.
        var header = new Grid { Height = headerHeight, Margin = new Thickness(6, 0, 6, 1) };
        AddColumnSet(header, star: true, 56, 56, 34);
        AddHeaderCell(header, 0, "NAME", TextAlignment.Left);
        AddHeaderCell(header, 1, "DMG", TextAlignment.Right);
        AddHeaderCell(header, 2, "TAKEN", TextAlignment.Right);
        AddHeaderCell(header, 3, "KILLS", TextAlignment.Right);
        stack.Children.Add(header);

        // Divider under the header.
        stack.Children.Add(new Rectangle
        {
            Height = 1,
            Fill = PanelBorderBrush,
            Opacity = 0.5,
            Margin = new Thickness(6, 0, 6, 2),
        });

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

            var line = new Grid { Height = rowHeight, Margin = new Thickness(6, 0, 6, 1) };
            AddColumnSet(line, star: true, 56, 56, 34);

            var nameCell = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            nameCell.Children.Add(new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = brush,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            nameCell.Children.Add(new TextBlock
            {
                Text = row.PlayerName,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = brush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextDecorations = row.Alive ? null : TextDecorations.Strikethrough,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(nameCell, 0);
            line.Children.Add(nameCell);

            AddScoreCell(line, 1, row.DamageDealt.ToString(CultureInfo.InvariantCulture), brush);
            AddScoreCell(line, 2, row.DamageTaken.ToString(CultureInfo.InvariantCulture), brush);
            AddScoreCell(line, 3, row.Kills.ToString(CultureInfo.InvariantCulture), brush);

            stack.Children.Add(line);
            shown++;
        }

        Canvas.SetRight(root, margin);
        Canvas.SetTop(root, margin);
        return root;
    }

    /// <summary>
    /// Adds four scoreboard columns; the first is star-sized (name), the
    /// remaining three fixed-width right-aligned numeric columns.
    /// </summary>
    private static void AddColumnSet(Grid grid, bool star, params double[] widths)
    {
        if (star)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        foreach (double width in widths)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        }
    }

    private static void AddHeaderCell(Grid grid, int column, string text, TextAlignment alignment)
    {
        var cell = new TextBlock
        {
            Text = text,
            FontSize = 8,
            FontWeight = FontWeights.Bold,
            Foreground = FaintBrush,
            TextAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static void AddScoreCell(Grid grid, int column, string text, Brush brush)
    {
        var cell = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = brush,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    /// <summary>
    /// Formats the penetration badge's text: the struck face (when known),
    /// the banded verdict, and the numeric readout (penetration at range over
    /// effective armor, in mm) when both are known. A ricochet overrides the
    /// band with its own label. Pure and invariant-culture for unit tests.
    /// </summary>
    public static string PenBadgeLabel(
        string band,
        double? effectiveArmorMm,
        double? penetrationMmAtRange,
        bool ricochet,
        string? shell = null,
        string? face = null)
    {
        string prefix = string.IsNullOrEmpty(shell) ? string.Empty : $"[{shell}] ";
        // Struck face token: the in-game armor terminology is front/side/rear
        // (the wire value is the StruckFace enum name, where Back == rear).
        string faceToken = face switch
        {
            "Front" => "FRONT",
            "Back" => "REAR",
            "Side" => "SIDE",
            _ => string.Empty,
        };
        string facePart = string.IsNullOrEmpty(faceToken) ? string.Empty : faceToken + " ";
        string verdict = band switch
        {
            "Pen" => "PEN",
            "Marginal" => "MARGINAL",
            "NoPen" => "NO PEN",
            _ => "",
        };

        // Keep the pure formatter fail-closed too: a malformed band must not
        // produce a numeric-looking readout or a standalone ricochet label.
        if (string.IsNullOrEmpty(verdict))
        {
            return string.Empty;
        }

        if (ricochet)
        {
            return prefix + facePart + "RICOCHET";
        }

        if (effectiveArmorMm is double eff
            && penetrationMmAtRange is double pen)
        {
            return string.Concat(
                prefix,
                facePart,
                verdict,
                "  ",
                pen.ToString("F0", CultureInfo.InvariantCulture),
                "/",
                eff.ToString("F0", CultureInfo.InvariantCulture),
                " mm");
        }

        return prefix + facePart + verdict;
    }

    /// <summary>
    /// Builds the penetration indicator: a colored pill showing the struck
    /// face, the banded verdict, and its numeric readout. When the aimed
    /// tank's nameplate is on screen the badge anchors to it (centred above
    /// the plate), so the readout is visually tied to the tank being aimed
    /// at; when the aimed tank is off-viewport it falls back to the reticle
    /// position below centre. Unknown bands never reach here (the view model
    /// drops them).
    /// </summary>
    private static Canvas BuildPenBadge(
        PenBadgeItem badge,
        NameplateItem? aimed,
        int pulseAge,
        double viewportWidth,
        double viewportHeight)
    {
        Brush brush = badge.Band switch
        {
            "Pen" => PenVerdictBrush,
            "Marginal" => MarginalVerdictBrush,
            _ => NoPenVerdictBrush,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = "\u25C6",
            FontSize = 9,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = PenBadgeLabel(
                badge.Band,
                badge.EffectiveArmorMm,
                badge.PenetrationMmAtRange,
                badge.Ricochet,
                badge.Shell,
                badge.Face),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = brush,
        });

        double pulseScale = PenPulseScale(pulseAge, PenPulseFrames);
        var label = new Border
        {
            Background = PanelGlass,
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            Child = content,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(pulseScale, pulseScale),
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double left;
        double top;
        if (aimed is not null)
        {
            // Anchor to the aimed tank: centre the badge on the nameplate and
            // lift it above the plate's top edge so it reads as part of the
            // tank being aimed at rather than a floating reticle element.
            Rect plate = AnchorRect(aimed.ScreenX, aimed.ScreenY, viewportWidth, viewportHeight);
            left = plate.Left + (NameplateWidth - label.DesiredSize.Width) / 2.0;
            top = plate.Top - label.DesiredSize.Height - AnchorGap;
        }
        else
        {
            // Aimed tank is off-viewport (no nameplate): fall back to the
            // reticle position just below centre.
            left = viewportWidth / 2.0 - label.DesiredSize.Width / 2.0;
            top = viewportHeight * 0.56;
        }

        var root = new Canvas();
        Canvas.SetLeft(root, Math.Clamp(left, 0, Math.Max(0, viewportWidth - label.DesiredSize.Width)));
        Canvas.SetTop(root, Math.Clamp(top, 0, Math.Max(0, viewportHeight - label.DesiredSize.Height)));
        root.Children.Add(label);
        return root;
    }

    /// <summary>
    /// Builds the playback progress bar: a thin track pinned to the bottom-
    /// centre of the overlay with a filled portion proportional to playback
    /// progress, a position knob on the leading edge, and a centred time
    /// label above the track. Only drawn while a session is selected and its
    /// duration is known.
    /// </summary>
    private static Canvas BuildPlaybackBar(
        double progress,
        string? label,
        double viewportWidth)
    {
        const double barWidth = 320;
        const double barHeight = 3;
        const double knobSize = 7;
        const double margin = 12;
        const double gap = 40;
        double trackWidth = Math.Min(barWidth, Math.Max(0, viewportWidth - (2 * margin) - gap));
        double left = (viewportWidth - trackWidth) / 2.0;
        double bottom = margin;
        double fillWidth = PlaybackFillWidth(trackWidth, progress);
        const double trackTop = (knobSize - barHeight) / 2.0;

        var panel = new Canvas
        {
            Width = trackWidth,
            Height = knobSize,
        };
        Canvas.SetLeft(panel, left);
        Canvas.SetBottom(panel, bottom);

        // Track.
        var track = new Rectangle
        {
            Width = trackWidth,
            Height = barHeight,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = HpTrackBrush,
            Stroke = HpTrackBorderBrush,
            StrokeThickness = 1,
        };
        Canvas.SetTop(track, trackTop);
        panel.Children.Add(track);

        // Fill.
        if (fillWidth > 0)
        {
            var fill = new Rectangle
            {
                Width = Math.Max(barHeight, fillWidth),
                Height = barHeight,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = PlaybackFillBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Canvas.SetTop(fill, trackTop);
            panel.Children.Add(fill);

            // Leading-edge knob.
            var knob = new Ellipse
            {
                Width = knobSize,
                Height = knobSize,
                Fill = PlaybackFillBrush,
                Stroke = OutlineBrush,
                StrokeThickness = 1,
            };
            Canvas.SetLeft(knob, Math.Clamp(fillWidth - knobSize / 2.0, 0, trackWidth - knobSize));
            Canvas.SetTop(knob, 0);
            panel.Children.Add(knob);
        }

        if (!string.IsNullOrWhiteSpace(label))
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = InkBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
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
    /// Computes the playback bar's scrub hit band in viewport coordinates
    /// (top-left origin), mirroring <see cref="BuildPlaybackBar"/>'s horizontal
    /// geometry but with a taller band so the track is easy to grab. Pure for
    /// unit tests. The returned rect's width equals the bar's track width.
    /// </summary>
    public static Rect PlaybackScrubRegionRect(double viewportWidth, double viewportHeight)
    {
        const double barWidth = 320;
        const double margin = 12;
        const double gap = 40;
        const double bandHeight = 24;
        double trackWidth = Math.Min(barWidth, Math.Max(0, viewportWidth - (2 * margin) - gap));
        double left = (viewportWidth - trackWidth) / 2.0;
        double top = viewportHeight - margin - bandHeight;
        return new Rect(left, top, trackWidth, bandHeight);
    }

    /// <summary>
    /// Maps a pointer X (relative to the scrub band's left edge) to a 0..1
    /// timeline fraction, clamping so drags past either end stay at the ends.
    /// Pure for unit tests; a non-positive band width fails closed to 0.
    /// </summary>
    public static double PlaybackScrubFraction(double pointerX, double trackWidth)
    {
        if (!double.IsFinite(trackWidth) || trackWidth <= 0)
        {
            return 0;
        }

        return Math.Clamp(pointerX / trackWidth, 0, 1);
    }

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
    /// Positions the persistent scrub hit band over the playback bar so the
    /// user can click/drag to seek. Called on every render with the same
    /// geometry, so it stays aligned with the rebuilt track.
    /// </summary>
    private void PositionPlaybackScrubRegion(double viewportWidth, double viewportHeight)
    {
        Rect rect = PlaybackScrubRegionRect(viewportWidth, viewportHeight);
        PlaybackScrubRegion.Width = rect.Width;
        PlaybackScrubRegion.Height = rect.Height;
        PlaybackScrubRegion.Margin = new Thickness(rect.Left, 0, 0, viewportHeight - rect.Bottom);
        PlaybackScrubRegion.Visibility = Visibility.Visible;
    }

    private void PlaybackScrubRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _scrubbing = true;
        PlaybackScrubRegion.CaptureMouse();
        RequestScrub(e.GetPosition(PlaybackScrubRegion));
        e.Handled = true;
    }

    private void PlaybackScrubRegion_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_scrubbing)
        {
            return;
        }

        RequestScrub(e.GetPosition(PlaybackScrubRegion));
        e.Handled = true;
    }

    private void PlaybackScrubRegion_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_scrubbing)
        {
            return;
        }

        _scrubbing = false;
        PlaybackScrubRegion.ReleaseMouseCapture();
        RequestScrub(e.GetPosition(PlaybackScrubRegion));
        e.Handled = true;
    }

    private void RequestScrub(Point pointer)
    {
        PlaybackScrubRequested?.Invoke(
            PlaybackScrubFraction(pointer.X, PlaybackScrubRegion.ActualWidth));
    }

    /// <summary>
    /// Formats the compact nameplate totals line (damage dealt + kills),
    /// invariant-culture numbers, for unit tests. "0 dmg · 0 kills" when
    /// the tank has no stats evidence.
    /// </summary>
    public static string NameplateTotalsLabel(long damageDealt, long kills) =>
        $"{damageDealt.ToString(CultureInfo.InvariantCulture)} dmg · {kills.ToString(CultureInfo.InvariantCulture)} kills";

    /// <summary>
    /// Formats the nameplate's single muted meta line: exact HP (when the
    /// type-5 max-HP broadcast decoded), range, then cumulative damage and
    /// kills. Parts are joined with " · " and numbers stay invariant-culture.
    /// Pure for unit tests.
    /// </summary>
    public static string NameplateMetaLabel(
        double distanceMeters,
        long maxHealth,
        long currentHealth,
        long damageDealt,
        long kills)
    {
        var parts = new List<string>();
        if (maxHealth > 0)
        {
            parts.Add(
                $"{Math.Max(currentHealth, 0).ToString(CultureInfo.InvariantCulture)}/{maxHealth.ToString(CultureInfo.InvariantCulture)} HP");
        }

        double safeDistance = double.IsFinite(distanceMeters) ? distanceMeters : 0;
        parts.Add($"{safeDistance.ToString("F0", CultureInfo.InvariantCulture)} m");
        parts.Add(NameplateTotalsLabel(damageDealt, kills));
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Builds the kill-feed panel: the most recent entries as a stacked list
    /// pinned to the bottom-left corner, "Killer → Victim" with a right-
    /// aligned time tag, newest first (the view model already orders it).
    /// Environmental kills render the victim with an em-dash killer label.
    /// </summary>
    private static Border BuildKillFeed(
        IReadOnlyList<KillItem> killFeed,
        Dictionary<long, int> ages)
    {
        const int maxEntries = 8;
        const double margin = 12;
        const double entryHeight = 18;
        const double panelWidth = 262;

        var root = new Border
        {
            Width = panelWidth,
            Background = PanelGlass,
            BorderBrush = PanelBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4, 3, 6, 5),
        };
        var stack = new StackPanel();
        root.Child = stack;

        stack.Children.Add(new TextBlock
        {
            Text = "KILL FEED",
            FontSize = 8,
            FontWeight = FontWeights.Bold,
            Foreground = FaintBrush,
            Margin = new Thickness(6, 0, 0, 2),
        });

        int shown = 0;
        foreach (KillItem kill in killFeed)
        {
            if (shown >= maxEntries)
            {
                break;
            }

            var line = new Grid { Height = entryHeight, Margin = new Thickness(6, 0, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            // Newest entries slide in from the left and fade up; older ones
            // are already settled. A missing age means "settled".
            (double slideOffset, double opacity) = FeedEntryAnimation(
                ages.TryGetValue(kill.VictimEntityId, out int age) ? age : int.MaxValue,
                FeedAnimationFrames);
            line.RenderTransform = new TranslateTransform(slideOffset, 0);
            line.Opacity = opacity;

            // A malformed kill timestamp must not render as "NaNs".
            double killTime = double.IsFinite(kill.ReplayTimeSeconds)
                ? kill.ReplayTimeSeconds
                : 0;

            var summary = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            summary.Children.Add(new TextBlock
            {
                Text = kill.KillerLabel,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = InkBrush,
                MaxWidth = 108,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            summary.Children.Add(new TextBlock
            {
                Text = "  →  ",
                FontSize = 11,
                Foreground = FaintBrush,
            });
            summary.Children.Add(new TextBlock
            {
                Text = kill.VictimLabel,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = PipKillBrush,
                MaxWidth = 108,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(summary, 0);
            line.Children.Add(summary);

            var time = new TextBlock
            {
                Text = $"{killTime:F0}s",
                FontSize = 9,
                Foreground = FaintBrush,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(time, 1);
            line.Children.Add(time);

            stack.Children.Add(line);
            shown++;
        }

        Canvas.SetLeft(root, margin);
        Canvas.SetBottom(root, margin);
        return root;
    }

    /// <summary>
    /// Builds the god-view minimap panel: a fixed-size square pinned to the
    /// bottom-right corner, with the map texture as background, one dot per
    /// tank at its normalized position (team-colored; grey when destroyed),
    /// a translucent view-cone plus white ring for the camera, and a soft
    /// vignette so dots read over bright map areas. The camera marker is only
    /// drawn when the viewpoint position is known. The texture is stretched to
    /// the panel so dots in normalized coordinates align with terrain
    /// features; a non-square boundary therefore distorts the texture rather
    /// than the dots (dot alignment is the invariant that matters). Pure layout
    /// math is unit-tested via <see cref="MinimapMath"/>, <see cref="MinimapDotRect"/>
    /// and <see cref="MinimapImageRect"/>.
    /// </summary>
    private static Border BuildMinimap(
        IReadOnlyList<MinimapItem> minimap,
        IReadOnlyList<MinimapBeaconItem> minimapBeacons,
        double? cameraX,
        double? cameraZ,
        double? cameraYawRadians,
        double viewportWidth,
        double viewportHeight,
        ImageSource? minimapImage)
    {
        const double panelSize = 164;
        const double margin = 12;
        const double dotRadius = 4;

        var panel = new Canvas
        {
            Width = panelSize,
            Height = panelSize,
            Clip = new RectangleGeometry(new Rect(0, 0, panelSize, panelSize), 9, 9),
        };

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

            // Soft vignette so the dots and cone keep contrast on bright maps.
            panel.Children.Add(new Rectangle
            {
                Width = panelSize,
                Height = panelSize,
                Fill = MinimapVignette,
            });
        }

        // Beacons next (under the tank dots): small diamonds in their own
        // marker color, so POIs read even where they overlap tanks.
        foreach (MinimapBeaconItem beacon in minimapBeacons)
        {
            Brush markerBrush = MarkerBrush(beacon.Color);
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
                Stroke = OutlineBrush,
                StrokeThickness = 1,
                RenderTransform = new TranslateTransform(
                    beacon.NormalizedX * panelSize,
                    beacon.NormalizedZ * panelSize),
            };
            panel.Children.Add(diamond);
        }

        // Camera view-cone under the tank dots, so tanks cover it when close.
        if (cameraX is not null && cameraZ is not null)
        {
            double cx = cameraX.Value * panelSize;
            double cz = cameraZ.Value * panelSize;

            if (cameraYawRadians is double yaw && double.IsFinite(yaw))
            {
                const double coneLength = 16;
                const double coneHalfBase = 7;
                Point apex = CameraTickApex(cameraX.Value, cameraZ.Value, yaw, panelSize, coneLength);
                double px = Math.Sin(yaw);
                double pz = Math.Cos(yaw);
                var cone = new Polygon
                {
                    Points = new PointCollection
                    {
                        new Point(cx + pz * coneHalfBase, cz - px * coneHalfBase),
                        apex,
                        new Point(cx - pz * coneHalfBase, cz + px * coneHalfBase),
                    },
                    Fill = Solid("#2EFFFFFF"),
                };
                panel.Children.Add(cone);
            }
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
                Stroke = OutlineBrush,
                StrokeThickness = 1,
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
                Stroke = Solid("#FFFFFFFF"),
                StrokeThickness = 1.5,
                Fill = null,
            };
            Canvas.SetLeft(ring, cameraX.Value * panelSize - (dotRadius + 1.5));
            Canvas.SetTop(ring, cameraZ.Value * panelSize - (dotRadius + 1.5));
            panel.Children.Add(ring);
        }

        var root = new Border
        {
            Width = panelSize,
            Height = panelSize,
            Background = PanelGlass,
            BorderBrush = PanelBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = panel,
        };
        Canvas.SetRight(root, margin);
        Canvas.SetBottom(root, margin);
        return root;
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

    /// <summary>
    /// The own-tank edge marker: a small chevron whose apex points toward
    /// the player's hull (angle from <see cref="OwnMarkerItem"/>, 0 = +X,
    /// +π/2 = +Y in viewport pixels), anchored at the clamped viewport edge.
    /// A faint halo keeps it visible over bright game footage.
    /// </summary>
    private static Canvas BuildOwnMarker(OwnMarkerItem marker)
    {
        const double size = 14.0;
        var chevron = new Polygon
        {
            Points =
            [
                new Point(0, -size),
                new Point(-size * 0.45, size * 0.55),
                new Point(size * 0.45, size * 0.55),
            ],
            Fill = Solid("#E6FFFFFF"),
            Stroke = OutlineBrush,
            StrokeThickness = 1.5,
            RenderTransform = new RotateTransform(
                marker.AngleRadians * 180.0 / Math.PI),
        };

        var root = new Canvas();
        Canvas.SetLeft(root, marker.ScreenX);
        Canvas.SetTop(root, marker.ScreenY);
        root.Children.Add(new Ellipse
        {
            Width = 26,
            Height = 26,
            Fill = Solid("#1AFFFFFF"),
            RenderTransform = new TranslateTransform(-13, -13),
        });
        root.Children.Add(chevron);
        return root;
    }

    private static Canvas BuildBeacon(BeaconItem beacon)
    {
        Brush markerBrush = MarkerBrush(beacon.Color);

        var root = new Canvas();
        Canvas.SetLeft(root, beacon.ScreenX);
        Canvas.SetTop(root, beacon.ScreenY);

        // Halo diamond under the pin for contrast over busy footage.
        var halo = new Polygon
        {
            Points = new PointCollection
            {
                new Point(0, -9),
                new Point(9, 0),
                new Point(0, 9),
                new Point(-9, 0),
            },
            Fill = Solid("#26FFFFFF"),
            RenderTransform = new TranslateTransform(0, 0),
        };
        root.Children.Add(halo);

        // Pin: a filled circle with a dark outline, centered on the anchor.
        root.Children.Add(new Ellipse
        {
            Width = BeaconDotRadius * 2,
            Height = BeaconDotRadius * 2,
            Fill = markerBrush,
            Stroke = OutlineBrush,
            StrokeThickness = 1,
            RenderTransform = new TranslateTransform(-BeaconDotRadius, -BeaconDotRadius),
        });

        // Label above the pin.
        var label = new Border
        {
            Background = PanelGlass,
            BorderBrush = PanelBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5, 1, 5, 1),
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
        Canvas.SetTop(label, -BeaconDotRadius - 18);
        root.Children.Add(label);

        return root;
    }

    private static Border BuildNameplate(
        NameplateItem item,
        double ghostFraction,
        double? lowHpPulseAlpha,
        double viewportWidth,
        double viewportHeight)
    {
        Rect rect = AnchorRect(item.ScreenX, item.ScreenY, viewportWidth, viewportHeight);
        Brush teamBrush = item.Alive
            ? (item.TeamNumber == 1 ? Team1Brush : item.TeamNumber == 2 ? Team2Brush : NeutralBrush)
            : DeadBrush;

        var panel = new Border
        {
            Width = NameplateWidth,
            Background = PanelGlass,
            BorderBrush = PanelBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };
        Canvas.SetLeft(panel, rect.Left);
        Canvas.SetTop(panel, rect.Top);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2.5) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Child = grid;

        // Team-color accent strip across the top edge (grey when destroyed).
        grid.Children.Add(new Border
        {
            Height = 2.5,
            Background = teamBrush,
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        });

        var content = new StackPanel
        {
            Margin = new Thickness(5, 3, 5, 4),
        };
        Grid.SetRow(content, 1);
        grid.Children.Add(content);

        // Facing arrow: a screen-space heading (0 = away from the viewer)
        // drawn above the label, rotated to the tank's hull direction. No
        // arrow when the heading is unknown (no packet rotation evidence or
        // a facing that projects to a single pixel).
        if (item.ScreenHeadingDegrees is double heading && double.IsFinite(heading))
        {
            content.Children.Add(BuildHeadingArrow(heading, teamBrush));
        }

        content.Children.Add(new TextBlock
        {
            Text = item.Label,
            FontSize = LabelFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = teamBrush,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextDecorations = item.Alive ? null : TextDecorations.Strikethrough,
            MaxWidth = NameplateWidth - 12,
        });

        // HP bar: fraction-scaled fill, color by health, gradient-filled.
        // The fill is sized against the track's inner area (border inset), so
        // a full-health bar reaches the inner edge without overflowing and a
        // NaN fraction from a malformed frame degrades to an empty bar.
        var hpTrack = new Border
        {
            Width = HpBarWidth,
            Height = HpBarHeight,
            Background = HpTrackBrush,
            BorderBrush = HpTrackBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        };
        double hpFraction = double.IsFinite(item.HpFraction)
            ? Math.Clamp(item.HpFraction, 0, 1)
            : 0;
        double innerWidth = Math.Max(0, HpBarWidth - 2);
        var hpTrackGrid = new Grid();

        // Damage ghost: the pale trail behind the live fill shows the health
        // that was just lost, easing down each frame. Suppressed on destroyed
        // tanks (their plate is greyscaled, so a ghost would read as noise).
        double ghost = double.IsFinite(ghostFraction) ? Math.Clamp(ghostFraction, 0, 1) : 0;
        if (item.Alive && ghost > hpFraction)
        {
            hpTrackGrid.Children.Add(new Border
            {
                Width = innerWidth * ghost,
                Height = Math.Max(0, HpBarHeight - 2),
                Background = HpGhostBrush,
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            });
        }

        var hpFill = new Border
        {
            Width = innerWidth * hpFraction,
            Height = Math.Max(0, HpBarHeight - 2),
            Background = item.Alive ? HpColor(hpFraction) : DeadBrush,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        hpTrackGrid.Children.Add(hpFill);

        // Killable-target cue: an alive tank in the low-HP band gets a soft
        // red outline over the HP bar whose opacity pulses each frame. The
        // pulse is computed by the owner and passed in, so this builder stays
        // pure layout (and the effect survives the clear-and-rebuild model).
        if (lowHpPulseAlpha is double pulseAlpha)
        {
            hpTrackGrid.Children.Add(new Border
            {
                BorderBrush = LowHpGlowBrush,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(2),
                Opacity = Math.Clamp(pulseAlpha, 0, 1),
            });
        }

        hpTrack.Child = hpTrackGrid;
        content.Children.Add(hpTrack);

        // Muted meta line: exact HP (when known), range, damage, kills.
        content.Children.Add(new TextBlock
        {
            Text = NameplateMetaLabel(
                item.DistanceMeters,
                item.MaxHealth,
                item.CurrentHealth,
                item.DamageDealt,
                item.Kills),
            FontSize = MetaFontSize,
            Foreground = MutedBrush,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = NameplateWidth - 12,
            Margin = new Thickness(0, 2, 0, 0),
        });

        return panel;
    }

    /// <summary>
    /// Rise-and-fade progress for a combat pip: a linear fade-out combined
    /// with an ease-out vertical rise, parameterized by the pip's age in
    /// frames. Pure for unit tests; age is tracked per rendered frame so the
    /// effect survives the HUD's clear-and-rebuild render model.
    /// </summary>
    public static (double RiseOffset, double Opacity) PipAnimation(int ageFrames, int durationFrames)
    {
        if (durationFrames <= 0)
        {
            return (0, 0);
        }

        double t = Math.Clamp((double)ageFrames / durationFrames, 0, 1);
        double eased = 1.0 - ((1.0 - t) * (1.0 - t));
        return (PipRisePixels * eased, 1.0 - t);
    }

    /// <summary>
    /// Pulse intensity for the low-HP killable-target cue: a triangle wave
    /// between full (1.0) and <see cref="LowHpPulseMinAlpha"/>, so the red
    /// edge breathes instead of blinking harshly. Pure for unit tests.
    /// </summary>
    public static double LowHpPulseAlpha(int ageFrames, int periodFrames)
    {
        if (periodFrames <= 0)
        {
            return 1.0;
        }

        int frame = ageFrames % periodFrames;
        if (frame < 0)
        {
            frame += periodFrames;
        }

        double t = (double)frame / periodFrames;
        double triangle = 1.0 - (2.0 * Math.Abs(t - 0.5));
        return LowHpPulseMinAlpha + ((1.0 - LowHpPulseMinAlpha) * (1.0 - triangle));
    }

    /// <summary>
    /// One step of the HP damage-ghost easing: after a hit the ghost lags
    /// above the live fill and eases down toward it; on heal/regen it snaps
    /// forward so the ghost never trails *behind* the live bar. Finite-safe
    /// and pure for unit tests.
    /// </summary>
    public static double HpGhostEase(double ghost, double target)
    {
        double g = double.IsFinite(ghost) ? ghost : 0;
        double t = double.IsFinite(target) ? Math.Clamp(target, 0, 1) : 0;
        if (t >= g)
        {
            return t;
        }

        double next = g + ((t - g) * HpGhostEaseRate);
        return Math.Abs(next - t) < HpGhostSnapThreshold ? t : next;
    }

    /// <summary>
    /// Slide-and-fade progress for a kill-feed entry: the newest entry slides
    /// in from the left and fades up, easing out; older entries settle to
    /// their full position and opacity. Pure for unit tests.
    /// </summary>
    public static (double SlideOffset, double Opacity) FeedEntryAnimation(int ageFrames, int durationFrames)
    {
        if (durationFrames <= 0)
        {
            return (0, 1);
        }

        double t = Math.Clamp((double)ageFrames / durationFrames, 0, 1);
        double eased = 1.0 - ((1.0 - t) * (1.0 - t));
        return (-FeedSlidePixels * (1.0 - eased), eased);
    }

    /// <summary>
    /// Pulse scale for the pen badge on a verdict change: a brief overshoot
    /// that eases back to 1.0. Pure for unit tests; a settled badge (age at
    /// or past the pulse window) stays at full size.
    /// </summary>
    public static double PenPulseScale(int ageFrames, int durationFrames)
    {
        if (durationFrames <= 0)
        {
            return 1.0;
        }

        double t = Math.Clamp((double)ageFrames / durationFrames, 0, 1);
        double eased = 1.0 - ((1.0 - t) * (1.0 - t));
        return 1.0 + (PenPulseOvershoot * (1.0 - eased));
    }

    private static Border BuildPip(PipItem pip, int ageFrames)
    {
        // Damage pips read "+N", death pips read "✕" (a dark skull-like
        // marker); both rise and fade above the affected tank's anchor.
        bool isDamage = string.Equals(pip.Kind, "Damage", StringComparison.Ordinal);
        string text = isDamage ? $"+{pip.Damage}" : "\u2716";
        Brush brush = isDamage ? PipDamageBrush : PipKillBrush;
        (double riseOffset, double opacity) = PipAnimation(ageFrames, PipAnimationFrames);

        var pipBorder = new Border
        {
            Background = PanelGlass,
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5, 1, 5, 1),
            Opacity = opacity,
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
        Canvas.SetTop(
            pipBorder,
            pip.ScreenY - pipBorder.DesiredSize.Height - 4 - riseOffset);
        return pipBorder;
    }

    private static Canvas BuildHeadingArrow(double headingDegrees, Brush teamBrush)
    {
        // Arrow drawn pointing UP (away from the viewer) at 0 degrees;
        // RotateTransform turns it to the tank's screen-space hull heading
        // (positive = clockwise, matching the packet's yaw convention).
        var canvas = new Canvas
        {
            Width = NameplateWidth - 12,
            Height = HeadingArrowLength + 2,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(headingDegrees),
        };
        var arrow = new Polygon
        {
            Points = new PointCollection
            {
                new Point(canvas.Width / 2.0, 0),
                new Point(canvas.Width / 2.0 - HeadingArrowHalfWidth, HeadingArrowLength),
                new Point(canvas.Width / 2.0 + HeadingArrowHalfWidth, HeadingArrowLength),
            },
            Fill = teamBrush,
            Stroke = OutlineBrush,
            StrokeThickness = 0.75,
        };
        canvas.Children.Add(arrow);
        return canvas;
    }

    private static Brush HpColor(double fraction) =>
        fraction > 0.5 ? HpGoodBrush : fraction > 0.25 ? HpMidBrush : HpLowBrush;

    // ── Frozen paint helpers (zero per-frame resource churn) ──

    /// <summary>
    /// Parses an HTML-style hex color for a beacon/marker, returning null for
    /// null, empty, or malformed input — the fail-closed marker-color
    /// contract, unit-tested.
    /// </summary>
    internal static Color? ResolveMarkerColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        try
        {
            return ColorOf(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a beacon/marker brush with a fail-closed fallback: a hostile
    /// or corrupt frame renders neutral instead of crashing the HUD.
    /// </summary>
    private static Brush MarkerBrush(string? hex) =>
        ResolveMarkerColor(hex) is Color color
            ? Freeze(new SolidColorBrush(color))
            : NeutralBrush;

    private static Color ColorOf(string hex) =>
        (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Solid(string hex) =>
        Freeze(new SolidColorBrush(ColorOf(hex)));

    private static LinearGradientBrush VerticalGradient(string topHex, string bottomHex)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        brush.GradientStops.Add(new GradientStop(ColorOf(topHex), 0));
        brush.GradientStops.Add(new GradientStop(ColorOf(bottomHex), 1));
        return Freeze(brush);
    }

    private static RadialGradientBrush RadialVignette()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.8,
            RadiusY = 0.8,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(96, 0, 0, 0), 1.0));
        return Freeze(brush);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
