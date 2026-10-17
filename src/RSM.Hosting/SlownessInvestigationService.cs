using System.Globalization;
using Microsoft.Extensions.Logging;
using RSM.Core.Live;
using RSM.Core.Model;
using RSM.Core.Slowness;
using RSM.Storage.Investigations;

namespace RSM.Hosting;

/// <summary>Loads the embedded <c>rules/slowness</c> definitions (validated; a failure is a build defect caught by tests).</summary>
public static class SlownessDefinitionSource
{
    private static readonly Lazy<SlownessDefinitions> Lazy = new(LoadEmbedded);

    /// <summary>The embedded definitions, parsed once.</summary>
    public static SlownessDefinitions Cached => Lazy.Value;

    public static SlownessDefinitions LoadEmbedded()
    {
        var assembly = typeof(SlownessDefinitionSource).Assembly;
        string Read(string name)
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith("slowness/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        return SlownessDefinitions.Parse(Read("slowness/detectors.json"), names.Where(n => n != "slowness/detectors.json").Select(n => (n, Read(n))));
    }
}

/// <summary>
/// Spec §12 "Why is Rahkaran slow?": a synchronized capture over the running live view (every collector of every node
/// at investigation cadence for <c>duration</c>, default 120 s), then the decision engine, then the result is saved.
/// Read-only like everything else: it only reads what the live collectors already read, more often.
/// </summary>
public sealed class SlownessInvestigationService(LiveMonitorService live, InvestigationStore store, ILogger<SlownessInvestigationService> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    public SlownessDefinitions Definitions => SlownessDefinitionSource.Cached;

    /// <summary>Captures and analyses; requires the live view to be running for the site. Progress reports 0..1.</summary>
    public async Task<SavedInvestigation> RunAsync(TimeSpan duration, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (live.Current is not { } session || live.Site is not { } site)
        {
            throw new InvalidOperationException("Start the live view for the site before investigating.");
        }

        var capture = await CaptureAsync(session, site.Id, duration, progress, _time, cancellationToken).ConfigureAwait(false);
        var result = SlownessEngine.Analyze(capture, Definitions);
        var saved = new SavedInvestigation(capture, result, site.Name);
        await store.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Slowness investigation {Id}: {Count} causes, top {Top}", capture.CaptureId, result.Causes.Count, result.Causes.FirstOrDefault()?.Cause.Id);
        return saved;
    }

    /// <summary>Records every sample of every node for <paramref name="duration"/> at investigation cadence, then restores the cadence.</summary>
    public static async Task<SlownessCapture> CaptureAsync(LiveSession session, string siteId, TimeSpan duration, IProgress<double>? progress, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var samples = session.Nodes.ToDictionary(n => n.Id, _ => new List<MetricSample>(), StringComparer.Ordinal);
        var events = new List<UnifiedEvent>();
        var gate = new object();
        void OnUpdated(string nodeId, CollectorResult result)
        {
            lock (gate)
            {
                if (samples.TryGetValue(nodeId, out var list))
                {
                    list.AddRange(result.Samples);
                }

                events.AddRange(result.Events);
            }
        }

        var from = time.GetUtcNow();
        var id = from.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var previousScale = session.Scheduler.IntervalScale;
        session.Updated += OnUpdated;
        try
        {
            session.Scheduler.IntervalScale = Math.Min(previousScale, SlownessCapture.InvestigationIntervalScale);
            await session.TickAsync(cancellationToken).ConfigureAwait(false); // every collector once, together, at t0
            // Wait against the clock, not by counting delays: a timer may fire a tick early, and the capture must cover
            // the whole requested duration.
            var step = TimeSpan.FromSeconds(1);
            var deadline = from + duration;
            for (var remaining = deadline - time.GetUtcNow(); remaining > TimeSpan.Zero; remaining = deadline - time.GetUtcNow())
            {
                await Task.Delay(remaining < step ? remaining : step, time, cancellationToken).ConfigureAwait(false);
                progress?.Report(Math.Min(1, (time.GetUtcNow() - from) / duration));
            }
        }
        finally
        {
            session.Updated -= OnUpdated;
            session.Scheduler.IntervalScale = previousScale;
        }

        var to = time.GetUtcNow();
        lock (gate)
        {
            var nodes = session.Nodes.Select(n => new CaptureNode(n.Id, n.Context.Node.Name, n.Context.Node.Roles,
                samples[n.Id].Where(s => s.AtUtc >= from.AddSeconds(-30) && s.AtUtc <= to).OrderBy(s => s.AtUtc).ToList(),
                session.Facts(n.Id), session.Findings(n.Id))).ToList();
            return new SlownessCapture(id, siteId, from, to, nodes, events.Where(e => e.AtUtc >= from.AddMinutes(-5)).OrderBy(e => e.AtUtc).ToList());
        }
    }
}
