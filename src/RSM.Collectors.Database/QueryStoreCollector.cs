using RSM.Collectors.Sql;
using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Database;

/// <summary>
/// §7.10–7.11 inside the Rahkaran database(s): Query Store state, regressed queries and failing forced plans
/// (read only — RSM never forces or unforces a plan), and index usage since restart (display only; unused-index
/// candidates are never "fixed" — the Rahkaran schema is vendor-owned, rule DB-IDX-000).
/// </summary>
public sealed class QueryStoreCollector(SafeSqlExecutor executor) : SqlCollectorBase(executor), ILiveCollector
{
    public const int RecentHours = 24;
    public const int BaselineDays = 7;

    public override string Id => "sql.querystore";

    public override Layer Layer => Layer.Database;

    public override NodeRoles AppliesTo => NodeRoles.Data;

    protected override async Task<IReadOnlyDictionary<string, object?>> CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, List<Fact> facts, List<UnifiedEvent> events)
    {
        var databases = await runner.QueryAsync<DatabaseCollector.DatabaseRow>("db.list").ConfigureAwait(false);
        var activity = await runner.QueryAsync<DatabaseCollector.ActivityRow>("db.detect.activity").ConfigureAwait(false);
        if (databases is null)
        {
            return new Dictionary<string, object?>();
        }

        var sessions = activity?.Where(a => a.DatabaseName is not null).ToDictionary(a => a.DatabaseName!, a => a.Sessions, StringComparer.OrdinalIgnoreCase) ?? [];
        var detection = RahkaranDatabaseDetector.Detect(
            databases.Select(d => new DatabaseCandidate(d.Name, d.DatabaseId > 4, d.StateDesc == "ONLINE", d.DataSizeMb ?? 0, sessions.GetValueOrDefault(d.Name))).ToList(),
            node.Site.Settings.RahkaranDatabases);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in detection.Databases)
        {
            var key = $"db[{name}]";
            var options = (await runner.QueryAsync<OptionsRow>("sql.qds.options", database: name).ConfigureAwait(false))?.FirstOrDefault();
            IReadOnlyList<RegressedRow>? regressed = null;
            IReadOnlyList<ForcedPlanRow>? forced = null;
            if (options is not null)
            {
                Add(facts, $"{key}.qds.state", options.ActualState);
                Add(facts, $"{key}.qds.desired_state", options.DesiredState);
                Add(facts, $"{key}.qds.storage_pct", options.MaxStorageMb > 0 ? 100d * options.CurrentStorageMb / options.MaxStorageMb : null);
                if (options.ActualState is "READ_WRITE" or "READ_ONLY")
                {
                    regressed = await runner.QueryAsync<RegressedRow>("sql.qds.regressed", new { RecentHours, BaselineDays }, name).ConfigureAwait(false);
                    forced = await runner.QueryAsync<ForcedPlanRow>("sql.qds.forced_plans", database: name).ConfigureAwait(false);
                }
            }

            Apply(key, regressed, forced, facts);
            var usage = await runner.QueryAsync<IndexUsageRow>("sql.idx.usage", database: name).ConfigureAwait(false);
            if (usage is not null)
            {
                Add(facts, $"{key}.idx.unused_candidates", UnusedCandidates(usage).Count);
            }

            payload[name] = new Dictionary<string, object?>
            {
                ["queryStore"] = options,
                ["regressed"] = regressed,
                ["forcedPlans"] = forced,
                ["indexUsage"] = usage,
            };
        }

        return payload;
    }

    internal static void Apply(string key, IReadOnlyList<RegressedRow>? regressed, IReadOnlyList<ForcedPlanRow>? forced, List<Fact> facts)
    {
        if (regressed is not null)
        {
            Add(facts, $"{key}.qds.regressed_count", regressed.Count);
            if (regressed.FirstOrDefault() is { } worst)
            {
                Add(facts, $"{key}.qds.worst_regression", string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"query {worst.QueryId}: {worst.BaselineAvgUs / 1000:0.#} ms → {worst.RecentAvgUs / 1000:0.#} ms, {worst.Plans} plans"));
            }
        }

        if (forced is not null)
        {
            Add(facts, $"{key}.qds.forced_plans", forced.Count);
            Add(facts, $"{key}.qds.forced_failures", forced.Count(f => f.ForceFailureCount > 0));
            if (forced.FirstOrDefault(f => f.ForceFailureCount > 0) is { } failing)
            {
                Add(facts, $"{key}.qds.forced_failure_reason", failing.LastFailureReason);
            }
        }
    }

    /// <summary>Non-key, non-unique indexes written since restart but never read (candidates to review, never to drop).</summary>
    internal static IReadOnlyList<IndexUsageRow> UnusedCandidates(IEnumerable<IndexUsageRow> rows) =>
        rows.Where(r => !r.IsPrimaryKey && !r.IsUnique && r.IndexType == "NONCLUSTERED" && r.UserSeeks + r.UserScans + r.UserLookups == 0 && r.UserUpdates > 0).ToList();

    public sealed class OptionsRow
    {
        public string? ActualState { get; set; }

        public string? DesiredState { get; set; }

        public int ReadonlyReason { get; set; }

        public long CurrentStorageMb { get; set; }

        public long MaxStorageMb { get; set; }
    }

    public sealed class RegressedRow
    {
        public long QueryId { get; set; }

        public double RecentAvgUs { get; set; }

        public double BaselineAvgUs { get; set; }

        public long RecentExecutions { get; set; }

        public int Plans { get; set; }

        public string? QueryText { get; set; }
    }

    public sealed class ForcedPlanRow
    {
        public long QueryId { get; set; }

        public long PlanId { get; set; }

        public long ForceFailureCount { get; set; }

        public string? LastFailureReason { get; set; }

        public DateTimeOffset? LastExecution { get; set; }
    }

    public sealed class IndexUsageRow
    {
        public string? SchemaName { get; set; }

        public string? TableName { get; set; }

        public string? IndexName { get; set; }

        public string? IndexType { get; set; }

        public bool IsPrimaryKey { get; set; }

        public bool IsUnique { get; set; }

        public long UserSeeks { get; set; }

        public long UserScans { get; set; }

        public long UserLookups { get; set; }

        public long UserUpdates { get; set; }

        public DateTime? LastUserSeek { get; set; }

        public DateTime? LastUserScan { get; set; }
    }
}
