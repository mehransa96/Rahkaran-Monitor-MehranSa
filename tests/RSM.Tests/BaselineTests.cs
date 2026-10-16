using FluentAssertions;
using RSM.Core.History;
using RSM.Core.Live;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;
using RSM.Safety.Files;
using RSM.Storage.History;

namespace RSM.Tests;

public sealed class BaselineTests : IDisposable
{
    // Saturday 2026-03-07 00:00 local (UTC+03:30), the start of an Iranian week.
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("rsm-test+0330", TimeSpan.FromMinutes(210), "test", "test");
    private static readonly DateTimeOffset Week0 = new DateTimeOffset(2026, 3, 7, 0, 0, 0, TimeSpan.FromMinutes(210)).ToUniversalTime();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rsm-base-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Hour_of_week_starts_on_saturday_in_local_time()
    {
        Baselines.HourOfWeek(Week0, Zone).Should().Be(0);
        Baselines.HourOfWeek(Week0.AddHours(25), Zone).Should().Be(25, "Sunday 01:00");
        Baselines.HourOfWeek(Week0.AddDays(7).AddMinutes(-1), Zone).Should().Be(167, "Friday 23:59");
        Baselines.HourOfWeek(Week0.AddMinutes(-1), TimeZoneInfo.Utc).Should().Be(Baselines.HourOfWeek(Week0.AddMinutes(-1), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Slots_need_enough_samples_and_use_interpolated_percentiles()
    {
        var b = Baselines.Build("k", null, Enumerable.Range(1, 20).Select(i => (Week0.AddMinutes(i), (double)i)).Append((Week0.AddHours(5), 1.0)), Zone);
        b.Slots[0].Should().Be(new BaselineSlot(10.5, 19.05, 20));
        b.Slots[5].Should().BeNull("one value is not a baseline");
        Baselines.Percentile([5.0], 0.95).Should().Be(5);
    }

    [Fact]
    public void Tracker_flags_only_runs_of_at_least_three_samples_above_p95_times_factor()
    {
        var baseline = new SeriesBaseline("k", null, Enumerable.Repeat<BaselineSlot?>(new BaselineSlot(10, 20, 16), SeriesBaseline.SlotCount).ToList());
        double[] values = [10, 31, 31, 10, 35, 40, 32, 10, 31, 31, 31];
        var anomalies = AnomalyDetector.Detect(baseline, values.Select((v, i) => (Week0.AddMinutes(i), v)), Zone);

        anomalies.Should().HaveCount(2);
        anomalies[0].Should().Be(new Anomaly("k", null, Week0.AddMinutes(4), Week0.AddMinutes(6), 40, 30, 3));
        anomalies[1].FromUtc.Should().Be(Week0.AddMinutes(8), "a run still open at the end is reported too");
        AnomalyDetector.Detect(baseline with { Slots = new BaselineSlot?[SeriesBaseline.SlotCount] }, values.Select((v, i) => (Week0.AddMinutes(i), v)), Zone)
            .Should().BeEmpty("no baseline, no flag");
    }

    /// <summary>
    /// P5 acceptance: four weeks of a business-hours CPU pattern are replayed into history, the baseline is computed,
    /// then a fifth-week day with one injected 30-minute anomaly (plus short spikes that must not flag) is replayed.
    /// </summary>
    [Fact]
    public async Task Replayed_series_with_a_known_anomaly_is_flagged_exactly_there()
    {
        var folder = new DataFolder(_root, isPortable: true);
        var history = new HistoryStore(folder);
        var baselines = new BaselineStore(history, folder);
        var random = new Random(42);
        double Normal(DateTimeOffset utc)
        {
            var local = TimeZoneInfo.ConvertTime(utc, Zone);
            var business = local.DayOfWeek != DayOfWeek.Friday && local.Hour is >= 8 and < 17;
            return (business ? 55 : 15) + (random.NextDouble() * 10);
        }

        for (var day = 0; day < 28; day++)
        {
            var start = Week0.AddDays(day);
            await history.AppendAsync("site", "app1", Enumerable.Range(0, 1440).Select(m => start.AddMinutes(m)).Select(t => new MetricSample("os.cpu_pct", null, Normal(t), t)).ToList(), CancellationToken.None);
        }

        var now = Week0.AddDays(28);
        await history.MaintainAsync(now, CancellationToken.None);
        var computed = await baselines.ComputeAsync("site", "app1", now, Zone, CancellationToken.None);
        var slot = computed.For("os.cpu_pct", null)!.Slots[(2 * 24) + 10]!.Value; // Monday 10:00
        slot.N.Should().Be(16);
        slot.P95.Should().BeInRange(55, 65);

        // Week 5, Monday: anomaly 10:00–10:29 local at ~2× p95; one 1-minute and one 2-minute spike elsewhere.
        var monday = now.AddDays(2);
        var anomalyFrom = monday.AddHours(10);
        var replay = Enumerable.Range(0, 1440).Select(m => monday.AddMinutes(m)).Select(t =>
        {
            var minute = (int)(t - monday).TotalMinutes;
            var value = t >= anomalyFrom && t < anomalyFrom.AddMinutes(30) ? 130 : minute is 300 or 900 or 901 ? 150 : Normal(t);
            return new MetricSample("os.cpu_pct", null, value, t);
        }).ToList();
        await history.AppendAsync("site", "app1", replay, CancellationToken.None);

        var flagged = await baselines.AnomaliesAsync("site", "app1", monday, monday.AddDays(1).AddMinutes(-1), Zone, null, CancellationToken.None);

        flagged.Should().ContainSingle();
        flagged[0].FromUtc.Should().Be(anomalyFrom);
        flagged[0].ToUtc.Should().Be(anomalyFrom.AddMinutes(29));
        flagged[0].Samples.Should().Be(30);
        flagged[0].Peak.Should().Be(130);
        (await baselines.LoadAsync("site", "app1", CancellationToken.None))!.Series.Should().ContainSingle()
            .Which.Slots.Should().BeEquivalentTo(computed.Series[0].Slots, "the stored baseline round-trips");
    }

    [Fact]
    public async Task Compare_periods_reports_changes_largest_first()
    {
        var folder = new DataFolder(_root, isPortable: true);
        var history = new HistoryStore(folder);
        var baselines = new BaselineStore(history, folder);
        List<MetricSample> Week(DateTimeOffset from, double cpu, double io) => Enumerable.Range(0, 7 * 24)
            .SelectMany(h => new[] { new MetricSample("os.cpu_pct", null, cpu, from.AddHours(h)), new MetricSample("sql.io_ms", "Rahkaran", io, from.AddHours(h)) }).ToList();
        await history.AppendAsync("s", "n", Week(Week0, 40, 10), CancellationToken.None);
        await history.AppendAsync("s", "n", Week(Week0.AddDays(7), 50, 30), CancellationToken.None);

        var (a, b) = PeriodCompare.ThisWeekVsLastWeek(Week0.AddDays(14));
        var rows = await baselines.CompareAsync("s", "n", a, b, CancellationToken.None);

        rows.Select(r => r.Key).Should().Equal("sql.io_ms", "os.cpu_pct");
        rows[0].AvgChange.Should().BeApproximately(2.0, 1e-9);
        rows[1].A!.Avg.Should().Be(40);
        rows[1].B!.P95.Should().Be(50);
        rows[1].AvgChange.Should().BeApproximately(0.25, 1e-9);
    }

    private sealed class ScriptedCollector(Func<int, MetricSample> sample) : ICollector
    {
        private int _runs;

        public string Id => "test.cpu";

        public Layer Layer => Layer.Os;

        public CollectorCost Cost => CollectorCost.Cheap;

        public NodeRoles AppliesTo => NodeRoles.App;

        public TimeSpan DefaultInterval => TimeSpan.FromHours(1);

        public Task<CollectorResult> CollectAsync(INodeContext node, CollectContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new CollectorResult(Id, node.Node.Id, DateTimeOffset.UtcNow, TimeSpan.Zero, CollectorStatus.Ok, null, [sample(Interlocked.Increment(ref _runs))], [], []));
    }

    [Fact]
    public async Task Live_monitor_flags_samples_against_the_stored_baseline()
    {
        var folder = new DataFolder(_root, isPortable: true);
        var history = new HistoryStore(folder);
        var baselines = new BaselineStore(history, folder);
        await history.AppendAsync("site", "app1", Enumerable.Range(0, 28 * 96).Select(i => new MetricSample("os.cpu_pct", null, 10, Week0.AddMinutes(15 * i))).ToList(), CancellationToken.None);
        var now = Week0.AddDays(28);
        await baselines.ComputeAsync("site", "app1", now, Zone, CancellationToken.None);

        double[] values = [12, 100, 100, 100, 9];
        var node = new LiveNode(new NodeContext(new SiteProfile { Id = "site", Name = "s" }, new NodeProfile { Id = "app1", Name = "APP01", Host = "app01", Roles = NodeRoles.App }, []),
            [new ScriptedCollector(run => new MetricSample("os.cpu_pct", null, values[run - 1], now.AddSeconds(15 * run)))], new Dictionary<string, object?>());
        await using var session = new LiveSession([node], new RulesEngine([]));
        var monitor = await LiveAnomalyMonitor.AttachAsync("site", session, baselines, Zone, null, CancellationToken.None);
        var changes = 0;
        monitor.Changed += () => Interlocked.Increment(ref changes);
        monitor.TrackedSeries.Should().Be(1);

        for (var i = 0; i < 4; i++)
        {
            await session.TickAsync(CancellationToken.None);
        }

        monitor.Active().Should().ContainSingle().Which.Anomaly.Limit.Should().Be(15);
        await session.TickAsync(CancellationToken.None);
        monitor.Active().Should().BeEmpty();
        monitor.Ended().Should().ContainSingle().Which.Anomaly.Samples.Should().Be(3);
        changes.Should().Be(2);
        monitor.Detach();
    }
}
