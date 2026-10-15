using System.Collections.Concurrent;
using System.Diagnostics;
using RSM.Core.Model;

namespace RSM.Core.Scheduling;

/// <summary>
/// Runs collectors for nodes on their own cadence with: one in-flight run per (node, collector), a per-node
/// concurrency cap, adaptive back-off and cancellation. Results are published through <see cref="ResultProduced"/>.
/// The live view (P2) adds pause and an interval scale on top.
/// </summary>
public sealed class CollectorScheduler : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _nodeGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Node, string Collector), int> _inFlight = new();
    private readonly ConcurrentDictionary<(string Node, string Collector), BackoffPolicy> _policies = new();
    private readonly List<Task> _loops = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly int _maxConcurrentPerNode;
    private readonly TimeProvider _time;

    public CollectorScheduler(int maxConcurrentPerNode = 2, TimeProvider? timeProvider = null)
    {
        _maxConcurrentPerNode = maxConcurrentPerNode;
        _time = timeProvider ?? TimeProvider.System;
    }

    public event Action<CollectorResult>? ResultProduced;

    /// <summary>While true, loops keep their schedule but do not query anything (window minimised, user pause).</summary>
    public bool Paused { get; set; }

    /// <summary>Multiplies every interval (interval selector: 1 = default, 2 = half the load, …). Minimum 0.25.</summary>
    public double IntervalScale
    {
        get => _intervalScale;
        set => _intervalScale = Math.Max(0.25, value);
    }

    private double _intervalScale = 1;

    /// <summary>Collectors currently slowed down by adaptive back-off, with their multiplier.</summary>
    public IReadOnlyDictionary<(string Node, string Collector), int> BackedOff =>
        _policies.Where(p => p.Value.IsBackedOff).ToDictionary(p => p.Key, p => p.Value.Multiplier);

    /// <summary>Number of ticks skipped because the previous run of the same pair was still in flight.</summary>
    public long SkippedTicks => Interlocked.Read(ref _skippedTicks);

    private long _skippedTicks;

    /// <summary>Runs one collector once, honouring single-flight and the node gate. Returns null if skipped.</summary>
    public async Task<CollectorResult?> RunOnceAsync(INodeContext node, ICollector collector, CollectContext context, CancellationToken cancellationToken)
    {
        var key = (node.Node.Id, collector.Id);
        if (!_inFlight.TryAdd(key, 0))
        {
            Interlocked.Increment(ref _skippedTicks);
            return null;
        }

        var gate = _nodeGates.GetOrAdd(node.Node.Id, _ => new SemaphoreSlim(_maxConcurrentPerNode, _maxConcurrentPerNode));
        var started = Stopwatch.GetTimestamp();
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await collector.CollectAsync(node, context, cancellationToken).ConfigureAwait(false);
                ResultProduced?.Invoke(result);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A collector must never take the scheduler down; surface the failure as a result.
                var failed = CollectorResult.FromStatus(collector.Id, node.Node.Id, CollectorStatus.Error(ex.GetType().Name, ex.Message), Stopwatch.GetElapsedTime(started));
                ResultProduced?.Invoke(failed);
                return failed;
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>Starts a periodic loop for one (node, collector) pair until the scheduler is disposed.</summary>
    public void Start(INodeContext node, ICollector collector, TimeSpan? interval = null, TimeSpan? budget = null)
    {
        var baseInterval = interval ?? collector.DefaultInterval;
        var policy = _policies.GetOrAdd((node.Node.Id, collector.Id), _ => new BackoffPolicy(baseInterval, budget ?? TimeSpan.FromTicks(baseInterval.Ticks / 4)));
        var token = _stop.Token;
        _loops.Add(Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                TimeSpan next;
                if (Paused)
                {
                    next = TimeSpan.FromSeconds(1);
                }
                else
                {
                    var result = await RunOnceAsync(node, collector, new CollectContext(), token).ConfigureAwait(false);
                    next = (result is null ? policy.CurrentInterval : policy.Record(result.Duration)) * IntervalScale;
                }

                await Task.Delay(next, _time, token).ConfigureAwait(false);
            }
        }, token));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }

        _stop.Dispose();
        foreach (var gate in _nodeGates.Values)
        {
            gate.Dispose();
        }
    }
}
