using RSM.Collectors.Database;
using RSM.Collectors.Sql.Live;
using RSM.Core.Model;
using RSM.Parsers.Showplan;
using RSM.Parsers.Sql;

namespace RSM.Tests;

/// <summary>Analysis logic of the live collectors (the SQL itself is exercised against a real server in CI).</summary>
public class LiveCollectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Start = new(2026, 10, 14, 22, 0, 0);

    private static string Fixture(params string[] path) => File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "Fixtures", .. path]));

    private static object? FactValue(LiveOutput output, string key) => output.Facts.LastOrDefault(f => f.Key == key)?.Value;

    // ---------- sql.perf ----------

    private static PerfSample Sample(DateTimeOffset at, long batches, long grantsPending, long ple, long waitMs, long signalMs, long reads, long readStall,
        DateTime? start = null, IReadOnlyList<WaitingTaskRow>? waiting = null) =>
        new(at,
            new PerfCounterSnapshot(at, start ?? Start,
            [
                new("SQLServer:SQL Statistics", "Batch Requests/sec", "", batches, PerfCounterMath.PerSecond),
                new("SQLServer:Memory Manager", "Memory Grants Pending", "", grantsPending, PerfCounterMath.Raw),
                new("SQLServer:Buffer Manager", "Page life expectancy", "", ple, PerfCounterMath.RawLarge),
                new("SQLServer:Memory Manager", "Total Server Memory (KB)", "", 16L * 1024 * 1024, PerfCounterMath.RawLarge),
                new("SQLServer:Memory Manager", "Target Server Memory (KB)", "", 16L * 1024 * 1024, PerfCounterMath.RawLarge),
            ]),
            [new WaitRow("PAGEIOLATCH_SH", 100, waitMs, signalMs), new WaitRow("SLEEP_TASK", 100, 999_999, 0)],
            [new FileIoSample("Rahkaran", reads, readStall, 1_000, 2_000)],
            waiting);

    [Fact]
    public void Perf_turns_cumulative_counters_waits_and_file_stats_into_interval_values()
    {
        var state = new PerfState();
        SqlPerfCollector.Apply(state, Sample(T0, 1_000, 0, 5_000, 10_000, 1_000, 500, 5_000), new LiveOutput());
        var output = new LiveOutput();

        SqlPerfCollector.Apply(state, Sample(T0.AddSeconds(10), 1_500, 0, 5_010, 30_000, 7_000, 600, 8_000), output);

        FactValue(output, "sql.perf.interval_s").Should().Be(10d);
        FactValue(output, "sql.perf.batch_requests_sec").Should().Be(50d);
        FactValue(output, "sql.waits.ms_per_sec").Should().Be(2_000d, "SLEEP_TASK is benign and dropped");
        FactValue(output, "sql.waits.signal_pct").Should().Be(30d);
        FactValue(output, "sql.waits.dominant").Should().Be("IoData");
        ((string)FactValue(output, "sql.waits.dominant_explain_en")!).Should().Contain("PAGEIOLATCH");
        FactValue(output, "db[Rahkaran].io.interval_reads").Should().Be(100L);
        FactValue(output, "db[Rahkaran].io.interval_read_ms").Should().Be(30d);
        FactValue(output, "db[Rahkaran].io.interval_log_writes").Should().Be(0L);
        FactValue(output, "sql.perf.ple_floor_s").Should().Be(1_200d, "300 s per 4 GB of a 16 GB buffer pool");
        output.Samples.Should().Contain(new MetricSample("sql.io.data_read_ms", "Rahkaran", 30, T0.AddSeconds(10)));
        output.Samples.Should().Contain(s => s.Key == "sql.waits.ms_per_sec" && s.Instance == "IoData");
    }

    [Fact]
    public void Perf_sustained_facts_need_three_samples()
    {
        var state = new PerfState();
        LiveOutput Tick(int i, long pending, long ple)
        {
            var o = new LiveOutput();
            SqlPerfCollector.Apply(state, Sample(T0.AddSeconds(15 * i), 1_000 + i, pending, ple, 1_000 + i, 0, 10, 10), o);
            return o;
        }

        FactValue(Tick(0, 2, 100), "sql.perf.memory_grants_pending_min3").Should().BeNull();
        FactValue(Tick(1, 3, 200), "sql.perf.memory_grants_pending_min3").Should().BeNull();
        var third = Tick(2, 1, 150);
        FactValue(third, "sql.perf.memory_grants_pending_min3").Should().Be(1d);
        FactValue(third, "sql.perf.ple_max3").Should().Be(200d);
        FactValue(Tick(3, 0, 150), "sql.perf.memory_grants_pending_min3").Should().Be(0d);
    }

    [Fact]
    public void Perf_restart_or_stale_baseline_gives_no_interval_values()
    {
        var state = new PerfState();
        SqlPerfCollector.Apply(state, Sample(T0, 1_000, 0, 5_000, 10_000, 1_000, 500, 5_000), new LiveOutput());
        var restarted = new LiveOutput();
        SqlPerfCollector.Apply(state, Sample(T0.AddSeconds(10), 10, 0, 5, 100, 10, 5, 5, start: Start.AddHours(2)), restarted);
        FactValue(restarted, "sql.perf.interval_s").Should().BeNull();
        FactValue(restarted, "sql.waits.ms_per_sec").Should().BeNull();
        FactValue(restarted, "sql.perf.ple_s").Should().Be(5d, "raw counters still flow");

        var stale = new LiveOutput();
        SqlPerfCollector.Apply(state, Sample(T0.AddMinutes(30), 2_000, 0, 5, 200, 10, 6, 6, start: Start.AddHours(2)), stale);
        FactValue(stale, "sql.perf.batch_requests_sec").Should().BeNull("a 30-minute-old baseline is not an interval");
        SqlPerfCollector.NeedsBaseline(null, T0).Should().BeTrue();
    }

    [Theory]
    [InlineData("2:1:1", true)]
    [InlineData("2:3:8088", true)]
    [InlineData("2:1:2", true)]
    [InlineData("2:1:3", true)]
    [InlineData("2:1:511234", true)]
    [InlineData("2:1:120", false)]
    [InlineData("5:1:1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Recognises_tempdb_allocation_pages(string? resource, bool expected) =>
        SqlPerfCollector.IsTempdbAllocationPage(resource).Should().Be(expected);

    [Fact]
    public void Perf_counts_waiters_on_tempdb_allocation_pages_and_reports_pending_io()
    {
        var output = new LiveOutput();
        SqlPerfCollector.Apply(new PerfState(), Sample(T0, 1, 0, 1, 1, 0, 1, 1, waiting:
        [
            new WaitingTaskRow(60, "PAGELATCH_UP", 12, 0, "2:1:1"),
            new WaitingTaskRow(61, "PAGELATCH_EX", 3, 0, "2:4:8088"),
            new WaitingTaskRow(62, "PAGELATCH_EX", 3, 0, "7:1:200"),
            new WaitingTaskRow(63, "LCK_M_X", 30_000, 60, "keylock …"),
        ]) with { Pending = new() { PendingCount = 2, MaxPendingMs = 16_000 } }, output);

        FactValue(output, "tempdb.alloc_contention_waiters").Should().Be(2);
        FactValue(output, "io.max_pending_ms").Should().Be(16_000L);
    }

    // ---------- sql.cpu ----------

    private static CpuPoint Point(int minutesAgo, int sql, int idle) => new(T0.AddMinutes(-minutesAgo), sql, idle, Math.Max(0, 100 - sql - idle));

    [Fact]
    public void Cpu_sustained_facts_use_the_last_five_recent_minutes()
    {
        var points = Enumerable.Range(0, 10).Reverse().Select(m => Point(m, m < 5 ? 90 : 20, 5)).ToList();
        var output = new LiveOutput();

        SqlCpuCollector.Apply(new CpuState(), points, T0, output);

        FactValue(output, "sql.cpu.sql_pct").Should().Be(90);
        FactValue(output, "sql.cpu.other_pct").Should().Be(5);
        FactValue(output, "sql.cpu.sql_pct_min5").Should().Be(90);
        FactValue(output, "sql.cpu.other_pct_min5").Should().Be(5);
        output.Samples.Count(s => s.Key == "sql.cpu.sql_pct").Should().Be(10, "the first read back-fills the chart");
    }

    [Fact]
    public void Cpu_emits_only_new_points_and_skips_sustained_facts_when_history_has_gaps()
    {
        var state = new CpuState();
        SqlCpuCollector.Apply(state, [Point(30, 50, 40), Point(29, 50, 40)], T0, new LiveOutput());
        var output = new LiveOutput();

        SqlCpuCollector.Apply(state, [Point(30, 50, 40), Point(29, 50, 40), Point(1, 95, 0), Point(0, 95, 0)], T0, output);

        output.Samples.Count(s => s.Key == "sql.cpu.sql_pct").Should().Be(2);
        FactValue(output, "sql.cpu.sql_pct_min5").Should().BeNull("only two points in the last six minutes");
        FactValue(output, "sql.cpu.other_pct").Should().Be(5);
    }

    // ---------- sql.activity ----------

    private static SqlActivityCollector.ChainRow Chain(int head, int session, int blocker, int level, string status, long waitMs, string statement, long? tranAge = null) => new()
    {
        HeadSessionId = head, SessionId = session, BlockingSessionId = blocker, ChainLevel = level, LoginName = "rk_app", HostName = "APP01",
        ProgramName = "Rahkaran", DatabaseName = "Rahkaran", SessionStatus = status, RequestStatus = level == 0 && status == "sleeping" ? null : "suspended",
        WaitType = level == 0 ? null : "LCK_M_U", WaitMs = waitMs, OpenTranCount = level == 0 ? 1 : 0, TranAgeS = tranAge, StatementText = statement,
    };

    [Fact]
    public void Blocking_chains_name_the_head_blocker_its_statement_and_transaction_age()
    {
        var rows = new[]
        {
            Chain(55, 55, 0, 0, "sleeping", 0, "BEGIN TRAN; UPDATE dbo.Accounts SET Balance = 1 WHERE Id = 1", tranAge: 240),
            Chain(55, 60, 55, 1, "running", 45_000, "UPDATE dbo.Accounts SET Balance = 2 WHERE Id = 1"),
            Chain(55, 61, 60, 2, "running", 20_000, "SELECT * FROM dbo.Accounts WITH (UPDLOCK)"),
            Chain(70, 70, 0, 0, "running", 0, "UPDATE dbo.Other"),
            Chain(70, 71, 70, 1, "running", 2_000, "SELECT 1"),
        };
        var output = new LiveOutput();

        SqlActivityCollector.Apply(T0, [], rows, null, null, null, output);

        var chains = (IReadOnlyList<BlockingChain>)output.Payload["blockingChains"]!;
        chains.Should().HaveCount(2);
        chains[0].Head.SessionId.Should().Be(55);
        chains[0].HeadIsIdle.Should().BeTrue();
        chains[0].Blocked.Select(b => b.SessionId).Should().Equal(60, 61);
        FactValue(output, "sql.blocking.chains").Should().Be(2);
        FactValue(output, "sql.blocking.blocked_sessions").Should().Be(3);
        FactValue(output, "sql.blocking.max_wait_s").Should().Be(45d);
        FactValue(output, "sql.blocking.head_session").Should().Be(55);
        FactValue(output, "sql.blocking.head_idle").Should().Be(true);
        ((string)FactValue(output, "sql.blocking.head_summary")!).Should().Contain("session 55").And.Contain("open 240 s").And.Contain("UPDATE dbo.Accounts");
    }

    [Fact]
    public void No_blocking_reports_zero_not_unknown()
    {
        var output = new LiveOutput();
        SqlActivityCollector.Apply(T0, [], [], [], [], [], output);

        FactValue(output, "sql.blocking.max_wait_s").Should().Be(0d);
        FactValue(output, "sql.idle_open_tran.max_age_s").Should().Be(0L);
        FactValue(output, "sql.tran.max_age_s").Should().Be(0L);
        FactValue(output, "sql.tran.max_log_mb").Should().Be(0d);
    }

    [Fact]
    public void Transactions_sum_log_use_across_databases_and_find_the_oldest()
    {
        var output = new LiveOutput();
        SqlActivityCollector.Apply(T0, null, null,
            [new() { SessionId = 80, LoginName = "u", HostName = "h", ProgramName = "p", IdleTranAgeS = 600, LastStatement = "UPDATE x" }],
            [
                new() { SessionId = 80, TransactionId = 1, AgeS = 700, DatabaseName = "Rahkaran", LogBytesUsed = 1L << 30, LogBytesReserved = 1L << 29, SessionStatus = "sleeping" },
                new() { SessionId = 80, TransactionId = 1, AgeS = 700, DatabaseName = "tempdb", LogBytesUsed = 1L << 20, LogBytesReserved = 0 },
                new() { SessionId = 81, TransactionId = 2, AgeS = 5, DatabaseName = "Rahkaran", LogBytesUsed = 0, LogBytesReserved = 0 },
            ],
            null, output);

        FactValue(output, "sql.idle_open_tran.max_age_s").Should().Be(600L);
        FactValue(output, "sql.tran.open_count").Should().Be(2);
        FactValue(output, "sql.tran.max_age_s").Should().Be(700L);
        FactValue(output, "sql.tran.max_log_mb").Should().Be(1_537d);
    }

    // ---------- sql.deadlocks ----------

    [Fact]
    public void Deadlocks_are_counted_in_the_window_and_raised_as_events_once()
    {
        var graphs = DeadlockReport.Parse(Fixture("deadlock", "ring_buffer.xml"));
        var state = new DeadlockState();
        var now = graphs.Max(g => g.AtUtc)!.Value.AddHours(1);
        var first = new LiveOutput();

        SqlDeadlockCollector.Apply(state, "n1", graphs.Concat(graphs), now.AddHours(-24), now, "ring_buffer", first);

        FactValue(first, "sql.deadlocks.count_24h").Should().Be(2, "duplicates from file rollover are merged");
        first.Events.Should().HaveCount(2).And.OnlyContain(e => e.Code == "xml_deadlock_report" && e.Message.StartsWith("victim "));
        var second = new LiveOutput();
        SqlDeadlockCollector.Apply(state, "n1", graphs, now.AddHours(-24), now, "ring_buffer", second);
        second.Events.Should().BeEmpty();

        var later = new LiveOutput();
        SqlDeadlockCollector.Apply(state, "n1", graphs, now.AddDays(2).AddHours(-24), now.AddDays(2), "file", later);
        FactValue(later, "sql.deadlocks.count_24h").Should().Be(0);
    }

    [Fact]
    public void Deadlock_description_names_victim_objects_and_modes()
    {
        var graph = DeadlockReport.Parse(Fixture("deadlock", "event_file.xml")).Single();

        SqlDeadlockCollector.Describe(graph).Should().StartWith("victim 61 (BEGIN TRAN; UPDATE dbo.Accounts")
            .And.Contain("Rahkaran.dbo.Accounts").And.Contain("X/U");
    }

    // ---------- sql.querystats ----------

    [Fact]
    public void Query_stats_report_plan_cache_share_and_plan_warning_kinds()
    {
        var plan = ShowplanAnalyzer.Analyze(Fixture("showplan", "key_lookup.sqlplan"));
        var output = new LiveOutput();

        SqlQueryStatsCollector.Apply(T0,
            [new TopQuery(new() { Executions = 4, TotalCpuMs = 400, StatementText = "SELECT …" }, plan), new TopQuery(new() { Executions = 0 }, null)],
            [],
            [
                new() { ObjType = "Adhoc", Plans = 900, Bytes = 600L << 20, SingleUsePlans = 800, SingleUseBytes = 500L << 20 },
                new() { ObjType = "Proc", Plans = 100, Bytes = 400L << 20, SingleUsePlans = 0, SingleUseBytes = 0 },
            ],
            [new() { TableName = "[Rahkaran].[dbo].[Voucher]", EqualityColumns = "[Date]", AvgUserImpact = 87 }],
            output);

        FactValue(output, "sql.plan_cache.single_use_pct").Should().Be(50d);
        FactValue(output, "sql.plan_cache.mb").Should().Be(1_000d);
        FactValue(output, "sql.qs.plans_analysed").Should().Be(1);
        ((IEnumerable<object?>)FactValue(output, "sql.qs.plan_warning_kinds")!).Should().Contain("KeyLookup");
        FactValue(output, "sql.idx.missing_count").Should().Be(1);
        new TopQuery(new() { Executions = 4, TotalCpuMs = 400 }, null).AvgCpuMs.Should().Be(100);
        SqlQueryStatsCollector.Describe(new() { TableName = "T", EqualityColumns = "[A]", AvgUserImpact = 87.4 }).Should().Contain("impact 87 %");
    }

    // ---------- rsm.overhead.sql ----------

    [Fact]
    public void Self_overhead_is_cpu_delta_over_server_time_and_cores_and_survives_pool_trims()
    {
        var state = new OverheadState();
        SqlSelfOverheadCollector.Apply(state, new() { Sessions = 2, CpuMs = 1_000, CpuCount = 8, MsTicks = 100_000 }, TimeSpan.FromMilliseconds(3), T0, new LiveOutput());
        var output = new LiveOutput();

        SqlSelfOverheadCollector.Apply(state, new() { Sessions = 2, CpuMs = 1_160, CpuCount = 8, MsTicks = 110_000 }, TimeSpan.FromMilliseconds(4), T0.AddSeconds(10), output);

        FactValue(output, "rsm.sql_cpu_pct").Should().Be(0.2, "160 ms of CPU over 10 s × 8 cores");
        FactValue(output, "rsm.sql_rtt_ms").Should().Be(4d);
        var trimmed = new LiveOutput();
        SqlSelfOverheadCollector.Apply(state, new() { Sessions = 1, CpuMs = 10, CpuCount = 8, MsTicks = 120_000 }, null, T0.AddSeconds(20), trimmed);
        FactValue(trimmed, "rsm.sql_cpu_pct").Should().BeNull();
    }

    // ---------- sql.ha ----------

    [Fact]
    public void Availability_groups_report_health_and_queues_per_replica()
    {
        var output = new LiveOutput();
        SqlAvailabilityCollector.Apply(
        [
            new() { AgName = "AG1", ReplicaName = "SQL1", RoleDesc = "PRIMARY", Health = "HEALTHY", DatabaseName = "Rahkaran", SyncState = "SYNCHRONIZED" },
            new() { AgName = "AG1", ReplicaName = "SQL2", RoleDesc = "SECONDARY", Health = "NOT_HEALTHY", DatabaseName = "Rahkaran", SyncState = "NOT SYNCHRONIZING", LogSendQueueKb = 2L * 1024 * 1024, RedoQueueKb = 1024 },
        ], T0, output);

        FactValue(output, "sql.ha.ag_replicas").Should().Be(2);
        FactValue(output, "ag[AG1/SQL1].healthy").Should().Be(true);
        FactValue(output, "ag[AG1/SQL2].healthy").Should().Be(false);
        FactValue(output, "ag[AG1/SQL2].send_queue_mb").Should().Be(2_048d);
        ((IEnumerable<object?>)FactValue(output, "ag[AG1/SQL2].not_synchronizing_dbs")!).Should().Equal("Rahkaran");
    }

    // ---------- sql.querystore ----------

    [Fact]
    public void Query_store_facts_count_regressions_and_failing_forced_plans()
    {
        var facts = new List<Fact>();
        QueryStoreCollector.Apply("db[Rahkaran]",
            [new() { QueryId = 7, BaselineAvgUs = 10_000, RecentAvgUs = 45_000, Plans = 2 }],
            [new() { QueryId = 7, PlanId = 3, ForceFailureCount = 4, LastFailureReason = "NO_INDEX" }, new() { QueryId = 8, PlanId = 9 }],
            facts);

        facts.Should().Contain(new Fact("db[Rahkaran].qds.regressed_count", 1));
        facts.Should().Contain(new Fact("db[Rahkaran].qds.forced_failures", 1));
        facts.Should().Contain(new Fact("db[Rahkaran].qds.forced_failure_reason", "NO_INDEX"));
        facts.Single(f => f.Key == "db[Rahkaran].qds.worst_regression").Value.Should().Be("query 7: 10 ms → 45 ms, 2 plans");
    }

    [Fact]
    public void Unused_index_candidates_exclude_keys_unique_and_read_indexes()
    {
        var rows = new QueryStoreCollector.IndexUsageRow[]
        {
            new() { IndexName = "IX_unused", IndexType = "NONCLUSTERED", UserUpdates = 50 },
            new() { IndexName = "IX_read", IndexType = "NONCLUSTERED", UserUpdates = 50, UserSeeks = 1 },
            new() { IndexName = "UX", IndexType = "NONCLUSTERED", IsUnique = true, UserUpdates = 50 },
            new() { IndexName = "PK", IndexType = "CLUSTERED", IsPrimaryKey = true, UserUpdates = 50 },
            new() { IndexName = "IX_idle", IndexType = "NONCLUSTERED" },
        };

        QueryStoreCollector.UnusedCandidates(rows).Select(r => r.IndexName).Should().Equal("IX_unused");
    }
}
