namespace RSM.Collectors.Windows;

/// <summary>
/// Windows counters over CIM (remote nodes, or local when PDH is unavailable): one query per performance class,
/// mapped to the keys of <see cref="WindowsMetricCatalog"/>. Rate counters need two samples, so the first read
/// warms up and samples again after <see cref="WarmUp"/>.
/// </summary>
public sealed class CimCounterSource(ICimQuery cim) : IWindowsCounters
{
    private readonly Dictionary<(string Class, string Instance), CimRow> _previousRaw = [];
    private bool _warm;

    public string Transport => "cim";

    public TimeSpan WarmUp { get; init; } = TimeSpan.FromSeconds(1);

    public async Task<IReadOnlyList<MetricReading>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!_warm)
        {
            await ReadOnceAsync(cancellationToken).ConfigureAwait(false);
            _warm = true;
            if (WarmUp > TimeSpan.Zero)
            {
                await Task.Delay(WarmUp, cancellationToken).ConfigureAwait(false);
            }
        }

        return await ReadOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MetricReading>> ReadOnceAsync(CancellationToken cancellationToken)
    {
        var readings = new List<MetricReading>();
        foreach (var group in WindowsMetricCatalog.All.GroupBy(m => (m.CimClass, m.Cooking, m.Instances)))
        {
            var (className, cooking, instances) = group.Key;
            var properties = new List<string> { "Name" };
            properties.AddRange(group.Select(m => m.CimProperty));
            if (cooking == CimCooking.RawAverageTimer)
            {
                properties.AddRange(group.Select(m => m.CimProperty + "_Base"));
                properties.Add("Frequency_PerfTime");
            }

            var where = instances == CounterInstances.Total ? "Name = '_Total'" : null;
            IReadOnlyList<CimRow> rows;
            try
            {
                rows = await cim.QueryAsync(new CimQuery(className, properties.Distinct().ToList(), where), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not UnauthorizedAccessException)
            {
                continue; // one missing class (e.g. no TCPv4 provider) must not hide the others
            }

            foreach (var row in rows)
            {
                var instance = instances == CounterInstances.Single ? null : row.String("Name") ?? "_Total";
                foreach (var metric in group)
                {
                    if (instance is not null && !WindowsMetricCatalog.IsUsefulInstance(metric, instance))
                    {
                        continue;
                    }

                    var value = cooking == CimCooking.Formatted
                        ? row.Number(metric.CimProperty)
                        : AverageTimer(className, instance ?? string.Empty, metric.CimProperty, row);
                    if (value is { } v)
                    {
                        readings.Add(new MetricReading(metric.Key, instances == CounterInstances.Total ? null : instance, v * metric.CimScale));
                    }
                }

                if (cooking == CimCooking.RawAverageTimer)
                {
                    _previousRaw[(className, instance ?? string.Empty)] = row;
                }
            }
        }

        return readings;
    }

    /// <summary>PERF_AVERAGE_TIMER: (ΔN / F) / ΔB seconds; no previous sample or no operations → 0 / null.</summary>
    private double? AverageTimer(string className, string instance, string property, CimRow row)
    {
        if (!_previousRaw.TryGetValue((className, instance), out var previous))
        {
            return null;
        }

        var n = row.Number(property) - previous.Number(property);
        var b = row.Number(property + "_Base") - previous.Number(property + "_Base");
        var f = row.Number("Frequency_PerfTime");
        if (n is null || b is null || f is not > 0 || n < 0 || b < 0)
        {
            return null;
        }

        return b == 0 ? 0 : n.Value / f.Value / b.Value;
    }
}
