using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RSM.App.Localization;
using RSM.App.Views;
using RSM.Core.History;
using RSM.Core.Model;
using RSM.Core.Slowness;
using RSM.Hosting;
using RSM.Storage.History;
using RSM.Storage.Investigations;

namespace RSM.App.ViewModels;

public sealed record EvidenceRowViewModel(string Detector, string Node, string Instance, string Value, string Strength, string Time);

/// <summary>One ranked cause of an investigation, in the current UI language.</summary>
public sealed record CauseViewModel(
    int Rank,
    string Id,
    string Title,
    string Confidence,
    double ConfidenceValue,
    string Explanation,
    IReadOnlyList<EvidenceRowViewModel> Evidence,
    IReadOnlyList<string> Context,
    IReadOnlyList<string> NextSteps,
    string GuidanceLabel)
{
    public string Header => $"{Rank}. {Title} — {Confidence}";
}

public sealed record InvestigationRowViewModel(string CaptureId, string Started, string Site, string TopCause);

/// <summary>
/// §12 "Why is Rahkaran slow?": a synchronized capture over the running live view, then ranked causes with evidence, a
/// shared timeline and text-only next steps. Starting a capture only reads more often for a short time; nothing acts on
/// a server.
/// </summary>
public sealed partial class SlownessPageViewModel : LivePageViewModel
{
    private readonly SlownessInvestigationService _service;
    private readonly InvestigationStore _store;
    private readonly ReportService _reports;
    private readonly StatusBarViewModel _status;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private SavedInvestigation? _current;

    public SlownessPageViewModel(LiveViewModel live, SlownessInvestigationService service, InvestigationStore store, ReportService reports, StatusBarViewModel status, ILogger logger)
        : base(live, "Nav_Slowness")
    {
        _service = service;
        _store = store;
        _reports = reports;
        _status = status;
        _logger = logger;
        BuildOptions();
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            BuildOptions();
            Show(_current);
        };
        _ = LoadPastAsync();
    }

    public ObservableCollection<ChoiceOption> DurationOptions { get; } = [];

    public ObservableCollection<CauseViewModel> Causes { get; } = [];

    public ObservableCollection<StaticChartData> Timeline { get; } = [];

    public ObservableCollection<InvestigationRowViewModel> Past { get; } = [];

    [ObservableProperty]
    private ChoiceOption? _selectedDuration;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InvestigateCommand), nameof(CancelCommand))]
    private bool _isCapturing;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private CauseViewModel? _selectedCause;

    [ObservableProperty]
    private InvestigationRowViewModel? _selectedPast;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _hasResult;

    private void BuildOptions()
    {
        var selected = SelectedDuration?.Value ?? 120;
        DurationOptions.Clear();
        foreach (var seconds in new[] { 60, 120, 300 })
        {
            DurationOptions.Add(new ChoiceOption(seconds, Loc.Instance.Format("Duration_Seconds", seconds)));
        }

        SelectedDuration = DurationOptions.First(o => o.Value.Equals(selected));
    }

    protected override void Rebuild()
    {
        InvestigateCommand.NotifyCanExecuteChanged();
        if (!IsCapturing && Causes.Count == 0)
        {
            Message = Live.IsConnected ? Loc.Instance["Slow_Ready"] : Loc.Instance["Slow_NeedsLive"];
        }
    }

    private bool CanInvestigate() => Live.IsConnected && !IsCapturing;

    [RelayCommand(CanExecute = nameof(CanInvestigate))]
    private async Task InvestigateAsync()
    {
        IsCapturing = true;
        Progress = 0;
        _cts = new CancellationTokenSource();
        var duration = TimeSpan.FromSeconds(SelectedDuration?.Value ?? 120);
        Message = Loc.Instance.Format("Slow_Capturing", (int)duration.TotalSeconds);
        try
        {
            var saved = await _service.RunAsync(duration, new Progress<double>(p => Progress = p * 100), _cts.Token);
            Show(saved);
            await LoadPastAsync();
            _status.Show(Loc.Instance.Format("Slow_Done", saved.Result.Causes.Count));
        }
        catch (OperationCanceledException)
        {
            Message = Loc.Instance["Diag_Cancelled"];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Slowness investigation failed");
            Message = Loc.Instance.Format("Slow_Failed", ex.Message);
        }
        finally
        {
            IsCapturing = false;
        }
    }

    private bool CanCancel() => IsCapturing;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task ExportAsync()
    {
        if (_current is not { } current)
        {
            return;
        }

        try
        {
            var path = await _reports.ExportInvestigationAsync(current, Loc.Instance.Language, CancellationToken.None);
            _status.Show(Loc.Instance.Format("Diag_Exported", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Show(Loc.Instance.Format("Status_Error", ex.Message));
        }
    }

    partial void OnSelectedCauseChanged(CauseViewModel? value) => BuildTimeline();

    partial void OnSelectedPastChanged(InvestigationRowViewModel? value)
    {
        if (value is not null && !IsCapturing)
        {
            _ = OpenAsync(value.CaptureId);
        }
    }

    private async Task OpenAsync(string captureId)
    {
        try
        {
            if (await _store.LoadJsonAsync(captureId, CancellationToken.None) is { } json && JsonSerializer.Deserialize<SavedInvestigation>(json, InvestigationStore.Json) is { } saved)
            {
                Show(saved);
            }
        }
        catch (JsonException ex)
        {
            Message = Loc.Instance.Format("Slow_Failed", ex.Message);
        }
    }

    private async Task LoadPastAsync()
    {
        var list = await _store.ListAsync(null, CancellationToken.None);
        Replace(Past, list.Take(50).Select(i => new InvestigationRowViewModel(i.CaptureId, RedisPageViewModel.LocalTime(i.FromUtc), i.SiteName,
            i.TopCauseId is { } c ? $"{c} ({(i.TopConfidence ?? 0).ToString("P0", Loc.Instance.Culture)})" : Loc.Instance["Slow_NoCauseShort"])));
    }

    private void Show(SavedInvestigation? saved)
    {
        _current = saved;
        HasResult = saved is not null;
        Causes.Clear();
        Timeline.Clear();
        if (saved is null)
        {
            return;
        }

        var fa = Loc.Instance.Language == UiLanguage.Fa;
        var nodes = saved.Capture.Nodes.ToDictionary(n => n.NodeId, n => n.NodeName);
        var labels = _service.Definitions.Detectors.ToDictionary(d => d.Id, d => fa ? d.Label.Fa : d.Label.En);
        var rank = 0;
        foreach (var c in saved.Result.Causes)
        {
            Causes.Add(new CauseViewModel(++rank, c.Cause.Id, fa ? c.Cause.Title.Fa : c.Cause.Title.En, c.Confidence.ToString("P0", Loc.Instance.Culture), c.Confidence,
                fa ? c.Cause.Explanation.Fa : c.Cause.Explanation.En,
                c.Evidence.Select(s => new EvidenceRowViewModel(labels.GetValueOrDefault(s.DetectorId, s.DetectorId), nodes.GetValueOrDefault(s.NodeId, s.NodeId), s.Instance ?? s.RuleId ?? string.Empty,
                    Text(s.Value), s.Strength.ToString("P0", Loc.Instance.Culture), s.AtUtc is { } t ? RedisPageViewModel.LocalTime(t) : string.Empty)).ToList(),
                c.Context.Select(x => $"{nodes.GetValueOrDefault(x.NodeId, x.NodeId)} — {x.Key}: {Text(x.Value)}").ToList(),
                c.Cause.NextSteps.Select(s => fa ? s.Fa : s.En).ToList(),
                Loc.Instance["General_Guidance_Label"]));
        }

        Message = saved.Result.Causes.Count == 0
            ? Loc.Instance["Slow_NoCause"]
            : Loc.Instance.Format("Slow_Result", saved.Capture.CaptureId, RedisPageViewModel.LocalTime(saved.Capture.FromUtc), saved.Result.Causes.Count);
        SelectedCause = Causes.FirstOrDefault();
        BuildTimeline();
    }

    /// <summary>Shared timeline: one chart per evidence series of the selected cause, all on the capture's X range.</summary>
    private void BuildTimeline()
    {
        Timeline.Clear();
        if (_current is not { } saved || SelectedCause is not { } selected || saved.Result.Causes.FirstOrDefault(c => c.Cause.Id == selected.Id) is not { } cause)
        {
            return;
        }

        var from = saved.Capture.FromUtc.AddSeconds(-30);
        var to = saved.Capture.ToUtc;
        var colors = new[] { "#2F6FEB", "#CF6A2E", "#1A7F37", "#8250DF" };
        foreach (var group in cause.Evidence.Where(s => s.Metric is not null).GroupBy(s => s.DetectorId))
        {
            var definition = _service.Definitions.Detectors.FirstOrDefault(d => d.Id == group.Key);
            var lines = group.Select((s, i) =>
            {
                var node = saved.Capture.Nodes.First(n => n.NodeId == s.NodeId);
                var points = node.Samples.Where(p => p.Key == s.Metric && p.Instance == s.Instance).Select(p => (p.AtUtc, p.Value)).ToList();
                return new StaticLine($"{node.NodeName} {s.Metric}{(s.Instance is null ? string.Empty : $" [{s.Instance}]")}", colors[i % colors.Length], points);
            }).ToList();
            var title = definition is null ? group.Key : Loc.Instance.Language == UiLanguage.Fa ? definition.Label.Fa : definition.Label.En;
            Timeline.Add(new StaticChartData(title, lines, [], from, to));
        }
    }
}

public sealed record SeriesOption(string Key, string? Instance)
{
    public string Label => Instance is null ? Key : $"{Key} [{Instance}]";
}

public sealed record AnomalyRowViewModel(string Node, string Metric, string From, string To, string Peak, string Limit, string Samples);

public sealed record CompareRowViewModel(string Metric, string AAvg, string BAvg, string AvgChange, string AP95, string BP95, string P95Change, bool Worse);

/// <summary>
/// §15 History, baseline &amp; comparison: local history (recorded while the live view runs) per node and metric, the
/// hour-of-week baseline with anomaly bands, and "this week vs last week" / "before vs after" comparisons.
/// </summary>
public sealed partial class HistoryPageViewModel : ObservableObject
{
    private readonly HistoryStore _history;
    private readonly BaselineStore _baselines;
    private readonly StatusBarViewModel _status;

    public HistoryPageViewModel(SitesViewModel sites, HistoryStore history, BaselineStore baselines, StatusBarViewModel status)
    {
        Sites = sites;
        _history = history;
        _baselines = baselines;
        _status = status;
        BuildOptions();
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            BuildOptions();
            OnPropertyChanged(nameof(Title));
        };
        SelectedSite = sites.Sites.FirstOrDefault();
    }

    public string Title => Loc.Instance["Nav_History"];

    public SitesViewModel Sites { get; }

    public ObservableCollection<NodeOption> Nodes { get; } = [];

    public ObservableCollection<SeriesOption> Series { get; } = [];

    public ObservableCollection<ChoiceOption> RangeOptions { get; } = [];

    public ObservableCollection<AnomalyRowViewModel> Anomalies { get; } = [];

    public ObservableCollection<CompareRowViewModel> Comparison { get; } = [];

    [ObservableProperty]
    private SiteItemViewModel? _selectedSite;

    [ObservableProperty]
    private NodeOption? _selectedNode;

    [ObservableProperty]
    private SeriesOption? _selectedSeries;

    [ObservableProperty]
    private ChoiceOption? _selectedRange;

    [ObservableProperty]
    private StaticChartData? _chart;

    [ObservableProperty]
    private string _baselineInfo = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _compareWeeks = true;

    [ObservableProperty]
    private DateTime _splitDate = DateTime.Today;

    public bool CompareSplit
    {
        get => !CompareWeeks;
        set => CompareWeeks = !value;
    }

    partial void OnCompareWeeksChanged(bool value) => OnPropertyChanged(nameof(CompareSplit));

    private void BuildOptions()
    {
        var selected = SelectedRange?.Value ?? 24;
        RangeOptions.Clear();
        foreach (var hours in new[] { 6, 24, 7 * 24, 30 * 24, 90 * 24 })
        {
            RangeOptions.Add(new ChoiceOption(hours, hours < 48 ? Loc.Instance.Format("Range_Hours", hours) : Loc.Instance.Format("Range_Days", hours / 24)));
        }

        SelectedRange = RangeOptions.First(o => o.Value.Equals(selected));
    }

    partial void OnSelectedSiteChanged(SiteItemViewModel? value)
    {
        Nodes.Clear();
        foreach (var n in value?.Nodes ?? [])
        {
            Nodes.Add(new NodeOption(n.Id, n.Name));
        }

        SelectedNode = Nodes.FirstOrDefault();
    }

    partial void OnSelectedNodeChanged(NodeOption? value) => _ = LoadSeriesAsync();

    partial void OnSelectedSeriesChanged(SeriesOption? value) => _ = LoadAsync();

    partial void OnSelectedRangeChanged(ChoiceOption? value) => _ = LoadAsync();

    private async Task LoadSeriesAsync()
    {
        Series.Clear();
        Chart = null;
        Anomalies.Clear();
        if (SelectedSite is not { } site || SelectedNode is not { } node)
        {
            return;
        }

        foreach (var s in await _history.SeriesAsync(site.Id, node.Id, CancellationToken.None))
        {
            Series.Add(new SeriesOption(s.Key, s.Instance));
        }

        var baseline = await _baselines.LoadAsync(site.Id, node.Id, CancellationToken.None);
        BaselineInfo = baseline is null
            ? Loc.Instance["Hist_NoBaseline"]
            : Loc.Instance.Format("Hist_BaselineInfo", baseline.Weeks, RedisPageViewModel.LocalTime(baseline.ComputedUtc), baseline.Series.Count);
        Message = Series.Count == 0 ? Loc.Instance["Hist_Empty"] : string.Empty;
        SelectedSeries = Series.FirstOrDefault(s => s.Key == "win.cpu.total_pct") ?? Series.FirstOrDefault();
    }

    [RelayCommand]
    private Task LoadAsync() => LoadChartAsync();

    private async Task LoadChartAsync()
    {
        if (SelectedSite is not { } site || SelectedNode is not { } node || SelectedSeries is not { } series)
        {
            return;
        }

        var to = DateTimeOffset.UtcNow;
        var from = to.AddHours(-(SelectedRange?.Value ?? 24));
        var points = await _history.ReadAsync(site.Id, node.Id, series.Key, series.Instance, from, to, null, CancellationToken.None);
        var baseline = (await _baselines.LoadAsync(site.Id, node.Id, CancellationToken.None))?.For(series.Key, series.Instance);
        var lines = new List<StaticLine>
        {
            new(Loc.Instance["Series_Avg"], "#2F6FEB", points.Select(p => (p.AtUtc, p.Avg)).ToList()),
        };
        if (points.Any(p => p.N > 1))
        {
            lines.Add(new StaticLine(Loc.Instance["Series_Max"], "#9A6700", points.Select(p => (p.AtUtc, p.Max)).ToList(), Dashed: true));
        }

        IReadOnlyList<Anomaly> anomalies = [];
        if (baseline is not null)
        {
            var zone = TimeZoneInfo.Local;
            var withSlot = points.Select(p => (p.AtUtc, Slot: baseline.At(p.AtUtc, zone))).Where(x => x.Slot is not null).ToList();
            lines.Add(new StaticLine(Loc.Instance["Series_BaselineP95"], "#1A7F37", withSlot.Select(x => (x.AtUtc, x.Slot!.Value.P95)).ToList(), Dashed: true));
            lines.Add(new StaticLine(Loc.Instance.Format("Series_AnomalyLimit", AnomalyOptions.Default.Factor), "#CF222E",
                withSlot.Select(x => (x.AtUtc, x.Slot!.Value.P95 * AnomalyOptions.Default.Factor)).ToList(), Dashed: true));
            anomalies = AnomalyDetector.Detect(baseline, points.Select(p => (p.AtUtc, p.Avg)), zone);
        }

        Chart = new StaticChartData(Loc.Instance.Format("Chart_History", series.Label, SelectedNode.Name), lines, anomalies.Select(a => (a.FromUtc, a.ToUtc)).ToList(), from, to);
        Anomalies.Clear();
        foreach (var a in anomalies)
        {
            Anomalies.Add(new AnomalyRowViewModel(node.Name, series.Label, RedisPageViewModel.LocalTime(a.FromUtc), RedisPageViewModel.LocalTime(a.ToUtc), Num(a.Peak), Num(a.Limit),
                a.Samples.ToString(Loc.Instance.Culture)));
        }

        Message = points.Count == 0 ? Loc.Instance["Hist_NoPoints"] : Loc.Instance.Format("Hist_Loaded", points.Count, Loc.Instance["Tier_" + HistoryStore.TierFor(to - from)]);
    }

    [RelayCommand]
    private async Task ComputeBaselineAsync()
    {
        if (SelectedSite is not { } site || SelectedNode is not { } node)
        {
            return;
        }

        var result = await _baselines.ComputeAsync(site.Id, node.Id, DateTimeOffset.UtcNow, TimeZoneInfo.Local, CancellationToken.None);
        _status.Show(Loc.Instance.Format("Hist_BaselineComputed", result.Series.Count));
        BaselineInfo = Loc.Instance.Format("Hist_BaselineInfo", result.Weeks, RedisPageViewModel.LocalTime(result.ComputedUtc), result.Series.Count);
        await LoadChartAsync();
    }

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (SelectedSite is not { } site || SelectedNode is not { } node)
        {
            return;
        }

        var (a, b) = CompareWeeks
            ? PeriodCompare.ThisWeekVsLastWeek(DateTimeOffset.UtcNow)
            : ((new DateTimeOffset(SplitDate.Date.AddDays(-7)).ToUniversalTime(), new DateTimeOffset(SplitDate.Date).ToUniversalTime()),
               (new DateTimeOffset(SplitDate.Date).ToUniversalTime(), new DateTimeOffset(SplitDate.Date.AddDays(7)).ToUniversalTime()));
        var rows = await _baselines.CompareAsync(site.Id, node.Id, a, b, CancellationToken.None);
        Comparison.Clear();
        foreach (var r in rows)
        {
            Comparison.Add(new CompareRowViewModel(r.Instance is null ? r.Key : $"{r.Key} [{r.Instance}]", Num(r.A?.Avg), Num(r.B?.Avg), Pct(r.AvgChange), Num(r.A?.P95), Num(r.B?.P95),
                Pct(r.P95Change), r.AvgChange > 0.25));
        }

        _status.Show(Loc.Instance.Format("Hist_Compared", rows.Count));
    }

    private static string Num(double? v) => v is { } d ? d.ToString("0.##", Loc.Instance.Culture) : string.Empty;

    private static string Pct(double? v) => v is { } d ? (d >= 0 ? "+" : string.Empty) + d.ToString("P0", Loc.Instance.Culture) : string.Empty;
}
