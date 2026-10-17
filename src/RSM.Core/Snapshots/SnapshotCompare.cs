using System.Globalization;
using System.Text.Json;
using RSM.Core.Model;
using RSM.Core.Rules;

namespace RSM.Core.Snapshots;

/// <summary>A fact that differs between two diagnoses of the same node. <see cref="Change"/> is relative (0.25 = +25 %) for numbers.</summary>
public sealed record FactChange(string NodeName, string Key, string Before, string After, double? Change);

public enum FindingChangeKind
{
    New,
    Resolved,
    SeverityChanged,
}

public sealed record FindingChange(string NodeName, string RuleId, string? Instance, Severity? Before, Severity? After, LocalizedText Title, FindingChangeKind Kind);

/// <summary>"Before vs after" (spec §15): what changed between two diagnoses of a site.</summary>
public sealed record SnapshotComparison(
    SiteSnapshot Before,
    SiteSnapshot After,
    IReadOnlyList<FactChange> Numbers,
    IReadOnlyList<FactChange> Texts,
    IReadOnlyList<FindingChange> Findings,
    IReadOnlyList<string> UnmatchedNodes);

/// <summary>
/// Compares two diagnoses (e.g. before and after a Rahkaran or SQL Server update). It reports:
/// <list type="bullet">
/// <item>numeric facts that moved by at least <c>minRelativeChange</c> — waits, IIS p95 per site, CPU, memory, file
/// latency…, largest change first;</item>
/// <item>text facts that changed — versions, top statement, top waits, slowest URL;</item>
/// <item>findings that are new, resolved or changed severity.</item>
/// </list>
/// Nodes are matched by id, then host, then name. Clock-like facts (uptime, ages, RSM's own state) are left out
/// because they always change.
/// </summary>
public static class SnapshotCompare
{
    public const double DefaultMinRelativeChange = 0.10;

    private static readonly string[] AlwaysChanging = ["uptime", "_ago", "age_s", "age_days", "days_left", "start_time", "rsm.", "node.role", "count_24h", "_started_utc"];

    public static SnapshotComparison Compare(SiteSnapshot before, SiteSnapshot after, double minRelativeChange = DefaultMinRelativeChange)
    {
        var numbers = new List<FactChange>();
        var texts = new List<FactChange>();
        var findings = new List<FindingChange>();
        var unmatched = new List<string>();
        var used = new HashSet<NodeSnapshot>();
        foreach (var a in after.Nodes)
        {
            var b = before.Nodes.FirstOrDefault(n => !used.Contains(n) && n.Node.Id == a.Node.Id)
                ?? before.Nodes.FirstOrDefault(n => !used.Contains(n) && string.Equals(n.Node.Host, a.Node.Host, StringComparison.OrdinalIgnoreCase))
                ?? before.Nodes.FirstOrDefault(n => !used.Contains(n) && string.Equals(n.Node.Name, a.Node.Name, StringComparison.OrdinalIgnoreCase));
            if (b is null)
            {
                unmatched.Add(a.Node.Name);
                continue;
            }

            used.Add(b);
            var name = a.Node.Name;
            foreach (var key in a.Facts.Keys.Intersect(b.Facts.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
            {
                if (AlwaysChanging.Any(x => key.Contains(x, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var x0 = FactValues.Plain(b.Facts[key]);
                var x1 = FactValues.Plain(a.Facts.First(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)).Value);
                if (Number(x0) is { } n0 && Number(x1) is { } n1)
                {
                    var change = n0 == 0 ? (n1 == 0 ? 0 : (double?)null) : (n1 - n0) / Math.Abs(n0);
                    if (change is null || Math.Abs(change.Value) >= minRelativeChange)
                    {
                        numbers.Add(new FactChange(name, key, Format(n0), Format(n1), change));
                    }
                }
                else if (Text(x0) is { } t0 && Text(x1) is { } t1 && !string.Equals(t0, t1, StringComparison.Ordinal))
                {
                    texts.Add(new FactChange(name, key, t0, t1, null));
                }
            }

            var oldFindings = b.Findings.ToDictionary(f => (f.RuleId, f.Instance));
            var newFindings = a.Findings.ToDictionary(f => (f.RuleId, f.Instance));
            foreach (var (k, f) in newFindings)
            {
                if (!oldFindings.TryGetValue(k, out var o))
                {
                    findings.Add(new FindingChange(name, f.RuleId, f.Instance, null, f.Severity, f.Title, FindingChangeKind.New));
                }
                else if (o.Severity != f.Severity)
                {
                    findings.Add(new FindingChange(name, f.RuleId, f.Instance, o.Severity, f.Severity, f.Title, FindingChangeKind.SeverityChanged));
                }
            }

            findings.AddRange(oldFindings.Where(o => !newFindings.ContainsKey(o.Key))
                .Select(o => new FindingChange(name, o.Value.RuleId, o.Value.Instance, o.Value.Severity, null, o.Value.Title, FindingChangeKind.Resolved)));
        }

        unmatched.AddRange(before.Nodes.Where(n => !used.Contains(n)).Select(n => n.Node.Name));
        return new SnapshotComparison(before, after,
            numbers.OrderByDescending(c => c.Change is { } v ? Math.Abs(v) : double.MaxValue).ThenBy(c => c.NodeName, StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.Ordinal).ToList(),
            texts,
            findings.OrderBy(f => f.Kind).ThenByDescending(f => f.After ?? f.Before).ThenBy(f => f.RuleId, StringComparer.Ordinal).ToList(),
            unmatched);
    }

    private static double? Number(object? value) => value switch
    {
        double d when double.IsFinite(d) => d,
        float f when float.IsFinite(f) => f,
        int i => i,
        long l => l,
        decimal m => (double)m,
        _ => null,
    };

    private static string? Text(object? value) => value switch
    {
        string s => s,
        IEnumerable<object?> list => string.Join(", ", list.Select(v => Convert.ToString(FactValues.Plain(v), CultureInfo.InvariantCulture))),
        JsonElement { ValueKind: JsonValueKind.Array } e => string.Join(", ", e.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())),
        _ => null,
    };

    private static string Format(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
