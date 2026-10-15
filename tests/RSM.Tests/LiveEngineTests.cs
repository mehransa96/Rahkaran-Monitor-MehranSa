using Microsoft.Extensions.Logging.Abstractions;
using RSM.Core.Live;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;
using RSM.Core.Scheduling;
using RSM.Hosting;

namespace RSM.Tests;

public class LiveEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 6, 0, 0, TimeSpan.Zero);

    // ---------- metric store ----------

    [Fact]
    public void Store_keeps_series_sorted_bounded_and_within_retention()
    {
        var store = new MetricStore(TimeSpan.FromMinutes(10), maxPointsPerSeries: 50);
        store.Add("n1", Enumerable.Range(0, 30).Select(i => new MetricSample("sql.cpu.sql_pct", null, i, T0.AddMinutes(i))));

        var series = store.Series("n1", "sql.cpu.sql_pct", null, DateTimeOffset.MinValue);
        series.Should().HaveCount(11, "only the last 10 minutes are kept");
        series[0].AtUtc.Should().Be(T0.AddMinutes(19));

        // Back-filled points land in order; a repeated timestamp replaces the value.
        store.Add("n1", [new MetricSample("sql.cpu.sql_pct", null, 99, T0.AddMinutes(25).AddSeconds(30)), new MetricSample("sql.cpu.sql_pct", null, 7, T0.AddMinutes(29))]);
        series = store.Series("n1", "sql.cpu.sql_pct", null, DateTimeOffset.MinValue);
        series.Select(p => p.AtUtc).Should().BeInAscendingOrder();
        series[^1].Value.Should().Be(7);
        series.Should().Contain(p => p.Value == 99);

        store.Add("n1", Enumerable.Range(0, 200).Select(i => new MetricSample("sql.perf.batch_requests_sec", null, i, T0.AddMinutes(30).AddSeconds(i))));
        store.Series("n1", "sql.perf.batch_requests_sec", null, DateTimeOffset.MinValue).Should().HaveCount(50, "series are capped");
        store.Latest("n1", "sql.perf.batch_requests_sec")!.Value.Value.Should().Be(199);
    }

    [Fact]
    public void Store_lists_instances_and_ignores_non_finite_values()
    {
        var store = new MetricStore();
        store.Add("n1",
        [
            new MetricSample("sql.io.data_read_ms", "Rahkaran", 4, T0),
            new MetricSample("sql.io.data_read_ms", "tempdb", 1, T0),
            new MetricSample("sql.io.data_read_ms", "master", double.NaN, T0),
        ]);

        store.Instances("n1", "sql.io.data_read_ms").Should().Equal("Rahkaran", "tempdb");
        store.Instances("n2", "sql.io.data_read_ms").Should().BeEmpty();
    }

    // ---------- live session ----------

    private sealed class FactCollector(string id, Func<int, IReadOnlyList<Fact>> facts) : ICollector
    {
        private int _runs;

        public string Id => id;

        public Layer Layer => Layer.Sql;

        public CollectorCost Cost => CollectorCost.Cheap;

        public NodeRoles AppliesTo => NodeRoles.Data;

        public TimeSpan DefaultInterval => TimeSpan.FromMilliseconds(50);

        public int Runs => _runs;

        public Task<CollectorResult> CollectAsync(INodeContext node, CollectContext context, CancellationToken cancellationToken)
        {
            var run = Interlocked.Increment(ref _runs);
            return Task.FromResult(new CollectorResult(id, node.Node.Id, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(5), CollectorStatus.Ok, null,
                [new MetricSample("test.metric", null, run, DateTimeOffset.UtcNow.AddMilliseconds(run))], facts(run),
                [new UnifiedEvent(DateTimeOffset.UtcNow, node.Node.Id, Layer.Sql, "test", "e", Severity.Info, $"run {run}")]));
        }
    }

    private static readonly IReadOnlyList<CompiledRule> Rules = new RuleRepository(NullLogger<RuleRepository>.Instance).Load(null).Rules;

    private static LiveNode Node(params ICollector[] collectors) => new(
        new NodeContext(new SiteProfile { Id = "s", Name = "s" }, new NodeProfile { Id = "n1", Name = "DB01", Host = "db01", Roles = NodeRoles.Data }, []),
        collectors,
        new Dictionary<string, object?> { ["node.role.data"] = true });

    [Fact]
    public async Task Live_facts_drive_rules_and_findings_keep_their_first_seen_time()
    {
        var tempdbFiles = 1;
        var collector = new FactCollector("test.tempdb", _ => [new Fact("tempdb.data_files", tempdbFiles), new Fact("sql.cpu.logical", 16)]);
        await using var session = new LiveSession([Node(collector)], new RulesEngine(Rules));

        await session.TickAsync(CancellationToken.None);
        var first = session.Findings("n1").Should().ContainSingle(f => f.RuleId == "SQL-TDB-001").Subject;
        await Task.Delay(20);
        await session.TickAsync(CancellationToken.None);
        var again = session.Findings("n1").Single(f => f.RuleId == "SQL-TDB-001");
        again.FirstSeenUtc.Should().Be(first.FirstSeenUtc);
        again.LastSeenUtc.Should().BeAfter(first.LastSeenUtc);

        tempdbFiles = 8;
        await session.TickAsync(CancellationToken.None);
        session.Findings("n1").Should().NotContain(f => f.RuleId == "SQL-TDB-001", "the condition cleared");

        session.Metrics.Series("n1", "test.metric", null, DateTimeOffset.MinValue).Should().HaveCount(3);
        session.Events().Should().HaveCount(3);
        session.Latest("n1", "test.tempdb")!.Facts.Should().Contain(new Fact("tempdb.data_files", 8));
        session.Facts("n1")["node.role.data"].Should().Be(true, "static node facts are seeded");
    }

    [Fact]
    public async Task Stats_report_runs_and_rsm_overhead_per_node()
    {
        var collector = new FactCollector("rsm.overhead.sql", _ => [new Fact("rsm.sql_cpu_pct", 0.4), new Fact("rsm.sql_rtt_ms", 2.5)]);
        await using var session = new LiveSession([Node(collector)], new RulesEngine([]));

        await session.TickAsync(CancellationToken.None);
        await session.TickAsync(CancellationToken.None);
        var stats = session.Stats();

        stats.Nodes["n1"].RunsPerMinute.Should().Be(2);
        stats.Nodes["n1"].CollectorSecondsPerMinute.Should().BeApproximately(0.01, 1e-9);
        stats.Nodes["n1"].SqlCpuPercent.Should().Be(0.4);
        stats.Nodes["n1"].SqlRoundTripMs.Should().Be(2.5);
        stats.ClientWorkingSetMb.Should().BeGreaterThan(0);
        stats.Series.Should().Be(1);
    }

    [Fact]
    public async Task Paused_scheduler_runs_nothing_until_resumed()
    {
        var collector = new FactCollector("test.pause", _ => []);
        await using var session = new LiveSession([Node(collector)], new RulesEngine([]));
        session.Scheduler.Paused = true;
        session.Start();

        await Task.Delay(300);
        collector.Runs.Should().Be(0);

        session.Scheduler.Paused = false;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (collector.Runs == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        collector.Runs.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Interval_scale_has_a_floor()
    {
        var scheduler = new CollectorScheduler();
        scheduler.IntervalScale = 0.01;
        scheduler.IntervalScale.Should().Be(0.25);
        scheduler.IntervalScale = 4;
        scheduler.IntervalScale.Should().Be(4);
    }
}
