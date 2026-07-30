using System.Windows;
using System.Windows.Media;

namespace WotBTreader.Overlay.Views;

/// <summary>
/// Lightweight WPF renderer that draws position dots and velocity trails
/// directly via <see cref="DrawingContext"/>, bypassing the layout engine.
/// All brushes and pens are frozen at static init time — zero per-frame GC.
/// </summary>
public sealed class FastPlotRenderer : FrameworkElement
{
    private static readonly Brush Team1Dot = Freeze(new SolidColorBrush(Color.FromRgb(40, 160, 255)));
    private static readonly Brush Team2Dot = Freeze(new SolidColorBrush(Color.FromRgb(255, 80, 20)));
    private static readonly Brush UnknownDot = Freeze(new SolidColorBrush(Colors.Gray));
    private static readonly Brush LivePlayerDot = Freeze(new SolidColorBrush(Color.FromRgb(0, 255, 100)));

    private static readonly Brush Team1Glow = Freeze(new SolidColorBrush(Color.FromArgb(40, 40, 160, 255)));
    private static readonly Brush Team2Glow = Freeze(new SolidColorBrush(Color.FromArgb(40, 255, 80, 20)));
    private static readonly Brush UnknownGlow = Freeze(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)));
    private static readonly Brush LivePlayerGlow = Freeze(new SolidColorBrush(Color.FromArgb(80, 0, 255, 100)));

    private static readonly Pen Team1Trail = Freeze(new Pen(
        new SolidColorBrush(Color.FromRgb(40, 160, 255)), 1.0));
    private static readonly Pen Team2Trail = Freeze(new Pen(
        new SolidColorBrush(Color.FromRgb(255, 80, 20)), 1.0));
    private static readonly Pen UnknownTrail = Freeze(new Pen(
        new SolidColorBrush(Colors.Gray), 1.0));
    private static readonly Pen LivePlayerTrailPen = Freeze(new Pen(
        new SolidColorBrush(Color.FromRgb(0, 255, 100)), 2.0));

    /// <summary>Team number used for live player rendering (green).</summary>
    public const int LivePlayerTeamNumber = 9;

    /// <summary>Trail segments to draw. Cleared and repopulated each frame.</summary>
    public List<RenderLine> LinesToDraw { get; } = new();

    /// <summary>Position dots to draw. Cleared and repopulated each frame.</summary>
    public List<RenderDot> DotsToDraw { get; } = new();

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        foreach (RenderLine line in LinesToDraw)
        {
            drawingContext.PushOpacity(line.Opacity);
            drawingContext.DrawLine(SelectTrailPen(line.TeamNumber), line.P1, line.P2);
            drawingContext.Pop();
        }

        foreach (RenderDot dot in DotsToDraw)
        {
            bool isLive = dot.TeamNumber == LivePlayerTeamNumber;
            // Halation glow — larger semi-transparent circle behind the dot.
            double glowRadius = isLive ? 7.0 : 3.5;
            drawingContext.DrawEllipse(SelectGlowBrush(dot.TeamNumber), null, dot.Location, glowRadius, glowRadius);
            // Core dot.
            double dotRadius = isLive ? 4.0 : 2.2;
            drawingContext.DrawEllipse(SelectDotBrush(dot.TeamNumber), null, dot.Location, dotRadius, dotRadius);
        }
    }

    private static Pen SelectTrailPen(int teamNumber) => teamNumber switch
    {
        1 => Team1Trail,
        2 => Team2Trail,
        LivePlayerTeamNumber => LivePlayerTrailPen,
        _ => UnknownTrail,
    };

    private static Brush SelectGlowBrush(int teamNumber) => teamNumber switch
    {
        1 => Team1Glow,
        2 => Team2Glow,
        LivePlayerTeamNumber => LivePlayerGlow,
        _ => UnknownGlow,
    };

    private static Brush SelectDotBrush(int teamNumber) => teamNumber switch
    {
        1 => Team1Dot,
        2 => Team2Dot,
        LivePlayerTeamNumber => LivePlayerDot,
        _ => UnknownDot,
    };

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>Lightweight trail segment for DrawingContext rendering.</summary>
public readonly record struct RenderLine(Point P1, Point P2, int TeamNumber, double Opacity);

/// <summary>Lightweight position dot for DrawingContext rendering.</summary>
public readonly record struct RenderDot(Point Location, int TeamNumber);
