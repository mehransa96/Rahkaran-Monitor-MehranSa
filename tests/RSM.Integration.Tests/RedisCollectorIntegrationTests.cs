using RSM.Collectors.Redis;
using RSM.Core.Model;
using RSM.Core.Profiles;

namespace RSM.Integration.Tests;

/// <summary>The Redis collector against a real Redis (RSM_IT_REDIS = host:port), through the whitelist client only.</summary>
public class RedisCollectorIntegrationTests
{
    [EnvFact("RSM_IT_REDIS")]
    public async Task Collector_reads_info_slowlog_latency_and_clients()
    {
        var target = Environment.GetEnvironmentVariable("RSM_IT_REDIS")!.Split(':');
        var endpoint = new RedisEndpoint { Host = target[0], Port = int.Parse(target[1], System.Globalization.CultureInfo.InvariantCulture) };
        var node = new NodeProfile { Id = "r", Name = "redis", Host = target[0], Roles = NodeRoles.Redis, Redis = endpoint };
        var site = new SiteProfile { Id = "s", Name = "s", Nodes = [node] };
        using var transport = new RedisNodeTransport(endpoint, Environment.GetEnvironmentVariable("RSM_IT_REDIS_PASSWORD"));

        var result = await new RedisCollector().CollectAsync(new NodeContext(site, node, [transport]), new CollectContext(), CancellationToken.None);

        result.Status.Kind.Should().BeOneOf([CollectorStatusKind.Ok, CollectorStatusKind.Partial], result.Status.Detail);
        var facts = result.Facts.ToDictionary(f => f.Key, f => f.Value);
        facts["redis.reachable"].Should().Be(true);
        facts["redis.version"].Should().NotBeNull();
        facts.Should().ContainKey("redis.used_memory_mb").And.ContainKey("redis.slowlog_60m").And.ContainKey("redis.persistence_error");
        facts["redis.maxmemory_zero"].Should().Be(true, "the CI container runs without a maxmemory limit");
    }

    [EnvFact("RSM_IT_REDIS")]
    public async Task Unreachable_redis_is_a_fact_not_a_crash()
    {
        var endpoint = new RedisEndpoint { Host = "127.0.0.1", Port = 1 };
        var node = new NodeProfile { Id = "r", Name = "redis", Host = "127.0.0.1", Roles = NodeRoles.Redis, Redis = endpoint };
        using var transport = new RedisNodeTransport(endpoint, null);

        var result = await new RedisCollector().CollectAsync(new NodeContext(new SiteProfile { Id = "s", Name = "s", Nodes = [node] }, node, [transport]), new CollectContext(), CancellationToken.None);

        result.Status.Kind.Should().Be(CollectorStatusKind.Unavailable);
        result.Facts.Should().Contain(new Fact("redis.reachable", false));
    }
}
