using RSM.Core.Model;

namespace RSM.Core.Capabilities;

public enum CapabilityState
{
    Available,
    Denied,
    NotInstalled,
    Unreachable,
    NeedsElevation,
    NotApplicable,
    NotConfigured,
    /// <summary>The probe for this capability arrives in a later phase.</summary>
    NotImplemented,
    Error,
}

/// <summary>Static description of a capability (what it enables and how to grant it).</summary>
public sealed record CapabilityDefinition(
    string Id,
    Layer Layer,
    LocalizedText Title,
    LocalizedText Enables,
    LocalizedText Remediation);

public sealed record CapabilityResult(string Id, CapabilityState State, string? Detail = null)
{
    public bool IsAvailable => State == CapabilityState.Available;
}

/// <summary>What works on one node with the current credentials, and why something does not.</summary>
public sealed class CapabilityMatrix
{
    private readonly Dictionary<string, CapabilityResult> _results = new(StringComparer.Ordinal);

    public CapabilityMatrix(string nodeId, DateTimeOffset probedAtUtc)
    {
        NodeId = nodeId;
        ProbedAtUtc = probedAtUtc;
    }

    public string NodeId { get; }

    public DateTimeOffset ProbedAtUtc { get; }

    /// <summary>Informational banners (e.g. sysadmin login used).</summary>
    public List<LocalizedText> Banners { get; } = [];

    /// <summary>Facts learned while probing (version, edition…), reused by collectors and reports.</summary>
    public Dictionary<string, string> Facts { get; } = new(StringComparer.Ordinal);

    public IReadOnlyCollection<CapabilityResult> Results => _results.Values;

    public void Set(CapabilityResult result) => _results[result.Id] = result;

    public void Set(string id, CapabilityState state, string? detail = null) => Set(new CapabilityResult(id, state, detail));

    public CapabilityResult? Get(string id) => _results.GetValueOrDefault(id);

    public bool Has(string id) => _results.TryGetValue(id, out var r) && r.IsAvailable;
}
