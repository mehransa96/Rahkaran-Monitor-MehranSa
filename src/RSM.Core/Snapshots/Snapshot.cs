using RSM.Core.Capabilities;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;

namespace RSM.Core.Snapshots;

/// <summary>Everything RSM learned about one node in one diagnosis run.</summary>
public sealed record NodeSnapshot(
    NodeProfile Node,
    CapabilityMatrix Capabilities,
    IReadOnlyList<CollectorResult> Collectors,
    IReadOnlyDictionary<string, object?> Facts,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<IndeterminateRule> Indeterminate,
    IReadOnlyList<RuleError> RuleErrors);

/// <summary>A full diagnosis ("snapshot") of a site — input for reports, bundles and history.</summary>
public sealed record SiteSnapshot(
    string Id,
    SiteProfile Site,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    bool IncludedHeavy,
    string RsmVersion,
    int RuleCount,
    IReadOnlyList<NodeSnapshot> Nodes)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<Finding> AllFindings => Nodes.SelectMany(n => n.Findings);
}

public sealed record SnapshotOptions(bool IncludeHeavy = false);
