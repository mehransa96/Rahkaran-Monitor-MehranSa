using RSM.Core.Model;

namespace RSM.Collectors.Windows;

/// <summary>Per-node state of <see cref="WindowsPerfCollector"/>: machine constants and short value histories.</summary>
public sealed class WindowsPerfState
{
    public const int HistorySize = 40;

    internal object Gate { get; } = new();

    public double? LogicalCpus { get; internal set; }

    public double? RamMb { get; internal set; }

    /// <summary>Recent values per "key|instance" (oldest first).</summary>
    public Dictionary<string, List<(DateTimeOffset At, double Value)>> History { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// §9.4 live Windows counters (CPU, throttling, memory, disks, network, TCP) every 15 s, through PDH locally or CIM
/// remotely — same metric keys either way. Adds "sustained" facts (e.g. available memory over the last 60 s) and
/// per-volume latency facts keyed like SQL Server's volume mount points (<c>volume[D:\]</c>).
/// </summary>
public sealed class WindowsPerfCollector : WindowsCollectorBase<WindowsPerfState>
{
    public static readonly TimeSpan SustainWindow = TimeSpan.FromSeconds(60);

    public override string Id => "win.perf";

    public override TimeSpan DefaultInterval => TimeSpan.FromSeconds(15);

    protected override async Task CollectCoreAsync(WindowsNodeTransport transport, WindowsPerfState state, LiveOutput output, List<string> problems, CancellationToken cancellationToken)
    {
        if (state.LogicalCpus is null || state.RamMb is null)
        {
            var cs = (await TryQueryAsync(transport.Cim, new CimQuery("Win32_ComputerSystem", ["NumberOfLogicalProcessors"]), problems, cancellationToken).ConfigureAwait(false))?.FirstOrDefault();
            var os = (await TryQueryAsync(transport.Cim, new CimQuery("Win32_OperatingSystem", ["TotalVisibleMemorySize"]), problems, cancellationToken).ConfigureAwait(false))?.FirstOrDefault();
            lock (state.Gate)
            {
                state.LogicalCpus ??= cs?.Number("NumberOfLogicalProcessors");
                state.RamMb ??= os?.Number("TotalVisibleMemorySize") / 1024;
            }
        }

        IReadOnlyList<MetricReading> readings;
        try
        {
            readings = await transport.Counters.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnauthorizedAccessException)
        {
            problems.Add($"{transport.Counters.Transport} counters: {ex.GetType().Name}");
            return;
        }

        output.Fact("win.perf.transport", transport.Counters.Transport);
        Apply(state, readings, Clock.GetUtcNow(), output);
    }

    /// <summary>Pure analysis of one set of readings (unit-tested).</summary>
    internal static void Apply(WindowsPerfState state, IReadOnlyList<MetricReading> readings, DateTimeOffset at, LiveOutput output)
    {
        double? Get(string key, string? instance = null) =>
            readings.FirstOrDefault(r => r.Key == key && string.Equals(r.Instance, instance, StringComparison.OrdinalIgnoreCase))?.Value;

        foreach (var r in readings)
        {
            var instance = r.Instance is not null && r.Key.StartsWith("win.disk.", StringComparison.Ordinal) ? WindowsMetricCatalog.VolumeKey(r.Instance) : r.Instance;
            output.Sample(r.Key, instance, r.Value, at);
            if (r.Instance is null)
            {
                output.Fact(r.Key, r.Value);
            }
        }

        lock (state.Gate)
        {
            var cpus = state.LogicalCpus;
            var ram = state.RamMb;
            output.Fact("os.logical_cpus", cpus);

            // CPU, queue, context switches
            var cpu = Get("win.cpu.total_pct");
            var perf = Get("win.cpu.perf_pct");
            if (Get("win.cpu.queue_length") is { } queue && cpus is > 0)
            {
                Push(state, "queue_per_cpu", queue / cpus.Value, at);
                output.Fact("win.cpu.queue_per_cpu", queue / cpus.Value);
                output.Fact("win.cpu.queue_per_cpu_min60", Sustained(state, "queue_per_cpu", at, min: true));
            }

            if (Get("win.cpu.context_switches_sec") is { } cs && cpus is > 0)
            {
                Push(state, "cs_per_core", cs / cpus.Value, at);
                output.Fact("win.cpu.context_switches_per_core", cs / cpus.Value);
                output.Fact("win.cpu.context_switches_per_core_min60", Sustained(state, "cs_per_core", at, min: true));
            }

            if (cpu is { } c && perf is { } p)
            {
                // Throttled sample: busy (> 20 %) while running below nominal frequency (general guidance).
                Push(state, "throttled", c > 20 && p < 95 ? 1 : 0, at);
                output.Fact("win.cpu.throttled_samples", TrailingCount(state, "throttled"));
            }

            // Memory
            if (Get("win.mem.available_mb") is { } avail)
            {
                Push(state, "avail_mb", avail, at);
                output.Fact("win.mem.available_mb_max60", Sustained(state, "avail_mb", at, min: false));
                if (ram is > 0)
                {
                    var pct = 100 * avail / ram.Value;
                    Push(state, "avail_pct", pct, at);
                    output.Fact("win.mem.available_pct", pct);
                    output.Fact("win.mem.available_pct_max60", Sustained(state, "avail_pct", at, min: false));
                    output.Sample("win.mem.available_pct", null, pct, at);
                }
            }

            // Volumes: latency facts keyed like SQL Server's mount points, sustained over the last 3 samples.
            foreach (var disk in readings.Where(r => r.Instance is not null && r.Key is "win.disk.read_ms" or "win.disk.write_ms" or "win.disk.queue_length"))
            {
                var volume = WindowsMetricCatalog.VolumeKey(disk.Instance!);
                var name = disk.Key["win.disk.".Length..];
                Push(state, $"{disk.Key}|{volume}", disk.Value, at);
                output.Fact($"volume[{volume}].{name}", disk.Value);
                output.Fact($"volume[{volume}].{name}_min3", MinOfLast(state, $"{disk.Key}|{volume}", 3));
            }

            // Network: utilisation vs link speed, queue, error counters increasing.
            foreach (var nic in readings.Where(r => r.Key == "win.net.bytes_total_sec" && r.Instance is not null))
            {
                var key = $"net[{nic.Instance}]";
                if (Get("win.net.bandwidth_bps", nic.Instance) is > 0 and var bandwidth)
                {
                    var util = 100 * nic.Value * 8 / bandwidth;
                    Push(state, $"util|{nic.Instance}", util, at);
                    output.Fact($"{key}.util_pct", util);
                    output.Fact($"{key}.util_pct_min60", Sustained(state, $"util|{nic.Instance}", at, min: true));
                    output.Sample("win.net.util_pct", nic.Instance, util, at);
                }

                output.Fact($"{key}.output_queue", Get("win.net.output_queue", nic.Instance));
                var errors = (Get("win.net.errors_received", nic.Instance) ?? 0) + (Get("win.net.errors_outbound", nic.Instance) ?? 0) + (Get("win.net.discards_received", nic.Instance) ?? 0);
                var previous = Last(state, $"errors|{nic.Instance}");
                Push(state, $"errors|{nic.Instance}", errors, at);
                if (previous is { } prev)
                {
                    output.Fact($"{key}.errors_increasing", errors > prev);
                }
            }
        }
    }

    private static void Push(WindowsPerfState state, string key, double value, DateTimeOffset at)
    {
        if (!state.History.TryGetValue(key, out var list))
        {
            state.History[key] = list = [];
        }

        Window.Push(list, (at, value), WindowsPerfState.HistorySize);
    }

    private static double? Last(WindowsPerfState state, string key) =>
        state.History.TryGetValue(key, out var list) && list.Count > 0 ? list[^1].Value : null;

    private static double? MinOfLast(WindowsPerfState state, string key, int count) =>
        state.History.TryGetValue(key, out var list) ? Window.MinOfLast(list.Select(v => v.Value).ToList(), count) : null;

    private static int TrailingCount(WindowsPerfState state, string key)
    {
        if (!state.History.TryGetValue(key, out var list))
        {
            return 0;
        }

        var n = 0;
        for (var i = list.Count - 1; i >= 0 && list[i].Value > 0; i--)
        {
            n++;
        }

        return n;
    }

    /// <summary>
    /// Min (or max) over the last <see cref="SustainWindow"/>, or null until the samples cover at least 75 % of it
    /// ("available memory below X for 60 s" ⇔ the max over 60 s is below X).
    /// </summary>
    private static double? Sustained(WindowsPerfState state, string key, DateTimeOffset at, bool min)
    {
        if (!state.History.TryGetValue(key, out var list))
        {
            return null;
        }

        var window = list.Where(v => v.At >= at - SustainWindow - TimeSpan.FromSeconds(5)).ToList();
        if (window.Count < 2 || at - window[0].At < SustainWindow * 0.75)
        {
            return null;
        }

        return min ? window.Min(v => v.Value) : window.Max(v => v.Value);
    }
}
