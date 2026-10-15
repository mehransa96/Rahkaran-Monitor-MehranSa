using RSM.Parsers.Showplan;
using RSM.Parsers.Sql;
using RSM.Parsers.Xml;

namespace RSM.Tests;

public class LiveParserTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Start = new(2026, 10, 14, 22, 0, 0);

    private static string Fixture(params string[] path) => File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "Fixtures", .. path]));

    // ---------- performance counters ----------

    private static PerfCounterRow Row(string obj, string counter, long value, int type, string instance = "") => new($"SQLServer:{obj}", counter, instance, value, type);

    [Fact]
    public void Per_second_counters_use_the_delta_over_elapsed_time()
    {
        var before = new PerfCounterSnapshot(T0, Start, [Row("SQL Statistics", "Batch Requests/sec", 1_000, PerfCounterMath.PerSecond)]);
        var after = new PerfCounterSnapshot(T0.AddSeconds(10), Start, [Row("SQL Statistics", "Batch Requests/sec", 1_500, PerfCounterMath.PerSecond)]);

        PerfCounterMath.Compute(before, after).Should().ContainSingle()
            .Which.Should().Be(new PerfCounterValue("sql.perf.batch_requests_sec", null, 50));
    }

    [Fact]
    public void First_sample_restart_or_reset_gives_no_rate_but_raw_counters_still_flow()
    {
        var rows = new[] { Row("SQL Statistics", "Batch Requests/sec", 10, PerfCounterMath.PerSecond), Row("Buffer Manager", "Page life expectancy", 3_600, PerfCounterMath.RawLarge) };
        PerfCounterMath.Compute(null, new PerfCounterSnapshot(T0, Start, rows)).Select(v => v.Key).Should().Equal("sql.perf.ple_s");

        var before = new PerfCounterSnapshot(T0, Start, [Row("SQL Statistics", "Batch Requests/sec", 9_000, PerfCounterMath.PerSecond)]);
        var restarted = new PerfCounterSnapshot(T0.AddSeconds(5), Start.AddHours(1), [Row("SQL Statistics", "Batch Requests/sec", 10, PerfCounterMath.PerSecond)]);
        PerfCounterMath.Compute(before, restarted).Should().BeEmpty();

        var reset = new PerfCounterSnapshot(T0.AddSeconds(5), Start, [Row("SQL Statistics", "Batch Requests/sec", 10, PerfCounterMath.PerSecond)]);
        PerfCounterMath.Compute(before, reset).Should().BeEmpty();
    }

    [Fact]
    public void Instance_counters_keep_their_database_or_numa_node_and_named_instances_are_recognised()
    {
        var before = new PerfCounterSnapshot(T0, Start, [new("MSSQL$RK:Databases", "Transactions/sec", "Rahkaran", 100, PerfCounterMath.PerSecond)]);
        var after = new PerfCounterSnapshot(T0.AddSeconds(2), Start,
        [
            new("MSSQL$RK:Databases", "Transactions/sec", "Rahkaran", 140, PerfCounterMath.PerSecond),
            new("MSSQL$RK:Buffer Node", "Page life expectancy", "001", 1_200, PerfCounterMath.RawLarge),
            new("MSSQL$RK:Memory Manager", "Total Server Memory (KB)", "", 8_388_608, PerfCounterMath.RawLarge),
        ]);

        PerfCounterMath.Compute(before, after).Should().BeEquivalentTo(new[]
        {
            new PerfCounterValue("sql.perf.transactions_sec", "Rahkaran", 20),
            new PerfCounterValue("sql.perf.ple_node_s", "001", 1_200),
            new PerfCounterValue("sql.perf.total_memory_mb", null, 8_192),
        });
    }

    [Fact]
    public void Fraction_and_average_counters_divide_by_their_base()
    {
        var before = new PerfCounterSnapshot(T0, Start,
        [
            Row("Locks", "Lock Waits/sec", 0, PerfCounterMath.PerSecond, "_Total"),
            new("SQLServer:Access Methods", "Full Scans/sec", "", 0, PerfCounterMath.AverageBulk),
            new("SQLServer:Access Methods", "Full Scans/sec base", "", 0, PerfCounterMath.Base),
        ]);
        var after = new PerfCounterSnapshot(T0.AddSeconds(1), Start,
        [
            Row("Locks", "Lock Waits/sec", 3, PerfCounterMath.PerSecond, "_Total"),
            new("SQLServer:Access Methods", "Full Scans/sec", "", 30, PerfCounterMath.AverageBulk),
            new("SQLServer:Access Methods", "Full Scans/sec Base", "", 10, PerfCounterMath.Base),
        ]);

        var values = PerfCounterMath.Compute(before, after).ToDictionary(v => v.Key, v => v.Value);
        values["sql.perf.lock_waits_sec"].Should().Be(3);
        values["sql.perf.full_scans_sec"].Should().Be(3);
    }

    // ---------- waits ----------

    [Fact]
    public void Wait_interval_drops_idle_waits_and_finds_the_dominant_category()
    {
        IReadOnlyList<WaitRow> before =
        [
            new("PAGEIOLATCH_SH", 100, 10_000, 100), new("SOS_SCHEDULER_YIELD", 10, 1_000, 1_000),
            new("LAZYWRITER_SLEEP", 5, 90_000, 0), new("WRITELOG", 50, 2_000, 10),
        ];
        IReadOnlyList<WaitRow> after =
        [
            new("PAGEIOLATCH_SH", 400, 40_000, 400), new("SOS_SCHEDULER_YIELD", 20, 3_000, 3_000),
            new("LAZYWRITER_SLEEP", 6, 190_000, 0), new("WRITELOG", 40, 1_000, 5),   // reset → dropped
            new("ASYNC_NETWORK_IO", 5, 5_000, 50),                                  // new wait type
        ];

        var interval = WaitStats.Interval(before, after, 60);

        interval.Waits.Select(w => w.WaitType).Should().Equal("PAGEIOLATCH_SH", "ASYNC_NETWORK_IO", "SOS_SCHEDULER_YIELD");
        interval.TotalWaitMs.Should().Be(37_000);
        interval.SignalPercent.Should().BeApproximately(100.0 * (300 + 50 + 2_000) / 37_000, 0.001);
        interval.Dominant()!.Category.Should().Be(WaitCategory.IoData);
        WaitStats.Categorize("LCK_M_X").Should().Be(WaitCategory.Locking);
        WaitStats.Categorize("ASYNC_NETWORK_IO").Should().Be(WaitCategory.Network);
        WaitStats.Categorize("THREADPOOL").Should().Be(WaitCategory.Worker);
        WaitStats.IsBenign("XE_TIMER_EVENT").Should().BeTrue();
        WaitStats.Explain(WaitCategory.Network).Fa.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_quiet_interval_has_no_dominant_category() =>
        WaitStats.Interval([new("WRITELOG", 1, 10, 0)], [new("WRITELOG", 2, 20, 0)], 60).Dominant().Should().BeNull();

    // ---------- CPU ring buffer ----------

    [Fact]
    public void Ring_buffer_records_split_sql_and_other_process_cpu()
    {
        static string Record(int sql, int idle) =>
            $"<Record id=\"1\" type=\"RING_BUFFER_SCHEDULER_MONITOR\" time=\"1\"><SchedulerMonitorEvent><SystemHealth><ProcessUtilization>{sql}</ProcessUtilization><SystemIdle>{idle}</SystemIdle><UserModeTime>0</UserModeTime><KernelModeTime>0</KernelModeTime><PageFaults>0</PageFaults><WorkingSetDelta>0</WorkingSetDelta><MemoryUtilization>100</MemoryUtilization></SystemHealth></SchedulerMonitorEvent></Record>";

        var points = CpuRingBuffer.Parse(
        [
            new RingBufferRow(Record(35, 20), RecordTicks: 1_000_000, NowTicks: 1_060_000),
            new RingBufferRow(Record(10, 85), RecordTicks: 1_030_000, NowTicks: 1_060_000),
            new RingBufferRow("<not-xml", 0, 0),
        ], T0);

        points.Should().HaveCount(2);
        points[0].Should().Be(new CpuPoint(T0.AddMinutes(-1), 35, 20, 45));
        points[1].OtherPercent.Should().Be(5);
    }

    // ---------- deadlocks ----------

    [Fact]
    public void Deadlock_report_from_event_file_names_victim_statements_objects_and_modes()
    {
        var graph = DeadlockReport.Parse(Fixture("deadlock", "event_file.xml")).Should().ContainSingle().Subject;

        graph.AtUtc.Should().Be(new DateTimeOffset(2026, 10, 15, 6, 12, 30, 123, TimeSpan.Zero));
        graph.Processes.Should().HaveCount(2);
        var victim = graph.Victims.Should().ContainSingle().Subject;
        victim.Spid.Should().Be(61);
        victim.Login.Should().Be("rk_app");
        victim.Host.Should().Be("APP01");
        victim.Database.Should().Be("Rahkaran");
        victim.LockMode.Should().Be("U");
        victim.Statement.Should().StartWith("BEGIN TRAN; UPDATE dbo.Accounts");
        victim.Frames.Should().ContainSingle().Which.Should().Contain("WHERE Id = 1");
        graph.Resources.Should().HaveCount(2).And.OnlyContain(r => r.Kind == "keylock" && r.ObjectName == "Rahkaran.dbo.Accounts" && r.IndexName == "PK_Accounts");
        graph.Resources[0].Owners.Should().ContainSingle().Which.Should().Be(new DeadlockLockParty("process1f2a3c088", "X", null));
        graph.Resources[0].Waiters.Should().ContainSingle().Which.Should().Be(new DeadlockLockParty("process1f2a3b4c8", "U", "wait"));
        graph.Id.Should().HaveLength(16);
    }

    [Fact]
    public void Ring_buffer_target_yields_every_deadlock_and_ignores_other_events()
    {
        var graphs = DeadlockReport.Parse(Fixture("deadlock", "ring_buffer.xml"));

        graphs.Should().HaveCount(2);
        graphs.Select(g => g.Id).Distinct().Should().HaveCount(2, "different graphs get different ids");
        graphs.SelectMany(g => g.Victims).Select(v => v.Spid).Should().Equal(61, 71);
    }

    // ---------- showplan ----------

    [Theory]
    [InlineData("missing_index.sqlplan", PlanWarningKind.MissingIndex, "VoucherItem")]
    [InlineData("implicit_conversion.sqlplan", PlanWarningKind.ImplicitConversion, "CONVERT_IMPLICIT")]
    [InlineData("spill.sqlplan", PlanWarningKind.SpillToTempDb, "Sort")]
    [InlineData("key_lookup.sqlplan", PlanWarningKind.KeyLookup, "Voucher")]
    [InlineData("rid_lookup.sqlplan", PlanWarningKind.RidLookup, "ImportBuffer")]
    [InlineData("large_scan.sqlplan", PlanWarningKind.LargeScan, "2500000")]
    [InlineData("excessive_grant.sqlplan", PlanWarningKind.ExcessiveMemoryGrant, "3072 MB")]
    [InlineData("parallelism.sqlplan", PlanWarningKind.Parallelism, "DOP 4")]
    [InlineData("no_join_predicate.sqlplan", PlanWarningKind.NoJoinPredicate, "predicate")]
    public void Showplan_analyzer_flags_each_warning_type(string file, PlanWarningKind kind, string detail)
    {
        var analysis = ShowplanAnalyzer.Analyze(Fixture("showplan", file));

        analysis.Parsed.Should().BeTrue();
        analysis.Warnings.Should().Contain(w => w.Kind == kind && w.Detail.Contains(detail, StringComparison.Ordinal), file);
        analysis.Warnings.Select(w => w.Kind).Should().OnlyContain(k => k == kind, $"{file} should flag only {kind}");
        ShowplanAnalyzer.Explain(kind).En.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_clean_plan_has_no_warnings_and_cost_is_read()
    {
        var analysis = ShowplanAnalyzer.Analyze(Fixture("showplan", "clean.sqlplan"));

        analysis.Warnings.Should().BeEmpty();
        analysis.EstimatedCost.Should().BeApproximately(0.0657, 1e-9);
        analysis.DegreeOfParallelism.Should().Be(1);
    }

    [Fact]
    public void Every_warning_kind_has_a_fixture() =>
        Enum.GetValues<PlanWarningKind>().Should().OnlyContain(k =>
            Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "showplan"))
                .Any(f => ShowplanAnalyzer.Analyze(File.ReadAllText(f), null).Has(k)));

    // ---------- untrusted XML ----------

    private const string BillionLaughs = """
        <?xml version="1.0"?>
        <!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">]>
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">&lol2;</ShowPlanXML>
        """;

    private const string ExternalEntity = """
        <?xml version="1.0"?>
        <!DOCTYPE d [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
        <deadlock>&xxe;</deadlock>
        """;

    [Fact]
    public void Dtds_entities_and_garbage_are_refused_without_throwing()
    {
        ShowplanAnalyzer.Analyze(BillionLaughs).Should().Be(PlanAnalysis.Unreadable);
        DeadlockReport.Parse(ExternalEntity).Should().BeEmpty();
        DeadlockReport.Parse("not xml").Should().BeEmpty();
        ShowplanAnalyzer.Analyze(null).Parsed.Should().BeFalse();
        SafeXml.TryParse("<a>" + new string('a', 100) + "</a>", maxCharacters: 20).Should().BeNull("documents over the size cap are refused");
        SafeXml.TryParse("<a>ok</a>", maxCharacters: 20).Should().NotBeNull();
        FluentActions.Invoking(() => SafeXml.Parse(BillionLaughs)).Should().Throw<System.Xml.XmlException>();
    }
}
