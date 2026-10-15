using System.Globalization;
using RSM.Core.Model;
using RSM.Parsers.Showplan;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Live;

/// <summary>A top statement from the plan cache with the warnings found in its cached plan.</summary>
public sealed record TopQuery(SqlQueryStatsCollector.TopQueryRow Stats, PlanAnalysis? Plan)
{
    public double AvgCpuMs => Stats.Executions > 0 ? (double)Stats.TotalCpuMs / Stats.Executions : 0;

    public double AvgDurationMs => Stats.Executions > 0 ? (double)Stats.TotalDurationMs / Stats.Executions : 0;

    public double AvgLogicalReads => Stats.Executions > 0 ? (double)Stats.TotalLogicalReads / Stats.Executions : 0;
}

/// <summary>
/// §7.9–7.11: top cached statements and procedures, plan warnings of the top few plans (fetched by handle, not a
/// plan-cache scan), plan-cache health (single-use share) and missing-index suggestions (display only — the Rahkaran
/// schema is vendor-owned, rule DB-IDX-000).
/// </summary>
public sealed class SqlQueryStatsCollector(SafeSqlExecutor executor) : SqlLiveCollectorBase<NoState>(executor)
{
    public const int TopN = 20;

    public override string Id => "sql.querystats";

    public override CollectorCost Cost => CollectorCost.Medium;

    public override TimeSpan DefaultInterval => TimeSpan.FromMinutes(5);

    /// <summary>How many of the top-CPU statements get their cached plan analysed.</summary>
    public int PlansToAnalyze { get; init; } = 5;

    public ShowplanOptions PlanOptions { get; init; } = new();

    protected override async Task CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, NoState state, LiveOutput output)
    {
        var top = await runner.QueryAsync<TopQueryRow>("sql.qs.top_queries", new { Top = TopN, OrderBy = 1 }).ConfigureAwait(false);
        var analysed = new List<TopQuery>();
        foreach (var row in top ?? [])
        {
            PlanAnalysis? plan = null;
            if (analysed.Count < PlansToAnalyze && row.PlanHandle is { Length: > 0 })
            {
                var text = await runner.QueryOptionalAsync<PlanRow>("sql.qs.plan_text",
                    new { row.PlanHandle, row.StatementStart, row.StatementEnd }).ConfigureAwait(false);
                plan = text?.FirstOrDefault()?.PlanXml is { } xml ? ShowplanAnalyzer.Analyze(xml, PlanOptions) : null;
            }

            analysed.Add(new TopQuery(row, plan));
        }

        var procs = await runner.QueryAsync<TopProcRow>("sql.qs.top_procs", new { Top = TopN }).ConfigureAwait(false);
        var cache = await runner.QueryAsync<PlanCacheRow>("sql.qs.plan_cache_health").ConfigureAwait(false);
        var missing = await runner.QueryAsync<MissingIndexRow>("sql.idx.missing").ConfigureAwait(false);
        Apply(Clock.GetUtcNow(), top is null ? null : analysed, procs, cache, missing, output);
    }

    internal static void Apply(
        DateTimeOffset at,
        IReadOnlyList<TopQuery>? top,
        IReadOnlyList<TopProcRow>? procs,
        IReadOnlyList<PlanCacheRow>? cache,
        IReadOnlyList<MissingIndexRow>? missing,
        LiveOutput output)
    {
        if (top is not null)
        {
            var warnings = top.Where(t => t.Plan is { Parsed: true }).SelectMany(t => t.Plan!.Warnings).ToList();
            output.Fact("sql.qs.plans_analysed", top.Count(t => t.Plan is { Parsed: true }));
            output.Fact("sql.qs.plan_warning_kinds", warnings.Select(w => w.Kind.ToString()).Distinct().Select(k => (object?)k).ToList());
            if (top.FirstOrDefault() is { } first)
            {
                output.Fact("sql.qs.top_cpu_statement", BlockingChain.Truncate(first.Stats.StatementText, 200));
            }

            output.Payload["topQueries"] = top;
        }

        if (procs is not null)
        {
            output.Payload["topProcedures"] = procs;
        }

        if (cache is not null)
        {
            var bytes = cache.Sum(c => c.Bytes);
            var single = cache.Sum(c => c.SingleUseBytes);
            output.Fact("sql.plan_cache.mb", bytes / 1048576d);
            output.Fact("sql.plan_cache.single_use_mb", single / 1048576d);
            output.Fact("sql.plan_cache.single_use_pct", bytes > 0 ? 100d * single / bytes : 0d);
            output.Fact("sql.plan_cache.adhoc_single_use_pct", bytes > 0 ? 100d * cache.Where(c => c.ObjType == "Adhoc").Sum(c => c.SingleUseBytes) / bytes : 0d);
            output.Sample("sql.plan_cache.mb", null, bytes / 1048576d, at);
            output.Sample("sql.plan_cache.single_use_pct", null, bytes > 0 ? 100d * single / bytes : 0d, at);
            output.Payload["planCache"] = cache;
        }

        if (missing is not null)
        {
            output.Fact("sql.idx.missing_count", missing.Count);
            output.Payload["missingIndexes"] = missing;
        }
    }

    /// <summary>"Rahkaran.dbo.Orders: =(CustomerId) &gt;(Date) +(Total), impact 87 %" for display (never as DDL).</summary>
    public static string Describe(MissingIndexRow row) => string.Create(CultureInfo.InvariantCulture,
        $"{row.TableName}: ={row.EqualityColumns} >{row.InequalityColumns} +{row.IncludedColumns}, impact {row.AvgUserImpact:0} %");

    public sealed class TopQueryRow
    {
        public byte[]? PlanHandle { get; set; }

        public int StatementStart { get; set; }

        public int StatementEnd { get; set; }

        public byte[]? QueryHash { get; set; }

        public string? DatabaseName { get; set; }

        public long Executions { get; set; }

        public long TotalCpuMs { get; set; }

        public long TotalDurationMs { get; set; }

        public long TotalLogicalReads { get; set; }

        public long TotalWrites { get; set; }

        public long MaxDurationMs { get; set; }

        public DateTime? CreationTime { get; set; }

        public DateTime? LastExecution { get; set; }

        public long? TotalSpills { get; set; }

        public long? MaxGrantKb { get; set; }

        public string? StatementText { get; set; }
    }

    public sealed class PlanRow
    {
        public string? PlanXml { get; set; }
    }

    public sealed class TopProcRow
    {
        public string? DatabaseName { get; set; }

        public string? SchemaName { get; set; }

        public string? ProcedureName { get; set; }

        public long Executions { get; set; }

        public long TotalCpuMs { get; set; }

        public long TotalDurationMs { get; set; }

        public long TotalLogicalReads { get; set; }

        public long TotalWrites { get; set; }

        public DateTime? LastExecution { get; set; }
    }

    public sealed class PlanCacheRow
    {
        public string? ObjType { get; set; }

        public long Plans { get; set; }

        public long Bytes { get; set; }

        public long SingleUsePlans { get; set; }

        public long SingleUseBytes { get; set; }
    }

    public sealed class MissingIndexRow
    {
        public string? DatabaseName { get; set; }

        public string? TableName { get; set; }

        public string? EqualityColumns { get; set; }

        public string? InequalityColumns { get; set; }

        public string? IncludedColumns { get; set; }

        public long UserSeeks { get; set; }

        public long UserScans { get; set; }

        public double AvgTotalUserCost { get; set; }

        public double AvgUserImpact { get; set; }

        public DateTime? LastUserSeek { get; set; }

        public double ImprovementMeasure { get; set; }
    }
}
