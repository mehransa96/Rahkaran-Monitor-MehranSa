using System.Collections.Concurrent;
using RSM.Core.Model;

namespace RSM.Collectors.Iis;

/// <summary>
/// Base for IIS collectors: resolves the node's <see cref="IisNodeTransport"/>, keeps per-node state and turns access
/// problems into a degraded status with the reason (never a crash).
/// </summary>
public abstract class IisCollectorBase<TState> : ILiveCollector, IRequiresTransport
    where TState : class, new()
{
    private readonly ConcurrentDictionary<string, TState> _state = new(StringComparer.Ordinal);

    public Type TransportType => typeof(IisNodeTransport);

    public abstract string Id { get; }

    public virtual Layer Layer => Layer.Iis;

    public virtual CollectorCost Cost => CollectorCost.Cheap;

    /// <summary>IIS runs on App nodes and the ARR server.</summary>
    public NodeRoles AppliesTo => NodeRoles.App | NodeRoles.Arr;

    public abstract TimeSpan DefaultInterval { get; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>The capability reported when the collector cannot read anything.</summary>
    protected virtual string Capability => "iis.config";

    public async Task<CollectorResult> CollectAsync(INodeContext node, CollectContext context, CancellationToken cancellationToken)
    {
        var started = Clock.GetUtcNow();
        var transport = node.GetTransport<IisNodeTransport>();
        if (transport is null)
        {
            return CollectorResult.FromStatus(Id, node.Node.Id, CollectorStatus.Unavailable("IIS access not available for this node", Capability), TimeSpan.Zero);
        }

        var output = new LiveOutput();
        var problems = new List<string>();
        try
        {
            await CollectCoreAsync(node, transport, _state.GetOrAdd(node.Node.Id, static _ => new TState()), context, output, problems, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            return CollectorResult.FromStatus(Id, node.Node.Id, CollectorStatus.Unavailable("access denied", Capability, ex.Message), Clock.GetUtcNow() - started);
        }

        var status = problems.Count == 0 ? CollectorStatus.Ok
            : output.Facts.Count + output.Samples.Count + output.Payload.Count == 0 ? CollectorStatus.Unavailable(problems[0], Capability, string.Join("; ", problems))
            : CollectorStatus.Partial("some sources unavailable", string.Join("; ", problems));
        return new CollectorResult(Id, node.Node.Id, started, Clock.GetUtcNow() - started, status, output.Payload, output.Samples, output.Facts, output.Events);
    }

    public TState StateFor(string nodeId) => _state.GetOrAdd(nodeId, static _ => new TState());

    protected abstract Task CollectCoreAsync(INodeContext node, IisNodeTransport transport, TState state, CollectContext context, LiveOutput output, List<string> problems, CancellationToken cancellationToken);

    /// <summary>Reads a server text file; on failure records why (missing, denied, share unreachable) and returns null.</summary>
    protected static async Task<string?> TryReadAsync(IServerFiles files, string? accessPath, string what, List<string> problems, CancellationToken cancellationToken)
    {
        if (accessPath is null)
        {
            problems.Add($"{what}: path cannot be mapped");
            return null;
        }

        try
        {
            return await files.ReadAllTextAsync(accessPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            problems.Add($"{what}: {ex.GetType().Name}");
            return null;
        }
    }
}
