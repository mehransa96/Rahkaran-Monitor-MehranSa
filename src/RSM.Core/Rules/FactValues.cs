using System.Text.Json;

namespace RSM.Core.Rules;

public static class FactValues
{
    /// <summary>
    /// A fact value read back from JSON as the CLR value a collector produces (number → double, string, bool, null), so
    /// stored or imported snapshots can be evaluated and rendered again. Objects and arrays stay <see cref="JsonElement"/>.
    /// </summary>
    public static object? Plain(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement e => e.Clone(),
        _ => value,
    };

    /// <summary><see cref="Plain"/> applied to every fact of every node of a deserialized snapshot.</summary>
    public static Snapshots.SiteSnapshot Plain(Snapshots.SiteSnapshot snapshot) => snapshot with
    {
        Nodes = snapshot.Nodes.Select(n => n with { Facts = n.Facts.ToDictionary(f => f.Key, f => Plain(f.Value), StringComparer.OrdinalIgnoreCase) }).ToList(),
    };
}
