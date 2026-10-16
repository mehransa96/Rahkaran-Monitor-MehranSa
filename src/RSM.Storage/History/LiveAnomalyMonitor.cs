using RSM.Core.History;
using RSM.Core.Live;
using RSM.Core.Model;

namespace RSM.Storage.History;

/// <summary>One live anomaly with the node it belongs to.</summary>
public sealed record LiveAnomaly(string NodeId, Anomaly Anomaly);

/// <summary>
/// Spec §15 "flag anomalies in live view": compares every live sample with the node's stored baseline (loaded when the
/// live view starts) and keeps the runs that currently exceed p95 × factor for the consecutive count, plus the last
/// ended ones. Nodes without a baseline (less than ~2 weeks of history) never flag.
/// </summary>
public sealed class LiveAnomalyMonitor
{
    public const int MaxEnded = 100;

    private readonly Dictionary<(string Node, string Key, string? Instance), AnomalyTracker> _trackers = [];
    private readonly List<LiveAnomaly> _ended = [];
    private readonly object _gate = new();
    private readonly LiveSession _session;

    private LiveAnomalyMonitor(LiveSession session, IReadOnlyDictionary<string, NodeBaselines> baselines, TimeZoneInfo zone, AnomalyOptions options)
    {
        _session = session;
        foreach (var (node, b) in baselines)
        {
            foreach (var s in b.Series)
            {
                _trackers[(node, s.Key, s.Instance)] = new AnomalyTracker(s, zone, options);
            }
        }

        session.Updated += OnUpdated;
    }

    /// <summary>Raised (on a worker thread) when an anomaly starts or ends.</summary>
    public event Action? Changed;

    public static async Task<LiveAnomalyMonitor> AttachAsync(string siteId, LiveSession session, BaselineStore store, TimeZoneInfo zone, AnomalyOptions? options,
        CancellationToken cancellationToken)
    {
        var baselines = new Dictionary<string, NodeBaselines>(StringComparer.Ordinal);
        foreach (var node in session.Nodes)
        {
            if (await store.LoadAsync(siteId, node.Id, cancellationToken).ConfigureAwait(false) is { } b && b.TimeZoneId == zone.Id)
            {
                baselines[node.Id] = b;
            }
        }

        return new LiveAnomalyMonitor(session, baselines, zone, options ?? AnomalyOptions.Default);
    }

    /// <summary>Number of series with a baseline (0 ⇒ the UI says "no baseline yet").</summary>
    public int TrackedSeries => _trackers.Count;

    public IReadOnlyList<LiveAnomaly> Active()
    {
        lock (_gate)
        {
            return _trackers.Where(t => t.Value.Current is not null).Select(t => new LiveAnomaly(t.Key.Node, t.Value.Current!)).OrderBy(a => a.Anomaly.FromUtc).ToList();
        }
    }

    public IReadOnlyList<LiveAnomaly> Ended()
    {
        lock (_gate)
        {
            return _ended.ToList();
        }
    }

    private void OnUpdated(string nodeId, CollectorResult result)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var s in result.Samples.OrderBy(s => s.AtUtc))
            {
                if (!_trackers.TryGetValue((nodeId, s.Key, s.Instance), out var tracker))
                {
                    continue;
                }

                var wasActive = tracker.Current is not null;
                if (tracker.Add(s.AtUtc, s.Value) is { } ended)
                {
                    _ended.Add(new LiveAnomaly(nodeId, ended));
                    if (_ended.Count > MaxEnded)
                    {
                        _ended.RemoveAt(0);
                    }
                }

                changed |= wasActive != (tracker.Current is not null);
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public void Detach() => _session.Updated -= OnUpdated;
}
