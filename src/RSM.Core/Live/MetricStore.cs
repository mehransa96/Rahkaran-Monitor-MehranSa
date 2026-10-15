using RSM.Core.Model;

namespace RSM.Core.Live;

/// <summary>One point of a series.</summary>
public readonly record struct MetricPoint(DateTimeOffset AtUtc, double Value);

/// <summary>Identifies a series: node, metric key and optional instance (database, volume, NIC…).</summary>
public readonly record struct SeriesId(string NodeId, string Key, string? Instance)
{
    public static SeriesId Of(string nodeId, MetricSample s) => new(nodeId, s.Key, s.Instance);
}

/// <summary>
/// In-memory ring buffers for the live charts (15/30/60 min windows). Points older than <see cref="Retention"/> are
/// dropped and every series is capped, so memory stays bounded however long the view runs (P2: process &lt; 500 MB).
/// Persistence and downsampling are P5.
/// </summary>
public sealed class MetricStore(TimeSpan? retention = null, int maxPointsPerSeries = 1500)
{
    private readonly Dictionary<SeriesId, List<MetricPoint>> _series = [];
    private readonly object _gate = new();

    public TimeSpan Retention { get; } = retention ?? TimeSpan.FromMinutes(60);

    public int MaxPointsPerSeries { get; } = maxPointsPerSeries;

    /// <summary>Raised after samples were added (on the collector's thread; the UI marshals).</summary>
    public event Action<string>? Changed;

    public int SeriesCount
    {
        get
        {
            lock (_gate)
            {
                return _series.Count;
            }
        }
    }

    public int PointCount
    {
        get
        {
            lock (_gate)
            {
                return _series.Values.Sum(s => s.Count);
            }
        }
    }

    public void Add(string nodeId, IEnumerable<MetricSample> samples)
    {
        var added = false;
        lock (_gate)
        {
            foreach (var sample in samples)
            {
                if (!double.IsFinite(sample.Value))
                {
                    continue;
                }

                var id = SeriesId.Of(nodeId, sample);
                if (!_series.TryGetValue(id, out var list))
                {
                    _series[id] = list = [];
                }

                // Back-filled history (CPU ring buffer) may arrive out of order: keep the list sorted.
                var point = new MetricPoint(sample.AtUtc, sample.Value);
                if (list.Count == 0 || list[^1].AtUtc < point.AtUtc)
                {
                    list.Add(point);
                }
                else if (list[^1].AtUtc == point.AtUtc)
                {
                    list[^1] = point;
                }
                else
                {
                    var index = list.FindIndex(p => p.AtUtc >= point.AtUtc);
                    if (index >= 0 && list[index].AtUtc == point.AtUtc)
                    {
                        list[index] = point;
                    }
                    else
                    {
                        list.Insert(index < 0 ? list.Count : index, point);
                    }
                }

                Trim(list);
                added = true;
            }
        }

        if (added)
        {
            Changed?.Invoke(nodeId);
        }
    }

    /// <summary>Points of one series since <paramref name="sinceUtc"/> (copy; safe to enumerate on the UI thread).</summary>
    public IReadOnlyList<MetricPoint> Series(string nodeId, string key, string? instance, DateTimeOffset sinceUtc)
    {
        lock (_gate)
        {
            return _series.TryGetValue(new SeriesId(nodeId, key, instance), out var list)
                ? list.Where(p => p.AtUtc >= sinceUtc).ToList()
                : [];
        }
    }

    public MetricPoint? Latest(string nodeId, string key, string? instance = null)
    {
        lock (_gate)
        {
            return _series.TryGetValue(new SeriesId(nodeId, key, instance), out var list) && list.Count > 0 ? list[^1] : null;
        }
    }

    /// <summary>Instances that have a series for this key on the node (e.g. every database for a per-DB counter).</summary>
    public IReadOnlyList<string?> Instances(string nodeId, string key)
    {
        lock (_gate)
        {
            return _series.Keys.Where(k => k.NodeId == nodeId && k.Key == key).Select(k => k.Instance).OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public void Clear(string? nodeId = null)
    {
        lock (_gate)
        {
            foreach (var id in _series.Keys.Where(k => nodeId is null || k.NodeId == nodeId).ToList())
            {
                _series.Remove(id);
            }
        }
    }

    private void Trim(List<MetricPoint> list)
    {
        var cutoff = list[^1].AtUtc - Retention;
        var old = list.FindIndex(p => p.AtUtc >= cutoff);
        if (old > 0)
        {
            list.RemoveRange(0, old);
        }

        if (list.Count > MaxPointsPerSeries)
        {
            list.RemoveRange(0, list.Count - MaxPointsPerSeries);
        }
    }
}
