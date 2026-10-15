namespace RSM.Reporting.Model;

// View model of the HTML report. Every string in it is already masked (when requested) and HTML-encoded by
// ReportBuilder, so the template never sees raw server data and can output values as-is.

public sealed record ReportOptions(
    RSM.Core.Model.UiLanguage Language,
    bool Mask,
    DateTimeOffset GeneratedUtc,
    TimeZoneInfo? TimeZone = null,
    bool PersianDigits = false);

public sealed class ReportModel
{
    public required string Lang { get; init; }
    public required string Dir { get; init; }
    public required string Title { get; init; }
    public required string SiteName { get; init; }
    public required string Generated { get; init; }
    public required string Window { get; init; }
    public required string RsmVersion { get; init; }
    public required int RuleCount { get; init; }
    public required string HeavyNote { get; init; }
    public required bool Masked { get; init; }
    public required int Score { get; init; }
    public required string ScoreClass { get; init; }
    public required int Critical { get; init; }
    public required int Warning { get; init; }
    public required int Info { get; init; }
    public required IReadOnlyList<FindingView> TopIssues { get; init; }
    public required IReadOnlyList<NodeView> Nodes { get; init; }
    public required IReadOnlyDictionary<string, string> L { get; init; }
    public required IReadOnlyList<KeyValueView> Glossary { get; init; }
}

public sealed class NodeView
{
    public required string Anchor { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required string Roles { get; init; }
    public required int Critical { get; init; }
    public required int Warning { get; init; }
    public required int Info { get; init; }
    public required IReadOnlyList<KeyValueView> Inventory { get; init; }
    public required IReadOnlyList<ComplianceRow> Compliance { get; init; }
    public required IReadOnlyList<LayerSection> Layers { get; init; }
    public required IReadOnlyList<DatabaseRow> Databases { get; init; }
    public required IReadOnlyList<CapabilityRow> Capabilities { get; init; }
    public required IReadOnlyList<CollectorRow> Collectors { get; init; }
    public required IReadOnlyList<IndeterminateRow> Indeterminate { get; init; }
    public required IReadOnlyList<string> RuleErrors { get; init; }
}

public sealed record KeyValueView(string Key, string Value);

public sealed record ComplianceRow(string Item, string Instance, string Expected, string Actual, string Status, string StatusClass, string RuleId, string Source, bool General);

public sealed record LayerSection(string Name, IReadOnlyList<FindingView> Findings);

public sealed record FindingView(
    string RuleId,
    string Severity,
    string SeverityClass,
    string Node,
    string Instance,
    string Title,
    string Explanation,
    string Recommendation,
    IReadOnlyList<KeyValueView> Evidence,
    string Source,
    bool General,
    bool Unverified,
    string? Caution);

public sealed record DatabaseRow(string Name, bool IsRahkaran, string State, string StateClass, string Recovery, string DataMb, string LastFull, string LastLog, string LastCheckdb);

public sealed record CapabilityRow(string Title, string State, string StateClass, string Detail);

public sealed record CollectorRow(string Id, string State, string StateClass, string Reason, string Duration);

public sealed record IndeterminateRow(string RuleId, string Instance, string MissingFact);
