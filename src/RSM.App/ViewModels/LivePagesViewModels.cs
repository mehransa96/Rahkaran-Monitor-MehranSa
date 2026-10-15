using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RSM.App.Localization;
using RSM.App.Views;
using RSM.Collectors.Sql.Live;
using RSM.Collectors.Windows;
using RSM.Parsers.Showplan;
using RSM.Parsers.Sql;

namespace RSM.App.ViewModels;

/// <summary>Base for live pages: holds the shared <see cref="LiveViewModel"/> and rebuilds its tables on every tick.</summary>
public abstract class LivePageViewModel : ObservableObject
{
    protected LivePageViewModel(LiveViewModel live, string titleKey)
    {
        Live = live;
        TitleKey = titleKey;
        live.Ticked += (_, _) => Rebuild();
        Loc.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public LiveViewModel Live { get; }

    public string TitleKey { get; }

    public string Title => Loc.Instance[TitleKey];

    protected virtual void Rebuild()
    {
    }

    protected static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    protected static string Text(object? value) => value switch
    {
        null => string.Empty,
        double d => d.ToString("0.##", Loc.Instance.Culture),
        IFormattable f => f.ToString(null, Loc.Instance.Culture),
        _ => value.ToString() ?? string.Empty,
    };

    protected static string Short(string? text, int max = 200)
    {
        var t = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return t.Length > max ? t[..max] + "…" : t;
    }
}

/// <summary>Overview: KPI tiles, live findings, CPU and memory at a glance, RSM's own overhead.</summary>
public sealed class OverviewViewModel(LiveViewModel live) : LivePageViewModel(live, "Overview_Title")
{
    public ChartSpec CpuChart { get; } = new("Chart_Cpu",
        [new("sql.cpu.sql_pct", null, "Series_SqlCpu", "#2F6FEB"), new("sql.cpu.other_pct", null, "Series_OtherCpu", "#CF6A2E")],
        [new("SQL-CPU-002", "max", false)], 100);

    public ChartSpec WorkloadChart { get; } = new("Chart_Workload",
        [new("sql.perf.batch_requests_sec", null, "Series_BatchRequests", "#1A7F37"), new("sql.waits.ms_per_sec", "_Total", "Series_WaitMs", "#8250DF")], []);

    public ChartSpec MemoryChart { get; } = new("Chart_FreeMemory",
        [new("win.mem.available_pct", null, "Series_FreeMemory", "#0E8A8A")],
        [new("OS-006", "warn", false), new("OS-006", "crit", true)], 100);
}

public sealed record WaitRowViewModel(string WaitType, string Category, string WaitMs, string Explanation);

/// <summary>SQL Server page: live charts and waits, plus the diagnosis findings of the SQL layer.</summary>
public sealed class SqlPageViewModel(LiveViewModel live, LayerFindingsViewModel findings) : LivePageViewModel(live, "Nav_Sql")
{
    public LayerFindingsViewModel Diagnosis { get; } = findings;

    public ObservableCollection<WaitRowViewModel> Waits { get; } = [];

    public ChartSpec WaitsChart { get; } = new("Chart_WaitsByCategory", [new("sql.waits.ms_per_sec", null, "Series_WaitCategory", "#2F6FEB", AllInstances: true)], []);

    public ChartSpec PleChart { get; } = new("Chart_Ple", [new("sql.perf.ple_s", null, "Series_Ple", "#1A7F37")], []);

    public ChartSpec GrantsChart { get; } = new("Chart_MemoryGrants",
        [new("sql.perf.memory_grants_pending", null, "Series_GrantsPending", "#CF222E"), new("sql.perf.memory_grants_outstanding", null, "Series_GrantsOutstanding", "#2F6FEB")], []);

    public ChartSpec ReadLatencyChart { get; } = new("Chart_DataReadLatency", [new("sql.io.data_read_ms", null, "Series_Database", "#2F6FEB", AllInstances: true)],
        [new("SQL-IO-001", "warn", false), new("SQL-IO-001", "crit", true)]);

    public ChartSpec LogLatencyChart { get; } = new("Chart_LogWriteLatency", [new("sql.io.log_write_ms", null, "Series_Database", "#2F6FEB", AllInstances: true)],
        [new("SQL-IO-002", "warn", false), new("SQL-IO-002", "crit", true)]);

    public ChartSpec SessionsChart { get; } = new("Chart_Sessions",
        [new("sql.sessions.user", null, "Series_UserSessions", "#2F6FEB"), new("sql.req.active", null, "Series_ActiveRequests", "#1A7F37"), new("sql.req.blocked", null, "Series_BlockedRequests", "#CF222E")], []);

    protected override void Rebuild()
    {
        var language = Loc.Instance.Language;
        var interval = Live.Payload<WaitInterval>("sql.perf", "waits");
        Replace(Waits, interval?.Waits.Take(15).Select(w => new WaitRowViewModel(w.WaitType, Loc.Instance["WaitCategory_" + w.Category], Text(w.WaitMs),
            WaitStats.Explain(w.Category).Get(language))) ?? []);
    }
}

public sealed record ChainRowViewModel(string Head, string HeadState, string Blocked, string MaxWait, string TranAge, string Statement, string Members);

public sealed record RequestRowViewModel(string Session, string Login, string Host, string Program, string Database, string Status, string Wait, string BlockedBy, string Elapsed, string Cpu, string Reads, string Statement);

public sealed record TransactionRowViewModel(string Session, string Login, string Host, string Program, string State, string Age, string LogMb, string Database);

public sealed record DeadlockRowViewModel(string Time, string Victim, string Statements, string Objects, string Modes);

public sealed record TopQueryRowViewModel(string Database, string Executions, string TotalCpu, string AvgCpu, string AvgDuration, string AvgReads, string Warnings, string WarningDetails, string Statement);

public sealed record MissingIndexRowViewModel(string Database, string Description, string Seeks, string Impact);

/// <summary>Queries &amp; Blocking: head blockers with their chain, running requests, open transactions, deadlocks, top queries.</summary>
public sealed class QueriesPageViewModel(LiveViewModel live) : LivePageViewModel(live, "Nav_Queries")
{
    public ObservableCollection<ChainRowViewModel> Chains { get; } = [];

    public ObservableCollection<RequestRowViewModel> Requests { get; } = [];

    public ObservableCollection<TransactionRowViewModel> Transactions { get; } = [];

    public ObservableCollection<DeadlockRowViewModel> Deadlocks { get; } = [];

    public ObservableCollection<TopQueryRowViewModel> TopQueries { get; } = [];

    public ObservableCollection<MissingIndexRowViewModel> MissingIndexes { get; } = [];

    public ChartSpec BlockingChart { get; } = new("Chart_Blocking", [new("sql.blocking.max_wait_s", null, "Series_BlockingWait", "#CF222E")],
        [new("SQL-BLK-001", "warn", false), new("SQL-BLK-001", "crit", true)]);

    protected override void Rebuild()
    {
        var language = Loc.Instance.Language;
        Replace(Chains, Live.Payload<IReadOnlyList<BlockingChain>>("sql.activity", "blockingChains")?.Select(c => new ChainRowViewModel(
            $"{c.Head.SessionId} · {c.Head.Login}@{c.Head.Host}",
            c.HeadIsIdle ? Loc.Instance["Blocking_HeadIdle"] : c.Head.Status ?? string.Empty,
            Text(c.Blocked.Count),
            Text(c.MaxWaitMs / 1000d),
            c.Head.TranAgeS is { } age ? Text(age) : string.Empty,
            Short(c.Head.Statement, 300),
            string.Join("  ", c.Blocked.Select(b => $"{new string('›', b.Level)}{b.SessionId} ({b.WaitType} {b.WaitMs / 1000d:0.#}s)")))) ?? []);

        Replace(Requests, Live.Payload<IReadOnlyList<SqlActivityCollector.RequestRow>>("sql.activity", "requests")?.Take(50).Select(r => new RequestRowViewModel(
            Text(r.SessionId), r.LoginName ?? string.Empty, r.HostName ?? string.Empty, r.ProgramName ?? string.Empty, r.DatabaseName ?? string.Empty,
            r.Status ?? string.Empty, r.WaitType is null ? string.Empty : $"{r.WaitType} ({r.WaitMs} ms)", r.BlockingSessionId > 0 ? Text(r.BlockingSessionId) : string.Empty,
            Text(r.ElapsedMs / 1000d), Text(r.CpuMs), Text(r.LogicalReads), Short(r.StatementText))) ?? []);

        Replace(Transactions, Live.Payload<IReadOnlyList<SqlActivityCollector.TranRow>>("sql.activity", "transactions")?
            .GroupBy(t => (t.SessionId, t.TransactionId)).Select(g => g.First()).Take(50).Select(t => new TransactionRowViewModel(
                Text(t.SessionId), t.LoginName ?? string.Empty, t.HostName ?? string.Empty, t.ProgramName ?? string.Empty, t.SessionStatus ?? string.Empty,
                Text(t.AgeS), Text(((t.LogBytesUsed ?? 0) + (t.LogBytesReserved ?? 0)) / 1048576d), t.DatabaseName ?? string.Empty)) ?? []);

        Replace(Deadlocks, Live.Payload<IReadOnlyList<DeadlockGraph>>("sql.deadlocks", "deadlocks")?.Take(30).Select(d => new DeadlockRowViewModel(
            d.AtUtc is { } at ? Loc.Instance.FormatTimestamp(at) : string.Empty,
            string.Join(", ", d.Victims.Select(v => $"{v.Spid} {v.Login}@{v.Host}")),
            string.Join(" ‖ ", d.Processes.Select(p => $"{p.Spid}{(p.IsVictim ? "✗" : string.Empty)}: {Short(p.Statement, 120)}")),
            string.Join(", ", d.Resources.Select(r => r.ObjectName).Where(o => !string.IsNullOrEmpty(o)).Distinct()),
            string.Join("/", d.Resources.SelectMany(r => r.Owners.Concat(r.Waiters)).Select(p => p.Mode).Where(m => !string.IsNullOrEmpty(m)).Distinct()))) ?? []);

        Replace(TopQueries, Live.Payload<IReadOnlyList<TopQuery>>("sql.querystats", "topQueries")?.Select(q => new TopQueryRowViewModel(
            q.Stats.DatabaseName ?? string.Empty, Text(q.Stats.Executions), Text(q.Stats.TotalCpuMs), Text(q.AvgCpuMs), Text(q.AvgDurationMs), Text(q.AvgLogicalReads),
            q.Plan is { Parsed: true } p ? string.Join(", ", p.Warnings.Select(w => Loc.Instance["PlanWarning_" + w.Kind]).Distinct()) : string.Empty,
            q.Plan is { Parsed: true } p2 ? string.Join(Environment.NewLine, p2.Warnings.Select(w => $"{ShowplanAnalyzer.Explain(w.Kind).Get(language)} — {w.Detail}")) : string.Empty,
            Short(q.Stats.StatementText, 300))) ?? []);

        Replace(MissingIndexes, Live.Payload<IReadOnlyList<SqlQueryStatsCollector.MissingIndexRow>>("sql.querystats", "missingIndexes")?.Take(30).Select(m => new MissingIndexRowViewModel(
            m.DatabaseName ?? string.Empty, SqlQueryStatsCollector.Describe(m), Text(m.UserSeeks + m.UserScans), Text(m.AvgUserImpact))) ?? []);
    }
}

public sealed record ProcessRowViewModel(string Name, string ProcessId, string Cpu, string PrivateMb, string WorkingSetMb);

public sealed record InventoryRowViewModel(string Name, string Value);

/// <summary>Windows: CPU (and throttling), memory, disk latency per volume, network, top processes, inventory.</summary>
public sealed class WindowsPageViewModel(LiveViewModel live) : LivePageViewModel(live, "Nav_Windows")
{
    public ObservableCollection<ProcessRowViewModel> Processes { get; } = [];

    public ObservableCollection<InventoryRowViewModel> Inventory { get; } = [];

    public ChartSpec CpuChart { get; } = new("Chart_WindowsCpu",
        [new("win.cpu.total_pct", null, "Series_CpuTotal", "#2F6FEB"), new("win.cpu.perf_pct", null, "Series_CpuPerformance", "#CF6A2E")], [], 130);

    public ChartSpec MemoryChart { get; } = new("Chart_FreeMemory", [new("win.mem.available_pct", null, "Series_FreeMemory", "#0E8A8A")],
        [new("OS-006", "warn", false), new("OS-006", "crit", true)], 100);

    public ChartSpec DiskReadChart { get; } = new("Chart_DiskRead", [new("win.disk.read_ms", null, "Series_Volume", "#2F6FEB", AllInstances: true)],
        [new("STO-006", "warn", false), new("STO-006", "crit", true)]);

    public ChartSpec DiskWriteChart { get; } = new("Chart_DiskWrite", [new("win.disk.write_ms", null, "Series_Volume", "#2F6FEB", AllInstances: true)],
        [new("STO-007", "warn", false), new("STO-007", "crit", true)]);

    public ChartSpec NetworkChart { get; } = new("Chart_Network", [new("win.net.util_pct", null, "Series_Adapter", "#2F6FEB", AllInstances: true)],
        [new("OS-015", "util", false)], 100);

    private static readonly string[] InventoryPrefixes = ["os.", "cpu."];

    protected override void Rebuild()
    {
        Replace(Processes, Live.Payload<IReadOnlyList<ProcessUsage>>("win.processes", "processes")?.Select(p => new ProcessRowViewModel(
            p.Name, Text(p.ProcessId), Text(p.CpuPercent), Text(p.PrivateMb), Text(p.WorkingSetMb))) ?? []);
        Replace(Inventory, Live.Facts.Where(f => InventoryPrefixes.Any(p => f.Key.StartsWith(p, StringComparison.Ordinal)))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new InventoryRowViewModel(f.Key, f.Value is System.Collections.IEnumerable e and not string ? string.Join(", ", e.Cast<object?>()) : Text(f.Value))));
    }
}
