using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay.Views;

/// <summary>
/// Canvas-backed scatter plot of position samples for the selected battle session.
/// All coordinate math goes through <see cref="PlotTransform"/>.
/// When world boundary properties are set (non-zero extent), positions are
/// normalised against the full map extent for accurate minimap overlay.
/// </summary>
public sealed partial class PositionPlot : UserControl
{
    private const double PlotPadding = 8;
    private const int MaxRenderedPoints = 2000;

    public static readonly DependencyProperty PointsSourceProperty =
        DependencyProperty.Register(
            nameof(PointsSource),
            typeof(IEnumerable),
            typeof(PositionPlot),
            new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty WorldMinXProperty =
        DependencyProperty.Register(
            nameof(WorldMinX),
            typeof(double),
            typeof(PositionPlot),
            new PropertyMetadata(double.NaN, OnVisualChanged));

    public static readonly DependencyProperty WorldMaxXProperty =
        DependencyProperty.Register(
            nameof(WorldMaxX),
            typeof(double),
            typeof(PositionPlot),
            new PropertyMetadata(double.NaN, OnVisualChanged));

    public static readonly DependencyProperty WorldMinZProperty =
        DependencyProperty.Register(
            nameof(WorldMinZ),
            typeof(double),
            typeof(PositionPlot),
            new PropertyMetadata(double.NaN, OnVisualChanged));

    public static readonly DependencyProperty WorldMaxZProperty =
        DependencyProperty.Register(
            nameof(WorldMaxZ),
            typeof(double),
            typeof(PositionPlot),
            new PropertyMetadata(double.NaN, OnVisualChanged));

    public PositionPlot()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    public IEnumerable? PointsSource
    {
        get => (IEnumerable?)GetValue(PointsSourceProperty);
        set => SetValue(PointsSourceProperty, value);
    }

    public double WorldMinX
    {
        get => (double)GetValue(WorldMinXProperty);
        set => SetValue(WorldMinXProperty, value);
    }

    public double WorldMaxX
    {
        get => (double)GetValue(WorldMaxXProperty);
        set => SetValue(WorldMaxXProperty, value);
    }

    public double WorldMinZ
    {
        get => (double)GetValue(WorldMinZProperty);
        set => SetValue(WorldMinZProperty, value);
    }

    public double WorldMaxZ
    {
        get => (double)GetValue(WorldMaxZProperty);
        set => SetValue(WorldMaxZProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((PositionPlot)d).Redraw();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Redraw();
    }

    private void Redraw()
    {
        PlotCanvas.Children.Clear();

        if (PointsSource is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        List<PlotPoint> points = new();
        foreach (object? item in PointsSource)
        {
            if (item is PlotPoint point)
            {
                points.Add(point);
                if (points.Count >= MaxRenderedPoints)
                {
                    break;
                }
            }
        }

        double wMinX = WorldMinX;
        double wMaxX = WorldMaxX;
        double wMinZ = WorldMinZ;
        double wMaxZ = WorldMaxZ;

        bool hasBounds = !double.IsNaN(wMinX) && !double.IsNaN(wMaxX)
            && !double.IsNaN(wMinZ) && !double.IsNaN(wMaxZ)
            && wMaxX > wMinX && wMaxZ > wMinZ;

        IReadOnlyList<(double X, double Y, int TeamNumber)> fitted =
            PlotTransform.Fit(
                points, ActualWidth, ActualHeight, PlotPadding,
                hasBounds ? wMinX : null,
                hasBounds ? wMaxX : null,
                hasBounds ? wMinZ : null,
                hasBounds ? wMaxZ : null);

        // ── Draw velocity trails (fading lines per participant) ──
        // Group all fitted coordinates by participant, preserving insertion order.
        // This connects every position of a single tank chronologically, even when
        // positions from other tanks are interleaved in the data stream — each
        // participant's trail is an independent path through time.
        const int maxTrailSegments = 100;
        Dictionary<string, (List<(double X, double Y)> Coords, int TeamNumber)> groups = new();
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].ParticipantId is string pid)
            {
                if (!groups.TryGetValue(pid, out var group))
                {
                    group = (new List<(double X, double Y)>(), points[i].TeamNumber);
                    groups[pid] = group;
                }

                group.Coords.Add((fitted[i].X, fitted[i].Y));
            }
        }

        foreach (var (_, (coords, teamNumber)) in groups)
        {
            int totalSegments = coords.Count - 1;
            if (totalSegments <= 0) continue;

            Color teamColor = teamNumber switch
            {
                1 => Colors.DodgerBlue,
                2 => Colors.OrangeRed,
                _ => Colors.Gray,
            };

            // Cap trail segments to keep element count bounded.
            int segmentCount = Math.Min(totalSegments, maxTrailSegments);
            int skip = totalSegments > maxTrailSegments ? totalSegments / maxTrailSegments : 1;
            int drawn = 0;
            for (int i = 0; i < totalSegments && drawn < segmentCount; i += skip)
            {
                // Fade from 0.18 (oldest) to 0.85 (newest)
                double t = totalSegments == 1 ? 1.0 : (double)i / totalSegments;
                byte alpha = (byte)(30 + (int)(t * 187));
                drawn++;

                Line line = new()
                {
                    X1 = coords[i].X,
                    Y1 = coords[i].Y,
                    X2 = coords[i + 1].X,
                    Y2 = coords[i + 1].Y,
                    Stroke = new SolidColorBrush(Color.FromArgb(alpha, teamColor.R, teamColor.G, teamColor.B)),
                    StrokeThickness = 1.2,
                };
                PlotCanvas.Children.Add(line);
            }
        }

        // ── Draw position dots on top of trails ──
        foreach ((double x, double y, int teamNumber) in fitted)
        {
            Ellipse dot = new()
            {
                Width = 3,
                Height = 3,
                Fill = teamNumber switch
                {
                    1 => Brushes.DodgerBlue,
                    2 => Brushes.OrangeRed,
                    _ => Brushes.Gray,
                },
            };
            Canvas.SetLeft(dot, x);
            Canvas.SetTop(dot, y);
            PlotCanvas.Children.Add(dot);
        }
    }
}
