using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    }

    /// <summary>Replaces the HUD contents with the given nameplates.</summary>
    public void Render(IReadOnlyList<NameplateItem> items, double viewportWidth, double viewportHeight)
    {
        HudCanvas.Children.Clear();
        foreach (NameplateItem item in items)
        {
            HudCanvas.Children.Add(BuildNameplate(item, viewportWidth, viewportHeight));
        }
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

    private static Brush HpColor(double fraction) =>
        fraction > 0.5 ? HpGoodBrush : fraction > 0.25 ? HpMidBrush : HpLowBrush;

    private static SolidColorBrush CreateBrush(string hex)
    {
        Color color = (Color)ColorConverter.ConvertFromString(hex);
        return new SolidColorBrush(color);
    }
}
