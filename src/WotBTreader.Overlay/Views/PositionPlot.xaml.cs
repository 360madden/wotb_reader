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
            new PropertyMetadata(null, OnPointsSourceChanged));

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

    private static void OnPointsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
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

        IReadOnlyList<(double X, double Y, int TeamNumber)> fitted =
            PlotTransform.Fit(points, ActualWidth, ActualHeight, PlotPadding);

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
