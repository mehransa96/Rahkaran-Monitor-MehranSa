using RSM.Collectors.Windows;
using RSM.Core.Model;

namespace RSM.Collectors.Iis;

/// <summary>A worker process with its pool, CPU-independent facts and start time (uptime → recent recycle).</summary>
public sealed record WorkerProcessRow(int ProcessId, string AppPool, string? State, DateTimeOffset? StartedUtc);

public sealed record IisRuntimePayload(IReadOnlyList<PoolRuntime> Pools, IReadOnlyList<SiteRuntime> Sites, IReadOnlyList<WorkerProcessRow> WorkerProcesses,
    IReadOnlyList<LiveRequest> Requests, string? RequestsUnavailable);

/// <summary>
/// §10.3: pool and site state, worker processes (PID, start time) and the requests executing right now
/// (<c>WorkerProcess.GetRequests</c>: URL, verb, client, elapsed time, pipeline stage, module). Local only, needs RSM
/// elevated; live requests need the Request Monitor feature. Remote nodes get pool state from the WAS counters.
/// </summary>
public sealed class IisRuntimeCollector : IisCollectorBase<NoIisState>
{
    /// <summary>Requests shown in the live view run for at least this long (the "slow right now" list).</summary>
    public const int MinElapsedMs = 1000;

    public override string Id => "iis.runtime";

    public override TimeSpan DefaultInterval => TimeSpan.FromSeconds(15);

    protected override string Capability => "iis.requests";

    private static readonly CimQuery W3wpProcesses = new("Win32_Process", ["ProcessId", "CreationDate"], "Name = 'w3wp.exe'");

    protected override async Task CollectCoreAsync(INodeContext node, IisNodeTransport transport, NoIisState state, CollectContext context, LiveOutput output, List<string> problems, CancellationToken cancellationToken)
    {
        if (transport.Runtime is not { } runtime)
        {
            problems.Add(transport.IsLocal ? "IIS runtime needs RSM to run as Administrator" : "IIS runtime and live requests are local only (pool state comes from the WAS counters)");
            return;
        }

        IReadOnlyList<PoolRuntime> pools;
        IReadOnlyList<SiteRuntime> sites;
        IReadOnlyList<WorkerProcessInfo> workers;
        try
        {
            (pools, sites, workers) = await Task.Run(() => (runtime.Pools(), runtime.Sites(), runtime.WorkerProcesses()), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or FileNotFoundException)
        {
            problems.Add($"IIS runtime: {ex.GetType().Name}");
            return;
        }

        IReadOnlyList<LiveRequest> requests = [];
        string? requestsUnavailable = null;
        try
        {
            requests = await Task.Run(() => runtime.Requests(MinElapsedMs), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NotImplementedException or UnauthorizedAccessException)
        {
            requestsUnavailable = "IIS Request Monitor feature not installed or not accessible";
            problems.Add(requestsUnavailable);
        }

        var started = new Dictionary<int, DateTimeOffset>();
        if (node.GetTransport<WindowsNodeTransport>() is { } windows)
        {
            try
            {
                foreach (var row in await windows.Cim.QueryAsync(W3wpProcesses, cancellationToken).ConfigureAwait(false))
                {
                    if (row.Number("ProcessId") is { } pid && row.Time("CreationDate") is { } at)
                    {
                        started[(int)pid] = at;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
            {
                problems.Add($"w3wp start times: {ex.GetType().Name}");
            }
        }

        Apply(pools, sites, workers.Select(w => new WorkerProcessRow(w.ProcessId, w.AppPool, w.State, started.TryGetValue(w.ProcessId, out var at) ? at : null)).ToList(),
            requests, requestsUnavailable, Clock.GetUtcNow(), output);
    }

    internal static void Apply(IReadOnlyList<PoolRuntime> pools, IReadOnlyList<SiteRuntime> sites, IReadOnlyList<WorkerProcessRow> workers,
        IReadOnlyList<LiveRequest> requests, string? requestsUnavailable, DateTimeOffset now, LiveOutput output)
    {
        output.Payload["runtime"] = new IisRuntimePayload(pools, sites, workers, requests.OrderByDescending(r => r.TimeElapsedMs).ToList(), requestsUnavailable);
        foreach (var pool in pools)
        {
            var key = $"pool[{IisConfigCollector.Instance(pool.Name)}]";
            output.Fact($"{key}.state", pool.State);
            if (pool.State is not null)
            {
                output.Fact($"{key}.running", string.Equals(pool.State, "Started", StringComparison.OrdinalIgnoreCase));
            }

            var mine = workers.Where(w => string.Equals(w.AppPool, pool.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            output.Fact($"{key}.wp_count", mine.Count);
            if (mine.Where(w => w.StartedUtc is not null).Select(w => (now - w.StartedUtc!.Value).TotalSeconds).DefaultIfEmpty(-1).Min() is var youngest and >= 0)
            {
                output.Fact($"{key}.wp_youngest_age_s", youngest);
                output.Fact($"{key}.wp_youngest_started_utc", mine.Max(w => w.StartedUtc)!.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        foreach (var site in sites)
        {
            output.Fact($"site[{IisConfigCollector.Instance(site.Name)}].state", site.State);
        }

        if (requestsUnavailable is null)
        {
            output.Fact("iis.requests.executing", requests.Count);
            output.Fact("iis.requests.max_elapsed_s", requests.Count == 0 ? 0 : requests.Max(r => r.TimeElapsedMs) / 1000.0);
            output.Fact("iis.requests.over30s", requests.Count(r => r.TimeElapsedMs >= 30_000));
            output.Fact("iis.requests.slowest_url", requests.MaxBy(r => r.TimeElapsedMs)?.Url ?? string.Empty);
            output.Sample("iis.requests.executing", null, requests.Count, now);
        }
    }
}
