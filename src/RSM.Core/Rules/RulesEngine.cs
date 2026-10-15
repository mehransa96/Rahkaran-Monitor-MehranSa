using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RSM.Core.Model;
using RSM.Core.Rules.Expressions;

namespace RSM.Core.Rules;

public sealed record IndeterminateRule(string RuleId, string? Instance, string MissingFact);

public sealed record RuleError(string RuleId, string? Instance, string Message);

public sealed record RuleRunResult(
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<IndeterminateRule> Indeterminate,
    IReadOnlyList<RuleError> Errors);

/// <summary>
/// Evaluates compiled rules against one node's facts. Missing facts make a rule indeterminate (never "false");
/// recommendations are text only.
/// </summary>
public sealed partial class RulesEngine(IReadOnlyList<CompiledRule> rules)
{
    public IReadOnlyList<CompiledRule> Rules { get; } = rules;

    public RuleRunResult Evaluate(string nodeId, NodeRoles roles, FactStore facts, DateTimeOffset nowUtc)
    {
        var findings = new List<Finding>();
        var indeterminate = new List<IndeterminateRule>();
        var errors = new List<RuleError>();

        foreach (var rule in Rules)
        {
            var def = rule.Definition;
            if (def.AppliesTo.Count > 0 && !def.AppliesTo.Any(r => (roles & r) != 0))
            {
                continue;
            }

            IEnumerable<string?> instances = def.ForEach is null ? new string?[] { null } : facts.Instances(def.ForEach).Cast<string?>();
            foreach (var instance in instances)
            {
                var scope = new RuleScope(facts, def, instance, nowUtc);
                try
                {
                    if (!rule.When.EvaluateBool(scope))
                    {
                        continue;
                    }

                    var severity = def.Severity;
                    foreach (var (when, escalated) in rule.Escalations)
                    {
                        if (escalated > severity && when.EvaluateBool(scope))
                        {
                            severity = escalated;
                        }
                    }

                    findings.Add(BuildFinding(rule, nodeId, instance, severity, scope, nowUtc));
                }
                catch (MissingFactException ex)
                {
                    indeterminate.Add(new IndeterminateRule(def.Id, instance, ex.Path));
                }
                catch (Exception ex) when (ex is InvalidCastException or FormatException or ExpressionException or RegexMatchTimeoutException)
                {
                    errors.Add(new RuleError(def.Id, instance, ex.Message));
                }
            }
        }

        return new RuleRunResult(findings, indeterminate, errors);
    }

    private static Finding BuildFinding(CompiledRule rule, string nodeId, string? instance, Severity severity, RuleScope scope, DateTimeOffset nowUtc)
    {
        var def = rule.Definition;
        var evidenceKeys = def.Evidence.Count > 0 ? def.Evidence : rule.When.ReferencedPaths;
        var evidence = evidenceKeys
            .Select(k => scope.TryResolve(k, out var v) ? new Evidence(scope.Qualify(k), v, nowUtc) : null)
            .OfType<Evidence>()
            .ToList();

        return new Finding(
            def.Id,
            severity,
            def.Layer,
            nodeId,
            instance,
            Format(def.Title, scope),
            Format(def.Explanation, scope),
            Format(def.Recommendation, scope),
            evidence,
            new SourceRef(def.Source.Kind, def.Source.Citation),
            def.Verified,
            def.Confidence,
            nowUtc,
            nowUtc);
    }

    /// <summary>Replaces <c>{fact.path}</c> placeholders with values; unknown placeholders are kept verbatim.</summary>
    private static LocalizedText Format(LocalizedText text, RuleScope scope) => new(Format(text.Fa, scope), Format(text.En, scope));

    private static string Format(string text, RuleScope scope) =>
        Placeholder().Replace(text, m => scope.TryResolve(m.Groups[1].Value, out var v) ? FormatValue(v) : m.Value);

    private static string FormatValue(object? value) => Values.Normalize(value) switch
    {
        null => "—",
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        string s => s,
        System.Collections.IEnumerable items => FormatList(items.Cast<object?>().Select(FormatValue).ToList()),
        var other => Values.ToText(other),
    };

    /// <summary>Lists (e.g. user or job names) read as "a, b, c", capped at 10 items.</summary>
    private static string FormatList(List<string> items) =>
        items.Count == 0 ? "—" : items.Count <= 10 ? string.Join(", ", items) : string.Join(", ", items.Take(10)) + $", … (+{items.Count - 10})";

    [GeneratedRegex(@"\{([A-Za-z_][\w.\[\]]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private sealed class RuleScope(FactStore facts, RuleDefinition rule, string? instance, DateTimeOffset now) : IEvaluationScope
    {
        public DateTimeOffset Now { get; } = now;

        public bool TryResolve(string path, out object? value)
        {
            if (path.Equals("instance", StringComparison.OrdinalIgnoreCase) && instance is not null)
            {
                value = instance;
                return true;
            }

            if (path.StartsWith("params.", StringComparison.OrdinalIgnoreCase))
            {
                if (rule.Params.TryGetValue(path["params.".Length..], out var element))
                {
                    value = FromJson(element);
                    return true;
                }

                value = null;
                return false;
            }

            return facts.TryGet(Qualify(path), out value);
        }

        /// <summary>Maps <c>db.x</c> to <c>db[instance].x</c> inside a forEach rule.</summary>
        public string Qualify(string path)
        {
            if (instance is null || rule.ForEach is null)
            {
                return path;
            }

            var prefix = rule.ForEach + ".";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? $"{rule.ForEach}[{instance}].{path[prefix.Length..]}"
                : path;
        }

        private static object? FromJson(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => e.EnumerateArray().Select(FromJson).ToList(),
            _ => null,
        };
    }
}
