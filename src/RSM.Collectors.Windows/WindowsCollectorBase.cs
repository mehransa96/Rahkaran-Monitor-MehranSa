using System.Collections.Concurrent;
using RSM.Core.Model;

namespace RSM.Collectors.Windows;

/// <summary>
/// Base for Windows collectors: resolves the node's <see cref="WindowsNodeTransport"/>, keeps per-node state, and
/// turns access problems into a degraded status with the reason (never a crash).
/// </summary>
public abstract class WindowsCollectorBase<TState> : ILiveCollector, IRequiresTransport
    where TState : class, new()
{
    public Type TransportType => typeof(WindowsNodeTransport);

    private readonly ConcurrentDictionary<string, TState> _state = new(StringComparer.Ordinal);

    public abstract string Id { get; }

    public virtual Layer Layer => Layer.Os;

    public virtual CollectorCost Cost => CollectorCost.Cheap;

    /// <summary>Windows facts matter on every node role.</summary>
    public virtual NodeRoles AppliesTo => NodeRoles.Data | NodeRoles.App | NodeRoles.Report | NodeRoles.Arr | NodeRoles.Redis | NodeRoles.Lock | NodeRoles.Workflow;

    public abstract TimeSpan DefaultInterval { get; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public async Task<CollectorResult> CollectAsync(INodeContext node, CollectContext context, CancellationToken cancellationToken)
    {
        var started = Clock.GetUtcNow();
        var transport = node.GetTransport<WindowsNodeTransport>();
        if (transport is null)
        {
            return CollectorResult.FromStatus(Id, node.Node.Id, CollectorStatus.Unavailable("Windows access (CIM) not available for this node", "win.cim"), TimeSpan.Zero);
        }

        var output = new LiveOutput();
        var problems = new List<string>();
        try
        {
            await CollectCoreAsync(node, transport, _state.GetOrAdd(node.Node.Id, static _ => new TState()), output, problems, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            return CollectorResult.FromStatus(Id, node.Node.Id, CollectorStatus.Unavailable("access denied", "win.cim", ex.Message), Clock.GetUtcNow() - started);
        }

        var status = problems.Count == 0 ? CollectorStatus.Ok
            : output.Facts.Count + output.Samples.Count == 0 ? CollectorStatus.Unavailable(problems[0], null, string.Join("; ", problems))
            : CollectorStatus.Partial("some sources unavailable", string.Join("; ", problems));
        return new CollectorResult(Id, node.Node.Id, started, Clock.GetUtcNow() - started, status, output.Payload, output.Samples, output.Facts, output.Events);
    }

    public TState StateFor(string nodeId) => _state.GetOrAdd(nodeId, static _ => new TState());

    /// <summary>Collects; adds a short reason to <paramref name="problems"/> for every source that could not be read.</summary>
    protected abstract Task CollectCoreAsync(INodeContext node, WindowsNodeTransport transport, TState state, LiveOutput output, List<string> problems, CancellationToken cancellationToken);

    /// <summary>Runs a CIM query; on failure records why and returns null.</summary>
    protected static async Task<IReadOnlyList<CimRow>?> TryQueryAsync(ICimQuery cim, CimQuery query, List<string> problems, CancellationToken cancellationToken)
    {
        try
        {
            return await cim.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
        {
            problems.Add($"{query.ClassName}: {ex.GetType().Name}");
            return null;
        }
    }
}
