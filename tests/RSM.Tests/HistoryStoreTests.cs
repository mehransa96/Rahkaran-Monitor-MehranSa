using FluentAssertions;
using RSM.Core.Live;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;
using RSM.Safety.Files;
using RSM.Storage.History;

namespace RSM.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rsm-hist-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private HistoryStore Store() => new(new DataFolder(_root, isPortable: true));

    /// <summary>One sample every 15 s for <paramref name="span"/>, value = minute of day (so buckets are checkable).</summary>
    private static List<MetricSample> Series(DateTimeOffset from, TimeSpan span, string key = "os.cpu_pct", string? instance = null) =>
        Enumerable.Range(0, (int)(span.TotalSeconds / 15))
            .Select(i => from.AddSeconds(i * 15))
            .Select(t => new MetricSample(key, instance, t.Hour * 60 + t.Minute, t))
            .ToList();

    [Fact]
    public async Task Raw_samples_round_trip_per_series_and_skip_non_finite_values()
    {
        var store = Store();
        var samples = Series(T0, TimeSpan.FromMinutes(5));
        samples.Add(new MetricSample("os.cpu_pct", null, double.NaN, T0.AddSeconds(7)));
        samples.Add(new MetricSample("sql.waits_ms", "LCK_M_X", 42, T0.AddSeconds(3)));
        await store.AppendAsync("site1", "node1", samples, CancellationToken.None);

        var cpu = await store.ReadAsync("site1", "node1", "os.cpu_pct", null, T0, T0.AddMinutes(5), HistoryTier.Raw, CancellationToken.None);
        cpu.Should().HaveCount(20).And.BeInAscendingOrder(p => p.AtUtc);
        cpu.Should().OnlyContain(p => p.N == 1 && double.IsFinite(p.Avg));
        (await store.ReadAsync("site1", "node1", "sql.waits_ms", "LCK_M_X", T0, T0.AddMinutes(5), null, CancellationToken.None)).Single().Avg.Should().Be(42);
        (await store.SeriesAsync("site1", "node1", CancellationToken.None)).Select(s => s.Key).Should().BeEquivalentTo(["os.cpu_pct", "sql.waits_ms"]);

        // A fresh store (app restart) reads the persisted series index.
        (await Store().ReadAsync("site1", "node1", "sql.waits_ms", "LCK_M_X", T0, T0.AddMinutes(5), HistoryTier.Raw, CancellationToken.None)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Maintain_rolls_finished_hours_into_one_minute_buckets_with_weighted_aggregates()
    {
        var store = Store();
        await store.AppendAsync("s", "n", Series(T0, TimeSpan.FromHours(2)), CancellationToken.None);
        await store.MaintainAsync(T0.AddHours(1).AddMinutes(30), CancellationToken.None);

        // Hour 0 is rolled up, hour 1 (current) is not: a 1-minute read still covers both without gap or duplicate.
        var m1 = await store.ReadAsync("s", "n", "os.cpu_pct", null, T0, T0.AddHours(2), HistoryTier.OneMinute, CancellationToken.None);
        m1.Should().HaveCount(120);
        m1.Should().OnlyContain(p => p.N == 4 && p.Avg == p.Min && p.Min == p.Max);
        m1[61].Avg.Should().Be(61);
        Directory.GetFiles(Path.Combine(_root, "history", "s_n", "m1")).Should().ContainSingle();
    }

    [Fact]
    public async Task Fifteen_minute_tier_matches_on_the_fly_aggregation_before_and_after_rollup()
    {
        var store = Store();
        var samples = Series(T0, TimeSpan.FromDays(2));
        await store.AppendAsync("s", "n", samples, CancellationToken.None);
        var before = await store.ReadAsync("s", "n", "os.cpu_pct", null, T0, T0.AddDays(2), HistoryTier.FifteenMinutes, CancellationToken.None);

        await store.MaintainAsync(T0.AddDays(2).AddMinutes(1), CancellationToken.None);
        var after = await store.ReadAsync("s", "n", "os.cpu_pct", null, T0, T0.AddDays(2), HistoryTier.FifteenMinutes, CancellationToken.None);

        before.Should().HaveCount(192);
        after.Should().BeEquivalentTo(before, o => o.WithStrictOrdering());
        after[1].Should().Be(new HistoryPoint(T0.AddMinutes(15), 22, 15, 29, 60));
        Directory.GetFiles(Path.Combine(_root, "history", "s_n", "m15")).Should().HaveCount(2);

        // Maintenance is idempotent: running it again changes nothing.
        await store.MaintainAsync(T0.AddDays(2).AddMinutes(2), CancellationToken.None);
        (await store.ReadAsync("s", "n", "os.cpu_pct", null, T0, T0.AddDays(2), HistoryTier.FifteenMinutes, CancellationToken.None)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Retention_deletes_raw_after_a_day_one_minute_after_a_week_and_fifteen_minutes_after_ninety_days()
    {
        var store = Store();
        await store.AppendAsync("s", "n", Series(T0, TimeSpan.FromHours(1)), CancellationToken.None);
        string Tier(string t) => Path.Combine(_root, "history", "s_n", t);

        await store.MaintainAsync(T0.AddHours(20), CancellationToken.None);
        Directory.GetFiles(Tier("raw")).Should().ContainSingle("raw is kept 24 h");

        await store.MaintainAsync(T0.AddDays(2), CancellationToken.None);
        Directory.GetFiles(Tier("raw")).Should().BeEmpty();
        Directory.GetFiles(Tier("m1")).Should().ContainSingle();
        Directory.GetFiles(Tier("m15")).Should().ContainSingle();

        await store.MaintainAsync(T0.AddDays(9), CancellationToken.None);
        Directory.GetFiles(Tier("m1")).Should().BeEmpty();
        (await store.ReadAsync("s", "n", "os.cpu_pct", null, T0, T0.AddHours(1), HistoryTier.FifteenMinutes, CancellationToken.None)).Should().HaveCount(4);

        await store.MaintainAsync(T0.AddDays(92), CancellationToken.None);
        Directory.GetFiles(Tier("m15")).Should().BeEmpty();
    }

    [Fact]
    public async Task Tier_is_chosen_by_span()
    {
        var store = Store();
        await store.AppendAsync("s", "n", Series(T0, TimeSpan.FromHours(8)), CancellationToken.None);
        (await store.ReadAsync("s", "n", "os.cpu_pct", null, T0, T0.AddHours(1), null, CancellationToken.None)).Should().OnlyContain(p => p.N == 1);
        (await store.ReadAsync("s", "n", "os.cpu_pct", null, T0, T0.AddHours(8), null, CancellationToken.None)).Should().OnlyContain(p => p.N == 4);
        (await store.ReadAsync("s", "n", "os.cpu_pct", null, T0, T0.AddDays(4), null, CancellationToken.None)).Should().OnlyContain(p => p.N == 60);
    }

    [Fact]
    public async Task Unknown_series_reads_empty_and_ids_cannot_escape_the_history_folder()
    {
        var store = Store();
        (await store.ReadAsync("s", "n", "nope", null, T0, T0.AddHours(1), null, CancellationToken.None)).Should().BeEmpty();
        HistoryStore.NodeFolder("../../etc", "a/b\\c").Should().Be("history/------etc_a-b-c");
        await store.AppendAsync("../x", "..", Series(T0, TimeSpan.FromMinutes(1)), CancellationToken.None);
        Directory.GetDirectories(Path.Combine(_root, "history")).Should().ContainSingle().Which.Should().StartWith(Path.Combine(_root, "history"));
    }

    private sealed class SampleCollector : ICollector
    {
        private int _runs;

        public string Id => "test.samples";

        public Layer Layer => Layer.Os;

        public CollectorCost Cost => CollectorCost.Cheap;

        public NodeRoles AppliesTo => NodeRoles.App;

        public TimeSpan DefaultInterval => TimeSpan.FromHours(1);

        public Task<CollectorResult> CollectAsync(INodeContext node, CollectContext context, CancellationToken cancellationToken)
        {
            var run = Interlocked.Increment(ref _runs);
            return Task.FromResult(new CollectorResult(Id, node.Node.Id, DateTimeOffset.UtcNow, TimeSpan.Zero, CollectorStatus.Ok, null,
                [new MetricSample("os.cpu_pct", null, run, T0.AddSeconds(run))], [], []));
        }
    }

    [Fact]
    public async Task Recorder_writes_live_samples_and_flushes_the_rest_when_the_live_view_stops()
    {
        var store = Store();
        var node = new LiveNode(new NodeContext(new SiteProfile { Id = "site1", Name = "s" }, new NodeProfile { Id = "app1", Name = "APP01", Host = "app01", Roles = NodeRoles.App }, []),
            [new SampleCollector()], new Dictionary<string, object?>());
        await using var session = new LiveSession([node], new RulesEngine([]));
        var recording = new HistoryRecorder(store) { FlushInterval = TimeSpan.FromHours(1) }.Attach("site1", session);

        await session.TickAsync(CancellationToken.None);
        await session.TickAsync(CancellationToken.None);
        await session.TickAsync(CancellationToken.None);
        await recording.DisposeAsync();

        (await store.ReadAsync("site1", "app1", "os.cpu_pct", null, T0, T0.AddMinutes(1), HistoryTier.Raw, CancellationToken.None))
            .Select(p => p.Avg).Should().Equal(1, 2, 3);
    }
}
