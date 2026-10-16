using System.Text.Json;
using System.Text.Json.Serialization;
using RSM.Core.Model;

namespace RSM.Core.Slowness;

/// <summary>Statistic a detector takes over the capture window.</summary>
public enum DetectorStat
{
    Avg,
    P50,
    P95,
    Max,
    Min,
    Last,

    /// <summary>Percent drop from the first value to the lowest one (PLE drop).</summary>
    DropPct,
}

/// <summary>Denominator of a share detector: value = 100 × metric ÷ (this metric at the same instant).</summary>
public sealed record DetectorShareOf(string Metric, string? Instance);

/// <summary>
/// One evidence detector of <c>rules/slowness/detectors.json</c>. Exactly one input:
/// <list type="bullet">
/// <item><see cref="Metric"/>: capture samples (<see cref="Instance"/> null = no instance, "*" = worst instance), reduced
/// by <see cref="Stat"/>, optionally as a share of <see cref="ShareOf"/>; falls back to the fact of the same key;</item>
/// <item><see cref="Fact"/>: a fact (pattern with <c>[*]</c> = worst instance); booleans count as 0/1;</item>
/// <item><see cref="Findings"/>: rule ids active during the capture (critical/warning = 1, info = 0.6).</item>
/// </list>
/// Strength ramps linearly from 0 at <see cref="From"/> to 1 at <see cref="To"/> (From &gt; To = lower is worse).
/// </summary>
public sealed record DetectorDefinition(
    string Id,
    LocalizedText Label,
    string? Metric,
    string? Instance,
    DetectorStat Stat,
    DetectorShareOf? ShareOf,
    string? Fact,
    IReadOnlyList<string>? Findings,
    double From,
    double To,
    string? Unit,
    int MinSamples,
    SourceRef Source);

/// <summary>A detector's contribution to a cause.</summary>
public sealed record CauseEvidence(string Detector, double Weight, bool Required = false);

/// <summary>Lowers a cause when another detector is strong (e.g. "client/network" when the app tier is busy).</summary>
public sealed record CauseExclusion(string Detector, double Above, double Factor);

/// <summary>One cause of <c>rules/slowness/SLOW-*.json</c>: weighted evidence, exclusions and text-only next steps.</summary>
public sealed record CauseDefinition(
    string Id,
    Layer Layer,
    LocalizedText Title,
    LocalizedText Explanation,
    IReadOnlyList<LocalizedText> NextSteps,
    IReadOnlyList<CauseEvidence> Evidence,
    IReadOnlyList<CauseExclusion> Excludes,
    IReadOnlyList<string> ContextFacts,
    SourceRef Source);

public sealed record SlownessDefinitions(IReadOnlyList<DetectorDefinition> Detectors, IReadOnlyList<CauseDefinition> Causes)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false,
    };

    /// <summary>
    /// Parses <c>detectors.json</c> and the cause files, then validates them: unique ids, one input per detector,
    /// From ≠ To, every cause references known detectors with positive weights, every text has fa and en, and every
    /// next step is text (there is nothing executable in a cause).
    /// </summary>
    public static SlownessDefinitions Parse(string detectorsJson, IEnumerable<(string Name, string Json)> causeFiles)
    {
        var detectors = JsonSerializer.Deserialize<List<DetectorDefinition>>(detectorsJson, Json) ?? throw new InvalidDataException("detectors.json is empty.");
        var causes = causeFiles.Select(f => JsonSerializer.Deserialize<CauseDefinition>(f.Json, Json) ?? throw new InvalidDataException($"{f.Name} is empty.")).ToList();
        var errors = Validate(detectors, causes);
        return errors.Count == 0 ? new SlownessDefinitions(detectors, causes) : throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }

    public static IReadOnlyList<string> Validate(IReadOnlyList<DetectorDefinition> detectors, IReadOnlyList<CauseDefinition> causes)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in detectors)
        {
            if (!ids.Add(d.Id))
            {
                errors.Add($"Duplicate detector {d.Id}.");
            }

            var inputs = (d.Metric is null ? 0 : 1) + (d.Fact is null ? 0 : 1) + (d.Findings is { Count: > 0 } ? 1 : 0);
            if (inputs != 1)
            {
                errors.Add($"Detector {d.Id} needs exactly one of metric, fact, findings.");
            }

            if (d.Findings is not { Count: > 0 } && d.From.Equals(d.To))
            {
                errors.Add($"Detector {d.Id}: from and to must differ.");
            }

            errors.AddRange(Texts(d.Id, d.Label));
            if (d.Source is null || string.IsNullOrWhiteSpace(d.Source.Citation))
            {
                errors.Add($"Detector {d.Id} has no source.");
            }
        }

        var causeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in causes)
        {
            if (!causeIds.Add(c.Id) || !c.Id.StartsWith("SLOW-", StringComparison.Ordinal))
            {
                errors.Add($"Cause id {c.Id} is duplicated or does not start with SLOW-.");
            }

            errors.AddRange(Texts(c.Id, c.Title).Concat(Texts(c.Id, c.Explanation)).Concat(c.NextSteps.SelectMany(s => Texts(c.Id, s))));
            if (c.Evidence.Count == 0 || c.Evidence.Any(e => !ids.Contains(e.Detector) || e.Weight <= 0))
            {
                errors.Add($"Cause {c.Id}: evidence must reference known detectors with weight > 0.");
            }

            if (c.Excludes.Any(e => !ids.Contains(e.Detector) || e.Factor is < 0 or >= 1))
            {
                errors.Add($"Cause {c.Id}: exclusions must reference known detectors with 0 ≤ factor < 1.");
            }

            if (c.NextSteps.Count == 0)
            {
                errors.Add($"Cause {c.Id} has no next steps.");
            }

            if (c.Source is null || string.IsNullOrWhiteSpace(c.Source.Citation) || (c.Source.Kind == SourceKind.General && !c.Source.Citation.StartsWith("General guidance", StringComparison.Ordinal)))
            {
                errors.Add($"Cause {c.Id}: source missing, or a General source whose citation does not start with \"General guidance\".");
            }
        }

        return errors;

        static IEnumerable<string> Texts(string id, LocalizedText? text)
        {
            if (text is null || string.IsNullOrWhiteSpace(text.Fa) || string.IsNullOrWhiteSpace(text.En))
            {
                yield return $"{id}: every text needs fa and en.";
            }
        }
    }
}
