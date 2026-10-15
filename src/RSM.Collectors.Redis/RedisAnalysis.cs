using System.Globalization;
using RSM.Safety.Redis;

namespace RSM.Collectors.Redis;

/// <summary>The INFO values RSM uses (spec §11), with derived ratios. Null = not reported by this Redis version.</summary>
public sealed record RedisInfoSnapshot(
    string? Version,
    string? Mode,
    string? Role,
    long? UptimeSeconds,
    long? UsedMemory,
    long? MaxMemory,
    string? MaxMemoryPolicy,
    double? FragmentationRatio,
    long? EvictedKeys,
    long? ExpiredKeys,
    long? KeyspaceHits,
    long? KeyspaceMisses,
    long? ConnectedClients,
    long? BlockedClients,
    long? RejectedConnections,
    long? OpsPerSec,
    string? RdbLastBgsaveStatus,
    bool? AofEnabled,
    string? AofLastWriteStatus,
    long? Keys)
{
    public double? UsedMemoryPct => MaxMemory is > 0 && UsedMemory is { } used ? 100.0 * used / MaxMemory.Value : null;

    public double? HitRatioPct => KeyspaceHits is { } h && KeyspaceMisses is { } m && h + m > 0 ? 100.0 * h / (h + m) : null;

    public bool PersistenceError =>
        string.Equals(RdbLastBgsaveStatus, "err", StringComparison.OrdinalIgnoreCase)
        || (AofEnabled == true && string.Equals(AofLastWriteStatus, "err", StringComparison.OrdinalIgnoreCase));
}

/// <summary>One SLOWLOG entry as shown: the command and (truncated) key only — argument values are never kept.</summary>
public sealed record SlowlogEntry(long Id, DateTimeOffset AtUtc, double DurationMs, string Command, string? Key, string? Client, string? ClientName);

/// <summary>One LATENCY LATEST event.</summary>
public sealed record LatencyEvent(string Event, DateTimeOffset AtUtc, long LatestMs, long MaxMs);

/// <summary>Connections of one source address in CLIENT LIST.</summary>
public sealed record RedisClientHost(string Address, int Connections, IReadOnlyList<string> Names);

/// <summary>Pure parsing of whitelisted command replies (unit-tested with captured replies).</summary>
public static class RedisAnalysis
{
    public static RedisInfoSnapshot ParseInfo(string infoText, long? dbsize = null)
    {
        var sections = RedisInfo.Parse(infoText);
        var all = sections.SelectMany(s => s.Value).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        string? S(string key) => all.TryGetValue(key, out var v) ? v : null;
        long? L(string key) => long.TryParse(S(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        double? D(string key) => double.TryParse(S(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        long? keys = dbsize;
        if (keys is null && sections.TryGetValue("Keyspace", out var keyspace))
        {
            // db0:keys=12,expires=3,avg_ttl=0
            keys = keyspace.Values.Sum(v => v.Split(',').Select(p => p.Split('=')).Where(p => p.Length == 2 && p[0] == "keys")
                .Sum(p => long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0));
        }

        return new RedisInfoSnapshot(S("redis_version"), S("redis_mode"), S("role"), L("uptime_in_seconds"), L("used_memory"), L("maxmemory"),
            S("maxmemory_policy"), D("mem_fragmentation_ratio"), L("evicted_keys"), L("expired_keys"), L("keyspace_hits"), L("keyspace_misses"),
            L("connected_clients"), L("blocked_clients"), L("rejected_connections"), L("instantaneous_ops_per_sec"), S("rdb_last_bgsave_status"),
            S("aof_enabled") is { } aof ? aof == "1" : null, S("aof_last_write_status"), keys);
    }

    /// <summary>
    /// SLOWLOG GET reply: each entry is [id, unix time, microseconds, [command, args…], client address, client name]
    /// (address/name since Redis 4). Only the command name and the first argument (the key, truncated) are kept.
    /// </summary>
    public static IReadOnlyList<SlowlogEntry> ParseSlowlog(RespValue reply)
    {
        var result = new List<SlowlogEntry>();
        foreach (var e in reply.Items ?? [])
        {
            var items = e.Items;
            if (items is not { Count: >= 4 } || items[3].Items is not { Count: > 0 } args)
            {
                continue;
            }

            var command = (args[0].Text ?? "?").ToUpperInvariant();
            var key = args.Count > 1 && command is not "AUTH" and not "HELLO" and not "MIGRATE" ? Truncate(args[1].Text, 80) : null;
            result.Add(new SlowlogEntry(items[0].Integer, DateTimeOffset.FromUnixTimeSeconds(items[1].Integer), items[2].Integer / 1000.0, command, key,
                items.Count > 4 ? items[4].Text : null, items.Count > 5 ? items[5].Text : null));
        }

        return result;
    }

    /// <summary>LATENCY LATEST reply: [[event, unix time, latest ms, max ms], …].</summary>
    public static IReadOnlyList<LatencyEvent> ParseLatency(RespValue reply) =>
        (reply.Items ?? []).Where(e => e.Items is { Count: >= 4 })
            .Select(e => new LatencyEvent(e.Items![0].Text ?? "?", DateTimeOffset.FromUnixTimeSeconds(e.Items[1].Integer), e.Items[2].Integer, e.Items[3].Integer))
            .ToList();

    /// <summary>CLIENT LIST text: one line per connection ("id=3 addr=10.0.0.21:51234 … name=app01 …") → connections per source host.</summary>
    public static IReadOnlyList<RedisClientHost> ParseClientList(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]))
            // The connection that ran CLIENT LIST is RSM itself (its current command is "client").
            .Where(f => f.ContainsKey("addr") && !(f.GetValueOrDefault("cmd") ?? string.Empty).StartsWith("client", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => HostOf(f["addr"]), StringComparer.OrdinalIgnoreCase)
            .Select(g => new RedisClientHost(g.Key, g.Count(), g.Select(f => f.GetValueOrDefault("name")).Where(n => !string.IsNullOrEmpty(n)).Distinct().Take(5).ToList()!))
            .OrderByDescending(h => h.Connections)
            .ToList();

    /// <summary>"10.0.0.21:51234" → "10.0.0.21"; "[::1]:6379" → "::1"; unix sockets stay as is.</summary>
    public static string HostOf(string addr)
    {
        if (addr.StartsWith('['))
        {
            var end = addr.IndexOf(']', StringComparison.Ordinal);
            return end > 0 ? addr[1..end] : addr;
        }

        var colon = addr.LastIndexOf(':');
        return colon > 0 && addr.IndexOf(':') == colon ? addr[..colon] : addr;
    }

    /// <summary>CONFIG GET reply: [name, value, name, value…].</summary>
    public static IReadOnlyDictionary<string, string> ParseConfig(RespValue reply)
    {
        var items = reply.Items ?? [];
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < items.Count; i += 2)
        {
            if (items[i].Text is { } k)
            {
                d[k] = items[i + 1].Text ?? string.Empty;
            }
        }

        return d;
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length > max ? s[..max] + "…" : s;
}
