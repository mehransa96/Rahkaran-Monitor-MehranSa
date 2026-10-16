using FluentAssertions;
using RSM.Core.Live;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;
using RSM.Core.Slowness;
using RSM.Hosting;
using RSM.Safety.Files;
using RSM.Storage.Investigations;

namespace RSM.Tests;

/// <summary>
/// P5 acceptance for §12: synthetic captures modelled on the tests/lab scenarios (CPU burn, blocking, disk latency,
/// memory pressure, IIS queue) — each with the side effects the real scenario produces — must rank the injected cause
/// first. The lab scripts themselves are run by a human on a lab box, never by RSM.
/// </summary>
public sealed class SlownessTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 7, 0, 0, TimeSpan.Zero);
    private static readonly SlownessDefinitions Definitions = SlownessDefinitionSource.LoadEmbedded();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rsm-slow-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>One node's capture: metric → value generator over 24 samples (120 s at 5 s), plus facts and findings.</summary>
    private sealed class NodeBuilder(string id, NodeRoles roles, int seed)
    {
        private readonly Random _random = new(seed);
        public Dictionary<(string Key, string? Instance), Func<int, double>> Metrics { get; } = [];
        public Dictionary<string, object?> Facts { get; } = new(StringComparer.Ordinal);
        public List<Finding> Findings { get; } = [];

        public NodeBuilder Set(string key, string? instance, double value, double noise = 0.05)
        {
            Metrics[(key, instance)] = _ => value * (1 + ((_random.NextDouble() - 0.5) * 2 * noise));
            return this;
        }

        public NodeBuilder Ramp(string key, string? instance, double from, double to)
        {
            Metrics[(key, instance)] = i => from + ((to - from) * i / 23);
            return this;
        }

        public NodeBuilder Fact(string key, object? value)
        {
            Facts[key] = value;
            return this;
        }

        public NodeBuilder Finding(string ruleId, Severity severity, string? instance = null)
        {
            Findings.Add(new Finding(ruleId, severity, Layer.Iis, id, instance, new LocalizedText("ف", "f"), LocalizedText.Empty, LocalizedText.Empty, [],
                new SourceRef(SourceKind.General, "General guidance"), true, 0.8, T0, T0.AddMinutes(2)));
            return this;
        }

        public CaptureNode Build() => new(id, id.ToUpperInvariant(), roles,
            Enumerable.Range(0, 24).SelectMany(i => Metrics.Select(m => new MetricSample(m.Key.Key, m.Key.Instance, m.Value(i), T0.AddSeconds(5 * i)))).ToList(),
            Facts, Findings);
    }

    /// <summary>A healthy two-tier site: everything well inside its thresholds.</summary>
    private static (NodeBuilder Db, NodeBuilder App) Healthy(int seed = 1)
    {
        var db = new NodeBuilder("db01", NodeRoles.Data, seed)
            .Set("win.cpu.total_pct", null, 25).Set("sql.cpu.sql_pct", null, 20).Set("sql.cpu.other_pct", null, 3).Set("sql.waits.signal_pct", null, 8)
            .Set("win.mem.available_mb", null, 6000).Set("win.mem.pages_sec", null, 50).Set("sql.perf.memory_grants_pending", null, 0).Set("sql.perf.ple_s", null, 5000, 0.01)
            .Set("sql.io.data_read_ms", "Rahkaran", 4).Set("sql.io.log_write_ms", "Rahkaran", 1).Set("win.disk.read_ms", "D:", 3).Set("win.disk.write_ms", "D:", 2)
            .Set("sql.waits.ms_per_sec", "_Total", 400, 0).Set("sql.waits.ms_per_sec", "IoData", 60, 0).Set("sql.waits.ms_per_sec", "IoLog", 30, 0)
            .Set("sql.waits.ms_per_sec", "Locking", 10, 0).Set("sql.waits.ms_per_sec", "Network", 40, 0).Set("sql.waits.ms_per_sec", "Worker", 0, 0).Set("sql.waits.ms_per_sec", "Cpu", 80, 0)
            .Set("sql.blocking.max_wait_s", null, 0).Set("sql.tempdb.alloc_contention_waiters", null, 0).Set("win.net.util_pct", "Ethernet", 10).Set("rsm.sql_rtt_ms", null, 1)
            .Fact("sql.req.longest_running_s", 2.0).Fact("win.cpu.throttled_samples", 0).Fact("db[Rahkaran].qds.regressed_count", 0);
        var app = new NodeBuilder("app01", NodeRoles.App, seed + 100)
            .Set("win.cpu.total_pct", null, 20).Set("win.process.cpu_pct", "w3wp", 12).Set("dotnet.time_in_gc_pct", null, 2).Set("dotnet.threadpool_queue", null, 0)
            .Set("win.iis.queue.size", "RahkaranPool", 0).Set("win.mem.available_mb", null, 5000).Set("win.mem.pages_sec", null, 20).Set("win.net.util_pct", "Ethernet", 8)
            .Set("iis.log.err5xx_pct_15m", "Rahkaran", 0.1).Fact("win.cpu.throttled_samples", 0).Fact("iis.requests.over30s", 0);
        return (db, app);
    }

    private static InvestigationResult Analyze(params NodeBuilder[] nodes) =>
        SlownessEngine.Analyze(new SlownessCapture("cap", "site", T0, T0.AddMinutes(2), nodes.Select(n => n.Build()).ToList(), []), Definitions);

    [Fact]
    public void Embedded_definitions_are_valid_and_every_cause_has_text_only_next_steps()
    {
        Definitions.Detectors.Should().HaveCountGreaterThan(30);
        Definitions.Causes.Should().HaveCountGreaterThan(20);
        Definitions.Causes.Select(c => c.Id).Should().OnlyHaveUniqueItems().And.OnlyContain(id => id.StartsWith("SLOW-"));
        Definitions.Causes.SelectMany(c => c.NextSteps).Should().OnlyContain(s => s.Fa.Length > 10 && s.En.Length > 10);
        Definitions.Detectors.Concat<object>(Definitions.Causes).Should().NotBeEmpty();
        File.ReadAllText(Path.Combine(RepoRoot(), "rules", "slowness", "detectors.json")).Should().NotContainAny("sp_", "xp_", "DBCC", "KILL ");
    }

    [Fact]
    public void Healthy_site_produces_no_cause()
    {
        var (db, app) = Healthy();
        var result = Analyze(db, app);
        result.Causes.Should().BeEmpty();
        result.Signals.Should().BeEmpty();
    }

    [Fact]
    public void Lab_cpu_burn_ranks_host_cpu_by_another_process_first()
    {
        // tests/lab/cpu-burn.ps1 on the Data server: a busy loop outside SQL Server.
        var (db, app) = Healthy(2);
        db.Set("win.cpu.total_pct", null, 97, 0.02).Set("sql.cpu.other_pct", null, 75).Set("sql.waits.signal_pct", null, 30).Fact("win.process.top_cpu", "powershell 88 %");

        var result = Analyze(db, app);

        result.Causes[0].Cause.Id.Should().Be("SLOW-CPU-NONSQL");
        result.Causes[0].Confidence.Should().BeGreaterThan(0.8);
        result.Causes[0].Evidence.Select(e => e.DetectorId).Should().Contain(["HostCpuSaturated", "NonSqlCpu"]);
        result.Causes[0].Context.Should().ContainSingle(c => c.Key == "win.process.top_cpu");
        result.Causes.Should().NotContain(c => c.Cause.Id == "SLOW-CPU-SQL", "SQL Server is not the consumer");
    }

    [Fact]
    public void Lab_blocking_pair_ranks_blocking_first_with_the_head_blocker()
    {
        // tests/lab/blocking.sql: session A holds an update lock in an open transaction, session B waits.
        var (db, app) = Healthy(3);
        db.Ramp("sql.blocking.max_wait_s", null, 0, 110).Set("sql.waits.ms_per_sec", "Locking", 600, 0).Set("sql.waits.ms_per_sec", "_Total", 1000, 0)
            .Fact("sql.req.longest_running_s", 110.0).Fact("sql.blocking.head_summary", "spid 61 UPDATE dbo.LabBlocking (open 115 s)").Fact("sql.blocking.head_session", 61);
        app.Fact("iis.requests.over30s", 3);

        var result = Analyze(db, app);

        result.Causes[0].Cause.Id.Should().Be("SLOW-BLOCKING");
        result.Causes[0].Context.Select(c => c.Key).Should().Contain("sql.blocking.head_summary");
        result.Causes.Should().NotContain(c => c.Cause.Id == "SLOW-EXPENSIVE-QUERY" && c.Confidence >= 0.3, "long requests are explained by the blocking");
    }

    [Fact]
    public void Lab_disk_latency_ranks_data_volume_latency_first()
    {
        // tests/lab/disk-latency.ps1: DiskSpd on the lab data volume D: (reads and writes).
        var (db, app) = Healthy(4);
        db.Set("win.disk.read_ms", "D:", 150).Set("win.disk.write_ms", "D:", 120).Set("sql.io.data_read_ms", "Rahkaran", 140).Set("sql.io.log_write_ms", "Rahkaran", 60)
            .Set("sql.waits.ms_per_sec", "_Total", 800, 0).Set("sql.waits.ms_per_sec", "IoData", 500, 0).Set("sql.waits.ms_per_sec", "IoLog", 150, 0)
            .Ramp("sql.perf.ple_s", null, 5000, 3500);

        var result = Analyze(db, app);

        result.Causes[0].Cause.Id.Should().Be("SLOW-DISK-DATA");
        result.Causes[0].Evidence.Should().Contain(e => e.DetectorId == "DiskReadLatencyOs" && e.Instance == "D:");
        result.Causes.Select(c => c.Cause.Id).Should().Contain("SLOW-DISK-LOG", "the log shares the slow volume");
    }

    [Fact]
    public void Lab_memory_pressure_ranks_os_memory_first()
    {
        // tests/lab/memory-pressure.ps1 on the App server: allocate until little memory is left.
        var (db, app) = Healthy(5);
        app.Ramp("win.mem.available_mb", null, 1500, 150).Set("win.mem.pages_sec", null, 6500).Set("dotnet.time_in_gc_pct", null, 9).Set("win.process.cpu_pct", "w3wp", 30);

        var result = Analyze(db, app);

        result.Causes[0].Cause.Id.Should().Be("SLOW-MEM-OS");
        result.Causes[0].Evidence.Should().OnlyContain(e => e.NodeId == "app01");
    }

    [Fact]
    public void Lab_iis_queue_ranks_the_request_queue_first()
    {
        // tests/lab/iis-queue.ps1: slow endpoint + queueLength 50 on the lab pool, then a load loop.
        var (db, app) = Healthy(6);
        app.Ramp("win.iis.queue.size", "RahkaranPool", 0, 80).Set("dotnet.threadpool_queue", null, 50).Set("win.process.cpu_pct", "w3wp", 30)
            .Fact("iis.requests.over30s", 12).Fact("pool[RahkaranPool].queue_size", 80.0).Finding("IIS-Q-001", Severity.Warning, "RahkaranPool");

        var result = Analyze(db, app);

        result.Causes[0].Cause.Id.Should().Be("SLOW-IIS-QUEUE");
        result.Causes[0].Context.Should().Contain(c => c.Key == "pool[RahkaranPool].queue_size");
    }

    [Fact]
    public void Async_network_io_with_a_busy_app_tier_blames_the_app_not_the_network()
    {
        var (db, app) = Healthy(7);
        db.Set("sql.waits.ms_per_sec", "Network", 700, 0).Set("sql.waits.ms_per_sec", "_Total", 1000, 0);
        app.Set("win.process.cpu_pct", "w3wp", 90).Set("dotnet.time_in_gc_pct", null, 35);

        var ranked = Analyze(db, app).Causes.Select(c => c.Cause.Id).ToList();
        ranked.Should().StartWith("SLOW-APP-CONSUMING").And.NotContain("SLOW-CLIENT-NETWORK", "the busy app tier excludes the network explanation");

        var (db2, app2) = Healthy(8);
        db2.Set("sql.waits.ms_per_sec", "Network", 700, 0).Set("sql.waits.ms_per_sec", "_Total", 1000, 0).Set("rsm.sql_rtt_ms", null, 40);
        Analyze(db2, app2).Causes[0].Cause.Id.Should().Be("SLOW-CLIENT-NETWORK", "an idle app tier points at the network or client");
    }

    [Fact]
    public void Detectors_fall_back_to_facts_and_match_instance_patterns()
    {
        var node = new CaptureNode("n", "N", NodeRoles.Data, [], new Dictionary<string, object?>
        {
            ["sql.blocking.max_wait_s"] = 60.0,
            ["db[Rahkaran].qds.regressed_count"] = 5,
            ["db[Other].qds.regressed_count"] = System.Text.Json.JsonDocument.Parse("2").RootElement,
        }, []);

        SlownessEngine.Detect(Definitions.Detectors.Single(d => d.Id == "BlockingChain"), node).Single().Strength.Should().Be(1);
        SlownessEngine.Detect(Definitions.Detectors.Single(d => d.Id == "PlanRegression"), node).Select(s => (s.Instance, s.Strength))
            .Should().BeEquivalentTo([("Other", 0.4), ("Rahkaran", 1.0)]);
    }

    [Fact]
    public async Task Investigations_round_trip_through_the_data_folder()
    {
        var (db, app) = Healthy(9);
        db.Set("win.cpu.total_pct", null, 97, 0.02).Set("sql.cpu.other_pct", null, 75);
        var capture = new SlownessCapture("20261018-070000-abc123", "site", T0, T0.AddMinutes(2), [db.Build(), app.Build()], []);
        var store = new InvestigationStore(new DataFolder(_root, isPortable: true));

        await store.SaveAsync(new SavedInvestigation(capture, SlownessEngine.Analyze(capture, Definitions), "مشتری"), CancellationToken.None);

        var info = (await store.ListAsync("site", CancellationToken.None)).Single();
        info.TopCauseId.Should().Be("SLOW-CPU-NONSQL");
        var json = await store.LoadJsonAsync(info.CaptureId, CancellationToken.None);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<SavedInvestigation>(json!, InvestigationStore.Json)!;
        SlownessEngine.Analyze(reloaded.Capture, Definitions).Causes[0].Cause.Id.Should().Be("SLOW-CPU-NONSQL", "a saved capture can be re-scored");
        await FluentActions.Awaiting(() => store.LoadJsonAsync("../x", CancellationToken.None)).Should().ThrowAsync<ArgumentException>();
    }

    private sealed class BusyCpuCollector : ICollector
    {
        public string Id => "test.cpu";

        public Layer Layer => Layer.Os;

        public CollectorCost Cost => CollectorCost.Cheap;

        public NodeRoles AppliesTo => NodeRoles.Data;

        public TimeSpan DefaultInterval => TimeSpan.FromHours(1);

        public Task<CollectorResult> CollectAsync(INodeContext node, CollectContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new CollectorResult(Id, node.Node.Id, DateTimeOffset.UtcNow, TimeSpan.Zero, CollectorStatus.Ok, null,
                [new MetricSample("win.cpu.total_pct", null, 99, DateTimeOffset.UtcNow), new MetricSample("sql.cpu.other_pct", null, 80, DateTimeOffset.UtcNow)],
                [new Fact("win.process.top_cpu", "lab 90 %")], []));
    }

    [Fact]
    public async Task Capture_records_every_node_at_investigation_cadence_and_restores_the_interval()
    {
        var node = new LiveNode(new NodeContext(new SiteProfile { Id = "site", Name = "s" }, new NodeProfile { Id = "db01", Name = "DB01", Host = "db01", Roles = NodeRoles.Data }, []),
            [new BusyCpuCollector()], new Dictionary<string, object?>());
        await using var session = new LiveSession([node], new RulesEngine([]));
        session.Scheduler.IntervalScale = 2;

        var capture = await SlownessInvestigationService.CaptureAsync(session, "site", TimeSpan.FromSeconds(1), null, TimeProvider.System, CancellationToken.None);

        session.Scheduler.IntervalScale.Should().Be(2);
        capture.Nodes.Single().Samples.Should().Contain(s => s.Key == "win.cpu.total_pct");
        capture.Nodes.Single().Facts.Should().ContainKey("win.process.top_cpu");
        capture.ToUtc.Should().BeOnOrAfter(capture.FromUtc.AddSeconds(1));
        capture.CaptureId.Should().MatchRegex("^[0-9]{8}-[0-9]{6}-[0-9a-f]{6}$");
        SlownessEngine.Analyze(capture with { Nodes = [capture.Nodes[0] with { Samples = Enumerable.Repeat(capture.Nodes[0].Samples, 3).SelectMany(x => x).ToList() }] }, Definitions)
            .Causes[0].Cause.Id.Should().Be("SLOW-CPU-NONSQL");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RSM.sln")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
