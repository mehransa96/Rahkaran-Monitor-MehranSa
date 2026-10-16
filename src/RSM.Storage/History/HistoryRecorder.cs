using RSM.Core.Live;
using RSM.Core.Model;

namespace RSM.Storage.History;

/// <summary>
/// Feeds the live view's samples into <see cref="HistoryStore"/>: buffers them per node, flushes every
/// <c>flushInterval</c> (default 30 s) and runs roll-up + retention every <c>maintainInterval</c> (default 10 min).
/// Write failures are logged by the caller's handler and never stop the live view.
/// </summary>
public sealed class HistoryRecorder(HistoryStore store, TimeProvider? time = null)
{
    private readonly HistoryStore _store = store;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaintainInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Raised when a flush or maintenance pass fails (disk full, access denied); recording continues.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Starts recording one live session; dispose the result to flush the rest and stop.</summary>
    public IAsyncDisposable Attach(string siteId, LiveSession session) => new Recording(this, siteId, session);

    private sealed class Recording : IAsyncDisposable
    {
        private readonly HistoryRecorder _owner;
        private readonly string _siteId;
        private readonly LiveSession _session;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private Dictionary<string, List<MetricSample>> _pending = new(StringComparer.Ordinal);

        public Recording(HistoryRecorder owner, string siteId, LiveSession session)
        {
            _owner = owner;
            _siteId = siteId;
            _session = session;
            session.Updated += OnUpdated;
            _loop = Task.Run(() => LoopAsync(_stop.Token));
        }

        private void OnUpdated(string nodeId, CollectorResult result)
        {
            if (result.Samples.Count == 0)
            {
                return;
            }

            lock (_gate)
            {
                if (!_pending.TryGetValue(nodeId, out var list))
                {
                    _pending[nodeId] = list = [];
                }

                list.AddRange(result.Samples);
            }
        }

        private async Task LoopAsync(CancellationToken cancellationToken)
        {
            var lastMaintain = _owner._time.GetUtcNow();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_owner.FlushInterval, _owner._time, cancellationToken).ConfigureAwait(false);
                    await FlushAsync(cancellationToken).ConfigureAwait(false);
                    var now = _owner._time.GetUtcNow();
                    if (now - lastMaintain >= _owner.MaintainInterval)
                    {
                        lastMaintain = now;
                        await GuardAsync(() => _owner._store.MaintainAsync(now, cancellationToken)).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
        }

        private async Task FlushAsync(CancellationToken cancellationToken)
        {
            Dictionary<string, List<MetricSample>> batch;
            lock (_gate)
            {
                batch = _pending;
                _pending = new(StringComparer.Ordinal);
            }

            foreach (var (node, samples) in batch)
            {
                await GuardAsync(() => _owner._store.AppendAsync(_siteId, node, samples, cancellationToken)).ConfigureAwait(false);
            }
        }

        private async Task GuardAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _owner.Failed?.Invoke(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _session.Updated -= OnUpdated;
            await _stop.CancelAsync().ConfigureAwait(false);
            await _loop.ConfigureAwait(false);
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}
