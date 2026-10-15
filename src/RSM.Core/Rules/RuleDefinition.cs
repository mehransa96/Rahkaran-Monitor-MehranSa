using System.Text.Json;
using System.Text.Json.Serialization;
using RSM.Core.Model;
using RSM.Core.Rules.Expressions;

namespace RSM.Core.Rules;

/// <summary>JSON shape of a rule file in <c>rules/</c> (see ARCHITECTURE.md §9.1).</summary>
public sealed record RuleDefinition
{
    public required string Id { get; init; }

    public required Layer Layer { get; init; }

    /// <summary>Roles the rule applies to; empty = all nodes.</summary>
    public IReadOnlyList<NodeRoles> AppliesTo { get; init; } = [];

    /// <summary>Optional instance prefix, e.g. "db": the rule is evaluated once per <c>db[*]</c> instance.</summary>
    public string? ForEach { get; init; }

    public required string When { get; init; }

    public required Severity Severity { get; init; }

    public IReadOnlyList<RuleEscalation> Escalate { get; init; } = [];

    public required LocalizedText Title { get; init; }

    public required LocalizedText Explanation { get; init; }

    public required LocalizedText Recommendation { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];

    public required RuleSource Source { get; init; }

    public bool Verified { get; init; } = true;

    public double Confidence { get; init; } = 0.8;

    public IReadOnlyDictionary<string, JsonElement> Params { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Built-in fixtures: every rule ships at least one example that must fire (with the expected severity) and one
    /// that must stay quiet. A generic test evaluates all of them (CLAUDE.md: "new rule ⇒ … a fixture test").
    /// </summary>
    public IReadOnlyList<RuleExample> Examples { get; init; } = [];

    /// <summary>
    /// Set on rules that check a ستاد فنی / baseline setting: the report's compliance table lists one row per such
    /// rule (Compliant / Deviation / Unknown), so the expected value lives next to the threshold it describes.
    /// </summary>
    public RuleBaseline? Baseline { get; init; }
}

/// <summary>A compliance-table row: what is checked, the expected value, and the fact holding the actual value.</summary>
public sealed record RuleBaseline(LocalizedText Item, LocalizedText Expected, string Actual);

public sealed record RuleEscalation(string When, Severity Severity);

/// <summary>Facts for one example; <see cref="Expect"/> null = must not fire (and must be determinate).</summary>
public sealed record RuleExample(IReadOnlyDictionary<string, JsonElement> Facts, Severity? Expect, IReadOnlyList<NodeRoles>? Roles = null, string? Note = null);

public sealed record RuleSource(SourceKind Kind, string Citation);

/// <summary>A validated rule with pre-parsed expressions.</summary>
public sealed class CompiledRule
{
    internal CompiledRule(RuleDefinition definition, Expression when, IReadOnlyList<(Expression When, Severity Severity)> escalations)
    {
        Definition = definition;
        When = when;
        Escalations = escalations;
    }

    public RuleDefinition Definition { get; }

    public Expression When { get; }

    public IReadOnlyList<(Expression When, Severity Severity)> Escalations { get; }

    public string Id => Definition.Id;
}

public static class RuleLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Parses and validates one rule JSON document. Throws <see cref="RuleValidationException"/> on any problem.</summary>
    public static CompiledRule Load(string json, string origin)
    {
        RuleDefinition? def;
        try
        {
            def = JsonSerializer.Deserialize<RuleDefinition>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new RuleValidationException(origin, $"invalid JSON: {ex.Message}");
        }

        if (def is null)
        {
            throw new RuleValidationException(origin, "empty document");
        }

        return Compile(def, origin);
    }

    public static CompiledRule Compile(RuleDefinition def, string origin)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(def.Id))
        {
            problems.Add("id is required");
        }

        foreach (var (name, text) in new[] { ("title", def.Title), ("explanation", def.Explanation), ("recommendation", def.Recommendation) })
        {
            if (text is null || string.IsNullOrWhiteSpace(text.Fa) || string.IsNullOrWhiteSpace(text.En))
            {
                problems.Add($"{name} needs both fa and en text");
            }
        }

        if (def.Source is null || string.IsNullOrWhiteSpace(def.Source.Citation))
        {
            problems.Add("source.citation is required");
        }

        if (def.Baseline is { } baseline
            && (baseline.Item is null || baseline.Expected is null || string.IsNullOrWhiteSpace(baseline.Item.Fa) || string.IsNullOrWhiteSpace(baseline.Item.En)
                || string.IsNullOrWhiteSpace(baseline.Expected.Fa) || string.IsNullOrWhiteSpace(baseline.Expected.En) || string.IsNullOrWhiteSpace(baseline.Actual)))
        {
            problems.Add("baseline needs item and expected (fa and en) and an actual fact key");
        }

        if (def.Confidence is < 0 or > 1)
        {
            problems.Add("confidence must be within 0..1");
        }

        Expression? when = null;
        var escalations = new List<(Expression, Severity)>();
        try
        {
            when = Expression.Parse(def.When);
            foreach (var e in def.Escalate)
            {
                escalations.Add((Expression.Parse(e.When), e.Severity));
            }
        }
        catch (ExpressionException ex)
        {
            problems.Add($"condition: {ex.Message}");
        }

        if (problems.Count > 0 || when is null)
        {
            throw new RuleValidationException(origin, string.Join("; ", problems));
        }

        return new CompiledRule(def, when, escalations);
    }
}

public sealed class RuleValidationException(string origin, string problem) : Exception($"Rule '{origin}': {problem}")
{
    public string Origin { get; } = origin;
}
