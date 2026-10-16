using RSM.Core.Model;

namespace RSM.Core.Slowness;

/// <summary>What one node delivered during a synchronized capture: every sample, the last facts and the active findings.</summary>
public sealed record CaptureNode(
    string NodeId,
    string NodeName,
    NodeRoles Roles,
    IReadOnlyList<MetricSample> Samples,
    IReadOnlyDictionary<string, object?> Facts,
    IReadOnlyList<Finding> Findings);

/// <summary>
/// Spec §12 synchronized capture: all nodes over the same window (default 120 s) under one <see cref="CaptureId"/>,
/// so every chart of the result shares the same X axis.
/// </summary>
public sealed record SlownessCapture(
    string CaptureId,
    string SiteId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<CaptureNode> Nodes,
    IReadOnlyList<UnifiedEvent> Events)
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(120);

    /// <summary>During a capture every interval is multiplied by this (15 s collectors run every 5 s).</summary>
    public const double InvestigationIntervalScale = 1d / 3;
}
