using System.Collections.Concurrent;
using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Live;

/// <summary>What a live collector produces in one tick: facts for rules, samples for charts, events, payload rows.</summary>
public sealed class LiveOutput
{
    public List<Fact> Facts { get; } = [];

    public List<MetricSample> Samples { get; } = [];

    public List<UnifiedEvent> Events { get; } = [];

    public Dictionary<string, object?> Payload { get; } = new(StringComparer.Ordinal);

    public void Fact(string key, object? value)
    {
        if (value is not null)
        {
            Facts.Add(new Fact(key, value));
        }
    }

    public void Sample(string key, string? instance, double? value, DateTimeOffset atUtc)
    {
        if (value is { } v && double.IsFinite(v))
        {
            Samples.Add(new MetricSample(key, instance, v, atUtc));
        }
    }
}

/// <summary>
/// Base for live SQL collectors (P2). Collectors are singletons; anything that must survive between ticks (previous
/// cumulative samples for deltas, short windows for "sustained" facts) is kept per node in <see cref="StateFor"/>.
/// The analysis itself lives in pure methods so it can be unit-tested with fixture rows.
/// </summary>
public abstract class SqlLiveCollectorBase<TState>(SafeSqlExecutor executor) : ILiveCollector
    where TState : class, new()
{
    private readonly ConcurrentDictionary<string, TState> _state = new(StringComparer.Ordinal);

    public abstract string Id { get; }

    public virtual Layer Layer => Layer.Sql;

    public virtual CollectorCost Cost => CollectorCost.Cheap;

    public virtual NodeRoles AppliesTo => NodeRoles.Data | NodeRoles.Report;

    public abstract TimeSpan DefaultInterval { get; }

    /// <summary>Clock used for timestamps and delta intervals (tests replace it).</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public async Task<CollectorResult> CollectAsync(INodeContext node, CollectContext context, CancellationToken cancellationToken)
    {
        var started = Clock.GetUtcNow();
        var session = node.GetTransport<SqlNodeSession>();
        if (session is null)
        {
            return CollectorResult.FromStatus(Id, node.Node.Id, CollectorStatus.Unavailable("SQL Server not configured for this node", "sql.connect"), TimeSpan.Zero);
        }

        var runner = new SqlQueryRunner(executor, session, cancellationToken);
        var output = new LiveOutput();
        var state = _state.GetOrAdd(node.Node.Id, static _ => new TState());
        await CollectCoreAsync(node, context, runner, state, output).ConfigureAwait(false);
        return new CollectorResult(Id, node.Node.Id, started, Clock.GetUtcNow() - started, runner.ToStatus(),
            new SqlCollectorPayload(output.Payload, runner.Outcomes), output.Samples, output.Facts, output.Events);
    }

    /// <summary>Per-node state (exposed for tests and for "forget this node" when a site is closed).</summary>
    public TState StateFor(string nodeId) => _state.GetOrAdd(nodeId, static _ => new TState());

    public void Forget(string nodeId) => _state.TryRemove(nodeId, out _);

    protected abstract Task CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, TState state, LiveOutput output);
}

/// <summary>Collectors without state between ticks.</summary>
public sealed class NoState;

/// <summary>Small helpers for windows of recent values ("sustained for N samples").</summary>
public static class Window
{
    /// <summary>Min of the last <paramref name="count"/> values, or null when fewer values exist.</summary>
    public static double? MinOfLast(IReadOnlyList<double> values, int count) =>
        values.Count >= count ? values.Skip(values.Count - count).Min() : null;

    /// <summary>Max of the last <paramref name="count"/> values, or null when fewer values exist.</summary>
    public static double? MaxOfLast(IReadOnlyList<double> values, int count) =>
        values.Count >= count ? values.Skip(values.Count - count).Max() : null;

    public static void Push<T>(List<T> list, T value, int capacity)
    {
        list.Add(value);
        if (list.Count > capacity)
        {
            list.RemoveRange(0, list.Count - capacity);
        }
    }
}
