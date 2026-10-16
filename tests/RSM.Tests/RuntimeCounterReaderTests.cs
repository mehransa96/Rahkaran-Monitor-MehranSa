using RSM.Collectors.Windows;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Collectors.Rahkaran;

namespace RSM.Tests;

public class RuntimeCounterReaderTests
{
    /// <summary>End to end on any OS: a counters-only EventPipe session on this test process yields System.Runtime values.</summary>
    [Fact]
    public async Task Counters_of_a_live_dotnet_process_are_read()
    {
        var values = await new EventPipeCounterProvider().ReadAsync(Environment.ProcessId, TimeSpan.FromSeconds(3), CancellationToken.None);

        values.Should().ContainKey("System.Runtime/gc-heap-size");
        values.Should().ContainKey("System.Runtime/threadpool-thread-count");
        values["System.Runtime/gen-2-gc-count"].IsRate.Should().BeTrue();
        values["System.Runtime/gc-heap-size"].IsRate.Should().BeFalse();
        values.Keys.Should().OnlyContain(k => k.StartsWith("System.Runtime/", StringComparison.Ordinal) || k.StartsWith("Microsoft.AspNetCore.Hosting/", StringComparison.Ordinal));
    }
}

public class DotNetRuntimeCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static CimRow Proc(string name, int pid, int parent) => new(new Dictionary<string, object?> { ["Name"] = name, ["ProcessId"] = (uint)pid, ["ParentProcessId"] = (uint)parent });

    private static IReadOnlyDictionary<string, RuntimeCounterValue> Values(double timeInGc = 5, double queue = 0, double threads = 20, double gen2 = 0, double exceptions = 2, double failedTotal = 0, double rps = 100)
    {
        RuntimeCounterValue V(string provider, string name, double value, bool rate = false, double interval = 1) => new(provider, name, value, rate, null, interval);
        return new[]
        {
            V("System.Runtime", "gc-heap-size", 850),
            V("System.Runtime", "time-in-gc", timeInGc),
            V("System.Runtime", "gen-2-gc-count", gen2, rate: true),
            V("System.Runtime", "loh-size", 64 * 1048576.0),
            V("System.Runtime", "alloc-rate", 20 * 1048576.0, rate: true),
            V("System.Runtime", "threadpool-queue-length", queue),
            V("System.Runtime", "threadpool-thread-count", threads),
            V("System.Runtime", "exception-count", exceptions, rate: true),
            V("System.Runtime", "working-set", 1900),
            V("Microsoft.AspNetCore.Hosting", "requests-per-second", rps * 2, rate: true, interval: 2),
            V("Microsoft.AspNetCore.Hosting", "current-requests", 7),
            V("Microsoft.AspNetCore.Hosting", "failed-requests", failedTotal),
        }.ToDictionary(v => $"{v.Provider}/{v.Name}");
    }

    private static readonly DotNetProcess Pool = new(4242, "w3wp", "RahkaranPool", "in-process");

    private static object? Fact(LiveOutput o, string key) => o.Facts.LastOrDefault(f => f.Key == key)?.Value;

    [Fact]
    public void In_process_and_out_of_process_hosting_are_told_apart()
    {
        var rows = new[] { Proc("w3wp.exe", 100, 4), Proc("w3wp.exe", 200, 4), Proc("dotnet.exe", 300, 200), Proc("dotnet.exe", 400, 1), Proc("w3wp.exe", 500, 4) };

        var found = DotNetRuntimeCollector.Discover(rows, published: [100, 300, 400], pools: new Dictionary<int, string> { [100] = "InProcPool", [200] = "OutOfProcPool" });

        found.Should().BeEquivalentTo(
        [
            new DotNetProcess(100, "w3wp", "InProcPool", "in-process"),
            new DotNetProcess(300, "dotnet", "OutOfProcPool", "out-of-process"),
        ], "w3wp 500 is .NET Framework (no endpoint) and dotnet 400 is not a child of w3wp");
    }

    [Fact]
    public void Counters_become_per_pool_facts_in_sensible_units()
    {
        var output = new LiveOutput();
        DotNetRuntimeCollector.Apply(new DotNetState(), [(Pool, Values())], [], Now, output);

        Fact(output, "dotnet[RahkaranPool].gc_heap_mb").Should().Be(850.0);
        Fact(output, "dotnet[RahkaranPool].loh_mb").Should().Be(64.0);
        Fact(output, "dotnet[RahkaranPool].alloc_mb_s").Should().Be(20.0);
        Fact(output, "dotnet[RahkaranPool].requests_per_s").Should().Be(100.0, "the rate is divided by its 2-second interval");
        Fact(output, "dotnet[RahkaranPool].hosting").Should().Be("in-process");
        Fact(output, "dotnet[RahkaranPool].gc_time_sustained_60s").Should().Be(false, "one sample is not sustained");
        output.Samples.Should().Contain(s => s.Key == "dotnet.time_in_gc_pct" && s.Instance == "RahkaranPool");
    }

    [Fact]
    public void Sustained_gc_starvation_failures_and_exception_baseline_need_history()
    {
        var state = new DotNetState();
        LiveOutput last = new();
        for (var i = 0; i <= 12; i++)
        {
            last = new LiveOutput();
            var hot = i >= 10;
            DotNetRuntimeCollector.Apply(state, [(Pool, Values(timeInGc: hot ? 30 : 5, queue: hot ? 50 : 0, threads: 20 + i, exceptions: i == 12 ? 20 : 2, failedTotal: i * 30))], [], Now.AddSeconds(30 * i), last);
        }

        Fact(last, "dotnet[RahkaranPool].gc_time_sustained_60s").Should().Be(true, "samples at 300, 330 and 360 s are all above 20 %");
        Fact(last, "dotnet[RahkaranPool].threadpool_starvation_30s").Should().Be(true);
        Fact(last, "dotnet[RahkaranPool].exceptions_vs_baseline").Should().Be(10.0);
        Fact(last, "dotnet[RahkaranPool].failed_per_s").Should().Be(1.0);
        Fact(last, "dotnet[RahkaranPool].failed_pct").Should().Be(1.0);
    }

    private sealed class NoCounters : IWindowsCounters
    {
        public string Transport => "none";

        public Task<IReadOnlyList<MetricReading>> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricReading>>([]);
    }

    private sealed class FakeCim(IReadOnlyList<CimRow> rows) : ICimQuery
    {
        public string Protocol => "local";

        public Task<IReadOnlyList<CimRow>> QueryAsync(CimQuery query, CancellationToken cancellationToken) => Task.FromResult(rows);
    }

    private sealed class FakeCounters : IRuntimeCounterProvider
    {
        public List<int> Read { get; } = [];

        public IReadOnlyList<int> PublishedProcesses() => [100];

        public Task<IReadOnlyDictionary<string, RuntimeCounterValue>> ReadAsync(int processId, TimeSpan window, CancellationToken cancellationToken)
        {
            Read.Add(processId);
            return Task.FromResult(Values());
        }
    }

    private static NodeContext Context(bool enabled, bool local, FakeCounters counters)
    {
        var node = new NodeProfile { Id = "app", Name = "app01", Host = "localhost", Roles = NodeRoles.App };
        var site = new SiteProfile { Id = "s", Name = "s", Nodes = [node], Settings = new SiteSettings { DotNetRuntimeCounters = enabled } };
        return new NodeContext(site, node, [new WindowsNodeTransport(new FakeCim([Proc("w3wp.exe", 100, 4)]), new NoCounters(), null, local)]);
    }

    [Fact]
    public async Task Off_by_default_and_local_only()
    {
        var counters = new FakeCounters();
        var collector = new DotNetRuntimeCollector(counters);

        var off = await collector.CollectAsync(Context(enabled: false, local: true, counters), new CollectContext(), CancellationToken.None);
        off.Status.Kind.Should().Be(CollectorStatusKind.Unavailable);
        off.Status.Reason.Should().Contain("off by default");

        var remote = await collector.CollectAsync(Context(enabled: true, local: false, counters), new CollectContext(), CancellationToken.None);
        remote.Status.Reason.Should().Contain("local only");
        counters.Read.Should().BeEmpty("no process is attached unless enabled and local");

        var on = await collector.CollectAsync(Context(enabled: true, local: true, counters), new CollectContext(), CancellationToken.None);
        on.Status.Kind.Should().Be(CollectorStatusKind.Ok);
        counters.Read.Should().Equal(100);
        on.Facts.Should().Contain(new Fact("dotnet[w3wp 100].hosting", "in-process"));
    }
}
