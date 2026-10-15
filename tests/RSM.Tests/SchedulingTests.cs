using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Scheduling;

namespace RSM.Tests;

public class SchedulingTests
{
    [Fact]
    public void Backs_off_after_three_slow_runs_and_recovers_after_ten_fast_runs()
    {
        var policy = new BackoffPolicy(TimeSpan.FromSeconds(5), budget: TimeSpan.FromMilliseconds(100));

        policy.Record(TimeSpan.FromMilliseconds(500));
        policy.Record(TimeSpan.FromMilliseconds(500));
        policy.Multiplier.Should().Be(1);
        policy.Record(TimeSpan.FromMilliseconds(500)).Should().Be(TimeSpan.FromSeconds(10));

        for (var i = 0; i < 9; i++)
        {
            policy.Record(TimeSpan.FromMilliseconds(10));
        }

        policy.Multiplier.Should().Be(2);
        policy.Record(TimeSpan.FromMilliseconds(10)).Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Back_off_is_capped()
    {
        var policy = new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 100; i++)
        {
            policy.Record(TimeSpan.FromSeconds(1));
        }

        policy.Multiplier.Should().Be(8);
    }

    [Fact]
    public async Task Single_flight_skips_overlapping_runs_and_errors_become_results()
    {
        await using var scheduler = new CollectorScheduler();
        var gate = new TaskCompletionSource();
        var slow = new FakeCollector("slow", async () => { await gate.Task; return CollectorStatus.Ok; });
        var node = new FakeNode();

        var first = scheduler.RunOnceAsync(node, slow, new CollectContext(), CancellationToken.None);
        var second = await scheduler.RunOnceAsync(node, slow, new CollectContext(), CancellationToken.None);
        gate.SetResult();

        second.Should().BeNull();
        scheduler.SkippedTicks.Should().Be(1);
        (await first)!.Status.Kind.Should().Be(CollectorStatusKind.Ok);

        var failing = new FakeCollector("bad", () => throw new InvalidOperationException("boom"));
        var result = await scheduler.RunOnceAsync(node, failing, new CollectContext(), CancellationToken.None);
        result!.Status.Kind.Should().Be(CollectorStatusKind.Error);
        result.Status.Detail.Should().Be("boom");
    }

    [Fact]
    public async Task Concurrency_per_node_is_capped()
    {
        await using var scheduler = new CollectorScheduler(maxConcurrentPerNode: 2);
        var running = 0;
        var peak = 0;
        var release = new TaskCompletionSource();
        var node = new FakeNode();
        var tasks = Enumerable.Range(0, 5).Select(i => scheduler.RunOnceAsync(node, new FakeCollector($"c{i}", async () =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await release.Task;
            Interlocked.Decrement(ref running);
            return CollectorStatus.Ok;
        }), new CollectContext(), CancellationToken.None)).ToList();

        await Task.Delay(100);
        release.SetResult();
        await Task.WhenAll(tasks);

        peak.Should().Be(2);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class FakeNode : INodeContext
    {
        public SiteProfile Site { get; } = new() { Id = "s1", Name = "Site" };

        public NodeProfile Node { get; } = new() { Id = "n1", Name = "Node", Host = "localhost", Roles = NodeRoles.Data };

        public T? GetTransport<T>() where T : class => null;
    }

    private sealed class FakeCollector(string id, Func<Task<CollectorStatus>> run) : ICollector
    {
        public string Id { get; } = id;

        public Layer Layer => Layer.Sql;

        public CollectorCost Cost => CollectorCost.Cheap;

        public NodeRoles AppliesTo => NodeRoles.Data;

        public TimeSpan DefaultInterval => TimeSpan.FromSeconds(5);

        public async Task<CollectorResult> CollectAsync(INodeContext node, CollectContext context, CancellationToken cancellationToken) =>
            CollectorResult.FromStatus(Id, node.Node.Id, await run(), TimeSpan.Zero);
    }
}

public class DateTextTests
{
    [Fact]
    public void Formats_jalali_and_gregorian_in_both_languages()
    {
        var utc = new DateTimeOffset(2026, 10, 14, 19, 40, 0, TimeSpan.Zero);
        var tehran = TimeZoneInfo.CreateCustomTimeZone("IRST", TimeSpan.FromHours(3.5), "IRST", "IRST");

        RSM.Core.Localization.DateText.Format(utc, RSM.Core.Model.UiLanguage.Fa, zone: tehran).Should().Be("1405/07/22 23:10 — 2026-10-14 23:10");
        RSM.Core.Localization.DateText.Format(utc, RSM.Core.Model.UiLanguage.En, zone: tehran).Should().Be("2026-10-14 23:10 (1405/07/22 23:10)");
        RSM.Core.Localization.DateText.Format(utc, RSM.Core.Model.UiLanguage.Fa, persianDigits: true, zone: tehran).Should().StartWith("۱۴۰۵/۰۷/۲۲");
    }
}
