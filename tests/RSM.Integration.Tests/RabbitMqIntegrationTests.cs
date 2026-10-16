using RSM.Collectors.Rahkaran;
using RSM.Core.Capabilities;
using RSM.Core.Model;
using RSM.Core.Profiles;

namespace RSM.Integration.Tests;

/// <summary>The RabbitMQ collector against a real management API (RSM_IT_RABBIT = base URL, guest/guest), GET only.</summary>
public class RabbitMqIntegrationTests
{
    private static RabbitEndpoint Endpoint => new() { ManagementUrl = new Uri(Environment.GetEnvironmentVariable("RSM_IT_RABBIT")!), UserName = "guest" };

    [EnvFact("RSM_IT_RABBIT")]
    public async Task Collector_reads_overview_nodes_and_queues()
    {
        var node = new NodeProfile { Id = "wf", Name = "wf", Host = "127.0.0.1", Roles = NodeRoles.Workflow, RabbitMq = Endpoint };
        var site = new SiteProfile { Id = "s", Name = "s", Nodes = [node] };
        using var transport = new RabbitNodeTransport(Endpoint, "guest");

        // The management plugin needs a few seconds after the container reports started.
        CollectorResult? result = null;
        for (var i = 0; i < 30 && result?.Status.Kind != CollectorStatusKind.Ok; i++)
        {
            if (i > 0)
            {
                await Task.Delay(2000);
            }

            result = await new RabbitMqCollector().CollectAsync(new NodeContext(site, node, [transport]), new CollectContext(), CancellationToken.None);
        }

        result!.Status.Kind.Should().Be(CollectorStatusKind.Ok, result.Status.Reason);
        var facts = result.Facts.ToDictionary(f => f.Key, f => f.Value);
        facts["rmq.reachable"].Should().Be(true);
        facts["rmq.version"].Should().NotBeNull();
        facts["rmq.nodes"].Should().Be(1);
        facts["rmq.alarm"].Should().Be(false);
        facts.Should().ContainKey("rmq.fd_max_pct").And.ContainKey("rmq.stuck_queues");

        var matrix = new CapabilityMatrix("wf", DateTimeOffset.UtcNow);
        await RabbitCapabilityProbe.ProbeAsync(Endpoint, "guest", NodeRoles.Workflow, matrix, CancellationToken.None);
        matrix.Get(CapabilityIds.RabbitApi)!.State.Should().Be(CapabilityState.Available);

        var denied = new CapabilityMatrix("wf", DateTimeOffset.UtcNow);
        await RabbitCapabilityProbe.ProbeAsync(Endpoint, "wrong-password", NodeRoles.Workflow, denied, CancellationToken.None);
        denied.Get(CapabilityIds.RabbitApi)!.State.Should().Be(CapabilityState.Denied);
    }
}
