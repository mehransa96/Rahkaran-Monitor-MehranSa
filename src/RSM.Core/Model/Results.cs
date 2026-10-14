namespace RSM.Core.Model;

public sealed record CollectorStatus(CollectorStatusKind Kind, string? Reason = null, string? Detail = null, string? MissingCapability = null)
{
    public static readonly CollectorStatus Ok = new(CollectorStatusKind.Ok);

    public static CollectorStatus Unavailable(string reason, string? missingCapability = null, string? detail = null) =>
        new(CollectorStatusKind.Unavailable, reason, detail, missingCapability);

    public static CollectorStatus Error(string reason, string? detail = null) => new(CollectorStatusKind.Error, reason, detail);

    public static CollectorStatus Partial(string reason, string? detail = null) => new(CollectorStatusKind.Partial, reason, detail);
}

/// <summary>A numeric sample for charts and history. <see cref="Instance"/> is e.g. a DB name or disk.</summary>
public sealed record MetricSample(string Key, string? Instance, double Value, DateTimeOffset AtUtc);

/// <summary>
/// A flattened fact that rules can evaluate. Keys look like <c>sql.config.maxdop</c> or, for instance-scoped
/// facts, <c>db[Rahkaran].recovery_model</c>.
/// </summary>
public sealed record Fact(string Key, object? Value);

public sealed record UnifiedEvent(
    DateTimeOffset AtUtc,
    string NodeId,
    Layer Layer,
    string Source,
    string Code,
    Severity Severity,
    string Message,
    int Count = 1);

public sealed record CollectorResult(
    string CollectorId,
    string NodeId,
    DateTimeOffset CollectedAtUtc,
    TimeSpan Duration,
    CollectorStatus Status,
    object? Payload,
    IReadOnlyList<MetricSample> Samples,
    IReadOnlyList<Fact> Facts,
    IReadOnlyList<UnifiedEvent> Events)
{
    public static CollectorResult FromStatus(string collectorId, string nodeId, CollectorStatus status, TimeSpan duration) =>
        new(collectorId, nodeId, DateTimeOffset.UtcNow, duration, status, null, [], [], []);
}

public sealed record SourceRef(SourceKind Kind, string Citation)
{
    /// <summary>General guidance must be shown with the "confirm with ستاد فنی" label.</summary>
    public bool IsGeneralGuidance => Kind == SourceKind.General;
}

public sealed record Evidence(string FactKey, object? Value, DateTimeOffset AtUtc, string? Threshold = null);

public sealed record Finding(
    string RuleId,
    Severity Severity,
    Layer Layer,
    string NodeId,
    string? Instance,
    LocalizedText Title,
    LocalizedText Explanation,
    LocalizedText Recommendation,
    IReadOnlyList<Evidence> Evidence,
    SourceRef Source,
    bool Verified,
    double Confidence,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);
