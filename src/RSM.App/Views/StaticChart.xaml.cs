using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RSM.App.Localization;

namespace RSM.App.Views;

/// <summary>One line of a static chart (already labelled in the UI language).</summary>
public sealed record StaticLine(string Label, string Color, IReadOnlyList<(DateTimeOffset AtUtc, double Value)> Points, bool Dashed = false);

/// <summary>What a static chart shows: lines, shaded bands (anomalies, capture window) and the X range shared with sibling charts.</summary>
public sealed record StaticChartData(string Title, IReadOnlyList<StaticLine> Lines, IReadOnlyList<(DateTimeOffset FromUtc, DateTimeOffset ToUtc)> Bands,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc);

/// <summary>Draws a <see cref="StaticChartData"/>; times in local time; never touches a server.</summary>
public partial class StaticChart : UserControl
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(StaticChartData), typeof(StaticChart), new PropertyMetadata(null, (d, _) => ((StaticChart)d).Redraw()));

    public static readonly DependencyProperty ChartHeightProperty =
        DependencyProperty.Register(nameof(ChartHeight), typeof(double), typeof(StaticChart), new PropertyMetadata(200d));

    public StaticChart()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Redraw();
    }

    public StaticChartData? Data
    {
        get => (StaticChartData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double ChartHeight
    {
        get => (double)GetValue(ChartHeightProperty);
        set => SetValue(ChartHeightProperty, value);
    }

    private void Redraw()
    {
        if (Data is not { } data || !IsVisible)
        {
            return;
        }

        TitleText.Text = data.Title;
        var plot = Plot.Plot;
        plot.Clear();
        var legend = new List<LegendItem>();
        var max = 0d;
        foreach (var band in data.Bands)
        {
            var span = plot.Add.HorizontalSpan(band.FromUtc.LocalDateTime.ToOADate(), band.ToUtc.LocalDateTime.ToOADate());
            span.FillStyle.Color = ScottPlot.Color.FromHex("#CF222E").WithAlpha(0.15);
            span.LineStyle.Width = 0;
        }

        if (data.Bands.Count > 0)
        {
            legend.Add(new LegendItem(Loc.Instance["Chart_AnomalyBand"], new SolidColorBrush(Color.FromArgb(60, 0xCF, 0x22, 0x2E))));
        }

        foreach (var line in data.Lines.Where(l => l.Points.Count > 0))
        {
            var points = line.Points.OrderBy(p => p.AtUtc).ToList();
            var scatter = plot.Add.Scatter(points.Select(p => p.AtUtc.LocalDateTime.ToOADate()).ToArray(), points.Select(p => p.Value).ToArray());
            scatter.Color = ScottPlot.Color.FromHex(line.Color);
            scatter.LineWidth = line.Dashed ? 1 : 2;
            scatter.MarkerSize = points.Count == 1 ? 5 : 0;
            if (line.Dashed)
            {
                scatter.LinePattern = ScottPlot.LinePattern.Dashed;
            }

            max = Math.Max(max, points.Max(p => p.Value));
            legend.Add(new LegendItem(line.Label, (Brush)new BrushConverter().ConvertFromString(line.Color)!));
        }

        plot.Axes.DateTimeTicksBottom();
        plot.Axes.SetLimitsX(data.FromUtc.LocalDateTime.ToOADate(), data.ToUtc.LocalDateTime.ToOADate());
        plot.Axes.SetLimitsY(0, max <= 0 ? 1 : max * 1.15);
        Plot.Refresh();
        Legend.ItemsSource = legend;
        EmptyText.Text = Loc.Instance["Chart_NoData"];
        EmptyText.Visibility = data.Lines.All(l => l.Points.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
    }
}
