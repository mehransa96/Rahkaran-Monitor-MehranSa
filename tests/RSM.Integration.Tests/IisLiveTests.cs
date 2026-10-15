using System.Runtime.Versioning;
using RSM.Collectors.Iis;
using RSM.Collectors.Windows;
using RSM.Core.Capabilities;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Parsers.Events;

namespace RSM.Integration.Tests;

/// <summary>
/// Phase 3 acceptance on a real IIS (CI Windows job, RSM_IT_IIS=1, elevated): IIS with Request Monitor and ASP.NET,
/// site <c>AuxWebsite1</c> on port 8081 with pool <c>RsmItPool</c> and a page that sleeps 40 s
/// (<c>/slow.aspx</c>). RSM must see that request while it runs — and leave applicationHost.config untouched (the job
/// compares its hash before and after).
/// </summary>
[SupportedOSPlatform("windows")]
public class IisLiveTests
{
    private static readonly SiteProfile Site = new()
    {
        Id = "it", Name = "it",
        Nodes = [new NodeProfile { Id = "local", Name = "local", Host = "localhost", Roles = NodeRoles.App }],
    };

    private static NodeContext Context(params object[] transports) => new(Site, Site.Nodes[0], transports);

    [EnvFact("RSM_IT_IIS")]
    public async Task Live_request_of_a_slow_page_is_visible_while_it_runs()
    {
        var (windows, _) = WindowsTransportFactory.TryOpen(Site.Nodes[0], null);
        var (iis, reason) = await IisTransportFactory.TryOpenAsync(Site.Nodes[0], windows, elevated: true, CancellationToken.None);
        using var w = windows;
        using var transport = iis ?? throw new InvalidOperationException(reason);
        transport.Runtime.Should().NotBeNull("the CI job runs elevated on the IIS server itself");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var slow = http.GetAsync("http://localhost:8081/slow.aspx");
        IisRuntimePayload? payload = null;
        for (var i = 0; i < 20 && payload?.Requests.Any(r => r.Url?.Contains("slow.aspx", StringComparison.OrdinalIgnoreCase) == true && r.TimeElapsedMs >= 3000) != true; i++)
        {
            await Task.Delay(1000);
            var result = await new IisRuntimeCollector().CollectAsync(Context(transport, windows!), new CollectContext(), CancellationToken.None);
            payload = ((IReadOnlyDictionary<string, object?>)result.Payload!)["runtime"] as IisRuntimePayload;
        }

        var request = payload!.Requests.Should().Contain(r => r.Url!.Contains("slow.aspx", StringComparison.OrdinalIgnoreCase)).Subject;
        request.AppPool.Should().Be("RsmItPool");
        request.TimeElapsedMs.Should().BeGreaterThanOrEqualTo(3000);
        payload.Pools.Should().Contain(p => p.Name == "RsmItPool" && p.State == "Started");
        payload.WorkerProcesses.Should().Contain(wp => wp.AppPool == "RsmItPool" && wp.StartedUtc != null);
        (await slow).IsSuccessStatusCode.Should().BeTrue();
    }

    [EnvFact("RSM_IT_IIS")]
    public async Task Configuration_capabilities_and_iis_counters_are_read()
    {
        var (windows, _) = WindowsTransportFactory.TryOpen(Site.Nodes[0], null);
        var (iis, _) = await IisTransportFactory.TryOpenAsync(Site.Nodes[0], windows, elevated: true, CancellationToken.None);
        using var w = windows;
        using var transport = iis!;

        var matrix = new CapabilityMatrix("local", DateTimeOffset.UtcNow);
        await IisCapabilityProbe.ProbeAsync(transport, null, elevated: true, matrix, CancellationToken.None);
        matrix.Get(CapabilityIds.IisConfig)!.State.Should().Be(CapabilityState.Available);
        matrix.Get(CapabilityIds.IisRequests)!.State.Should().Be(CapabilityState.Available, "Request Monitor is installed");
        matrix.Get(CapabilityIds.IisLogs)!.State.Should().Be(CapabilityState.Available);

        var config = await new IisConfigCollector().CollectAsync(Context(transport, windows!), new CollectContext(), CancellationToken.None);
        config.Status.Kind.Should().Be(CollectorStatusKind.Ok, config.Status.Detail);
        config.Facts.Should().Contain(new Fact("site[AuxWebsite1].role", "Aux"));
        config.Facts.Should().Contain(new Fact("site[AuxWebsite1].logs_time_taken", true));
        config.Facts.Should().Contain(f => f.Key == "os.numa_nodes");

        // Remote CIM and local PDH return the same IIS metric keys (the CIM class names are verified here).
        using var pdh = new PdhCounterSource { WarmUp = TimeSpan.FromSeconds(1) };
        var pdhKeys = (await pdh.ReadAsync(CancellationToken.None)).Select(r => r.Key).Where(k => k.StartsWith("win.iis.", StringComparison.Ordinal)).ToHashSet();
        var cimKeys = (await new CimCounterSource(windows!.Cim).ReadAsync(CancellationToken.None)).Select(r => r.Key).Where(k => k.StartsWith("win.iis.", StringComparison.Ordinal)).ToHashSet();
        pdhKeys.Should().Contain(["win.iis.site.requests_sec", "win.iis.w3wp.active_requests", "win.iis.queue.size", "win.iis.pool.state"]);
        cimKeys.Should().BeEquivalentTo(pdhKeys);
    }

    [EnvFact("RSM_IT_IIS")]
    public async Task Services_and_event_logs_are_read_without_errors()
    {
        var (windows, _) = WindowsTransportFactory.TryOpen(Site.Nodes[0], null);
        using var w = windows!;
        windows!.Events.Should().NotBeNull();

        var services = await new WindowsServicesCollector().CollectAsync(Context(windows), new CollectContext(), CancellationToken.None);
        services.Status.Kind.Should().Be(CollectorStatusKind.Ok, services.Status.Detail);
        services.Facts.Should().Contain(new Fact("svc[W3SVC].state", "Running"));

        var events = await new WindowsEventLogCollector().CollectAsync(Context(windows), new CollectContext(), CancellationToken.None);
        events.Status.Kind.Should().Be(CollectorStatusKind.Ok, events.Status.Detail);
        var payload = (EventLogPayload)((IReadOnlyDictionary<string, object?>)events.Payload!)["events"]!;
        payload.LogsRead.Should().BeEquivalentTo(["Application", "System"]);
        events.Facts.Should().Contain(f => f.Key == "evt.unexpected_shutdowns_7d");
        payload.Groups.Where(g => g.KbKey is not null).Select(g => EventKnowledgeBase.ByKey(g.KbKey!)).Should().NotContainNulls();
    }
}
