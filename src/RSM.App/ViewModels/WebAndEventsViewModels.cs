using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RSM.App.Localization;
using RSM.App.Views;
using RSM.Collectors.Iis;
using RSM.Collectors.Windows;
using RSM.Core.Model;
using RSM.Parsers.Events;
using RSM.Parsers.Iis;

namespace RSM.App.ViewModels;

public sealed record SiteRowViewModel(string Name, string Id, string Role, string State, string Bindings, string Pools, string TimeTaken, string Hosting, string LogFolder);

public sealed record PoolRowViewModel(string Name, string State, string Runtime, string Pipeline, string Identity, string StartMode, string IdleTimeout, string Recycle,
    string QueueLength, string MaxProcesses, string Bitness, string Numa);

public sealed record WorkerRowViewModel(string ProcessId, string Pool, string State, string Started);

public sealed record LiveRequestRowViewModel(string Elapsed, string Verb, string Url, string Client, string Pool, string Stage, string Module);

public sealed record SiteLogRowViewModel(string Site, string Requests, string Errors5xx, string Status503, string Ancm, string Disconnects, string Slowest, string Regressions, string Stdout, string Freb, string Note);

public sealed record StemRowViewModel(string Site, string Stem, string Requests, string P50, string P95, string P99, string Max, string Errors5xx);

public sealed record StatusRowViewModel(string Site, string Status, string Count, string Meaning);

public sealed record HttpErrRowViewModel(string Reason, string Count, string Queue, string First, string Last, string Meaning);

public sealed record WebConfigRowViewModel(string Site, string Path, string Hosting, string Process, string Databases, string Redis, string Limits, string Note);

public sealed record CertificateRowViewModel(string Site, string Binding, string Subject, string Expires, string DaysLeft, string Key, string Chain);

/// <summary>
/// IIS &amp; web server (§10): sites, pools, worker processes, live requests, counters, log analysis, web.config (masked)
/// and certificates. Display only — there is no control that starts, stops or recycles anything. The on-demand log
/// analysis over a longer range shows its size and time estimate and asks before it starts.
/// </summary>
public sealed partial class IisPageViewModel : LivePageViewModel
{
    private readonly IUserDialogs _dialogs;

    public IisPageViewModel(LiveViewModel live, IUserDialogs dialogs) : base(live, "Nav_Iis")
    {
        _dialogs = dialogs;
        RangeOptions.Add(new ChoiceOption(1, Loc.Instance["Iis_Range_1h"]));
        RangeOptions.Add(new ChoiceOption(6, Loc.Instance["Iis_Range_6h"]));
        RangeOptions.Add(new ChoiceOption(24, Loc.Instance["Iis_Range_24h"]));
        SelectedRange = RangeOptions[0];
    }

    public ObservableCollection<SiteRowViewModel> Sites { get; } = [];

    public ObservableCollection<PoolRowViewModel> Pools { get; } = [];

    public ObservableCollection<WorkerRowViewModel> Workers { get; } = [];

    public ObservableCollection<LiveRequestRowViewModel> Requests { get; } = [];

    public ObservableCollection<SiteLogRowViewModel> SiteLogs { get; } = [];

    public ObservableCollection<StemRowViewModel> Stems { get; } = [];

    public ObservableCollection<StatusRowViewModel> Statuses { get; } = [];

    public ObservableCollection<HttpErrRowViewModel> HttpErr { get; } = [];

    public ObservableCollection<WebConfigRowViewModel> WebConfigs { get; } = [];

    public ObservableCollection<CertificateRowViewModel> Certificates { get; } = [];

    public ObservableCollection<string> SiteNames { get; } = [];

    public ObservableCollection<ChoiceOption> RangeOptions { get; } = [];

    [ObservableProperty]
    private string? _selectedSite;

    [ObservableProperty]
    private ChoiceOption? _selectedRange;

    [ObservableProperty]
    private string _requestsNote = string.Empty;

    [ObservableProperty]
    private string _analysisStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeLogsCommand))]
    private bool _isAnalyzing;

    public ChartSpec RequestsChart { get; } = new("Chart_IisRequests", [new("win.iis.site.requests_sec", null, "Series_Site", "#2F6FEB", AllInstances: true)], []);

    public ChartSpec QueueChart { get; } = new("Chart_IisQueue", [new("win.iis.queue.size", null, "Series_Pool", "#CF222E", AllInstances: true)], []);

    public ChartSpec ActiveChart { get; } = new("Chart_IisActive", [new("win.iis.w3wp.active_requests", null, "Series_WorkerProcess", "#8250DF", AllInstances: true)], []);

    public ChartSpec ErrorsChart { get; } = new("Chart_Iis5xx", [new("iis.log.err5xx_pct_15m", null, "Series_Site", "#CF6A2E", AllInstances: true)],
        [new("IIS-ERR-001", "warn", false), new("IIS-ERR-001", "crit", true)]);

    protected override void Rebuild()
    {
        var config = Live.Payload<IisConfigPayload>("iis.config", "iis");
        var runtime = Live.Payload<IisRuntimePayload>("iis.runtime", "runtime");
        var logs = Live.Payload<IisLogPayload>("iis.logs", "logs");
        var language = Loc.Instance.Language;
        var webBySite = config?.WebConfigs.Where(w => w.AppPath == "/" && w.Summary is not null).ToDictionary(w => w.Site, w => w.Summary!, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, WebConfigSummary>();
        var siteState = runtime?.Sites.ToDictionary(s => s.Name, s => s.State, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string?>();
        var poolState = runtime?.Pools.ToDictionary(p => p.Name, p => p.State, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string?>();

        Replace(Sites, config?.Config.Sites.Select(s => new SiteRowViewModel(
            s.Name, Text(s.Id), s.RahkaranRole ?? string.Empty, siteState.GetValueOrDefault(s.Name) ?? string.Empty,
            string.Join("  ", s.Bindings.Select(b => $"{b.Protocol}://{b.BindingInformation}")), string.Join(", ", s.AppPools),
            s.LogsTimeTaken ? Loc.Instance["Common_Yes"] : Loc.Instance["Iis_TimeTakenMissing"],
            webBySite.TryGetValue(s.Name, out var web) ? web.HostingModel ?? string.Empty : string.Empty,
            s.LogDirectory ?? string.Empty)) ?? []);

        Replace(Pools, config?.Config.AppPools.Select(p => new PoolRowViewModel(
            p.Name, poolState.GetValueOrDefault(p.Name) ?? PoolStateFromCounters(p.Name), p.NoManagedCode ? Loc.Instance["Iis_NoManagedCode"] : p.ManagedRuntimeVersion,
            p.PipelineMode, p.IdentityUserName is { } user ? $"{p.IdentityType} ({user})" : p.IdentityType, p.StartMode,
            Text(p.IdleTimeout.TotalMinutes), Recycle(p), Text(p.QueueLength), Text(p.MaxProcesses), p.Enable32Bit ? "32" : "64",
            $"{p.NumaNodeAssignment} / {p.NumaNodeAffinityMode}")) ?? []);

        Replace(Workers, runtime?.WorkerProcesses.Select(w => new WorkerRowViewModel(Text(w.ProcessId), w.AppPool, w.State ?? string.Empty,
            w.StartedUtc is { } at ? Loc.Instance.FormatTimestamp(at) : string.Empty)) ?? []);
        Replace(Requests, runtime?.Requests.Take(100).Select(r => new LiveRequestRowViewModel(Text(r.TimeElapsedMs / 1000d), r.Verb ?? string.Empty,
            r.Url ?? string.Empty, r.ClientIp ?? string.Empty, r.AppPool, r.PipelineState ?? string.Empty, r.CurrentModule ?? string.Empty)) ?? []);
        RequestsNote = runtime is null
            ? Loc.Instance["Iis_RuntimeUnavailable"]
            : runtime.RequestsUnavailable is not null ? Loc.Instance["Iis_RequestMonitorMissing"] : Loc.Instance.Format("Iis_RequestsNote", IisRuntimeCollector.MinElapsedMs / 1000);

        Replace(SiteLogs, logs?.Sites.Select(s => new SiteLogRowViewModel(
            s.Site, Text(s.Summary?.Requests), s.Summary is { } sum ? $"{Text(sum.Errors5xx)} ({Text(sum.ErrorRate5xx)} %)" : string.Empty,
            Text(s.Summary?.Statuses.Where(c => c.Status.StartsWith("503", StringComparison.Ordinal)).Sum(c => c.Count)),
            Text(s.Summary?.Statuses.Where(c => IisLogCollector.IsAncmStatus(c.Status)).Sum(c => c.Count)),
            Text(s.Summary?.ClientDisconnects),
            s.Summary?.Slowest is { } slow ? $"{slow.UriStem} (p95 {Text(slow.P95Ms)} ms)" : string.Empty,
            string.Join(", ", s.Regressions.Take(3).Select(r => $"{r.UriStem} {Text(r.P95Ms)}/{Text(r.BaselineP95Ms)} ms")),
            s.Stdout is { } o ? Text(o.ErrorLines) : string.Empty, Text(s.Freb.Count), s.Problem ?? string.Empty)) ?? []);

        Replace(Statuses, logs?.Sites.Where(s => s.Summary is not null)
            .SelectMany(s => s.Summary!.Statuses.Where(c => !c.Status.StartsWith('2') && !c.Status.StartsWith('3')).Select(c => new StatusRowViewModel(s.Site, c.Status, Text(c.Count), c.Meaning?.Get(language) ?? string.Empty)))
            .Take(100) ?? []);

        if (_analysis is null)
        {
            Replace(Stems, logs?.Sites.Where(s => s.Summary is not null)
                .SelectMany(s => s.Summary!.UrlStems.Select(u => (s.Site, u))).OrderByDescending(x => x.u.P95Ms).Take(50)
                .Select(x => StemRow(x.Site, x.u)) ?? []);
        }

        Replace(HttpErr, logs?.HttpErr.Select(r => new HttpErrRowViewModel(r.Reason, Text(r.Count), r.TopQueue ?? string.Empty,
            Loc.Instance.FormatTimestamp(r.First), Loc.Instance.FormatTimestamp(r.Last), r.Meaning?.Get(language) ?? string.Empty)) ?? []);

        Replace(WebConfigs, config?.WebConfigs.Select(w => new WebConfigRowViewModel(
            w.Site, w.AppPath, w.Summary?.HostingModel ?? string.Empty, w.Summary is { ProcessPath: { } pp } ? $"{pp} {w.Summary.Arguments}".Trim() : string.Empty,
            string.Join(", ", w.Summary?.ConnectionStrings.Select(c => $"{c.Name}: {c.Server}/{c.Database}") ?? []),
            string.Join(", ", w.Summary?.RedisAddresses ?? []),
            w.Summary is { } s ? string.Join("  ", new[] { s.MaxAllowedContentLength is { } m ? $"maxAllowedContentLength={Text(m)}" : null, s.ExecutionTimeoutSeconds is { } e ? $"executionTimeout={Text(e)}s" : null }.OfType<string>()) : string.Empty,
            w.Problem ?? string.Empty)) ?? []);

        Replace(Certificates, config?.Certificates.Select(c => new CertificateRowViewModel(
            c.Site, c.Binding, c.Certificate?.Subject ?? (config.CertificatesReadable ? Loc.Instance["Iis_CertNotFound"] : Loc.Instance["Iis_CertLocalOnly"]),
            c.Certificate is { } cert ? Loc.Instance.FormatTimestamp(cert.NotAfterUtc) : string.Empty,
            c.Certificate is { } cert2 ? Text(Math.Floor((cert2.NotAfterUtc - DateTimeOffset.UtcNow).TotalDays)) : string.Empty,
            c.Certificate is { } cert3 ? $"{cert3.KeyAlgorithm} {Text(cert3.KeySize)}" : string.Empty,
            c.Certificate is { } cert4 ? (cert4.ChainValid ? Loc.Instance["Common_Ok"] : cert4.ChainProblem ?? string.Empty) : string.Empty)) ?? []);

        var names = config?.Config.Sites.Select(s => s.Name).ToList() ?? [];
        if (!names.SequenceEqual(SiteNames))
        {
            Replace(SiteNames, names);
            SelectedSite ??= names.FirstOrDefault();
        }
    }

    private W3CSummary? _analysis;

    private string PoolStateFromCounters(string pool) =>
        Live.Facts.GetValueOrDefault($"pool[{pool}].was_state") is double s ? Loc.Instance["WasState_" + s.ToString("0", CultureInfo.InvariantCulture)] : string.Empty;

    private static string Recycle(IisAppPool p)
    {
        var parts = new List<string>();
        if (p.PeriodicRestart > TimeSpan.Zero)
        {
            parts.Add($"{p.PeriodicRestart.TotalMinutes:0} min");
        }

        if (p.RestartSchedule.Count > 0)
        {
            parts.Add(string.Join(", ", p.RestartSchedule.Select(t => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture))));
        }

        if (p.PrivateMemoryLimitKb > 0)
        {
            parts.Add($"{p.PrivateMemoryLimitKb / 1024} MB");
        }

        return string.Join(" · ", parts);
    }

    private static StemRowViewModel StemRow(string site, UrlStemStats u) =>
        new(site, u.UriStem, Text(u.Requests), Text(u.P50Ms), Text(u.P95Ms), Text(u.P99Ms), Text(u.MaxMs), Text(u.Errors5xx));

    private bool CanAnalyzeLogs() => !IsAnalyzing;

    /// <summary>On-demand analysis of the selected site's W3C logs over the chosen range (asks first, shows the estimate).</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyzeLogs))]
    private async Task AnalyzeLogsAsync()
    {
        var transport = Live.Session?.Nodes.FirstOrDefault(n => n.Id == Live.NodeId)?.Context.GetTransport<IisNodeTransport>();
        if (transport is null || SelectedSite is not { } site || SelectedRange is not { } range)
        {
            AnalysisStatus = Loc.Instance["Iis_AnalysisNeedsLive"];
            return;
        }

        IsAnalyzing = true;
        try
        {
            var to = DateTimeOffset.UtcNow;
            var from = to.AddHours(-range.Value);
            var estimate = await IisLogAnalysis.EstimateAsync(transport, site, from, CancellationToken.None);
            if (estimate is null || estimate.Files.Count == 0)
            {
                AnalysisStatus = Loc.Instance["Iis_AnalysisNoFiles"];
                return;
            }

            if (!_dialogs.Confirm(Loc.Instance.Format("Iis_AnalysisConfirm", estimate.Files.Count, Text(estimate.Bytes / 1048576d), Text(Math.Max(1, estimate.Estimated.TotalSeconds)))))
            {
                return;
            }

            var progress = new Progress<long>(b => AnalysisStatus = Loc.Instance.Format("Iis_AnalysisProgress", Text(b / 1048576d), Text(estimate.Bytes / 1048576d)));
            var summary = await Task.Run(() => IisLogAnalysis.AnalyzeAsync(transport, estimate, from, to, progress, CancellationToken.None));
            _analysis = summary;
            Replace(Stems, summary.UrlStems.Take(100).Select(u => StemRow(site, u)));
            AnalysisStatus = Loc.Instance.Format("Iis_AnalysisDone", Text(summary.Requests), Text(summary.ErrorRate5xx), range.Label);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Xml.XmlException or FormatException)
        {
            AnalysisStatus = Loc.Instance.Format("Iis_AnalysisFailed", ex.GetType().Name);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private void ShowLiveStems()
    {
        _analysis = null;
        AnalysisStatus = string.Empty;
        Rebuild();
    }
}

public sealed record FarmRowViewModel(string Farm, string Enabled, string Algorithm, string HealthUrl, string Servers, string Deviation);

public sealed record FarmServerRowViewModel(string Farm, string Address, string Enabled, string Weight, string Ports);

public sealed record RewriteRowViewModel(string Scope, string Name, string Enabled, string Match, string Conditions, string Action, string Target, string Segment, string Deployed);

/// <summary>ARR &amp; URL Rewrite (§10.9–10.10): farms, their servers and weights, rewrite rules with the "path deployed?" check.</summary>
public sealed class ArrPageViewModel(LiveViewModel live) : LivePageViewModel(live, "Nav_Arr")
{
    public ObservableCollection<FarmRowViewModel> Farms { get; } = [];

    public ObservableCollection<FarmServerRowViewModel> Servers { get; } = [];

    public ObservableCollection<RewriteRowViewModel> Rules { get; } = [];

    protected override void Rebuild()
    {
        var config = Live.Payload<IisConfigPayload>("iis.config", "iis");
        var facts = Live.Facts;
        string YesNo(bool b) => b ? Loc.Instance["Common_Yes"] : Loc.Instance["Common_No"];
        Replace(Farms, config?.Config.WebFarms.Select(f => new FarmRowViewModel(f.Name, YesNo(f.Enabled), f.LoadBalancing,
            f.HealthCheckUrl ?? Loc.Instance["Arr_NoHealthUrl"], $"{f.Servers.Count(s => s.Enabled)} / {f.Servers.Count}",
            facts.GetValueOrDefault($"farm[{IisConfigCollector.Instance(f.Name)}].distribution_deviation_pct") is { } d ? $"{Text(d)} %" : string.Empty)) ?? []);
        Replace(Servers, config?.Config.WebFarms.SelectMany(f => f.Servers.Select(s => new FarmServerRowViewModel(f.Name, s.Address, YesNo(s.Enabled), Text(s.Weight),
            string.Join("/", new[] { s.HttpPort, s.HttpsPort }.Where(p => p is not null).Select(p => Text(p)))))) ?? []);

        var rules = config is null ? [] : config.Config.RewriteRules
            .Concat(config.WebConfigs.Where(w => w.Summary is not null).SelectMany(w => w.Summary!.RewriteRules.Select(r => r with { Scope = $"{w.Site}{w.AppPath}" })));
        Replace(Rules, rules.Select(r =>
        {
            var deployed = facts.GetValueOrDefault($"rewrite[{IisConfigCollector.Instance($"{r.Scope}/{r.Name}")}].segment_deployed");
            return new RewriteRowViewModel(r.Scope, r.Name, YesNo(r.Enabled), r.MatchUrl ?? string.Empty, string.Join("; ", r.Conditions), r.ActionType ?? string.Empty,
                r.ActionUrl ?? string.Empty, r.TargetPathSegment ?? string.Empty, deployed is bool b ? YesNo(b) : string.Empty);
        }));
    }
}

public sealed record ServiceRowViewModel(string Name, string DisplayName, string Category, string State, string StartMode, string Account, string ProcessId, string Uptime, bool Problem, bool Unverified);

/// <summary>Watched Windows services (§9.7). Display only: no start/stop control exists, not even a disabled one.</summary>
public sealed class ServicesPageViewModel(LiveViewModel live) : LivePageViewModel(live, "Nav_Services")
{
    public ObservableCollection<ServiceRowViewModel> Services { get; } = [];

    protected override void Rebuild()
    {
        var now = DateTimeOffset.UtcNow;
        Replace(Services, Live.Payload<IReadOnlyList<ServiceInfo>>("win.services", "services")?.Select(s => new ServiceRowViewModel(
            s.Name, s.DisplayName ?? string.Empty, Loc.Instance["ServiceCategory_" + s.Category], s.State ?? string.Empty, s.StartMode ?? string.Empty,
            s.Account ?? string.Empty, Text(s.ProcessId), s.StartedUtc is { } at ? Duration(now - at) : string.Empty, s.AutomaticButStopped, s.Unverified)) ?? []);
    }

    private static string Duration(TimeSpan t) => t.TotalDays >= 1 ? Loc.Instance.Format("Duration_Days", Text(Math.Floor(t.TotalDays)))
        : t.TotalHours >= 1 ? Loc.Instance.Format("Duration_Hours", Text(Math.Floor(t.TotalHours))) : Loc.Instance.Format("Duration_Minutes", Text(Math.Max(0, Math.Floor(t.TotalMinutes))));
}

public sealed record TimelineRowViewModel(DateTimeOffset AtUtc, string Time, string Node, Layer Layer, string LayerText, Severity Severity, string SeverityText, string Source, string Code, string Count, string Message);

public sealed record EventGroupRowViewModel(string Severity, string Log, string Source, string Id, string Count, string First, string Last, string Title, string Message, string KbKey);

/// <summary>
/// Errors &amp; Events (§9.8, §12): one timeline of every layer's events (SQL error log, deadlocks, Windows event logs,
/// IIS) with layer/severity/node/text filters; selecting an event lists everything within ±5 minutes on every node and
/// shows the knowledge-base explanation and next step.
/// </summary>
public sealed partial class EventsPageViewModel : LivePageViewModel
{
    public static readonly TimeSpan CorrelationWindow = TimeSpan.FromMinutes(5);

    public EventsPageViewModel(LiveViewModel live) : base(live, "Nav_Events")
    {
        LayerOptions.Add(new LayerOption(null, Loc.Instance["Events_AllLayers"]));
        foreach (var layer in new[] { Layer.Sql, Layer.Database, Layer.Os, Layer.Storage, Layer.Iis, Layer.Services, Layer.Events })
        {
            LayerOptions.Add(new LayerOption(layer, Loc.Instance["LayerName_" + layer]));
        }

        SeverityOptions.Add(new SeverityOption(Severity.Ok, Loc.Instance["Events_AllSeverities"]));
        SeverityOptions.Add(new SeverityOption(Severity.Info, Loc.Instance["Severity_Info"]));
        SeverityOptions.Add(new SeverityOption(Severity.Warning, Loc.Instance["Severity_Warning"]));
        SeverityOptions.Add(new SeverityOption(Severity.Critical, Loc.Instance["Severity_Critical"]));
        SelectedLayer = LayerOptions[0];
        SelectedSeverity = SeverityOptions[0];
    }

    public sealed record LayerOption(Layer? Layer, string Label);

    public sealed record SeverityOption(Severity Minimum, string Label);

    public ObservableCollection<LayerOption> LayerOptions { get; } = [];

    public ObservableCollection<SeverityOption> SeverityOptions { get; } = [];

    public ObservableCollection<TimelineRowViewModel> Timeline { get; } = [];

    public ObservableCollection<TimelineRowViewModel> Correlated { get; } = [];

    public ObservableCollection<EventGroupRowViewModel> Groups { get; } = [];

    [ObservableProperty]
    private LayerOption _selectedLayer = null!;

    [ObservableProperty]
    private SeverityOption _selectedSeverity = null!;

    [ObservableProperty]
    private bool _allNodes = true;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private TimelineRowViewModel? _selectedEvent;

    [ObservableProperty]
    private string _kbTitle = string.Empty;

    [ObservableProperty]
    private string _kbExplanation = string.Empty;

    [ObservableProperty]
    private string _kbNextStep = string.Empty;

    [ObservableProperty]
    private string _logsNote = string.Empty;

    partial void OnSelectedLayerChanged(LayerOption value) => Rebuild();

    partial void OnSelectedSeverityChanged(SeverityOption value) => Rebuild();

    partial void OnAllNodesChanged(bool value) => Rebuild();

    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnSelectedEventChanged(TimelineRowViewModel? value) => ShowCorrelation(value);

    private IReadOnlyList<UnifiedEvent> _all = [];

    protected override void Rebuild()
    {
        var language = Loc.Instance.Language;
        _all = Live.Session?.Events() ?? [];
        var nodes = Live.Session?.Nodes.ToDictionary(n => n.Id, n => n.Context.Node.Name) ?? new Dictionary<string, string>();
        var filtered = _all.Where(e => (AllNodes || e.NodeId == Live.NodeId)
                && (SelectedLayer?.Layer is not { } layer || e.Layer == layer)
                && e.Severity >= (SelectedSeverity?.Minimum ?? Severity.Ok)
                && (SearchText.Length == 0 || e.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || e.Source.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                    || e.Code.Contains(SearchText, StringComparison.OrdinalIgnoreCase)))
            .Take(300);
        var selected = SelectedEvent;
        Replace(Timeline, filtered.Select(e => Row(e, nodes)));
        if (selected is not null)
        {
            SelectedEvent = Timeline.FirstOrDefault(r => r.AtUtc == selected.AtUtc && r.Source == selected.Source && r.Code == selected.Code);
        }

        var payload = Live.Payload<EventLogPayload>("win.events", "events");
        Replace(Groups, payload?.Groups.Take(200).Select(g =>
        {
            var kb = g.KbKey is null ? null : EventKnowledgeBase.ByKey(g.KbKey);
            return new EventGroupRowViewModel(Loc.Instance["Severity_" + g.Severity], g.Log, g.Provider, Text(g.Id), Text(g.Count),
                Loc.Instance.FormatTimestamp(g.FirstUtc), Loc.Instance.FormatTimestamp(g.LastUtc), kb?.Title.Get(language) ?? string.Empty, g.SampleMessage ?? string.Empty, g.KbKey ?? string.Empty);
        }) ?? []);
        LogsNote = payload is null ? Loc.Instance["Events_LogsUnavailable"]
            : payload.LogsFailed.Count > 0 ? Loc.Instance.Format("Events_LogsFailed", string.Join(", ", payload.LogsFailed))
            : payload.Truncated ? Loc.Instance["Events_Truncated"] : string.Empty;
    }

    private static TimelineRowViewModel Row(UnifiedEvent e, IReadOnlyDictionary<string, string> nodes) =>
        new(e.AtUtc, Loc.Instance.FormatTimestamp(e.AtUtc), nodes.GetValueOrDefault(e.NodeId) ?? e.NodeId, e.Layer, Loc.Instance["LayerName_" + e.Layer],
            e.Severity, Loc.Instance["Severity_" + e.Severity], e.Source, e.Code, e.Count > 1 ? Text(e.Count) : string.Empty, Short(e.Message, 400));

    /// <summary>Everything within ±5 minutes on every node and layer, and the knowledge-base text of the event.</summary>
    private void ShowCorrelation(TimelineRowViewModel? row)
    {
        var language = Loc.Instance.Language;
        var nodes = Live.Session?.Nodes.ToDictionary(n => n.Id, n => n.Context.Node.Name) ?? new Dictionary<string, string>();
        Replace(Correlated, row is null ? [] : _all.Where(e => (e.AtUtc - row.AtUtc).Duration() <= CorrelationWindow)
            .OrderBy(e => e.AtUtc).Select(e => Row(e, nodes)));
        var kb = row is not null && int.TryParse(row.Code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? EventKnowledgeBase.Find(row.Source, id, row.Severity >= Severity.Warning ? 2 : 4) : null;
        KbTitle = kb?.Title.Get(language) ?? (row is null ? string.Empty : Loc.Instance["Events_NoKb"]);
        KbExplanation = kb?.Explanation.Get(language) ?? string.Empty;
        KbNextStep = kb?.NextStep.Get(language) ?? string.Empty;
    }
}
