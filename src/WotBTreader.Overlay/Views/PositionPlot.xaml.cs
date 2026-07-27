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
