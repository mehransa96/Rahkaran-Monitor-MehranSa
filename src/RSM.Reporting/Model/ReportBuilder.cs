using System.Collections;
using System.Globalization;
using System.Net;
using System.Text.Json;
using RSM.Core.Capabilities;
using RSM.Core.Localization;
using RSM.Core.Model;
using RSM.Core.Rules;
using RSM.Core.Snapshots;
using RSM.Reporting.Masking;

namespace RSM.Reporting.Model;

/// <summary>
/// Turns a <see cref="SiteSnapshot"/> into the report view model (spec §13: cover → executive summary → per-node
/// inventory, compliance table vs the ستاد فنی baseline, findings per layer, databases → appendix with capability
/// matrix, collector status, indeterminate rules and glossary). All text is masked (if requested) and HTML-encoded here.
/// </summary>
public sealed class ReportBuilder(IReadOnlyList<RuleDefinition> rules)
{
    private const double Never = 99999;

    public ReportModel Build(SiteSnapshot snapshot, ReportOptions options)
    {
        var masker = options.Mask ? Masker.From(snapshot) : Masker.None;
        var ctx = new Ctx(options.Language, masker);
        var all = snapshot.Nodes.SelectMany(n => n.Findings.Select(f => (Node: n, Finding: f))).ToList();
        int Count(Severity s) => all.Count(x => x.Finding.Severity == s);
        var score = Math.Max(0, 100 - (15 * Count(Severity.Critical)) - (5 * Count(Severity.Warning)) - Count(Severity.Info));

        return new ReportModel
        {
            Lang = options.Language == UiLanguage.Fa ? "fa" : "en",
            Dir = options.Language == UiLanguage.Fa ? "rtl" : "ltr",
            Title = ctx.T(ReportText.Title),
            SiteName = ctx.H(snapshot.Site.Name),
            Generated = ctx.Plain(DateText.Format(options.GeneratedUtc, options.Language, options.PersianDigits, options.TimeZone)),
            Window = ctx.Plain(DateText.Format(snapshot.StartedUtc, options.Language, options.PersianDigits, options.TimeZone) + " → "
                + DateText.Format(snapshot.FinishedUtc, options.Language, options.PersianDigits, options.TimeZone)),
            RsmVersion = ctx.Plain(snapshot.RsmVersion),
            RuleCount = snapshot.RuleCount,
            HeavyNote = ctx.T(snapshot.IncludedHeavy ? ReportText.HeavyIncluded : ReportText.HeavyExcluded),
            Masked = options.Mask,
            Score = score,
            ScoreClass = score >= 85 ? "ok" : score >= 60 ? "warning" : "critical",
            Critical = Count(Severity.Critical),
            Warning = Count(Severity.Warning),
            Info = Count(Severity.Info),
            TopIssues = all.OrderByDescending(x => x.Finding.Severity).ThenBy(x => x.Finding.RuleId, StringComparer.Ordinal).ThenBy(x => x.Finding.Instance, StringComparer.Ordinal)
                .Take(5).Select(x => FindingView(ctx, x.Node, x.Finding)).ToList(),
            Nodes = snapshot.Nodes.Select((n, i) => NodeView(ctx, n, i)).ToList(),
            L = Labels(ctx),
            Glossary = ReportText.GlossaryTerms.Select(g => new KeyValueView(ctx.Plain(g.Term), ctx.T(g.Meaning))).ToList(),
        };
    }

    private NodeView NodeView(Ctx ctx, NodeSnapshot node, int index)
    {
        var facts = node.Facts;
        return new NodeView
        {
            Anchor = "node-" + index.ToString(CultureInfo.InvariantCulture),
            Name = ctx.H(node.Node.Name),
            Host = ctx.H(node.Node.Host),
            Roles = ctx.Plain(node.Node.Roles.ToString()),
            Critical = node.Findings.Count(f => f.Severity == Severity.Critical),
            Warning = node.Findings.Count(f => f.Severity == Severity.Warning),
            Info = node.Findings.Count(f => f.Severity == Severity.Info),
            Inventory = ReportText.InventoryFacts
                .Where(i => facts.ContainsKey(i.Fact))
                .Select(i => new KeyValueView(ctx.T(i.Label), ctx.H(Format(facts[i.Fact], ctx.Language))))
                .ToList(),
            Compliance = node.Node.Sql is null ? [] : Compliance(ctx, node),
            Layers = node.Findings.GroupBy(f => f.Layer).OrderBy(g => g.Key)
                .Select(g => new LayerSection(ctx.T(ReportText.Layer(g.Key)),
                    g.OrderByDescending(f => f.Severity).ThenBy(f => f.RuleId, StringComparer.Ordinal).ThenBy(f => f.Instance, StringComparer.Ordinal)
                     .Select(f => FindingView(ctx, node, f)).ToList()))
                .ToList(),
            Databases = Databases(ctx, facts),
            Capabilities = node.Capabilities.Results.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new CapabilityRow(
                    ctx.T(TryCapabilityTitle(r.Id)),
                    ctx.T(CapabilityCatalog.DescribeState(r.State)),
                    r.State switch { CapabilityState.Available => "ok", CapabilityState.NotApplicable or CapabilityState.NotImplemented or CapabilityState.NotConfigured => "muted", _ => "warning" },
                    ctx.H(r.Detail)))
                .ToList(),
            Collectors = node.Collectors.Select(c => new CollectorRow(
                    ctx.Plain(c.CollectorId),
                    ctx.T(ReportText.CollectorState(c.Status.Kind)),
                    c.Status.Kind switch { CollectorStatusKind.Ok => "ok", CollectorStatusKind.Partial => "warning", CollectorStatusKind.Skipped => "muted", _ => "critical" },
                    ctx.H(string.Join(" — ", new[] { c.Status.Reason, c.Status.Detail }.Where(s => !string.IsNullOrWhiteSpace(s)))),
                    ctx.Plain(c.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture))))
                .ToList(),
            Indeterminate = node.Indeterminate.OrderBy(i => i.RuleId, StringComparer.Ordinal)
                .Select(i => new IndeterminateRow(ctx.Plain(i.RuleId), ctx.H(i.Instance), ctx.H(i.MissingFact))).ToList(),
            RuleErrors = node.RuleErrors.Select(e => ctx.H($"{e.RuleId}{(e.Instance is null ? string.Empty : $" [{e.Instance}]")}: {e.Message}")).ToList(),
        };
    }

    /// <summary>One row per baseline rule (and per relevant database for <c>forEach: db</c> rules).</summary>
    private List<ComplianceRow> Compliance(Ctx ctx, NodeSnapshot node)
    {
        var rows = new List<ComplianceRow>();
        var facts = node.Facts;
        var dbs = Instances(facts, "db");
        bool Flag(string db, string prop) => facts.GetValueOrDefault($"db[{db}].{prop}") is true or JsonElement { ValueKind: JsonValueKind.True };
        var rahkaran = dbs.Where(d => Flag(d, "is_rahkaran")).ToList();
        var reportServer = dbs.Where(d => Flag(d, "is_reportserver") || Flag(d, "is_reportserver_temp")).ToList();
        bool VolumeFlag(string v, string prop) => facts.GetValueOrDefault($"volume[{v}].{prop}") is true or JsonElement { ValueKind: JsonValueKind.True };
        var sqlVolumes = Instances(facts, "volume").Where(v => VolumeFlag(v, "has_data") || VolumeFlag(v, "has_log") || VolumeFlag(v, "has_tempdb")).ToList();

        foreach (var rule in rules.Where(r => r.Baseline is not null).OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            if (rule.AppliesTo.Count > 0 && node.Node.Roles != NodeRoles.None && !rule.AppliesTo.Any(r => (r & node.Node.Roles) != 0))
            {
                continue;
            }

            var baseline = rule.Baseline!;
            IEnumerable<string?> instances = [null];
            var forEach = rule.ForEach;
            if (forEach is not null)
            {
                // Databases: the Rahkaran (or ReportServer) ones. Volumes: those holding SQL Server files.
                IEnumerable<string> scope = string.Equals(forEach, "db", StringComparison.OrdinalIgnoreCase)
                    ? rule.When.Contains("is_reportserver", StringComparison.Ordinal) ? reportServer : rahkaran
                    : string.Equals(forEach, "volume", StringComparison.OrdinalIgnoreCase) ? sqlVolumes
                    : [];
                if (!scope.Any() && !string.Equals(forEach, "db", StringComparison.OrdinalIgnoreCase) && !string.Equals(forEach, "volume", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                instances = scope.Concat(node.Findings.Where(f => f.RuleId == rule.Id && f.Instance is not null).Select(f => f.Instance!))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            }

            foreach (var instance in instances)
            {
                var actualKey = instance is null ? baseline.Actual : $"{forEach}[{instance}].{baseline.Actual[(baseline.Actual.IndexOf('.') + 1)..]}";
                var finding = node.Findings.FirstOrDefault(f => f.RuleId == rule.Id && string.Equals(f.Instance, instance, StringComparison.OrdinalIgnoreCase));
                var unknown = node.Indeterminate.Any(i => i.RuleId == rule.Id && string.Equals(i.Instance, instance, StringComparison.OrdinalIgnoreCase))
                    || facts.GetValueOrDefault(actualKey) is null;
                var (status, css) = finding is not null ? (ctx.T(ReportText.Deviation) + " · " + ctx.T(ReportText.Severity(finding.Severity)), finding.Severity.ToString().ToLowerInvariant())
                    : NotApplicable(rule, facts) ? (ctx.T(ReportText.NotApplicable), "muted")
                    : unknown ? (ctx.T(ReportText.Unknown), "unknown")
                    : (ctx.T(ReportText.Compliant), "ok");
                rows.Add(new ComplianceRow(
                    ctx.T(baseline.Item),
                    ctx.H(instance),
                    ctx.T(baseline.Expected),
                    ctx.H(facts.TryGetValue(actualKey, out var v) ? ValueText(actualKey, v, ctx.Language) : "—"),
                    status,
                    css,
                    ctx.Plain(rule.Id),
                    ctx.H(rule.Source.Citation),
                    rule.Source.Kind == SourceKind.General));
            }
        }

        // Instance settings first, then one block per database.
        return rows.OrderBy(r => r.Instance.Length == 0 ? 0 : 1).ThenBy(r => r.Instance, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.RuleId, StringComparer.Ordinal).ToList();
    }

    /// <summary>Shared-box / dedicated-box rules only make sense for one kind of node.</summary>
    private static bool NotApplicable(RuleDefinition rule, IReadOnlyDictionary<string, object?> facts)
    {
        var shared = facts.GetValueOrDefault("node.shared_box") is true;
        return (rule.When.StartsWith("node.shared_box &&", StringComparison.Ordinal) && !shared)
            || (rule.When.StartsWith("!node.shared_box &&", StringComparison.Ordinal) && shared);
    }

    private static List<DatabaseRow> Databases(Ctx ctx, IReadOnlyDictionary<string, object?> facts)
    {
        object? F(string db, string p) => facts.GetValueOrDefault($"db[{db}].{p}");
        string Hours(object? v) => v is null ? "—" : ToDouble(v) is { } d && d >= Never ? ctx.T(ReportText.Never) : ctx.H(Format(v, ctx.Language));

        return Instances(facts, "db").Select(db =>
        {
            var state = F(db, "state") as string ?? Format(F(db, "state"), ctx.Language);
            var recovery = Format(F(db, "recovery_model"), ctx.Language);
            return new DatabaseRow(
                ctx.H(db),
                F(db, "is_rahkaran") is true,
                ctx.H(state),
                state.Equals("ONLINE", StringComparison.OrdinalIgnoreCase) ? "ok" : "critical",
                ctx.H(recovery),
                ctx.H(Format(F(db, "data_size_mb"), ctx.Language)),
                Hours(F(db, "hours_since_full")),
                recovery.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase) ? "—" : Hours(F(db, "hours_since_log")),
                Hours(F(db, "days_since_checkdb")));
        }).ToList();
    }

    private static FindingView FindingView(Ctx ctx, NodeSnapshot node, Finding f) => new(
        ctx.Plain(f.RuleId),
        ctx.T(ReportText.Severity(f.Severity)),
        f.Severity.ToString().ToLowerInvariant(),
        ctx.H(node.Node.Name),
        ctx.H(f.Instance),
        ctx.H(f.Title.Get(ctx.Language)),
        ctx.H(f.Explanation.Get(ctx.Language)),
        ctx.H(f.Recommendation.Get(ctx.Language)),
        f.Evidence.Select(e => new KeyValueView(ctx.H(e.FactKey), ctx.H(ValueText(e.FactKey, e.Value, ctx.Language) + (e.Threshold is null ? string.Empty : $" ({e.Threshold})")))).ToList(),
        ctx.H(f.Source.Citation),
        f.Source.IsGeneralGuidance,
        !f.Verified,
        f.RuleId.StartsWith("DB-IDX", StringComparison.Ordinal) ? ctx.T(ReportText.IndexCaution) : null);

    private static LocalizedText TryCapabilityTitle(string id)
    {
        try
        {
            return CapabilityCatalog.Get(id).Title;
        }
        catch (KeyNotFoundException)
        {
            return new LocalizedText(id, id);
        }
    }

    private static List<string> Instances(IReadOnlyDictionary<string, object?> facts, string prefix)
    {
        var start = prefix + "[";
        return facts.Keys
            .Where(k => k.StartsWith(start, StringComparison.OrdinalIgnoreCase) && k.IndexOf("].", StringComparison.Ordinal) > start.Length)
            .Select(k => k[start.Length..k.IndexOf("].", StringComparison.Ordinal)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double? ToDouble(object? v) => v switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        decimal m => (double)m,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
        _ => null,
    };

    /// <summary>Like <see cref="Format"/>, but the collectors' "never" sentinel (99999 h/days since …) reads as "never".</summary>
    internal static string ValueText(string factKey, object? value, UiLanguage language) =>
        factKey.Contains("_since_", StringComparison.Ordinal) && ToDouble(value) >= Never ? ReportText.Never.Get(language) : Format(value, language);

    /// <summary>Invariant, human-readable rendering of a fact value (numbers trimmed, lists capped at 10 items).</summary>
    internal static string Format(object? value, UiLanguage language) => value switch
    {
        null => "—",
        bool b => language == UiLanguage.Fa ? (b ? "بله" : "خیر") : (b ? "yes" : "no"),
        string s => s,
        double or float or decimal or int or long or short or byte => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        JsonElement e => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? string.Empty,
            JsonValueKind.True => Format(true, language),
            JsonValueKind.False => Format(false, language),
            JsonValueKind.Number => Format(e.GetDouble(), language),
            JsonValueKind.Array => Format(e.EnumerateArray().Select(x => (object?)x).ToList(), language),
            JsonValueKind.Null or JsonValueKind.Undefined => "—",
            _ => e.GetRawText(),
        },
        IEnumerable items => JoinCapped(items.Cast<object?>().Select(i => Format(i, language)).ToList()),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static string JoinCapped(List<string> items) =>
        items.Count == 0 ? "—" : items.Count <= 10 ? string.Join(", ", items) : string.Join(", ", items.Take(10)) + $", … (+{items.Count - 10})";

    private static Dictionary<string, string> Labels(Ctx ctx) => typeof(ReportText)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.FieldType == typeof(LocalizedText))
        .ToDictionary(f => f.Name, f => ctx.T((LocalizedText)f.GetValue(null)!), StringComparer.Ordinal);

    /// <summary>Masking + encoding helpers. <c>H</c> = server data (masked + encoded); <c>T</c>/<c>Plain</c> = our own text (encoded).</summary>
    private sealed class Ctx(UiLanguage language, Masker masker)
    {
        public UiLanguage Language { get; } = language;

        public string H(string? text) => WebUtility.HtmlEncode(masker.Apply(text));

        public string T(LocalizedText text) => WebUtility.HtmlEncode(text.Get(Language));

        public string Plain(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
    }
}
