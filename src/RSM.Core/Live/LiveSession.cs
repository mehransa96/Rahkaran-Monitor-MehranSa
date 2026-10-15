using RSM.Core.Model;
using RSM.Core.Rules;
using RSM.Core.Scheduling;

namespace RSM.Core.Live;

/// <summary>One node in a live session: its context (with opened transports) and the collectors that can run there.</summary>
public sealed record LiveNode(INodeContext Context, IReadOnlyList<ICollector> Collectors, IReadOnlyDictionary<string, object?> StaticFacts)
{
    public string Id => Context.Node.Id;
}

/// <summary>RSM's own footprint, shown on the Overview page (P2 acceptance: &lt; 2 % server CPU, &lt; 500 MB).</summary>
public sealed record LiveStats(
    double? ClientCpuPercent,
    double ClientWorkingSetMb,
    double ManagedHeapMb,
    int Series,
    int Points,
    IReadOnlyDictionary<string, NodeLoad> Nodes,
    int BackedOffCollectors,
    long SkippedTicks,
    bool Paused,
    double IntervalScale);

/// <summary>Per node: collector runs and their time in the last minute, and RSM's measured SQL CPU share.</summary>
public sealed record NodeLoad(int RunsPerMinute, double CollectorSecondsPerMinute, double? SqlCpuPercent, double? SqlRoundTripMs);

/// <summary>
/// The live view's engine: schedules every live collector per node, keeps metric ring buffers, the latest facts per
/// node and re-evaluates the rules on them (findings keep their first-seen time while they persist).
/// </summary>
public sealed class LiveSession : IAsyncDisposable
{
    private readonly IReadOnlyList<LiveNode> _nodes;
    private readonly RulesEngine _engine;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, FactStore> _facts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<Finding>> _findings = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Node, string Rule, string? Instance), DateTimeOffset> _firstSeen = [];
    private readonly Dictionary<(string Node, string Collector), CollectorResult> _latest = [];
    private readonly Dictionary<string, List<(DateTimeOffset At, TimeSpan Duration)>> _runs = new(StringComparer.Ordinal);
    private readonly List<UnifiedEvent> _events = [];
    private readonly ClientOverheadMeter _meter;
    private readonly object _gate = new();
    private bool _started;

    public LiveSession(IReadOnlyList<LiveNode> nodes, RulesEngine engine, MetricStore? store = null, TimeProvider? time = null, int maxConcurrentPerNode = 2)
    {
        _nodes = nodes;
        _engine = engine;
        _time = time ?? TimeProvider.System;
        _meter = new ClientOverheadMeter(_time);
        Metrics = store ?? new MetricStore();
        Scheduler = new CollectorScheduler(maxConcurrentPerNode, _time);
        Scheduler.ResultProduced += OnResult;
        foreach (var node in nodes)
        {
            var facts = new FactStore();
            foreach (var (key, value) in node.StaticFacts)
            {
                facts.Set(key, value);
            }

            _facts[node.Id] = facts;
            _findings[node.Id] = [];
            _runs[node.Id] = [];
        }
    }

    public MetricStore Metrics { get; }

    /// <summary>The rules evaluated on live facts (charts read their thresholds from here, so overrides apply).</summary>
    public RulesEngine Engine => _engine;

    /// <summary>A numeric parameter of a loaded rule (e.g. SQL-IO-001 <c>warn</c>), or null when absent.</summary>
    public double? RuleParam(string ruleId, string name) =>
        _engine.Rules.FirstOrDefault(r => r.Definition.Id == ruleId)?.Definition.Params.TryGetValue(name, out var v) == true
        && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDouble() : null;

    public CollectorScheduler Scheduler { get; }

    public IReadOnlyList<LiveNode> Nodes => _nodes;

    /// <summary>Raised (on a worker thread) after a collector result was applied for the node.</summary>
    public event Action<string, CollectorResult>? Updated;

    public const int MaxEvents = 500;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        foreach (var node in _nodes)
        {
            foreach (var collector in node.Collectors)
            {
                Scheduler.Start(node.Context, collector);
            }
        }
    }

    /// <summary>Runs every collector of every node once (used for "refresh now" and by tests).</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        foreach (var node in _nodes)
        {
            await Task.WhenAll(node.Collectors.Select(c => Scheduler.RunOnceAsync(node.Context, c, new CollectContext(), cancellationToken))).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<Finding> Findings(string nodeId)
    {
        lock (_gate)
        {
            return _findings.GetValueOrDefault(nodeId) ?? [];
        }
    }

    public IReadOnlyList<Finding> AllFindings()
    {
        lock (_gate)
        {
            return _findings.Values.SelectMany(f => f).OrderByDescending(f => f.Severity).ThenBy(f => f.RuleId, StringComparer.Ordinal).ToList();
        }
    }

    public IReadOnlyDictionary<string, object?> Facts(string nodeId) =>
        _facts.TryGetValue(nodeId, out var store) ? store.Snapshot() : new Dictionary<string, object?>();

    public CollectorResult? Latest(string nodeId, string collectorId)
    {
        lock (_gate)
        {
            return _latest.GetValueOrDefault((nodeId, collectorId));
        }
    }

    public IReadOnlyList<CollectorResult> LatestResults(string nodeId)
    {
        lock (_gate)
        {
            return _latest.Where(p => p.Key.Node == nodeId).Select(p => p.Value).OrderBy(r => r.CollectorId, StringComparer.Ordinal).ToList();
        }
    }

    public IReadOnlyList<UnifiedEvent> Events()
    {
        lock (_gate)
        {
            return _events.OrderByDescending(e => e.AtUtc).ToList();
        }
    }

    public LiveStats Stats()
    {
        var now = _time.GetUtcNow();
        var nodes = new Dictionary<string, NodeLoad>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var node in _nodes)
            {
                var runs = _runs[node.Id].Where(r => r.At >= now.AddMinutes(-1)).ToList();
                var facts = _facts[node.Id];
                nodes[node.Id] = new NodeLoad(runs.Count, runs.Sum(r => r.Duration.TotalSeconds), AsDouble(facts, "rsm.sql_cpu_pct"), AsDouble(facts, "rsm.sql_rtt_ms"));
            }
        }

        var client = _meter.Sample();
        return new LiveStats(client.CpuPercent, client.WorkingSetMb, client.ManagedHeapMb, Metrics.SeriesCount, Metrics.PointCount, nodes,
            Scheduler.BackedOff.Count, Scheduler.SkippedTicks, Scheduler.Paused, Scheduler.IntervalScale);
    }

    private void OnResult(CollectorResult result)
    {
        if (!_facts.TryGetValue(result.NodeId, out var facts))
        {
            return;
        }

        Metrics.Add(result.NodeId, result.Samples);
        facts.SetAll(result.Facts);
        var node = _nodes.First(n => n.Id == result.NodeId);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            // Evaluated under the lock so a slower, older evaluation never overwrites a newer one.
            var evaluation = _engine.Evaluate(node.Id, node.Context.Node.Roles, facts, now);
            _latest[(result.NodeId, result.CollectorId)] = result;
            var runs = _runs[result.NodeId];
            runs.Add((now, result.Duration));
            runs.RemoveAll(r => r.At < now.AddMinutes(-2));

            _events.AddRange(result.Events);
            if (_events.Count > MaxEvents)
            {
                _events.RemoveRange(0, _events.Count - MaxEvents);
            }

            var current = new List<Finding>();
            var keys = new HashSet<(string, string, string?)>();
            foreach (var finding in evaluation.Findings)
            {
                var key = (node.Id, finding.RuleId, finding.Instance);
                keys.Add(key);
                if (!_firstSeen.TryGetValue(key, out var first))
                {
                    _firstSeen[key] = first = finding.FirstSeenUtc;
                }

                current.Add(finding with { FirstSeenUtc = first, LastSeenUtc = now });
            }

            // A condition that cleared and comes back later is a new occurrence.
            foreach (var gone in _firstSeen.Keys.Where(k => k.Node == node.Id && !keys.Contains(k)).ToList())
            {
                _firstSeen.Remove(gone);
            }

            _findings[node.Id] = current;
        }

        Updated?.Invoke(result.NodeId, result);
    }

    private static double? AsDouble(FactStore facts, string key) =>
        facts.TryGet(key, out var v) && v is IConvertible c ? c.ToDouble(System.Globalization.CultureInfo.InvariantCulture) : null;

    public ValueTask DisposeAsync()
    {
        Scheduler.ResultProduced -= OnResult;
        return Scheduler.DisposeAsync();
    }
}

/// <summary>RSM's own CPU and memory on the machine it runs on (no Process API: process-wide CPU time via AppDomain).</summary>
public sealed class ClientOverheadMeter(TimeProvider time)
{
    private (DateTimeOffset At, TimeSpan Cpu)? _previous;

    public (double? CpuPercent, double WorkingSetMb, double ManagedHeapMb) Sample()
    {
        TimeSpan? cpu;
        try
        {
            cpu = AppDomain.CurrentDomain.MonitoringTotalProcessorTime;
        }
        catch (Exception ex) when (ex is NotSupportedException or PlatformNotSupportedException or InvalidOperationException)
        {
            cpu = null;
        }

        var now = time.GetUtcNow();
        double? pct = null;
        if (cpu is { } c)
        {
            if (_previous is { } prev && now > prev.At)
            {
                pct = 100 * (c - prev.Cpu).TotalMilliseconds / ((now - prev.At).TotalMilliseconds * Environment.ProcessorCount);
            }

            _previous = (now, c);
        }

        return (pct, Environment.WorkingSet / 1048576d, GC.GetTotalMemory(false) / 1048576d);
    }
}
