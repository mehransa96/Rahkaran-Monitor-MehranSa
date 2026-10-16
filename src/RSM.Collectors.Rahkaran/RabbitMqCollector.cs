using System.Collections.Concurrent;
using System.Text.Json;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Safety.Http;

namespace RSM.Collectors.Rahkaran;

/// <summary>GET of one whitelisted RabbitMQ management path (<see cref="RabbitGetClient.AllowedPaths"/>).</summary>
public interface IRabbitApi
{
    Task<string> GetAsync(string path, CancellationToken cancellationToken);
}

/// <summary>A RabbitMQ management endpoint reached only through <see cref="RabbitGetClient"/> (GET on three paths).</summary>
public sealed class RabbitNodeTransport(RabbitEndpoint endpoint, string? password) : IRabbitApi, IDisposable
{
    private readonly RabbitGetClient _client = new(endpoint.ManagementUrl, endpoint.UserName, password, TimeSpan.FromSeconds(10));

    public RabbitEndpoint Endpoint { get; } = endpoint;

    public Task<string> GetAsync(string path, CancellationToken cancellationToken) => _client.GetAsync(path, cancellationToken);

    public void Dispose() => _client.Dispose();
}

public sealed record RabbitNode(string Name, bool Running, long? MemUsed, long? MemLimit, bool MemAlarm, long? DiskFree, long? DiskFreeLimit, bool DiskAlarm,
    long? FdUsed, long? FdTotal, long? SocketsUsed, long? SocketsTotal, long? UptimeMs)
{
    public double? FdPct => FdTotal is > 0 && FdUsed is { } u ? 100.0 * u / FdTotal.Value : null;

    public double? SocketsPct => SocketsTotal is > 0 && SocketsUsed is { } u ? 100.0 * u / SocketsTotal.Value : null;
}

public sealed record RabbitQueue(string Name, string VHost, long Messages, long Ready, long Unacked, int Consumers, string? State);

public sealed record RabbitOverview(string? Version, string? ErlangVersion, string? ClusterName, long? Connections, long? Consumers, long? Queues);

public sealed record RabbitPayload(RabbitOverview? Overview, IReadOnlyList<RabbitNode> Nodes, IReadOnlyList<RabbitQueue> Queues, string? Unreachable);

public sealed class RabbitState
{
    public List<(DateTimeOffset At, long Unacked)> UnackedHistory { get; } = [];
}

/// <summary>
/// §11 RabbitMQ (Workflow module, optional): GET /api/overview, /api/nodes, /api/queues only — node memory/disk alarms,
/// file-descriptor and socket usage, queues with messages but no consumers (workflow stuck), unacked growth.
/// </summary>
public sealed class RabbitMqCollector : ILiveCollector, IRequiresTransport
{
    public const int StuckMessages = 100;
    public static readonly TimeSpan GrowthWindow = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, RabbitState> _state = new(StringComparer.Ordinal);

    public string Id => "rabbitmq";

    public Layer Layer => Layer.RabbitMq;

    public CollectorCost Cost => CollectorCost.Cheap;

    public NodeRoles AppliesTo => NodeRoles.Workflow;

    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(1);

    public Type TransportType => typeof(IRabbitApi);

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public async Task<CollectorResult> CollectAsync(INodeContext node, CollectContext context, CancellationToken cancellationToken)
    {
        var started = Clock.GetUtcNow();
        if (node.GetTransport<IRabbitApi>() is not { } rabbit)
        {
            return CollectorResult.FromStatus(Id, node.Node.Id, CollectorStatus.Unavailable("RabbitMQ management API not configured (off by default)", "rabbit.api"), TimeSpan.Zero);
        }

        var output = new LiveOutput();
        CollectorStatus status;
        try
        {
            var overview = ParseOverview(await rabbit.GetAsync("api/overview", cancellationToken).ConfigureAwait(false));
            var nodes = ParseNodes(await rabbit.GetAsync("api/nodes", cancellationToken).ConfigureAwait(false));
            var queues = ParseQueues(await rabbit.GetAsync("api/queues", cancellationToken).ConfigureAwait(false));
            Apply(_state.GetOrAdd(node.Node.Id, static _ => new RabbitState()), overview, nodes, queues, Clock.GetUtcNow(), output);
            status = CollectorStatus.Ok;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or HttpRequestRejectedException && !cancellationToken.IsCancellationRequested)
        {
            output.Fact("rmq.reachable", false);
            output.Fact("rmq.unreachable_reason", ex.Message);
            output.Payload["rabbitmq"] = new RabbitPayload(null, [], [], ex.Message);
            status = CollectorStatus.Unavailable(ex.Message, "rabbit.api");
        }

        return new CollectorResult(Id, node.Node.Id, started, Clock.GetUtcNow() - started, status, output.Payload, output.Samples, output.Facts, output.Events);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static RabbitOverview ParseOverview(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var totals = r.TryGetProperty("object_totals", out var t) ? t : default;
        return new RabbitOverview(Str(r, "rabbitmq_version"), Str(r, "erlang_version"), Str(r, "cluster_name"),
            totals.ValueKind == JsonValueKind.Object ? Long(totals, "connections") : null,
            totals.ValueKind == JsonValueKind.Object ? Long(totals, "consumers") : null,
            totals.ValueKind == JsonValueKind.Object ? Long(totals, "queues") : null);
    }

    public static IReadOnlyList<RabbitNode> ParseNodes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(n => new RabbitNode(Str(n, "name") ?? "?", Bool(n, "running"), Long(n, "mem_used"), Long(n, "mem_limit"),
            Bool(n, "mem_alarm"), Long(n, "disk_free"), Long(n, "disk_free_limit"), Bool(n, "disk_free_alarm"), Long(n, "fd_used"), Long(n, "fd_total"),
            Long(n, "sockets_used"), Long(n, "sockets_total"), Long(n, "uptime"))).ToList();
    }

    public static IReadOnlyList<RabbitQueue> ParseQueues(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(q => new RabbitQueue(Str(q, "name") ?? "?", Str(q, "vhost") ?? "/", Long(q, "messages") ?? 0,
            Long(q, "messages_ready") ?? 0, Long(q, "messages_unacknowledged") ?? 0, (int)(Long(q, "consumers") ?? 0), Str(q, "state"))).ToList();
    }

    internal static void Apply(RabbitState state, RabbitOverview overview, IReadOnlyList<RabbitNode> nodes, IReadOnlyList<RabbitQueue> queues, DateTimeOffset now, LiveOutput output)
    {
        output.Payload["rabbitmq"] = new RabbitPayload(overview, nodes, queues.OrderByDescending(q => q.Messages).Take(200).ToList(), null);
        output.Fact("rmq.reachable", true);
        output.Fact("rmq.version", overview.Version);
        output.Fact("rmq.nodes", nodes.Count);
        output.Fact("rmq.nodes_not_running", nodes.Count(n => !n.Running));
        output.Fact("rmq.alarm", nodes.Any(n => n.MemAlarm || n.DiskAlarm));
        output.Fact("rmq.alarm_nodes", string.Join(", ", nodes.Where(n => n.MemAlarm || n.DiskAlarm).Select(n => $"{n.Name} ({(n.MemAlarm ? "memory" : "disk")})")));
        output.Fact("rmq.fd_max_pct", nodes.Select(n => n.FdPct).Where(p => p is not null).DefaultIfEmpty(0).Max());
        output.Fact("rmq.sockets_max_pct", nodes.Select(n => n.SocketsPct).Where(p => p is not null).DefaultIfEmpty(0).Max());
        var stuck = queues.Where(q => q.Messages > StuckMessages && q.Consumers == 0).ToList();
        output.Fact("rmq.stuck_queues", stuck.Count);
        output.Fact("rmq.stuck_queue_names", string.Join(", ", stuck.Take(5).Select(q => $"{q.Name} ({q.Messages})")));
        var unacked = queues.Sum(q => q.Unacked);
        output.Fact("rmq.unacked", unacked);
        output.Sample("rmq.messages", null, queues.Sum(q => q.Messages), now);
        output.Sample("rmq.unacked", null, unacked, now);

        state.UnackedHistory.Add((now, unacked));
        state.UnackedHistory.RemoveAll(h => h.At < now - GrowthWindow - TimeSpan.FromMinutes(2));
        var window = state.UnackedHistory.Where(h => h.At >= now - GrowthWindow - TimeSpan.FromMinutes(1)).ToList();
        if (window.Count >= 3 && now - window[0].At >= GrowthWindow * 0.9)
        {
            // Growing = never fell during the window and ended higher than it started.
            var growing = window.Zip(window.Skip(1)).All(p => p.Second.Unacked >= p.First.Unacked) && window[^1].Unacked > window[0].Unacked;
            output.Fact("rmq.unacked_growing_15m", growing);
        }
    }
}
