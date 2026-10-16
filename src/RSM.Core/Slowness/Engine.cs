using System.Text.Json;
using System.Text.RegularExpressions;
using RSM.Core.Model;

namespace RSM.Core.Slowness;

/// <summary>
/// One piece of evidence: a detector fired on a node with <see cref="Strength"/> 0..1. <see cref="Metric"/> and
/// <see cref="Instance"/> point at the chart series (null for fact/finding detectors).
/// </summary>
public sealed record Signal(
    string DetectorId,
    string NodeId,
    string NodeName,
    string? Instance,
    double Strength,
    double? Value,
    DateTimeOffset? AtUtc,
    string? Metric,
    string? RuleId);

/// <summary>A fact shown next to a cause (e.g. the head blocker's statement).</summary>
public sealed record ContextValue(string NodeId, string Key, object? Value);

public sealed record RankedCause(CauseDefinition Cause, double Confidence, IReadOnlyList<Signal> Evidence, IReadOnlyList<ContextValue> Context);

/// <summary>The decision engine's output for one capture (spec §12): ranked causes plus every signal.</summary>
public sealed record InvestigationResult(
    string CaptureId,
    string SiteId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<RankedCause> Causes,
    IReadOnlyList<Signal> Signals);

public static partial class SlownessEngine
{
    /// <summary>Causes below this confidence are not listed.</summary>
    public const double MinConfidence = 0.15;

    /// <summary>Confidence is capped: a 120 s capture never proves a cause.</summary>
    public const double MaxConfidence = 0.95;

    public static InvestigationResult Analyze(SlownessCapture capture, SlownessDefinitions definitions)
    {
        var signals = capture.Nodes.SelectMany(n => definitions.Detectors.SelectMany(d => Detect(d, n))).ToList();
        var best = signals.GroupBy(s => s.DetectorId).ToDictionary(g => g.Key, g => g.MaxBy(s => s.Strength)!);
        var causes = new List<RankedCause>();
        foreach (var cause in definitions.Causes)
        {
            if (cause.Evidence.Any(e => e.Required && !best.ContainsKey(e.Detector)))
            {
                continue;
            }

            var confidence = cause.Evidence.Sum(e => e.Weight * (best.TryGetValue(e.Detector, out var s) ? s.Strength : 0)) / cause.Evidence.Sum(e => e.Weight);
            foreach (var x in cause.Excludes)
            {
                if (best.TryGetValue(x.Detector, out var s) && s.Strength > x.Above)
                {
                    confidence *= x.Factor;
                }
            }

            confidence = Math.Min(confidence, MaxConfidence);
            if (confidence < MinConfidence)
            {
                continue;
            }

            var evidence = cause.Evidence.Select(e => e.Detector).Distinct()
                .SelectMany(d => signals.Where(s => s.DetectorId == d).OrderByDescending(s => s.Strength).Take(3))
                .ToList();
            var context = capture.Nodes.SelectMany(n => cause.ContextFacts.SelectMany(k => MatchFacts(n.Facts, k).Select(f => new ContextValue(n.NodeId, f.Key, f.Value)))).ToList();
            causes.Add(new RankedCause(cause, Math.Round(confidence, 3), evidence, context));
        }

        return new InvestigationResult(capture.CaptureId, capture.SiteId, capture.FromUtc, capture.ToUtc,
            causes.OrderByDescending(c => c.Confidence).ThenBy(c => c.Cause.Id, StringComparer.Ordinal).ToList(),
            signals.OrderByDescending(s => s.Strength).ThenBy(s => s.DetectorId, StringComparer.Ordinal).ToList());
    }

    /// <summary>Signals of one detector on one node (at most one per instance, strength &gt; 0 only).</summary>
    public static IEnumerable<Signal> Detect(DetectorDefinition d, CaptureNode node)
    {
        if (d.Findings is { Count: > 0 } rules)
        {
            return node.Findings.Where(f => rules.Contains(f.RuleId, StringComparer.Ordinal))
                .GroupBy(f => (f.RuleId, f.Instance))
                .Select(g => g.First())
                .Select(f => new Signal(d.Id, node.NodeId, node.NodeName, f.Instance, f.Severity is Severity.Critical or Severity.Warning ? 1 : 0.6, null, f.LastSeenUtc, null, f.RuleId));
        }

        if (d.Fact is { } factKey)
        {
            return FromFacts(d, node, factKey);
        }

        var samples = node.Samples.Where(s => s.Key == d.Metric && (d.Instance == "*" || s.Instance == d.Instance) && double.IsFinite(s.Value)).ToList();
        if (d.ShareOf is { } of)
        {
            var totals = node.Samples.Where(s => s.Key == of.Metric && s.Instance == of.Instance && s.Value > 0).GroupBy(s => s.AtUtc).ToDictionary(g => g.Key, g => g.First().Value);
            samples = samples.Where(s => totals.ContainsKey(s.AtUtc)).Select(s => s with { Value = 100 * s.Value / totals[s.AtUtc] }).ToList();
        }

        var result = new List<Signal>();
        foreach (var series in samples.GroupBy(s => s.Instance))
        {
            var points = series.OrderBy(s => s.AtUtc).ToList();
            if (points.Count < Math.Max(1, d.MinSamples))
            {
                continue;
            }

            var value = Reduce(d.Stat, points.Select(p => p.Value).ToList());
            var worst = d.From <= d.To ? points.MaxBy(p => p.Value)! : points.MinBy(p => p.Value)!;
            var strength = Ramp(value, d.From, d.To);
            if (strength > 0)
            {
                result.Add(new Signal(d.Id, node.NodeId, node.NodeName, series.Key, strength, value, worst.AtUtc, d.Metric, null));
            }
        }

        // No samples at all (collector without samples, or snapshot input): the fact of the same key, if any.
        return samples.Count == 0 && d.ShareOf is null && d.Instance is null && d.Metric is { } metric ? FromFacts(d, node, metric) : result;
    }

    private static IEnumerable<Signal> FromFacts(DetectorDefinition d, CaptureNode node, string pattern)
    {
        foreach (var (key, raw) in MatchFacts(node.Facts, pattern))
        {
            if (Number(raw) is { } value && Ramp(value, d.From, d.To) is > 0 and var strength)
            {
                yield return new Signal(d.Id, node.NodeId, node.NodeName, InstanceOf(key), strength, value, null, null, null);
            }
        }
    }

    /// <summary>Facts whose key equals <paramref name="pattern"/>, or matches it with <c>[*]</c> standing for any instance.</summary>
    public static IEnumerable<KeyValuePair<string, object?>> MatchFacts(IReadOnlyDictionary<string, object?> facts, string pattern)
    {
        if (!pattern.Contains("[*]", StringComparison.Ordinal))
        {
            return facts.TryGetValue(pattern, out var v) && v is not null ? [new(pattern, v)] : [];
        }

        var regex = new Regex("^" + Regex.Escape(pattern).Replace(@"\[\*]", @"\[[^\]]+]", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return facts.Where(f => f.Value is not null && regex.IsMatch(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal);
    }

    private static string? InstanceOf(string key) => InstanceRegex().Match(key) is { Success: true } m ? m.Groups[1].Value : null;

    public static double? Number(object? value) => value switch
    {
        bool b => b ? 1 : 0,
        double d when double.IsFinite(d) => d,
        float f when float.IsFinite(f) => f,
        int i => i,
        long l => l,
        decimal m => (double)m,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
        JsonElement { ValueKind: JsonValueKind.True } => 1,
        JsonElement { ValueKind: JsonValueKind.False } => 0,
        _ => null,
    };

    public static double Ramp(double value, double from, double to)
    {
        var t = (value - from) / (to - from);
        return double.IsFinite(t) ? Math.Clamp(t, 0, 1) : 0;
    }

    public static double Reduce(DetectorStat stat, IReadOnlyList<double> values) => stat switch
    {
        DetectorStat.Avg => values.Average(),
        DetectorStat.P50 => History.Baselines.Percentile(values, 0.5),
        DetectorStat.P95 => History.Baselines.Percentile(values, 0.95),
        DetectorStat.Max => values.Max(),
        DetectorStat.Min => values.Min(),
        DetectorStat.Last => values[^1],
        DetectorStat.DropPct => values[0] > 0 ? 100 * (values[0] - values.Min()) / values[0] : 0,
        _ => double.NaN,
    };

    [GeneratedRegex(@"\[([^\]]+)\]")]
    private static partial Regex InstanceRegex();
}
