using System.Diagnostics;
using System.Runtime.Versioning;

namespace RSM.Collectors.Windows;

/// <summary>
/// Local Windows counters through PDH (<see cref="PerformanceCounter"/>, read-only: counters are opened with
/// readOnly = true and only <c>NextValue</c> is called). Keys come from <see cref="WindowsMetricCatalog"/>, exactly as
/// for <see cref="CimCounterSource"/>. Instances of disks/NICs are re-enumerated every <see cref="InstanceRefresh"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PdhCounterSource : IWindowsCounters, IDisposable
{
    private readonly Dictionary<(string Key, string? Instance), (WinMetric Metric, PerformanceCounter Counter)> _counters = [];
    private readonly object _gate = new();
    private DateTimeOffset _instancesReadAt = DateTimeOffset.MinValue;
    private bool _fresh = true;

    public string Transport => "pdh";

    public TimeSpan InstanceRefresh { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan WarmUp { get; init; } = TimeSpan.FromSeconds(1);

    public Task<IReadOnlyList<MetricReading>> ReadAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _instancesReadAt > InstanceRefresh)
            {
                RefreshCounters();
            }

            if (_fresh)
            {
                // Rate counters return 0 on the first NextValue(): prime them once.
                foreach (var (_, counter) in _counters.Values)
                {
                    TryNext(counter);
                }

                _fresh = false;
                cancellationToken.WaitHandle.WaitOne(WarmUp);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var readings = new List<MetricReading>(_counters.Count);
            foreach (var ((key, instance), (metric, counter)) in _counters)
            {
                if (TryNext(counter) is { } value)
                {
                    readings.Add(new MetricReading(key, metric.Instances == CounterInstances.Total ? null : instance, value * metric.PdhScale));
                }
            }

            return (IReadOnlyList<MetricReading>)readings;
        }
    }, cancellationToken);

    private void RefreshCounters()
    {
        var wanted = new HashSet<(string, string?)>();
        foreach (var metric in WindowsMetricCatalog.All)
        {
            foreach (var instance in Instances(metric))
            {
                wanted.Add((metric.Key, instance));
                if (!_counters.ContainsKey((metric.Key, instance)))
                {
                    try
                    {
                        _counters[(metric.Key, instance)] = (metric, new PerformanceCounter(metric.PdhCategory, metric.PdhCounter, instance ?? string.Empty, readOnly: true));
                        _fresh = true;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    {
                        // Counter missing on this OS build or not readable (Performance Monitor Users group): skip it.
                    }
                }
            }
        }

        foreach (var gone in _counters.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _counters[gone].Counter.Dispose();
            _counters.Remove(gone);
        }

        _instancesReadAt = DateTimeOffset.UtcNow;
    }

    private static IEnumerable<string?> Instances(WinMetric metric)
    {
        switch (metric.Instances)
        {
            case CounterInstances.Single:
                return [null];
            case CounterInstances.Total:
                return ["_Total"];
            default:
                try
                {
                    return new PerformanceCounterCategory(metric.PdhCategory).GetInstanceNames()
                        .Where(i => WindowsMetricCatalog.IsUsefulInstance(metric, i)).Cast<string?>().ToList();
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    return [];
                }
        }
    }

    private static double? TryNext(PerformanceCounter counter)
    {
        try
        {
            return counter.NextValue();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null; // instance disappeared (NIC disabled, volume dismounted)
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var (_, counter) in _counters.Values)
            {
                counter.Dispose();
            }

            _counters.Clear();
        }
    }
}
