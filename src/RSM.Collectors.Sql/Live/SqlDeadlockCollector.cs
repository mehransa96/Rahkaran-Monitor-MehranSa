using System.Globalization;
using RSM.Core.Model;
using RSM.Parsers.Sql;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Live;

/// <summary>Per-node state of <see cref="SqlDeadlockCollector"/>: deadlocks already reported as events.</summary>
public sealed class DeadlockState
{
    internal object Gate { get; } = new();

    public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// §7.7 deadlocks read from the built-in <c>system_health</c> session (event files first, ring buffer as fallback).
/// RSM never creates or alters an XE session. Each graph is parsed: victim, statements, objects, lock modes.
/// </summary>
public sealed class SqlDeadlockCollector(SafeSqlExecutor executor) : SqlLiveCollectorBase<DeadlockState>(executor)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public override string Id => "sql.deadlocks";

    public override CollectorCost Cost => CollectorCost.Medium;

    public override TimeSpan DefaultInterval => TimeSpan.FromMinutes(5);

    protected override async Task CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, DeadlockState state, LiveOutput output)
    {
        var now = Clock.GetUtcNow();
        var since = now - Window;
        var graphs = new List<DeadlockGraph>();
        var source = "file";
        var fromFile = await runner.QueryOptionalAsync<EventRow>("sql.dl.from_file", new { Since = since.UtcDateTime }).ConfigureAwait(false);
        if (fromFile is not null)
        {
            graphs.AddRange(fromFile.SelectMany(r => DeadlockReport.Parse(r.EventXml ?? string.Empty)));
        }
        else
        {
            // Event files unreadable (permission on the LOG folder, or Linux/containers): the ring buffer keeps fewer events.
            source = "ring_buffer";
            var ring = await runner.QueryAsync<RingRow>("sql.dl.from_ring").ConfigureAwait(false);
            graphs.AddRange(ring?.SelectMany(r => DeadlockReport.Parse(r.TargetXml ?? string.Empty)) ?? []);
        }

        Apply(state, node.Node.Id, graphs, since, now, source, output);
    }

    internal static void Apply(DeadlockState state, string nodeId, IEnumerable<DeadlockGraph> graphs, DateTimeOffset since, DateTimeOffset now, string source, LiveOutput output)
    {
        var recent = graphs
            .Where(g => g.AtUtc is null || g.AtUtc >= since)
            .GroupBy(g => g.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(g => g.AtUtc)
            .ToList();
        output.Fact("sql.deadlocks.count_24h", recent.Count);
        output.Fact("sql.deadlocks.source", source);
        output.Sample("sql.deadlocks.count_24h", null, recent.Count, now);
        if (recent.FirstOrDefault() is { } latest)
        {
            output.Fact("sql.deadlocks.latest", Describe(latest));
        }

        lock (state.Gate)
        {
            foreach (var graph in recent.Where(g => state.Seen.Add(g.Id)))
            {
                output.Events.Add(new UnifiedEvent(graph.AtUtc ?? now, nodeId, Layer.Sql, "system_health", "xml_deadlock_report", Severity.Warning, Describe(graph)));
            }
        }

        output.Payload["deadlocks"] = recent;
    }

    /// <summary>"victim 57 (UPDATE …) vs 63 on dbo.Orders (X/U)".</summary>
    public static string Describe(DeadlockGraph graph)
    {
        var victim = graph.Victims.FirstOrDefault();
        var others = graph.Processes.Where(p => !p.IsVictim).Select(p => p.Spid?.ToString(CultureInfo.InvariantCulture) ?? p.Id);
        var objects = graph.Resources.Select(r => r.ObjectName).Where(o => !string.IsNullOrEmpty(o)).Distinct().Take(3);
        var modes = graph.Resources.SelectMany(r => r.Owners.Concat(r.Waiters)).Select(p => p.Mode).Where(m => !string.IsNullOrEmpty(m)).Distinct();
        return string.Create(CultureInfo.InvariantCulture,
            $"victim {victim?.Spid?.ToString(CultureInfo.InvariantCulture) ?? "?"} ({BlockingChain.Truncate(victim?.Statement, 120)}) vs {string.Join(", ", others)} on {string.Join(", ", objects)} ({string.Join("/", modes)})");
    }

    public sealed class EventRow
    {
        public string? EventXml { get; set; }
    }

    public sealed class RingRow
    {
        public string? TargetXml { get; set; }
    }
}
