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
    private static readonly Brush Team1Dot = Freeze(new SolidColorBrush(Color.FromRgb(30, 144, 255)));
    private static readonly Brush Team2Dot = Freeze(new SolidColorBrush(Color.FromRgb(255, 69, 0)));
    private static readonly Brush UnknownDot = Freeze(new SolidColorBrush(Colors.Gray));

    private static readonly Pen Team1Trail = Freeze(new Pen(
        new SolidColorBrush(Color.FromRgb(30, 144, 255)), 1.2));
    private static readonly Pen Team2Trail = Freeze(new Pen(
        new SolidColorBrush(Color.FromRgb(255, 69, 0)), 1.2));
    private static readonly Pen UnknownTrail = Freeze(new Pen(
        new SolidColorBrush(Colors.Gray), 1.2));

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
            drawingContext.DrawEllipse(SelectDotBrush(dot.TeamNumber), null, dot.Location, 1.5, 1.5);
        }
    }

    private static Pen SelectTrailPen(int teamNumber) => teamNumber switch
    {
        1 => Team1Trail,
        2 => Team2Trail,
        _ => UnknownTrail,
    };

    private static Brush SelectDotBrush(int teamNumber) => teamNumber switch
    {
        1 => Team1Dot,
        2 => Team2Dot,
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
