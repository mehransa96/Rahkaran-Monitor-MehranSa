using RSM.Core.Model;
using RSM.Parsers.Ssrs;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Live;

public sealed record SubscriptionRow
{
    public string? ReportPath { get; set; }

    public string? Description { get; set; }

    public string? DeliveryExtension { get; set; }

    public string? LastStatus { get; set; }

    public int? LastRunAgoMin { get; set; }

    public int? InactiveFlags { get; set; }
}

public sealed record SsrsPayload(string Database, ExecutionSummary Summary, IReadOnlyList<SubscriptionRow> Subscriptions, bool Truncated);

/// <summary>
/// §11 SSRS from the ReportServer database: the last 24 hours of <c>ExecutionLog3</c> (≤ <see cref="MaxRows"/> rows,
/// aggregated in RSM) and subscription statuses. Explains whether slow reports wait on data (SQL) or rendering.
/// </summary>
public sealed class SsrsCollector(SafeSqlExecutor executor) : SqlLiveCollectorBase<NoState>(executor)
{
    public const int Hours = 24;
    public const int MaxRows = 20000;
    public const int MinExecutionsForLatency = 5;
    public const int SlowP95Ms = 60_000;

    public override string Id => "ssrs";

    public override Layer Layer => Layer.Ssrs;

    public override CollectorCost Cost => CollectorCost.Medium;

    public override NodeRoles AppliesTo => NodeRoles.Report | NodeRoles.Data;

    public override TimeSpan DefaultInterval => TimeSpan.FromMinutes(15);

    protected override async Task CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, NoState state, LiveOutput output)
    {
        var databases = await runner.QueryAsync<DatabaseName>("ssrs.detect").ConfigureAwait(false);
        if (databases is null)
        {
            return;
        }

        if (databases.Count == 0)
        {
            output.Fact("ssrs.installed", false);
            return;
        }

        var database = databases[0].Name;
        var rows = await runner.QueryAsync<ExecutionLogRow>("ssrs.exec.recent", new { Hours, MaxRows }, database).ConfigureAwait(false);
        var subscriptions = await runner.QueryAsync<SubscriptionRow>("ssrs.subscriptions", null, database).ConfigureAwait(false);
        if (rows is null)
        {
            return;
        }

        Apply(database, rows, subscriptions ?? [], output);
    }

    internal static void Apply(string database, IReadOnlyList<ExecutionLogRow> rows, IReadOnlyList<SubscriptionRow> subscriptions, LiveOutput output)
    {
        var summary = ExecutionLogAggregator.Aggregate(rows);
        output.Payload["ssrs"] = new SsrsPayload(database, summary, subscriptions, rows.Count >= MaxRows);
        output.Fact("ssrs.installed", true);
        output.Fact("ssrs.database", database);
        output.Fact("ssrs.executions_24h", summary.Executions);
        output.Fact("ssrs.failures_24h", summary.Failures);
        output.Fact("ssrs.failure_pct_24h", summary.FailurePct);
        var slow = summary.Reports.Where(r => r.Executions >= MinExecutionsForLatency && r.P95Ms > SlowP95Ms).ToList();
        output.Fact("ssrs.slow_reports", slow.Count);
        output.Fact("ssrs.slow_reports_data_bound", slow.Count(r => r.Dominant == ReportPhase.Data));
        output.Fact("ssrs.slow_reports_top", string.Join(", ", slow.Take(3).Select(r => $"{r.ItemPath} p95 {r.P95Ms / 1000.0:0.#} s ({r.Dominant})")));
        var failed = subscriptions.Where(s => s.InactiveFlags is null or 0 && ExecutionLogAggregator.IsFailedSubscriptionStatus(s.LastStatus)).ToList();
        output.Fact("ssrs.subscriptions", subscriptions.Count);
        output.Fact("ssrs.subscriptions_failed", failed.Count);
        output.Fact("ssrs.subscriptions_failed_names", string.Join(", ", failed.Take(5).Select(s => s.Description ?? s.ReportPath)));
    }

    public sealed class DatabaseName
    {
        public string Name { get; set; } = string.Empty;
    }
}
