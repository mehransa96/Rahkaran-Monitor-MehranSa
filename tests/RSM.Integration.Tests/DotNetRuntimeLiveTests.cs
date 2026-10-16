using System.Runtime.Versioning;
using RSM.Collectors.Iis;
using RSM.Collectors.Rahkaran;
using RSM.Collectors.Windows;
using RSM.Core.Model;
using RSM.Core.Profiles;

namespace RSM.Integration.Tests;

/// <summary>
/// Phase 4 acceptance on a real IIS (CI Windows job, RSM_IT_DOTNET=1, elevated): the CI ASP.NET Core app runs in pool
/// <c>CoreInPool</c> (in-process, w3wp) and <c>CoreOutPool</c> (out-of-process, dotnet child of w3wp). RSM must read
/// their GC and thread-pool counters through the counters-only EventPipe session.
/// </summary>
[SupportedOSPlatform("windows")]
public class DotNetRuntimeLiveTests
{
    private static readonly SiteProfile Site = new()
    {
        Id = "it", Name = "it",
        Nodes = [new NodeProfile { Id = "local", Name = "local", Host = "localhost", Roles = NodeRoles.App }],
        Settings = new SiteSettings { DotNetRuntimeCounters = true },
    };

    [EnvFact("RSM_IT_DOTNET")]
    public async Task Gc_and_thread_pool_counters_are_read_in_process_and_out_of_process()
    {
        var (windows, _) = WindowsTransportFactory.TryOpen(Site.Nodes[0], null);
        var (iis, reason) = await IisTransportFactory.TryOpenAsync(Site.Nodes[0], windows, elevated: true, CancellationToken.None);
        using var w = windows!;
        using var transport = iis ?? throw new InvalidOperationException(reason);

        // Keep both apps busy while RSM reads, so requests and GC counters move.
        using var http = new HttpClient();
        using var cts = new CancellationTokenSource();
        var load = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.WhenAll(http.GetAsync("http://localhost:8082/alloc"), http.GetAsync("http://localhost:8083/alloc"));
            }
        });

        var collector = new DotNetRuntimeCollector();
        CollectorResult result = null!;
        for (var i = 0; i < 3; i++)
        {
            result = await collector.CollectAsync(new NodeContext(Site, Site.Nodes[0], [windows!, transport]), new CollectContext(), CancellationToken.None);
        }

        await cts.CancelAsync();
        await load.ContinueWith(_ => { }, TaskScheduler.Default);

        result.Status.Kind.Should().Be(CollectorStatusKind.Ok, result.Status.Reason);
        var facts = result.Facts.ToDictionary(f => f.Key, f => f.Value);
        facts["dotnet[CoreInPool].hosting"].Should().Be("in-process");
        facts["dotnet[CoreInPool].process"].Should().Be("w3wp");
        facts["dotnet[CoreOutPool].hosting"].Should().Be("out-of-process");
        facts["dotnet[CoreOutPool].process"].Should().Be("dotnet");
        foreach (var pool in new[] { "CoreInPool", "CoreOutPool" })
        {
            facts[$"dotnet[{pool}].gc_heap_mb"].Should().BeOfType<double>().Which.Should().BeGreaterThan(0);
            facts[$"dotnet[{pool}].threadpool_threads"].Should().BeOfType<double>().Which.Should().BeGreaterThan(0);
            facts.Should().ContainKey($"dotnet[{pool}].time_in_gc_pct");
            facts.Should().ContainKey($"dotnet[{pool}].requests_per_s");
        }

        result.Samples.Should().Contain(s => s.Key == "dotnet.gc_heap_mb" && s.Instance == "CoreOutPool");
    }
}
