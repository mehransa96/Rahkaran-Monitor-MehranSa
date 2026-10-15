using RSM.Core.Model;
using RSM.Parsers.Events;

namespace RSM.Collectors.Windows;

/// <summary>An event kept in the rolling window, with its KB match computed once.</summary>
public sealed record KeptEvent(EventRecordData Event, string? KbKey, bool WebWorker);

public sealed class EventLogState
{
    public Dictionary<string, DateTimeOffset> LastReadUtc { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> LogsRead { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool ShutdownHistoryRead { get; internal set; }

    public List<KeptEvent> Window { get; } = [];
}

/// <summary>What the Events page shows for one node.</summary>
public sealed record EventLogPayload(IReadOnlyList<EventGroup> Groups, IReadOnlyList<string> LogsRead, IReadOnlyList<string> LogsFailed, bool Truncated);

/// <summary>
/// System and Application event logs (spec §9.8): errors/criticals/warnings plus the KB's informational ids, last 24 h
/// on the first read and incrementally after that; shutdown events (Kernel-Power 41, EventLog 6008, User32 1074) over
/// 7 days. Groups by source+id with counts and the latest masked message, attaches the fa/en knowledge base, feeds the
/// unified timeline and the EVT/OS-010/SVC-002/IIS-POOL-002 rules. Read-only.
/// </summary>
public sealed class WindowsEventLogCollector : WindowsCollectorBase<EventLogState>
{
    public static readonly TimeSpan Lookback = TimeSpan.FromHours(24);
    public static readonly TimeSpan ShutdownLookback = TimeSpan.FromDays(7);
    public const int MaxEventsPerRead = 2000;
    public const int MaxKept = 20000;

    private static readonly int[] ShutdownIds = [41, 6008, 1074];

    public override string Id => "win.events";

    public override Layer Layer => Layer.Events;

    public override TimeSpan DefaultInterval => TimeSpan.FromMinutes(2);

    /// <summary>Logs read on every node (spec §9.8). Optional IIS/CodeMeter channels arrive with their modules.</summary>
    public IReadOnlyList<string> Logs { get; init; } = ["System", "Application"];

    protected override async Task CollectCoreAsync(INodeContext node, WindowsNodeTransport transport, EventLogState state, LiveOutput output, List<string> problems, CancellationToken cancellationToken)
    {
        if (transport.Events is null)
        {
            problems.Add("event logs not readable (Event Log Readers / Remote Event Log Management)");
            return;
        }

        var now = Clock.GetUtcNow();
        var failed = new List<string>();
        var fresh = new List<EventRecordData>();
        var truncated = false;
        foreach (var log in Logs)
        {
            var since = state.LastReadUtc.TryGetValue(log, out var last) ? last : now - Lookback;
            try
            {
                var (events, cut) = await transport.Events.ReadAsync(new EventQuery(log, since, EventKnowledgeBase.InformationalIds, true, MaxEventsPerRead), cancellationToken).ConfigureAwait(false);
                if (log.Equals("System", StringComparison.OrdinalIgnoreCase) && !state.ShutdownHistoryRead)
                {
                    var (history, _) = await transport.Events.ReadAsync(new EventQuery(log, now - ShutdownLookback, ShutdownIds, false, 500), cancellationToken).ConfigureAwait(false);
                    events = events.Concat(history.Where(h => h.AtUtc <= since)).ToList();
                    state.ShutdownHistoryRead = true;
                }

                truncated |= cut;
                fresh.AddRange(events);
                state.LastReadUtc[log] = events.Count == 0 ? now : events.Max(e => e.AtUtc) is var max && max > since ? max : since;
                state.LogsRead.Add(log);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
            {
                failed.Add(log);
                problems.Add($"{log}: {ex.GetType().Name}");
            }
        }

        Apply(state, fresh, now, node.Node.Id, output);
        output.Payload["events"] = new EventLogPayload(EventDigest.Group(state.Window.Select(k => k.Event)), state.LogsRead.Order().ToList(), failed, truncated);
    }

    /// <summary>Adds new events to the window, prunes it, emits timeline events and the rule facts.</summary>
    internal static void Apply(EventLogState state, IReadOnlyList<EventRecordData> fresh, DateTimeOffset now, string nodeId, LiveOutput output)
    {
        foreach (var e in fresh)
        {
            state.Window.Add(new KeptEvent(e, EventKnowledgeBase.Find(e.Provider, e.Id, e.Level)?.Key, EventKnowledgeBase.NamesWebWorker(e.Message)));
        }

        state.Window.RemoveAll(k => k.Event.AtUtc < now - (IsShutdown(k) ? ShutdownLookback : Lookback));
        if (state.Window.Count > MaxKept)
        {
            state.Window.Sort((a, b) => a.Event.AtUtc.CompareTo(b.Event.AtUtc));
            state.Window.RemoveRange(0, state.Window.Count - MaxKept);
        }

        foreach (var g in EventDigest.Group(fresh))
        {
            output.Events.Add(new UnifiedEvent(g.LastUtc, nodeId, LayerOf(g.Provider), g.Provider, g.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                g.Severity, g.SampleMessage ?? g.Provider, (int)Math.Min(g.Count, int.MaxValue)));
        }

        var day = state.Window.Where(k => k.Event.AtUtc >= now - Lookback).ToList();
        long Count(IEnumerable<KeptEvent> events, params string[] keys) => events.LongCount(k => k.KbKey is not null && keys.Contains(k.KbKey));

        if (state.LogsRead.Contains("System"))
        {
            var k41 = Count(state.Window, "kernel-power-41");
            var k6008 = Count(state.Window, "eventlog-6008");
            output.Facts.Add(new Fact("evt.kernel_power_41_7d", k41));
            output.Facts.Add(new Fact("evt.eventlog_6008_7d", k6008));
            // One crash usually logs both 41 and 6008: the larger count approximates the number of unexpected restarts.
            output.Facts.Add(new Fact("evt.unexpected_shutdowns_7d", Math.Max(k41, k6008)));
            output.Facts.Add(new Fact("evt.resource_exhaustion_24h", Count(day, "resource-exhaustion-2004")));
            output.Facts.Add(new Fact("evt.low_disk_24h", Count(day, "srv-2013")));
            output.Facts.Add(new Fact("evt.scm_unexpected_24h", Count(day, "scm-unexpected-stop")));
            output.Facts.Add(new Fact("evt.was_failures_24h", Count(day, "was-5002", "was-5009", "was-5010", "was-5013")));
            output.Facts.Add(new Fact("evt.was_rapid_fail_24h", Count(day, "was-5002")));
            output.Facts.Add(new Fact("evt.schannel_24h", Count(day, "schannel-36887", "schannel-36874")));
            output.Facts.Add(new Fact("evt.time_service_24h", Count(day, "time-service")));
            output.Facts.Add(new Fact("evt.disk_errors_24h", Count(day, "disk-7", "disk-11", "disk-51", "disk-153", "ntfs-55", "ntfs-98")));
        }

        if (state.LogsRead.Contains("Application"))
        {
            output.Facts.Add(new Fact("evt.web_worker_crash_24h", day.LongCount(k => k.WebWorker && k.KbKey is "app-error-1000" or "dotnet-1026")));
            output.Facts.Add(new Fact("evt.web_worker_wer_24h", day.LongCount(k => k.WebWorker && k.KbKey is "wer-1001")));
            output.Facts.Add(new Fact("evt.aspnet_1309_24h", Count(day, "aspnet-1309")));
            output.Facts.Add(new Fact("evt.ancm_errors_24h", Count(day, "ancm")));
        }

        if (state.LogsRead.Count > 0)
        {
            output.Facts.Add(new Fact("evt.critical_24h", day.LongCount(k => k.Event.Level == 1)));
            output.Facts.Add(new Fact("evt.errors_24h", day.LongCount(k => k.Event.Level == 2)));
        }
    }

    private static bool IsShutdown(KeptEvent k) => k.KbKey is "kernel-power-41" or "eventlog-6008" or "user32-1074";

    /// <summary>Timeline layer of an event source (so IIS, SQL and storage events line up with their own layers).</summary>
    public static Layer LayerOf(string provider) => provider switch
    {
        _ when provider.StartsWith("MSSQL", StringComparison.OrdinalIgnoreCase) || provider.StartsWith("SQLAgent", StringComparison.OrdinalIgnoreCase)
            || provider.StartsWith("SQLSERVERAGENT", StringComparison.OrdinalIgnoreCase) => Layer.Sql,
        _ when provider.Contains("WAS", StringComparison.Ordinal) || provider.StartsWith("IIS", StringComparison.OrdinalIgnoreCase)
            || provider.StartsWith("ASP.NET", StringComparison.OrdinalIgnoreCase) || provider.Contains("W3SVC", StringComparison.OrdinalIgnoreCase) => Layer.Iis,
        "Service Control Manager" => Layer.Services,
        _ when provider.Equals("disk", StringComparison.OrdinalIgnoreCase) || provider.Contains("Ntfs", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("srv", StringComparison.OrdinalIgnoreCase) || provider.Equals("volmgr", StringComparison.OrdinalIgnoreCase) => Layer.Storage,
        _ => Layer.Events,
    };
}
