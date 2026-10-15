using System.Net;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Safety.Redis;

namespace RSM.Collectors.Redis;

/// <summary>A Redis endpoint with one lazily opened whitelist connection (re-opened after a failure).</summary>
public sealed class RedisNodeTransport(RedisEndpoint endpoint, string? password) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RespWhitelistClient? _client;

    public RedisEndpoint Endpoint { get; } = endpoint;

    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(3);

    /// <summary>Runs one whitelisted command; connects and authenticates first when needed.</summary>
    public async Task<RespValue> ExecuteAsync(CancellationToken cancellationToken, params string[] args)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is null)
            {
                var client = await RespWhitelistClient.ConnectAsync(Endpoint.Host, Endpoint.Port, Timeout, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(password) && await client.ExecuteAsync(cancellationToken, "AUTH", password).ConfigureAwait(false) is { IsError: true } auth)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    throw new RedisUnavailableException("AUTH failed: " + auth.Text);
                }

                _client = client;
            }

            return await _client.ExecuteAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisUnavailableException or RespProtocolException)
        {
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _gate.Dispose();
    }
}

public sealed class RedisState
{
    public long? EvictedKeys { get; set; }

    public long? RejectedConnections { get; set; }

    public DateTimeOffset ClientListAt { get; set; } = DateTimeOffset.MinValue;

    public IReadOnlyList<RedisClientHost> Clients { get; set; } = [];

    public DateTimeOffset ConfigAt { get; set; } = DateTimeOffset.MinValue;

    public IReadOnlyDictionary<string, string> Config { get; set; } = new Dictionary<string, string>();
}

public sealed record RedisPayload(RedisInfoSnapshot? Info, IReadOnlyList<SlowlogEntry> Slowlog, long? SlowlogLength, IReadOnlyList<LatencyEvent> Latency,
    IReadOnlyList<RedisClientHost> Clients, IReadOnlyList<string> AppNodesWithoutConnection, string? Unreachable);

/// <summary>
/// §11 Redis through the whitelist client only: INFO, DBSIZE, SLOWLOG GET/LEN, LATENCY LATEST, CLIENT LIST (every
/// minute) and CONFIG GET maxmemory* (every 10 minutes). Unreachable / AUTH failure is itself a fact (RDS-001).
/// </summary>
public sealed class RedisCollector : ILiveCollector, IRequiresTransport
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(60);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RedisState> _state = new(StringComparer.Ordinal);

    public string Id => "redis";

    public Layer Layer => Layer.Redis;

    public CollectorCost Cost => CollectorCost.Cheap;

    public NodeRoles AppliesTo => NodeRoles.Data | NodeRoles.App | NodeRoles.Report | NodeRoles.Arr | NodeRoles.Redis | NodeRoles.Lock | NodeRoles.Workflow;

    public TimeSpan DefaultInterval => TimeSpan.FromSeconds(15);

    public Type TransportType => typeof(RedisNodeTransport);

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Resolves App node host names to addresses for the CLIENT LIST comparison (RDS-009); replaceable in tests.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<string>>> Resolve { get; init; } = async (host, ct) =>
    {
        try
        {
            return (await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false)).Select(a => a.ToString()).ToList();
        }
        catch (System.Net.Sockets.SocketException)
        {
            return [];
        }
    };

    public async Task<CollectorResult> CollectAsync(INodeContext node, CollectContext context, CancellationToken cancellationToken)
    {
        var started = Clock.GetUtcNow();
        if (node.GetTransport<RedisNodeTransport>() is not { } redis)
        {
            return CollectorResult.FromStatus(Id, node.Node.Id, CollectorStatus.Unavailable("Redis not configured for this node", "redis.ping"), TimeSpan.Zero);
        }

        var state = _state.GetOrAdd(node.Node.Id, static _ => new RedisState());
        var output = new LiveOutput();
        var now = Clock.GetUtcNow();
        try
        {
            var info = await redis.ExecuteAsync(cancellationToken, "INFO").ConfigureAwait(false);
            if (info.IsError)
            {
                output.Fact("redis.reachable", false);
                output.Fact("redis.unreachable_reason", info.Text);
                output.Payload["redis"] = new RedisPayload(null, [], null, [], [], [], info.Text);
                return Result(node, started, CollectorStatus.Unavailable(info.Text ?? "INFO failed", "redis.info"), output);
            }

            var dbsize = await redis.ExecuteAsync(cancellationToken, "DBSIZE").ConfigureAwait(false);
            var snapshot = RedisAnalysis.ParseInfo(info.Text ?? string.Empty, dbsize.IsError ? null : dbsize.Integer);
            var problems = new List<string>();
            var slowlog = await Optional(redis, problems, cancellationToken, "SLOWLOG", "GET", "128").ConfigureAwait(false);
            var slowlogLen = await Optional(redis, problems, cancellationToken, "SLOWLOG", "LEN").ConfigureAwait(false);
            var latency = await Optional(redis, problems, cancellationToken, "LATENCY", "LATEST").ConfigureAwait(false);
            if (now - state.ClientListAt >= TimeSpan.FromMinutes(1) && await Optional(redis, problems, cancellationToken, "CLIENT", "LIST").ConfigureAwait(false) is { } clients)
            {
                state.Clients = RedisAnalysis.ParseClientList(clients.Text ?? string.Empty);
                state.ClientListAt = now;
            }

            if (now - state.ConfigAt >= TimeSpan.FromMinutes(10) && await Optional(redis, problems, cancellationToken, "CONFIG", "GET", "maxmemory*").ConfigureAwait(false) is { } config)
            {
                state.Config = RedisAnalysis.ParseConfig(config);
                state.ConfigAt = now;
            }

            var missing = await AppNodesWithoutConnectionAsync(node, state.Clients, cancellationToken).ConfigureAwait(false);
            Apply(state, snapshot, slowlog is null ? [] : RedisAnalysis.ParseSlowlog(slowlog), slowlogLen?.Integer,
                latency is null ? [] : RedisAnalysis.ParseLatency(latency), missing, now, output);
            return Result(node, started, problems.Count == 0 ? CollectorStatus.Ok : CollectorStatus.Partial("some commands unavailable", string.Join("; ", problems)), output);
        }
        catch (Exception ex) when (ex is RedisUnavailableException or RespProtocolException)
        {
            output.Fact("redis.reachable", false);
            output.Fact("redis.unreachable_reason", ex.Message);
            output.Payload["redis"] = new RedisPayload(null, [], null, [], [], [], ex.Message);
            output.Events.Add(new UnifiedEvent(now, node.Node.Id, Layer.Redis, "Redis", "unreachable", Severity.Critical, ex.Message));
            return Result(node, started, CollectorStatus.Unavailable(ex.Message, "redis.ping"), output);
        }
    }

    private CollectorResult Result(INodeContext node, DateTimeOffset started, CollectorStatus status, LiveOutput output) =>
        new(Id, node.Node.Id, started, Clock.GetUtcNow() - started, status, output.Payload, output.Samples, output.Facts, output.Events);

    /// <summary>A command that may be renamed or disabled on this Redis: an error reply is recorded, not thrown.</summary>
    private static async Task<RespValue?> Optional(RedisNodeTransport redis, List<string> problems, CancellationToken cancellationToken, params string[] args)
    {
        var reply = await redis.ExecuteAsync(cancellationToken, args).ConfigureAwait(false);
        if (reply.IsError)
        {
            problems.Add($"{args[0]} {args[1]}: {reply.Text}");
            return null;
        }

        return reply;
    }

    /// <summary>App nodes of the site whose addresses never appear in CLIENT LIST (RDS-009). Unknown until CLIENT LIST was read.</summary>
    private async Task<IReadOnlyList<string>?> AppNodesWithoutConnectionAsync(INodeContext node, IReadOnlyList<RedisClientHost> clients, CancellationToken cancellationToken)
    {
        if (clients.Count == 0)
        {
            return null;
        }

        var connected = clients.Select(c => c.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var app in node.Site.Nodes.Where(n => n.Roles.HasFlag(NodeRoles.App)))
        {
            var addresses = IPAddress.TryParse(app.Host, out _) ? [app.Host] : await Resolve(app.Host, cancellationToken).ConfigureAwait(false);
            var local = app.Id == node.Node.Id || string.Equals(app.Host, node.Node.Host, StringComparison.OrdinalIgnoreCase);
            if (!addresses.Any(connected.Contains) && !(local && (connected.Contains("127.0.0.1") || connected.Contains("::1"))))
            {
                missing.Add(app.Name);
            }
        }

        return missing;
    }

    internal static void Apply(RedisState state, RedisInfoSnapshot info, IReadOnlyList<SlowlogEntry> slowlog, long? slowlogLength, IReadOnlyList<LatencyEvent> latency,
        IReadOnlyList<string>? appNodesWithoutConnection, DateTimeOffset now, LiveOutput output)
    {
        output.Payload["redis"] = new RedisPayload(info, slowlog, slowlogLength, latency, state.Clients, appNodesWithoutConnection ?? [], null);
        output.Fact("redis.reachable", true);
        output.Fact("redis.version", info.Version);
        output.Fact("redis.role", info.Role);
        output.Fact("redis.uptime_s", info.UptimeSeconds);
        output.Fact("redis.used_memory_mb", info.UsedMemory / 1048576.0);
        var maxmemory = info.MaxMemory ?? (long.TryParse(state.Config.GetValueOrDefault("maxmemory"), out var m) ? m : null);
        output.Fact("redis.maxmemory_mb", maxmemory / 1048576.0);
        output.Fact("redis.maxmemory_zero", maxmemory == 0);
        output.Fact("redis.maxmemory_policy", info.MaxMemoryPolicy ?? state.Config.GetValueOrDefault("maxmemory-policy"));
        output.Fact("redis.used_memory_pct", maxmemory is > 0 && info.UsedMemory is { } used ? 100.0 * used / maxmemory.Value : null);
        output.Fact("redis.fragmentation_ratio", info.FragmentationRatio);
        output.Fact("redis.evicted_keys", info.EvictedKeys);
        output.Fact("redis.hit_ratio_pct", info.HitRatioPct);
        output.Fact("redis.connected_clients", info.ConnectedClients);
        output.Fact("redis.blocked_clients", info.BlockedClients);
        output.Fact("redis.ops_per_sec", info.OpsPerSec);
        output.Fact("redis.keys", info.Keys);
        output.Fact("redis.rdb_status", info.RdbLastBgsaveStatus);
        output.Fact("redis.aof_status", info.AofEnabled == true ? info.AofLastWriteStatus : "off");
        output.Fact("redis.persistence_error", info.PersistenceError);
        if (state.EvictedKeys is { } previousEvicted && info.EvictedKeys is { } evicted)
        {
            output.Fact("redis.evicted_increasing", evicted > previousEvicted);
        }

        if (state.RejectedConnections is { } previousRejected && info.RejectedConnections is { } rejected)
        {
            output.Fact("redis.rejected_increasing", rejected > previousRejected);
        }

        state.EvictedKeys = info.EvictedKeys;
        state.RejectedConnections = info.RejectedConnections;

        var recentSlow = slowlog.Where(s => s.AtUtc >= now - Window).ToList();
        output.Fact("redis.slowlog_60m", recentSlow.Count);
        output.Fact("redis.slowlog_max_ms", recentSlow.Count == 0 ? 0 : recentSlow.Max(s => s.DurationMs));
        var recentLatency = latency.Where(l => l.AtUtc >= now - Window).ToList();
        output.Fact("redis.latency_max_ms", recentLatency.Count == 0 ? 0 : recentLatency.Max(l => l.LatestMs));
        if (appNodesWithoutConnection is not null)
        {
            output.Fact("redis.app_nodes_without_connection", appNodesWithoutConnection.Count);
            output.Fact("redis.app_nodes_without_connection_names", string.Join(", ", appNodesWithoutConnection));
        }

        output.Fact("redis.client_hosts", state.Clients.Select(c => (object?)c.Address).ToList());
        output.Sample("redis.used_memory_mb", null, info.UsedMemory / 1048576.0, now);
        output.Sample("redis.ops_per_sec", null, info.OpsPerSec, now);
        output.Sample("redis.connected_clients", null, info.ConnectedClients, now);
        output.Sample("redis.hit_ratio_pct", null, info.HitRatioPct, now);
    }
}
