using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RSM.App.Localization;
using RSM.Collectors.Sql;
using RSM.Core.Live;
using RSM.Core.Model;
using RSM.Hosting;

namespace RSM.App.ViewModels;

public sealed record NodeOption(string Id, string Name);

public sealed record ChoiceOption(double Value, string Label);

/// <summary>A KPI tile: label, formatted value and the severity colour (Ok when within thresholds).</summary>
public sealed record KpiViewModel(string Label, string Value, Severity Severity);

/// <summary>
/// The live view shared by the Overview, SQL Server, Queries &amp; Blocking and Windows pages: connects to the selected
/// site (read-only collectors only), pauses when the user asks or the window is minimised, and refreshes the pages
/// every two seconds from the in-memory metric store. No control here acts on a server.
/// </summary>
public sealed partial class LiveViewModel : ObservableObject
{
    private readonly LiveMonitorService _service;
    private readonly StatusBarViewModel _status;
    private readonly ILogger<LiveViewModel> _logger;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cts;
    private bool _minimized;

    public LiveViewModel(SitesViewModel sites, LiveMonitorService service, StatusBarViewModel status, ILogger<LiveViewModel> logger)
    {
        Sites = sites;
        _service = service;
        _status = status;
        _logger = logger;
        BuildOptions();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            BuildOptions();
            Refresh();
        };
    }

    public SitesViewModel Sites { get; }

    public ObservableCollection<NodeOption> Nodes { get; } = [];

    public ObservableCollection<KpiViewModel> Kpis { get; } = [];

    public ObservableCollection<FindingRowViewModel> Findings { get; } = [];

    public ObservableCollection<ChoiceOption> WindowOptions { get; } = [];

    public ObservableCollection<ChoiceOption> IntervalOptions { get; } = [];

    /// <summary>Raised on the UI thread after each refresh; charts and grids redraw when visible.</summary>
    public event EventHandler? Ticked;

    public LiveSession? Session => _service.Current;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private SiteItemViewModel? _selectedSite;

    [ObservableProperty]
    private NodeOption? _selectedNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanPause), nameof(CanResume))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(PauseCommand), nameof(ResumeCommand), nameof(RefreshNowCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause), nameof(CanResume))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand), nameof(ResumeCommand))]
    private bool _isPaused;

    [ObservableProperty]
    private ChoiceOption? _selectedWindow;

    [ObservableProperty]
    private ChoiceOption? _selectedInterval;

    [ObservableProperty]
    private string _overheadText = string.Empty;

    [ObservableProperty]
    private string _stateText = string.Empty;

    public bool IsIdle => !IsBusy && !IsConnected;

    public bool CanPause => IsConnected && !IsPaused;

    public bool CanResume => IsConnected && IsPaused;

    public TimeSpan Window => TimeSpan.FromMinutes(SelectedWindow?.Value ?? 15);

    public string? NodeId => SelectedNode?.Id;

    private bool CanConnect() => SelectedSite is not null && !IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (SelectedSite is not { } site)
        {
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(p => _status.Show(Loc.Instance.Format("Live_Status_Connecting", p)));
        try
        {
            var profile = site.ToProfile();
            var session = await _service.StartAsync(profile, progress, _cts.Token);
            Nodes.Clear();
            foreach (var node in session.Nodes)
            {
                Nodes.Add(new NodeOption(node.Id, node.Context.Node.Name));
            }

            SelectedNode = Nodes.FirstOrDefault();
            IsConnected = true;
            IsPaused = false;
            ApplySchedulerSettings();
            _timer.Start();
            _status.Show(Loc.Instance.Format("Live_Status_Connected", profile.Name));
        }
        catch (OperationCanceledException)
        {
            _status.Show(Loc.Instance["Diag_Cancelled"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live view failed to connect");
            _status.Show(Loc.Instance.Format("Live_Status_Failed", ex.Message));
            await _service.StopAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDisconnect() => IsConnected || IsBusy;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        _cts?.Cancel();
        _timer.Stop();
        await _service.StopAsync();
        IsConnected = false;
        Nodes.Clear();
        Kpis.Clear();
        Findings.Clear();
        OverheadText = string.Empty;
        StateText = string.Empty;
        _status.Show(Loc.Instance["Live_Status_Disconnected"]);
        Ticked?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        IsPaused = true;
        ApplySchedulerSettings();
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        IsPaused = false;
        ApplySchedulerSettings();
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task RefreshNowAsync()
    {
        if (Session is { } session)
        {
            await session.TickAsync(CancellationToken.None);
            Refresh();
        }
    }

    /// <summary>Called by the main window: a minimised window pauses collection (no load on the servers for nothing).</summary>
    public void SetMinimized(bool minimized)
    {
        _minimized = minimized;
        ApplySchedulerSettings();
    }

    partial void OnSelectedIntervalChanged(ChoiceOption? value) => ApplySchedulerSettings();

    partial void OnSelectedNodeChanged(NodeOption? value) => Refresh();

    partial void OnSelectedWindowChanged(ChoiceOption? value) => Ticked?.Invoke(this, EventArgs.Empty);

    private void ApplySchedulerSettings()
    {
        if (Session is not { } session)
        {
            return;
        }

        session.Scheduler.Paused = IsPaused || _minimized;
        session.Scheduler.IntervalScale = SelectedInterval?.Value ?? 1;
        StateText = session.Scheduler.Paused
            ? Loc.Instance[_minimized && !IsPaused ? "Live_State_PausedMinimized" : "Live_State_Paused"]
            : Loc.Instance["Live_State_Live"];
    }

    private void BuildOptions()
    {
        var window = SelectedWindow?.Value ?? 15;
        var interval = SelectedInterval?.Value ?? 1;
        WindowOptions.Clear();
        foreach (var minutes in new[] { 15, 30, 60 })
        {
            WindowOptions.Add(new ChoiceOption(minutes, Loc.Instance.Format("Live_Window_Minutes", minutes)));
        }

        IntervalOptions.Clear();
        IntervalOptions.Add(new ChoiceOption(1, Loc.Instance["Live_Interval_Normal"]));
        IntervalOptions.Add(new ChoiceOption(2, Loc.Instance["Live_Interval_Half"]));
        IntervalOptions.Add(new ChoiceOption(4, Loc.Instance["Live_Interval_Quarter"]));
        SelectedWindow = WindowOptions.First(o => o.Value == window);
        SelectedInterval = IntervalOptions.First(o => o.Value == interval);
    }

    /// <summary>Latest facts of the selected node (empty when not connected).</summary>
    public IReadOnlyDictionary<string, object?> Facts => Session is { } s && NodeId is { } n ? s.Facts(n) : new Dictionary<string, object?>();

    /// <summary>A payload part of the selected node's latest result of a collector (SQL payload data or Windows payload).</summary>
    public T? Payload<T>(string collectorId, string key) where T : class
    {
        if (Session is not { } s || NodeId is not { } n || s.Latest(n, collectorId)?.Payload is not { } payload)
        {
            return null;
        }

        return payload switch
        {
            SqlCollectorPayload sql => sql.Data.GetValueOrDefault(key) as T,
            IReadOnlyDictionary<string, object?> dict => dict.GetValueOrDefault(key) as T,
            _ => null,
        };
    }

    private void Refresh()
    {
        if (Session is not { } session)
        {
            return;
        }

        var facts = Facts;
        Kpis.Clear();
        foreach (var kpi in BuildKpis(facts, session))
        {
            Kpis.Add(kpi);
        }

        Findings.Clear();
        var language = Loc.Instance.Language;
        foreach (var node in session.Nodes)
        {
            foreach (var f in session.Findings(node.Id).OrderByDescending(f => f.Severity).ThenBy(f => f.RuleId, StringComparer.Ordinal))
            {
                Findings.Add(new FindingRowViewModel(f.Severity, Loc.Instance["Severity_" + f.Severity], f.RuleId, node.Context.Node.Name,
                    f.Instance ?? string.Empty, f.Title.Get(language), f.Recommendation.Get(language), f.Source.Citation, f.Source.IsGeneralGuidance));
            }
        }

        var stats = session.Stats();
        var load = NodeId is { } id ? stats.Nodes.GetValueOrDefault(id) : null;
        OverheadText = Loc.Instance.Format("Live_Overhead",
            Num(stats.ClientCpuPercent, "0.0"), Num(stats.ClientWorkingSetMb, "0"),
            Num(load?.SqlCpuPercent, "0.00"), Num(load?.SqlRoundTripMs, "0.0"),
            load?.RunsPerMinute ?? 0, stats.BackedOffCollectors, stats.Series);
        ApplySchedulerSettings();
        Ticked?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<KpiViewModel> BuildKpis(IReadOnlyDictionary<string, object?> facts, LiveSession session)
    {
        double? F(string key) => facts.GetValueOrDefault(key) is IConvertible c and not string ? c.ToDouble(CultureInfo.InvariantCulture) : null;
        Severity Above(double? value, string rule, string warn, string? crit = null) =>
            value is not { } v ? Severity.Ok
            : crit is not null && session.RuleParam(rule, crit) is { } c && v > c ? Severity.Critical
            : session.RuleParam(rule, warn) is { } w && v > w ? Severity.Warning
            : Severity.Ok;
        Severity Below(double? value, string rule, string warn, string? crit = null) =>
            value is not { } v ? Severity.Ok
            : crit is not null && session.RuleParam(rule, crit) is { } c && v < c ? Severity.Critical
            : session.RuleParam(rule, warn) is { } w && v < w ? Severity.Warning
            : Severity.Ok;

        if (F("sql.cpu.sql_pct") is { } sqlCpu)
        {
            yield return new KpiViewModel(Loc.Instance["Kpi_SqlCpu"], Num(sqlCpu, "0") + " %", Above(sqlCpu, "SQL-CPU-002", "max"));
            yield return new KpiViewModel(Loc.Instance["Kpi_OtherCpu"], Num(F("sql.cpu.other_pct"), "0") + " %", Above(F("sql.cpu.other_pct"), "SQL-CPU-003", "max"));
        }

        if (F("sql.perf.batch_requests_sec") is { } batches)
        {
            yield return new KpiViewModel(Loc.Instance["Kpi_BatchRequests"], Num(batches, "0"), Severity.Ok);
        }

        if (F("sql.perf.ple_s") is { } ple)
        {
            yield return new KpiViewModel(Loc.Instance["Kpi_Ple"], Num(ple, "0") + " s", F("sql.perf.ple_floor_s") is { } floor && ple < floor ? Severity.Warning : Severity.Ok);
        }

        if (F("sql.waits.ms_per_sec") is { } waits)
        {
            yield return new KpiViewModel(Loc.Instance["Kpi_Waits"], Num(waits, "0") + " ms/s", Severity.Ok);
        }

        if (F("sql.blocking.max_wait_s") is { } blocking)
        {
            yield return new KpiViewModel(Loc.Instance["Kpi_Blocking"], Num(F("sql.blocking.blocked_sessions"), "0") + " / " + Num(blocking, "0") + " s", Above(blocking, "SQL-BLK-001", "warn", "crit"));
        }

        if (F("sql.deadlocks.count_24h") is { } deadlocks)
        {
            yield return new KpiViewModel(Loc.Instance["Kpi_Deadlocks"], Num(deadlocks, "0"), deadlocks > 0 ? Severity.Info : Severity.Ok);
        }

        if (F("win.cpu.total_pct") is { } winCpu)
        {
            yield return new KpiViewModel(Loc.Instance["Kpi_WindowsCpu"], Num(winCpu, "0") + " %", Severity.Ok);
        }

        if (F("win.mem.available_pct") is { } freeMem)
        {
            yield return new KpiViewModel(Loc.Instance["Kpi_FreeMemory"], Num(freeMem, "0") + " %", Below(freeMem, "OS-006", "warn", "crit"));
        }
    }

    internal static string Num(double? value, string format) =>
        value is { } v ? v.ToString(format, Loc.Instance.Culture) : "—";
}
