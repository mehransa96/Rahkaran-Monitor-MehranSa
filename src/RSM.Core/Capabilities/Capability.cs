using System.Text.Json;
using System.Text.Json.Serialization;
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
[JsonConverter(typeof(CapabilityMatrixJsonConverter))]
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

/// <summary>
/// Same JSON shape as before (nodeId, probedAtUtc, banners, facts, results), but readable back, so imported bundles
/// keep their capability matrix (results have no public setter).
/// </summary>
public sealed class CapabilityMatrixJsonConverter : JsonConverter<CapabilityMatrix>
{
    public override CapabilityMatrix Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        JsonElement? Get(string name) =>
            root.EnumerateObject().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).FirstOrDefault();

        var matrix = new CapabilityMatrix(Get("nodeId")?.GetString() ?? string.Empty, Get("probedAtUtc") is { ValueKind: JsonValueKind.String } at ? at.GetDateTimeOffset() : default);
        if (Get("banners") is { ValueKind: JsonValueKind.Array } banners)
        {
            matrix.Banners.AddRange(banners.Deserialize<List<LocalizedText>>(options) ?? []);
        }

        if (Get("facts") is { ValueKind: JsonValueKind.Object } facts)
        {
            foreach (var f in facts.EnumerateObject())
            {
                matrix.Facts[f.Name] = f.Value.ValueKind == JsonValueKind.String ? f.Value.GetString()! : f.Value.GetRawText();
            }
        }

        if (Get("results") is { ValueKind: JsonValueKind.Array } results)
        {
            foreach (var r in results.Deserialize<List<CapabilityResult>>(options) ?? [])
            {
                matrix.Set(r);
            }
        }

        return matrix;
    }

    public override void Write(Utf8JsonWriter writer, CapabilityMatrix value, JsonSerializerOptions options)
    {
        string Name(string n) => options.PropertyNamingPolicy?.ConvertName(n) ?? n;
        writer.WriteStartObject();
        writer.WriteString(Name("NodeId"), value.NodeId);
        writer.WritePropertyName(Name("ProbedAtUtc"));
        JsonSerializer.Serialize(writer, value.ProbedAtUtc, options);
        writer.WritePropertyName(Name("Banners"));
        JsonSerializer.Serialize(writer, value.Banners, options);
        writer.WritePropertyName(Name("Facts"));
        JsonSerializer.Serialize(writer, value.Facts, options);
        writer.WritePropertyName(Name("Results"));
        JsonSerializer.Serialize(writer, value.Results, options);
        writer.WriteEndObject();
    }
}
