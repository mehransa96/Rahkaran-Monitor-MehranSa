using System.Collections.ObjectModel;
using System.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using RSM.App.Localization;
using RSM.App.Views;
using RSM.Collectors.Database;
using RSM.Collectors.Rahkaran;
using RSM.Collectors.Redis;
using RSM.Collectors.Sql.Live;
using RSM.Collectors.Windows;
using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.App.ViewModels;

public sealed record SlowlogRowViewModel(string Time, string DurationMs, string Command, string Key, string Client);

public sealed record LatencyRowViewModel(string Event, string Time, string LatestMs, string MaxMs);

public sealed record ClientHostRowViewModel(string Address, string Connections, string Names);

/// <summary>Redis (§11): INFO summary, memory/ops/clients charts, SLOWLOG (command + key only), LATENCY, CLIENT LIST by host.</summary>
public sealed partial class RedisPageViewModel(LiveViewModel live) : LivePageViewModel(live, "Nav_Redis")
{
    public ChartSpec MemoryChart { get; } = new("Chart_RedisMemory", [new("redis.used_memory_mb", null, "Series_UsedMemoryMb", "#2F6FEB")], []);

    public ChartSpec OpsChart { get; } = new("Chart_RedisOps",
        [new("redis.ops_per_sec", null, "Series_OpsPerSec", "#1A7F37"), new("redis.connected_clients", null, "Series_Clients", "#CF6A2E")], []);

    public ObservableCollection<InventoryRowViewModel> Summary { get; } = [];

    public ObservableCollection<SlowlogRowViewModel> Slowlog { get; } = [];

    public ObservableCollection<LatencyRowViewModel> Latency { get; } = [];

    public ObservableCollection<ClientHostRowViewModel> Clients { get; } = [];

    [ObservableProperty]
    private string _status = string.Empty;

    protected override void Rebuild()
    {
        var payload = Live.Payload<RedisPayload>("redis", "redis");
        Status = payload is null ? Loc.Instance["Redis_NotConfigured"]
            : payload.Unreachable is { } reason ? Loc.Instance.Format("Redis_Unreachable", reason)
            : payload.AppNodesWithoutConnection.Count > 0 ? Loc.Instance.Format("Redis_AppNodesMissing", string.Join(", ", payload.AppNodesWithoutConnection))
            : string.Empty;
        var info = payload?.Info;
        Replace(Summary, info is null ? [] : new (string Key, object? Value)[]
        {
            ("Redis_Version", info.Version), ("Redis_Role", info.Role), ("Redis_Uptime", info.UptimeSeconds is { } u ? TimeSpan.FromSeconds(u).ToString(@"d\.hh\:mm", Loc.Instance.Culture) : null),
            ("Redis_UsedMemory", info.UsedMemory is { } m ? $"{Text(m / 1048576.0)} MB" : null), ("Redis_MaxMemory", info.MaxMemory is { } mx ? $"{Text(mx / 1048576.0)} MB" : null),
            ("Redis_UsedPct", info.UsedMemoryPct is { } p ? $"{Text(p)} %" : null), ("Redis_Policy", info.MaxMemoryPolicy), ("Redis_Fragmentation", info.FragmentationRatio),
            ("Redis_HitRatio", info.HitRatioPct is { } h ? $"{Text(h)} %" : null), ("Redis_Evicted", info.EvictedKeys), ("Redis_Rejected", info.RejectedConnections),
            ("Redis_Keys", info.Keys), ("Redis_Persistence", info.PersistenceError ? Loc.Instance["Redis_PersistenceError"] : Loc.Instance["Common_Ok"]),
        }.Where(r => r.Value is not null).Select(r => new InventoryRowViewModel(Loc.Instance[r.Key], Text(r.Value))));
        Replace(Slowlog, payload?.Slowlog.Take(100).Select(s => new SlowlogRowViewModel(LocalTime(s.AtUtc), Text(s.DurationMs), s.Command, s.Key ?? string.Empty,
            string.Join(" ", new[] { s.Client, s.ClientName }.Where(c => !string.IsNullOrEmpty(c))))) ?? []);
        Replace(Latency, payload?.Latency.Select(l => new LatencyRowViewModel(l.Event, LocalTime(l.AtUtc), Text(l.LatestMs), Text(l.MaxMs))) ?? []);
        Replace(Clients, payload?.Clients.Select(c => new ClientHostRowViewModel(c.Address, Text(c.Connections), string.Join(", ", c.Names))) ?? []);
    }

    internal static string LocalTime(DateTimeOffset utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record ReportRowViewModel(string Path, string Executions, string Failures, string P50, string P95, string Max, string Phase, string TopUser, bool Slow);

public sealed record SubscriptionRowViewModel(string Report, string Description, string Delivery, string LastStatus, string LastRun, bool Failed);

/// <summary>SSRS (§11): last 24 h of ExecutionLog3 per report (p50/p95, data vs rendering), load per hour, subscriptions.</summary>
public sealed partial class SsrsPageViewModel(LiveViewModel live) : LivePageViewModel(live, "Nav_Ssrs")
{
    public ObservableCollection<InventoryRowViewModel> Summary { get; } = [];

    public ObservableCollection<ReportRowViewModel> Reports { get; } = [];

    public ObservableCollection<InventoryRowViewModel> ByHour { get; } = [];

    public ObservableCollection<InventoryRowViewModel> FailureStatuses { get; } = [];

    public ObservableCollection<SubscriptionRowViewModel> Subscriptions { get; } = [];

    [ObservableProperty]
    private string _status = string.Empty;

    protected override void Rebuild()
    {
        var payload = Live.Payload<SsrsPayload>("ssrs", "ssrs");
        Status = payload is null ? Loc.Instance["Ssrs_NotFound"] : payload.Truncated ? Loc.Instance.Format("Ssrs_Truncated", Text(SsrsCollector.MaxRows)) : string.Empty;
        var s = payload?.Summary;
        Replace(Summary, s is null ? [] :
        [
            new(Loc.Instance["Ssrs_Database"], payload!.Database),
            new(Loc.Instance["Ssrs_Executions"], Text(s.Executions)),
            new(Loc.Instance["Ssrs_Failures"], $"{Text(s.Failures)} ({Text(s.FailurePct)} %)"),
        ]);
        Replace(Reports, s?.Reports.Select(r => new ReportRowViewModel(r.ItemPath, Text(r.Executions), Text(r.Failures), Seconds(r.P50Ms), Seconds(r.P95Ms), Seconds(r.MaxMs),
            Loc.Instance["ReportPhase_" + r.Dominant], r.TopUser ?? string.Empty, r.Executions >= SsrsCollector.MinExecutionsForLatency && r.P95Ms > SsrsCollector.SlowP95Ms)) ?? []);
        Replace(ByHour, s?.ByHour.Select(h => new InventoryRowViewModel(h.Key, Text(h.Count))) ?? []);
        Replace(FailureStatuses, s?.FailureStatuses.Select(h => new InventoryRowViewModel(h.Key, Text(h.Count))) ?? []);
        Replace(Subscriptions, payload?.Subscriptions.Select(x => new SubscriptionRowViewModel(x.ReportPath ?? string.Empty, x.Description ?? string.Empty, x.DeliveryExtension ?? string.Empty,
            x.LastStatus ?? string.Empty, x.LastRunAgoMin is { } m ? Loc.Instance.Format("Duration_Minutes", Text(m)) : string.Empty,
            RSM.Parsers.Ssrs.ExecutionLogAggregator.IsFailedSubscriptionStatus(x.LastStatus))) ?? []);
    }

    private static string Seconds(int ms) => $"{Text(ms / 1000.0)} s";
}

public sealed record RabbitNodeRowViewModel(string Name, string Running, string Memory, string Disk, string Fd, string Sockets, bool Alarm);

public sealed record RabbitQueueRowViewModel(string Name, string VHost, string Messages, string Unacked, string Consumers, string State, bool Stuck);

/// <summary>Workflow and licensing (§11): RabbitMQ through GET-only management API calls, CodeMeter service and listening port.</summary>
public sealed partial class WorkflowPageViewModel(LiveViewModel live) : LivePageViewModel(live, "Nav_Workflow")
{
    public ChartSpec QueueChart { get; } = new("Chart_RabbitMessages",
        [new("rmq.messages", null, "Series_Messages", "#2F6FEB"), new("rmq.unacked", null, "Series_Unacked", "#CF6A2E")], []);

    public ObservableCollection<RabbitNodeRowViewModel> Nodes { get; } = [];

    public ObservableCollection<RabbitQueueRowViewModel> Queues { get; } = [];

    public ObservableCollection<InventoryRowViewModel> CodeMeter { get; } = [];

    [ObservableProperty]
    private string _rabbitStatus = string.Empty;

    protected override void Rebuild()
    {
        var payload = Live.Payload<RabbitPayload>("rabbitmq", "rabbitmq");
        RabbitStatus = payload is null ? Loc.Instance["Rabbit_NotConfigured"]
            : payload.Unreachable is { } reason ? Loc.Instance.Format("Rabbit_Unreachable", reason)
            : Loc.Instance.Format("Rabbit_Version", payload.Overview?.Version ?? "?", payload.Overview?.ErlangVersion ?? "?");
        string Pct(long? used, long? total) => used is { } u && total is > 0 ? $"{Text(u)} / {Text(total)} ({Text(100.0 * u / total.Value)} %)" : string.Empty;
        string YesNo(bool b) => b ? Loc.Instance["Common_Yes"] : Loc.Instance["Common_No"];
        Replace(Nodes, payload?.Nodes.Select(n => new RabbitNodeRowViewModel(n.Name, YesNo(n.Running),
            (n.MemAlarm ? "⚠ " : string.Empty) + Pct(n.MemUsed / 1048576, n.MemLimit / 1048576),
            (n.DiskAlarm ? "⚠ " : string.Empty) + (n.DiskFree is { } f ? $"{Text(f / 1073741824.0)} GB" : string.Empty),
            Pct(n.FdUsed, n.FdTotal), Pct(n.SocketsUsed, n.SocketsTotal), n.MemAlarm || n.DiskAlarm)) ?? []);
        Replace(Queues, payload?.Queues.Select(q => new RabbitQueueRowViewModel(q.Name, q.VHost, Text(q.Messages), Text(q.Unacked), Text(q.Consumers), q.State ?? string.Empty,
            q.Messages > RabbitMqCollector.StuckMessages && q.Consumers == 0)) ?? []);

        var services = Live.Payload<IReadOnlyList<ServiceInfo>>("win.services", "services")?.Where(s => s.Category == ServiceCategory.CodeMeter).ToList() ?? [];
        var ports = Live.Payload<IReadOnlyList<string>>("win.services", "codemeter_listen");
        Replace(CodeMeter, services.Select(s => new InventoryRowViewModel(s.Name, $"{s.State} / {s.StartMode}"))
            .Append(new InventoryRowViewModel(Loc.Instance["CodeMeter_Ports"], ports is { Count: > 0 } ? string.Join(", ", ports) : Loc.Instance["CodeMeter_NoPorts"])));
    }
}

public sealed record DotNetRowViewModel(string Process, string Pid, string Hosting, string HeapMb, string TimeInGc, string Gen2PerMin, string ThreadPool, string Exceptions, string Requests, string WorkingSetMb);

/// <summary>.NET runtime of the Rahkaran processes (§9.4, optional, local, off by default): EventPipe counters only.</summary>
public sealed partial class DotNetPageViewModel(LiveViewModel live) : LivePageViewModel(live, "Nav_DotNet")
{
    public ChartSpec HeapChart { get; } = new("Chart_GcHeap", [new("dotnet.gc_heap_mb", null, "Series_Process", "#2F6FEB", AllInstances: true)], []);

    public ChartSpec GcChart { get; } = new("Chart_TimeInGc", [new("dotnet.time_in_gc_pct", null, "Series_Process", "#CF222E", AllInstances: true)], []);

    public ChartSpec ThreadPoolChart { get; } = new("Chart_ThreadPoolQueue", [new("dotnet.threadpool_queue", null, "Series_Process", "#CF6A2E", AllInstances: true)], []);

    public ChartSpec RequestsChart { get; } = new("Chart_AspNetRequests", [new("dotnet.requests_per_s", null, "Series_Process", "#1A7F37", AllInstances: true)], []);

    public ObservableCollection<DotNetRowViewModel> Processes { get; } = [];

    public ObservableCollection<string> Skipped { get; } = [];

    [ObservableProperty]
    private string _status = string.Empty;

    protected override void Rebuild()
    {
        var result = Live.Session is { } s && Live.NodeId is { } n ? s.Latest(n, "dotnet.runtime") : null;
        Status = result is null ? Loc.Instance["DotNet_NoData"]
            : result.Status.Kind == CollectorStatusKind.Ok ? string.Empty
            : Loc.Instance.Format("DotNet_Status", result.Status.Reason ?? string.Empty);
        var payload = Live.Payload<DotNetPayload>("dotnet.runtime", "dotnet");
        Replace(Processes, payload?.Processes.Select(p => new DotNetRowViewModel(p.Process.Label, Text(p.Process.ProcessId), p.Process.Hosting,
            Text(p.Latest.GcHeapMb), Text(p.Latest.TimeInGcPct), Text(p.Latest.Gen2PerMin), $"{Text(p.Latest.ThreadPoolQueue)} / {Text(p.Latest.ThreadPoolThreads)}",
            Text(p.Latest.ExceptionsPerSec), Text(p.Latest.RequestsPerSec), Text(p.Latest.WorkingSetMb))) ?? []);
        Replace(Skipped, payload?.Skipped ?? []);
    }
}

public sealed record TopologyBoxViewModel(double X, double Y, double Width, double Height, string Label, string Details, string Roles, bool Profiled);

public sealed record TopologyLineViewModel(double X1, double Y1, double X2, double Y2, string Color, bool Dashed, double LabelX, double LabelY, string Label);

public sealed record TopologyEdgeRowViewModel(string From, string To, string Kind, string Count, string Label, string Source);

/// <summary>
/// Topology (§6): users → ARR → App (Aux sites / NUMA) → SQL / Redis / SSRS, discovered from ARR farms, SQL sessions and
/// Redis CLIENT LIST of every node, laid out in tiers. Declared (profile-only) links are dashed.
/// </summary>
public sealed partial class TopologyPageViewModel(LiveViewModel live) : LivePageViewModel(live, "Nav_Topology")
{
    public const double BoxWidth = 190, BoxHeight = 58, ColumnGap = 250, RowGap = 80, Margin = 16;

    public ObservableCollection<TopologyBoxViewModel> Boxes { get; } = [];

    public ObservableCollection<TopologyLineViewModel> Lines { get; } = [];

    public ObservableCollection<TopologyEdgeRowViewModel> Edges { get; } = [];

    [ObservableProperty]
    private double _canvasWidth = 800;

    [ObservableProperty]
    private double _canvasHeight = 300;

    protected override void Rebuild()
    {
        if (Live.Session is not { } session || session.Nodes.Count == 0)
        {
            Boxes.Clear();
            Lines.Clear();
            Edges.Clear();
            return;
        }

        var site = session.Nodes[0].Context.Site;
        var facts = session.Nodes.ToDictionary(n => n.Id, n => session.Facts(n.Id), StringComparer.Ordinal);
        Layout(TopologyBuilder.Build(site, facts));
    }

    internal static int Column(TopologyNode node) => node.Id == TopologyMap.ClientsId ? 0
        : node.Roles.HasFlag(NodeRoles.Arr) ? 1
        : node.Roles.HasFlag(NodeRoles.App) || !node.Profiled ? 2
        : 3;

    private void Layout(TopologyMap map)
    {
        var positions = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var boxes = new List<TopologyBoxViewModel>();
        foreach (var column in map.Nodes.GroupBy(Column).OrderBy(g => g.Key))
        {
            var row = 0;
            foreach (var node in column)
            {
                var x = Margin + (column.Key * ColumnGap);
                var y = Margin + (row++ * RowGap);
                positions[node.Id] = (x, y);
                var label = node.Id == TopologyMap.ClientsId ? Loc.Instance["Topo_Users"] : node.Id == "@others" ? Loc.Instance.Format("Topo_Others", node.Label) : node.Label;
                var roles = string.Join(" · ", Enum.GetValues<NodeRoles>().Where(r => r != NodeRoles.None && node.Roles.HasFlag(r)).Select(r => Loc.Instance["Role_" + r]));
                boxes.Add(new TopologyBoxViewModel(x, y, BoxWidth, BoxHeight, label, string.Join(" · ", node.Details), node.Profiled ? roles : Loc.Instance["Topo_Unprofiled"], node.Profiled));
            }
        }

        var lines = new List<TopologyLineViewModel>();
        var rows = new List<TopologyEdgeRowViewModel>();
        var names = map.Nodes.ToDictionary(n => n.Id, n => boxes.FirstOrDefault(b => positions.TryGetValue(n.Id, out var p) && b.X == p.X && b.Y == p.Y)?.Label ?? n.Label, StringComparer.Ordinal);
        foreach (var edge in map.Edges)
        {
            if (!positions.TryGetValue(edge.From, out var from) || !positions.TryGetValue(edge.To, out var to))
            {
                continue;
            }

            var forward = to.X >= from.X;
            var x1 = from.X + (forward ? BoxWidth : 0);
            var x2 = to.X + (forward ? 0 : BoxWidth);
            var y1 = from.Y + (BoxHeight / 2);
            var y2 = to.Y + (BoxHeight / 2);
            var label = edge.Count is { } c ? Text(c) : string.Empty;
            lines.Add(new TopologyLineViewModel(x1, y1, x2, y2, Color(edge.Kind), !edge.Observed, ((x1 + x2) / 2) - 12, ((y1 + y2) / 2) - 16, label));
            rows.Add(new TopologyEdgeRowViewModel(names[edge.From], names[edge.To], Loc.Instance["TopoEdge_" + edge.Kind], label, edge.Label ?? string.Empty,
                edge.Observed ? Loc.Instance["Topo_Observed"] : Loc.Instance["Topo_Declared"]));
        }

        Replace(Boxes, boxes);
        Replace(Lines, lines);
        Replace(Edges, rows);
        CanvasWidth = boxes.Count == 0 ? 0 : boxes.Max(b => b.X) + BoxWidth + Margin;
        CanvasHeight = boxes.Count == 0 ? 0 : boxes.Max(b => b.Y) + BoxHeight + Margin;
    }

    private static string Color(TopologyEdgeKind kind) => kind switch
    {
        TopologyEdgeKind.ArrFarm => "#2F6FEB",
        TopologyEdgeKind.Sql => "#1A7F37",
        TopologyEdgeKind.Redis => "#CF222E",
        TopologyEdgeKind.Http => "#6E7781",
        _ => "#8C959F",
    };
}

public sealed record QueryPackRefusalRowViewModel(string Pack, string File, string Reason);

public sealed record QueryPackInfoRowViewModel(string Id, string Title, string Database, string Accepted, string Refused);

public sealed record QueryPackResultRowViewModel(string Pack, string Query, string Database, string Status, string Rows, string Breaches, QueryPackResult Result);

/// <summary>
/// Query packs (§8.7): which team packs are loaded, every refused file with its reason (SqlGuard rule and line), and the
/// latest results with threshold breaches. Packs are files next to RSM.exe; there is no SQL editor in RSM.
/// </summary>
public sealed partial class QueryPacksPageViewModel(LiveViewModel live, QueryPackCatalog catalog) : LivePageViewModel(live, "Nav_QueryPacks")
{
    public string Folder { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "QueryPacks");

    public ObservableCollection<QueryPackInfoRowViewModel> Packs { get; } = new(catalog.Packs.Select(p =>
        new QueryPackInfoRowViewModel(p.Id, p.Title.Get(Loc.Instance.Language), p.Database, p.Accepted.ToString(Loc.Instance.Culture), p.Refused.ToString(Loc.Instance.Culture))));

    public ObservableCollection<QueryPackRefusalRowViewModel> Refusals { get; } = new(catalog.Refusals.Select(r => new QueryPackRefusalRowViewModel(r.Pack, r.File ?? string.Empty, r.Reason)));

    public ObservableCollection<QueryPackResultRowViewModel> Results { get; } = [];

    public bool IsEmpty => catalog.Packs.Count == 0 && catalog.Refusals.Count == 0;

    [ObservableProperty]
    private QueryPackResultRowViewModel? _selectedResult;

    [ObservableProperty]
    private DataView? _selectedRows;

    partial void OnSelectedResultChanged(QueryPackResultRowViewModel? value) => SelectedRows = value is null ? null : ToTable(value.Result).DefaultView;

    protected override void Rebuild()
    {
        var language = Loc.Instance.Language;
        var selected = SelectedResult;
        Replace(Results, Live.Payload<List<QueryPackResult>>("sql.querypacks", "querypacks")?.Select(r => new QueryPackResultRowViewModel(
            r.PackTitle.Get(language), r.Title.Get(language), r.Database, r.Status.ToString(), Text(r.Rows.Count),
            string.Join("; ", r.Breaches.Select(b => $"{b.Threshold.Message.Get(language)} ({b.Threshold.Column} = {Text(b.Value)})").Distinct().Take(3)), r)) ?? []);
        SelectedResult = Results.FirstOrDefault(r => selected is not null && r.Pack == selected.Pack && r.Query == selected.Query && r.Database == selected.Database) ?? Results.FirstOrDefault();
    }

    internal static DataTable ToTable(QueryPackResult result)
    {
        var table = new DataTable();
        foreach (var column in result.Columns)
        {
            table.Columns.Add(column, typeof(string));
        }

        foreach (var row in result.Rows)
        {
            table.Rows.Add(result.Columns.Select(c => (object)Text(row.GetValueOrDefault(c))).ToArray());
        }

        return table;
    }
}
