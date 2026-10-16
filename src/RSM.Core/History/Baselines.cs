namespace RSM.Core.History;

/// <summary>Median and p95 of one hour-of-week slot; <see cref="N"/> = values it was built from.</summary>
public readonly record struct BaselineSlot(double Median, double P95, int N);

/// <summary>
/// Spec §15 baseline: per metric, per hour-of-week (local time, Saturday 00:00 = slot 0 so slots read in Iranian-week
/// order), median and p95 over the last N weeks. Built from 15-minute averages (the only tier kept that long), so
/// short spikes are smoothed — which is what the "≥ 3 consecutive samples" anomaly rule expects.
/// </summary>
public sealed record SeriesBaseline(string Key, string? Instance, IReadOnlyList<BaselineSlot?> Slots)
{
    public const int SlotCount = 168;

    /// <summary>Fewer values than this in a slot ⇒ no baseline for it (a slot gets 4 values per hour per week).</summary>
    public const int MinSamples = 8;

    public BaselineSlot? At(DateTimeOffset utc, TimeZoneInfo zone) => Slots[Baselines.HourOfWeek(utc, zone)];
}

public static class Baselines
{
    /// <summary>0..167 in <paramref name="zone"/> local time; Saturday 00:00–00:59 = 0, Friday 23:00 = 167.</summary>
    public static int HourOfWeek(DateTimeOffset utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        return ((int)(local.DayOfWeek + 1) % 7 * 24) + local.Hour;
    }

    /// <summary>Builds the 168 slots from (time, value) pairs; slots with fewer than <see cref="SeriesBaseline.MinSamples"/> values stay null.</summary>
    public static SeriesBaseline Build(string key, string? instance, IEnumerable<(DateTimeOffset AtUtc, double Value)> values, TimeZoneInfo zone)
    {
        var buckets = new List<double>?[SeriesBaseline.SlotCount];
        foreach (var (at, value) in values)
        {
            if (!double.IsFinite(value))
            {
                continue;
            }

            (buckets[HourOfWeek(at, zone)] ??= []).Add(value);
        }

        return new SeriesBaseline(key, instance, buckets
            .Select(b => b is { Count: >= SeriesBaseline.MinSamples } ? new BaselineSlot(Percentile(b, 0.5), Percentile(b, 0.95), b.Count) : (BaselineSlot?)null)
            .ToList());
    }

    /// <summary>Linear-interpolated percentile (same method as the SSRS/IIS percentiles elsewhere in RSM).</summary>
    public static double Percentile(IReadOnlyCollection<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        var rank = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo));
    }
}

/// <summary>
/// Anomaly rule (spec §15, <c>general</c> guidance — confirm with ستاد فنی): value &gt; p95 × <see cref="Factor"/> for at
/// least <see cref="Consecutive"/> consecutive samples. Slots without a baseline never flag.
/// </summary>
public sealed record AnomalyOptions(double Factor = 1.5, int Consecutive = 3)
{
    public static AnomalyOptions Default { get; } = new();

    public const string Source = "general";
}

/// <summary>One flagged run: first and last sample above the limit, the peak and the limit at the peak.</summary>
public sealed record Anomaly(string Key, string? Instance, DateTimeOffset FromUtc, DateTimeOffset ToUtc, double Peak, double Limit, int Samples);

public static class AnomalyDetector
{
    /// <summary>Flags runs in an ordered series (history replay or live points).</summary>
    public static IReadOnlyList<Anomaly> Detect(SeriesBaseline baseline, IEnumerable<(DateTimeOffset AtUtc, double Value)> points, TimeZoneInfo zone, AnomalyOptions? options = null)
    {
        var tracker = new AnomalyTracker(baseline, zone, options);
        var result = new List<Anomaly>();
        foreach (var (at, value) in points.OrderBy(p => p.AtUtc))
        {
            if (tracker.Add(at, value) is { } closed)
            {
                result.Add(closed);
            }
        }

        if (tracker.Current is { } open)
        {
            result.Add(open);
        }

        return result;
    }
}

/// <summary>
/// Incremental form for the live view: feed points in time order; <see cref="Current"/> is the run in progress once it
/// has reached the consecutive count, and <see cref="Add"/> returns a run when it ends.
/// </summary>
public sealed class AnomalyTracker(SeriesBaseline baseline, TimeZoneInfo zone, AnomalyOptions? options = null)
{
    private readonly AnomalyOptions _options = options ?? AnomalyOptions.Default;
    private readonly List<(DateTimeOffset At, double Value, double Limit)> _run = [];

    public SeriesBaseline Baseline => baseline;

    public Anomaly? Current => _run.Count >= _options.Consecutive ? ToAnomaly() : null;

    public Anomaly? Add(DateTimeOffset atUtc, double value)
    {
        if (!double.IsFinite(value))
        {
            return null;
        }

        var limit = baseline.At(atUtc, zone) is { } slot ? slot.P95 * _options.Factor : double.NaN;
        if (double.IsFinite(limit) && value > limit)
        {
            _run.Add((atUtc, value, limit));
            return null;
        }

        var ended = Current;
        _run.Clear();
        return ended;
    }

    private Anomaly ToAnomaly()
    {
        var peak = _run.MaxBy(r => r.Value);
        return new Anomaly(baseline.Key, baseline.Instance, _run[0].At, _run[^1].At, peak.Value, peak.Limit, _run.Count);
    }
}
