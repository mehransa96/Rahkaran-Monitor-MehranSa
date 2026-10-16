using System.Collections.Concurrent;
using System.Globalization;
using RSM.Collectors.Iis;
using RSM.Collectors.Windows;
using RSM.Core.Model;

namespace RSM.Collectors.Rahkaran;

/// <summary>A local .NET process of the Rahkaran tier: w3wp (in-process hosting) or a dotnet child of w3wp (out-of-process).</summary>
public sealed record DotNetProcess(int ProcessId, string Name, string? AppPool, string Hosting)
{
    public string Label => AppPool is null ? $"{Name} {ProcessId}" : AppPool;
}

/// <summary>Derived values of one process at one time (null = counter not published by this runtime).</summary>
public sealed record DotNetSample(
    DateTimeOffset At,
    double? GcHeapMb,
    double? TimeInGcPct,
    double? Gen2PerMin,
    double? LohMb,
    double? AllocMbPerSec,
    double? ThreadPoolQueue,
    double? ThreadPoolThreads,
    double? ExceptionsPerSec,
    double? LockContentionPerSec,
    double? WorkingSetMb,
    double? RequestsPerSec,
    double? CurrentRequests,
    double? FailedRequestsTotal);

public sealed record DotNetProcessView(DotNetProcess Process, DotNetSample Latest, double? FailedPerSec);

public sealed record DotNetPayload(IReadOnlyList<DotNetProcessView> Processes, IReadOnlyList<string> Skipped);

public sealed class DotNetState
{
    public ConcurrentDictionary<string, List<DotNetSample>> History { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// §9.4 (optional, local only, off by default — PLAN Q8): EventPipe counters of the w3wp/dotnet processes of the
/// Rahkaran pools — GC heap, % time in GC ("GC wait"), gen2 rate, LOH, allocation rate, thread pool queue/threads,
/// exceptions, lock contention, working set, ASP.NET Core requests. Sessions carry EventCounters only (see
/// <c>RSM.Safety.DotNet.RuntimeCounterSource</c>): no dumps, no profilers, nothing written to the process.
/// </summary>
public sealed class DotNetRuntimeCollector(IRuntimeCounterProvider counters) : ILiveCollector, IRequiresTransport
{
    public const int MaxProcesses = 8;
    public static readonly TimeSpan SessionWindow = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, DotNetState> _state = new(StringComparer.Ordinal);

    public DotNetRuntimeCollector()
        : this(new EventPipeCounterProvider())
    {
    }

    public string Id => "dotnet.runtime";

    public Layer Layer => Layer.DotNet;

    public CollectorCost Cost => CollectorCost.Medium;

    public NodeRoles AppliesTo => NodeRoles.App;

    public TimeSpan DefaultInterval => TimeSpan.FromSeconds(30);

    public Type TransportType => typeof(WindowsNodeTransport);

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    private static readonly CimQuery Processes = new("Win32_Process", ["Name", "ProcessId", "ParentProcessId"], "Name = 'w3wp.exe' OR Name = 'dotnet.exe'");

    public async Task<CollectorResult> CollectAsync(INodeContext node, CollectContext context, CancellationToken cancellationToken)
    {
        var started = Clock.GetUtcNow();
        if (!node.Site.Settings.DotNetRuntimeCounters)
        {
            return CollectorResult.FromStatus(Id, node.Node.Id, CollectorStatus.Unavailable("off by default — enable \".NET runtime counters\" in the site settings", "dotnet.eventpipe"), TimeSpan.Zero);
        }

        if (node.GetTransport<WindowsNodeTransport>() is not { } windows || !windows.IsLocal)
        {
            return CollectorResult.FromStatus(Id, node.Node.Id, CollectorStatus.Unavailable("local only: run RSM on this App server (local-per-server mode)", "dotnet.eventpipe"), TimeSpan.Zero);
        }

        var output = new LiveOutput();
        IReadOnlyList<CimRow> rows;
        try
        {
            rows = await windows.Cim.QueryAsync(Processes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
        {
            return CollectorResult.FromStatus(Id, node.Node.Id, CollectorStatus.Unavailable($"process list: {ex.GetType().Name}", "win.cim"), Clock.GetUtcNow() - started);
        }

        var pools = (node.GetTransport<IisNodeTransport>()?.Runtime is { } runtime ? TryWorkerProcesses(runtime) : [])
            .GroupBy(w => w.ProcessId).ToDictionary(g => g.Key, g => g.First().AppPool);
        var targets = Discover(rows, counters.PublishedProcesses(), pools);
        var skipped = new List<string>();
        var reads = targets.Take(MaxProcesses).Select(async t =>
        {
            try
            {
                return (t, Values: await counters.ReadAsync(t.ProcessId, SessionWindow, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (skipped)
                {
                    skipped.Add($"{t.Label}: {ex.GetType().Name} (same user as the pool identity or Administrators required)");
                }

                return (t, Values: (IReadOnlyDictionary<string, RuntimeCounterValue>?)null);
            }
        }).ToList();

        var results = await Task.WhenAll(reads).ConfigureAwait(false);
        var state = _state.GetOrAdd(node.Node.Id, static _ => new DotNetState());
        Apply(state, results.Where(r => r.Values is not null).Select(r => (r.t, r.Values!)).ToList(), skipped, Clock.GetUtcNow(), output);
        var status = targets.Count == 0 ? CollectorStatus.Unavailable("no w3wp/dotnet process publishes a .NET diagnostics endpoint (pool stopped, .NET Framework, or no access)", "dotnet.eventpipe")
            : skipped.Count > 0 && skipped.Count == Math.Min(targets.Count, MaxProcesses) ? CollectorStatus.Unavailable(string.Join("; ", skipped), "dotnet.eventpipe")
            : skipped.Count > 0 ? CollectorStatus.Partial(string.Join("; ", skipped)) : CollectorStatus.Ok;
        return new CollectorResult(Id, node.Node.Id, started, Clock.GetUtcNow() - started, status, output.Payload, output.Samples, output.Facts, output.Events);
    }

    private static IReadOnlyList<WorkerProcessInfo> TryWorkerProcesses(IIisRuntime runtime)
    {
        try
        {
            return runtime.WorkerProcesses();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    /// <summary>w3wp with a diagnostics endpoint = in-process hosting; dotnet whose parent is w3wp = out-of-process.</summary>
    internal static IReadOnlyList<DotNetProcess> Discover(IReadOnlyList<CimRow> rows, IReadOnlyList<int> published, IReadOnlyDictionary<int, string> pools)
    {
        var publishedSet = published.ToHashSet();
        var all = rows.Select(r => (Name: r.String("Name") ?? string.Empty, Pid: (int)(r.Number("ProcessId") ?? 0), Parent: (int)(r.Number("ParentProcessId") ?? 0))).ToList();
        var w3wp = all.Where(p => p.Name.Equals("w3wp.exe", StringComparison.OrdinalIgnoreCase)).Select(p => p.Pid).ToHashSet();
        return all.Where(p => publishedSet.Contains(p.Pid))
            .Select(p => p.Name.Equals("w3wp.exe", StringComparison.OrdinalIgnoreCase)
                ? new DotNetProcess(p.Pid, "w3wp", pools.GetValueOrDefault(p.Pid), "in-process")
                : w3wp.Contains(p.Parent) ? new DotNetProcess(p.Pid, "dotnet", pools.GetValueOrDefault(p.Parent), "out-of-process") : null)
            .OfType<DotNetProcess>()
            .OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static DotNetSample ToSample(IReadOnlyDictionary<string, RuntimeCounterValue> values, DateTimeOffset at)
    {
        double? Gauge(string name) => values.TryGetValue("System.Runtime/" + name, out var v) ? v.Value : null;
        double? PerSecond(string provider, string name) => values.TryGetValue($"{provider}/{name}", out var v) ? v.Value / v.IntervalSeconds : null;
        double? Asp(string name) => values.TryGetValue("Microsoft.AspNetCore.Hosting/" + name, out var v) ? v.Value : null;

        return new DotNetSample(
            at,
            Gauge("gc-heap-size"),
            Gauge("time-in-gc"),
            PerSecond("System.Runtime", "gen-2-gc-count") * 60,
            Gauge("loh-size") / 1048576,
            PerSecond("System.Runtime", "alloc-rate") / 1048576,
            Gauge("threadpool-queue-length"),
            Gauge("threadpool-thread-count"),
            PerSecond("System.Runtime", "exception-count"),
            PerSecond("System.Runtime", "monitor-lock-contention-count"),
            Gauge("working-set"),
            PerSecond("Microsoft.AspNetCore.Hosting", "requests-per-second"),
            Asp("current-requests"),
            Asp("failed-requests"));
    }

    internal static void Apply(DotNetState state, IReadOnlyList<(DotNetProcess Process, IReadOnlyDictionary<string, RuntimeCounterValue> Values)> reads, IReadOnlyList<string> skipped, DateTimeOffset now, LiveOutput output)
    {
        var views = new List<DotNetProcessView>();
        var duplicates = reads.GroupBy(r => r.Process.Label, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (process, values) in reads)
        {
            var label = duplicates.Contains(process.Label) ? $"{process.Label} ({process.ProcessId})" : process.Label;
            var sample = ToSample(values, now);
            var history = state.History.GetOrAdd($"{label}#{process.ProcessId}", _ => []);
            history.Add(sample);
            history.RemoveAll(s => s.At < now - HistoryWindow);
            var previous = history.Count >= 2 ? history[^2] : null;
            var failedPerSec = previous?.FailedRequestsTotal is { } before && sample.FailedRequestsTotal is { } after && sample.At > previous.At
                ? Math.Max(0, after - before) / (sample.At - previous.At).TotalSeconds : (double?)null;
            views.Add(new DotNetProcessView(process, sample, failedPerSec));

            var key = $"dotnet[{label}]";
            output.Fact($"{key}.pid", process.ProcessId);
            output.Fact($"{key}.process", process.Name);
            output.Fact($"{key}.hosting", process.Hosting);
            output.Fact($"{key}.gc_heap_mb", sample.GcHeapMb);
            output.Fact($"{key}.time_in_gc_pct", sample.TimeInGcPct);
            output.Fact($"{key}.gen2_per_min", sample.Gen2PerMin);
            output.Fact($"{key}.loh_mb", sample.LohMb);
            output.Fact($"{key}.alloc_mb_s", sample.AllocMbPerSec);
            output.Fact($"{key}.threadpool_queue", sample.ThreadPoolQueue);
            output.Fact($"{key}.threadpool_threads", sample.ThreadPoolThreads);
            output.Fact($"{key}.exceptions_per_s", sample.ExceptionsPerSec);
            output.Fact($"{key}.lock_contention_per_s", sample.LockContentionPerSec);
            output.Fact($"{key}.working_set_mb", sample.WorkingSetMb);
            output.Fact($"{key}.requests_per_s", sample.RequestsPerSec);
            output.Fact($"{key}.current_requests", sample.CurrentRequests);
            output.Fact($"{key}.failed_per_s", failedPerSec);
            if (failedPerSec is { } f && sample.RequestsPerSec is > 0)
            {
                output.Fact($"{key}.failed_pct", 100 * f / sample.RequestsPerSec.Value);
            }

            output.Fact($"{key}.gc_time_sustained_60s", Sustained(history, now, TimeSpan.FromSeconds(60), s => s.TimeInGcPct > 20));
            output.Fact($"{key}.gen2_sustained", Sustained(history, now, TimeSpan.FromMinutes(3), s => s.Gen2PerMin > 1));
            output.Fact($"{key}.threadpool_starvation_30s", Sustained(history, now, TimeSpan.FromSeconds(30), s => s.ThreadPoolQueue > 0)
                && Rising(history, now, TimeSpan.FromSeconds(30), s => s.ThreadPoolThreads));
            var baseline = history.Take(history.Count - 1).Select(s => s.ExceptionsPerSec).OfType<double>().ToList();
            if (baseline.Count >= 10 && sample.ExceptionsPerSec is { } exceptions)
            {
                var median = baseline.Order().ElementAt(baseline.Count / 2);
                output.Fact($"{key}.exceptions_vs_baseline", median > 0 ? exceptions / median : exceptions);
            }

            output.Sample("dotnet.gc_heap_mb", label, sample.GcHeapMb, now);
            output.Sample("dotnet.time_in_gc_pct", label, sample.TimeInGcPct, now);
            output.Sample("dotnet.threadpool_queue", label, sample.ThreadPoolQueue, now);
            output.Sample("dotnet.threadpool_threads", label, sample.ThreadPoolThreads, now);
            output.Sample("dotnet.requests_per_s", label, sample.RequestsPerSec, now);
            output.Sample("dotnet.exceptions_per_s", label, sample.ExceptionsPerSec, now);
        }

        output.Fact("dotnet.processes", views.Count);
        output.Payload["dotnet"] = new DotNetPayload(views, skipped);
    }

    /// <summary>True when every sample of the last <paramref name="span"/> matches and the samples cover the span.</summary>
    internal static bool Sustained(IReadOnlyList<DotNetSample> history, DateTimeOffset now, TimeSpan span, Func<DotNetSample, bool> condition)
    {
        var window = history.Where(s => s.At >= now - span - TimeSpan.FromSeconds(5)).ToList();
        return window.Count >= 2 && now - window[0].At >= span * 0.9 && window.All(condition);
    }

    private static bool Rising(IReadOnlyList<DotNetSample> history, DateTimeOffset now, TimeSpan span, Func<DotNetSample, double?> value)
    {
        var window = history.Where(s => s.At >= now - span - TimeSpan.FromSeconds(5)).Select(value).OfType<double>().ToList();
        return window.Count >= 2 && window[^1] > window[0];
    }
}
