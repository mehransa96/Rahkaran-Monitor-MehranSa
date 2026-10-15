using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RSM.App.Localization;
using RSM.App.ViewModels;
using RSM.Core.Live;

namespace RSM.App.Views;

/// <summary>One plotted series: a metric key and instance (or every instance of the key).</summary>
public sealed record ChartSeries(string Key, string? Instance, string LabelKey, string Color, bool AllInstances = false);

/// <summary>A threshold drawn as a dashed line: value taken from a loaded rule parameter, so team overrides apply.</summary>
public sealed record ChartThreshold(string RuleId, string Param, bool Critical);

/// <summary>What a chart shows; built by the page view-models.</summary>
public sealed record ChartSpec(string TitleKey, IReadOnlyList<ChartSeries> Series, IReadOnlyList<ChartThreshold> Thresholds, double? FixedMax = null);

public sealed record LegendItem(string Label, Brush Brush);

/// <summary>
/// Draws the selected node's series for the chosen window (15/30/60 min) from the live metric store. Redraws only when
/// visible; never interacts with a server.
/// </summary>
public partial class LiveChart : UserControl
{
    private static readonly string[] Palette = ["#2F6FEB", "#CF6A2E", "#1A7F37", "#8250DF", "#BF3989", "#0E8A8A", "#9A6700", "#57606A"];

    public static readonly DependencyProperty SpecProperty =
        DependencyProperty.Register(nameof(Spec), typeof(ChartSpec), typeof(LiveChart), new PropertyMetadata(null, (d, _) => ((LiveChart)d).Redraw()));

    public static readonly DependencyProperty LiveProperty =
        DependencyProperty.Register(nameof(Live), typeof(LiveViewModel), typeof(LiveChart), new PropertyMetadata(null, OnLiveChanged));

    public static readonly DependencyProperty ChartHeightProperty =
        DependencyProperty.Register(nameof(ChartHeight), typeof(double), typeof(LiveChart), new PropertyMetadata(180d));

    public LiveChart()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Redraw();
        Loc.Instance.PropertyChanged += (_, _) => Redraw();
    }

    public ChartSpec? Spec
    {
        get => (ChartSpec?)GetValue(SpecProperty);
        set => SetValue(SpecProperty, value);
    }

    public LiveViewModel? Live
    {
        get => (LiveViewModel?)GetValue(LiveProperty);
        set => SetValue(LiveProperty, value);
    }

    public double ChartHeight
    {
        get => (double)GetValue(ChartHeightProperty);
        set => SetValue(ChartHeightProperty, value);
    }

    private static void OnLiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (LiveChart)d;
        if (e.OldValue is LiveViewModel old)
        {
            old.Ticked -= chart.OnTicked;
        }

        if (e.NewValue is LiveViewModel live)
        {
            live.Ticked += chart.OnTicked;
        }

        chart.Redraw();
    }

    private void OnTicked(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        if (Spec is not { } spec || !IsVisible)
        {
            return;
        }

        TitleText.Text = Loc.Instance[spec.TitleKey];
        var plot = Plot.Plot;
        plot.Clear();
        var legend = new List<LegendItem>();
        var session = Live?.Session;
        var nodeId = Live?.NodeId;
        var since = DateTimeOffset.UtcNow - (Live?.Window ?? TimeSpan.FromMinutes(15));
        double max = 0;
        var colorIndex = 0;
        if (session is not null && nodeId is not null)
        {
            foreach (var series in spec.Series)
            {
                var instances = series.AllInstances ? session.Metrics.Instances(nodeId, series.Key).Where(i => i is not null && i != "_Total").ToList() : [series.Instance];
                foreach (var instance in instances)
                {
                    var points = session.Metrics.Series(nodeId, series.Key, instance, since);
                    if (points.Count == 0)
                    {
                        continue;
                    }

                    var hex = series.AllInstances ? Palette[colorIndex++ % Palette.Length] : series.Color;
                    var line = plot.Add.Scatter(points.Select(p => p.AtUtc.LocalDateTime.ToOADate()).ToArray(), points.Select(p => p.Value).ToArray());
                    line.Color = ScottPlot.Color.FromHex(hex);
                    line.LineWidth = 2;
                    line.MarkerSize = 0;
                    max = Math.Max(max, points.Max(p => p.Value));
                    var label = Loc.Instance[series.LabelKey];
                    legend.Add(new LegendItem(series.AllInstances ? $"{label}: {instance}" : label, (Brush)new BrushConverter().ConvertFromString(hex)!));
                }
            }

            foreach (var threshold in spec.Thresholds)
            {
                if (session.RuleParam(threshold.RuleId, threshold.Param) is not { } value)
                {
                    continue;
                }

                var hex = threshold.Critical ? "#CF222E" : "#9A6700";
                var hl = plot.Add.HorizontalLine(value);
                hl.Color = ScottPlot.Color.FromHex(hex).WithAlpha(0.8);
                hl.LineWidth = 1;
                hl.LinePattern = ScottPlot.LinePattern.Dashed;
                max = Math.Max(max, value);
                legend.Add(new LegendItem(Loc.Instance.Format(threshold.Critical ? "Chart_Threshold_Critical" : "Chart_Threshold_Warning", threshold.RuleId, value), (Brush)new BrushConverter().ConvertFromString(hex)!));
            }
        }

        plot.Axes.DateTimeTicksBottom();
        plot.Axes.SetLimitsX(since.LocalDateTime.ToOADate(), DateTimeOffset.UtcNow.LocalDateTime.ToOADate());
        plot.Axes.SetLimitsY(0, spec.FixedMax ?? (max <= 0 ? 1 : max * 1.15));
        Plot.Refresh();
        Legend.ItemsSource = legend;
        EmptyText.Text = Loc.Instance["Chart_NoData"];
        EmptyText.Visibility = legend.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
