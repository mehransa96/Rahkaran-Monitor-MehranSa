using RSM.Collectors.Redis;
using RSM.Core.Model;
using RSM.Safety.Redis;

namespace RSM.Tests;

public class RedisCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static object? FactValue(LiveOutput output, string key) => output.Facts.LastOrDefault(f => f.Key == key)?.Value;

    private const string Info = "# Server\r\nredis_version:7.2.4\r\nredis_mode:standalone\r\nuptime_in_seconds:1200\r\n\r\n# Clients\r\nconnected_clients:42\r\nblocked_clients:1\r\n\r\n"
        + "# Memory\r\nused_memory:943718400\r\nmaxmemory:1073741824\r\nmaxmemory_policy:allkeys-lru\r\nmem_fragmentation_ratio:1.62\r\n\r\n"
        + "# Persistence\r\nrdb_last_bgsave_status:err\r\naof_enabled:0\r\naof_last_write_status:ok\r\n\r\n"
        + "# Stats\r\ninstantaneous_ops_per_sec:850\r\nrejected_connections:3\r\nexpired_keys:10\r\nevicted_keys:120\r\nkeyspace_hits:900\r\nkeyspace_misses:100\r\n\r\n"
        + "# Replication\r\nrole:master\r\n\r\n# Keyspace\r\ndb0:keys=1500,expires=20,avg_ttl=0\r\ndb1:keys=5,expires=0,avg_ttl=0\r\n";

    [Fact]
    public void Info_is_parsed_with_derived_ratios()
    {
        var info = RedisAnalysis.ParseInfo(Info);

        info.Version.Should().Be("7.2.4");
        info.UsedMemoryPct.Should().BeApproximately(87.89, 0.01);
        info.HitRatioPct.Should().Be(90);
        info.Keys.Should().Be(1505, "summed over the keyspace when DBSIZE is not available");
        info.PersistenceError.Should().BeTrue("rdb_last_bgsave_status:err");
        RedisAnalysis.ParseInfo(Info, dbsize: 1500).Keys.Should().Be(1500);
    }

    private static RespValue Bulk(string s) => new(RespKind.BulkString, s);

    private static RespValue Int(long n) => new(RespKind.Integer, Integer: n);

    private static RespValue Arr(params RespValue[] items) => new(RespKind.Array, Items: items);

    [Fact]
    public void Slowlog_keeps_only_the_command_and_key_never_values()
    {
        var reply = Arr(
            Arr(Int(14), Int(Now.AddMinutes(-5).ToUnixTimeSeconds()), Int(45_000), Arr(Bulk("set"), Bulk("session:abc"), Bulk("SECRET-SESSION-PAYLOAD")), Bulk("10.0.0.21:51234"), Bulk("app01")),
            Arr(Int(13), Int(Now.AddHours(-3).ToUnixTimeSeconds()), Int(12_000), Arr(Bulk("KEYS"), Bulk("*"))));

        var entries = RedisAnalysis.ParseSlowlog(reply);

        entries.Should().HaveCount(2);
        entries[0].Should().Be(new SlowlogEntry(14, Now.AddMinutes(-5), 45, "SET", "session:abc", "10.0.0.21:51234", "app01"));
        System.Text.Json.JsonSerializer.Serialize(entries).Should().NotContain("SECRET-SESSION-PAYLOAD");
    }

    [Fact]
    public void Client_list_counts_connections_per_host_and_skips_rsm_itself()
    {
        const string text = "id=3 addr=10.0.0.21:51234 laddr=10.0.0.9:6379 fd=8 name=app01 age=10 idle=0 flags=N db=0 cmd=get\n"
            + "id=4 addr=10.0.0.21:51235 laddr=10.0.0.9:6379 fd=9 name= age=10 idle=0 flags=N db=0 cmd=set\n"
            + "id=5 addr=10.0.0.22:40000 fd=10 name=app02 age=3 idle=1 flags=N db=0 cmd=get\n"
            + "id=6 addr=10.0.0.99:50000 fd=11 name= age=0 idle=0 flags=N db=0 cmd=client|list\n"
            + "id=7 addr=[::1]:6000 fd=12 name= age=0 idle=0 flags=N db=0 cmd=ping\n";

        var hosts = RedisAnalysis.ParseClientList(text);

        hosts.Should().BeEquivalentTo([new RedisClientHost("10.0.0.21", 2, ["app01"]), new RedisClientHost("10.0.0.22", 1, ["app02"]), new RedisClientHost("::1", 1, [])]);
    }

    [Fact]
    public void Latency_and_config_replies_are_parsed()
    {
        RedisAnalysis.ParseLatency(Arr(Arr(Bulk("command"), Int(Now.ToUnixTimeSeconds()), Int(250), Int(900))))
            .Should().Equal(new LatencyEvent("command", Now, 250, 900));
        RedisAnalysis.ParseConfig(Arr(Bulk("maxmemory"), Bulk("0"), Bulk("maxmemory-policy"), Bulk("noeviction")))
            .Should().Contain("maxmemory", "0").And.Contain("maxmemory-policy", "noeviction");
    }

    [Fact]
    public void Facts_track_increasing_counters_and_windows()
    {
        var state = new RedisState();
        var first = new LiveOutput();
        RedisCollector.Apply(state, RedisAnalysis.ParseInfo(Info), [], 0, [], ["APP02"], Now, first);
        FactValue(first, "redis.evicted_increasing").Should().BeNull("no previous sample");

        var second = new LiveOutput();
        RedisCollector.Apply(state, RedisAnalysis.ParseInfo(Info.Replace("evicted_keys:120", "evicted_keys:180", StringComparison.Ordinal)),
            [new SlowlogEntry(1, Now.AddMinutes(-10), 30, "GET", "k", null, null), new SlowlogEntry(2, Now.AddHours(-2), 99, "GET", "k", null, null)], 2,
            [new LatencyEvent("command", Now.AddMinutes(-1), 140, 140)], ["APP02"], Now, second);

        FactValue(second, "redis.evicted_increasing").Should().Be(true);
        FactValue(second, "redis.rejected_increasing").Should().Be(false);
        FactValue(second, "redis.used_memory_pct").Should().BeOfType<double>().Which.Should().BeApproximately(87.89, 0.01);
        FactValue(second, "redis.slowlog_60m").Should().Be(1, "the two-hour-old entry is outside the window");
        FactValue(second, "redis.latency_max_ms").Should().Be(140L);
        FactValue(second, "redis.app_nodes_without_connection").Should().Be(1);
        FactValue(second, "redis.persistence_error").Should().Be(true);
    }
}
