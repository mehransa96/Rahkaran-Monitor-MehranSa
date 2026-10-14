using System.Text.RegularExpressions;
using RSM.Core.Model;

namespace RSM.Core.Rules;

/// <summary>
/// Latest value of every fact for one node. Keys are case-insensitive. Instance-scoped keys use
/// <c>prefix[instance].property</c>, e.g. <c>db[Rahkaran].recovery_model</c>.
/// </summary>
public sealed partial class FactStore
{
    private readonly Dictionary<string, object?> _facts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void Set(string key, object? value)
    {
        lock (_gate)
        {
            _facts[key] = value;
        }
    }

    public void SetAll(IEnumerable<Fact> facts)
    {
        lock (_gate)
        {
            foreach (var fact in facts)
            {
                _facts[fact.Key] = fact.Value;
            }
        }
    }

    public bool TryGet(string key, out object? value)
    {
        lock (_gate)
        {
            return _facts.TryGetValue(key, out value);
        }
    }

    public IReadOnlyDictionary<string, object?> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, object?>(_facts, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Distinct instance names for a prefix, e.g. <c>Instances("db")</c> → ["master", "Rahkaran"].</summary>
    public IReadOnlyList<string> Instances(string prefix)
    {
        var pattern = InstancePattern();
        lock (_gate)
        {
            return _facts.Keys
                .Select(k => pattern.Match(k))
                .Where(m => m.Success && string.Equals(m.Groups["prefix"].Value, prefix, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Groups["instance"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    [GeneratedRegex(@"^(?<prefix>[A-Za-z_][\w.]*)\[(?<instance>[^\]]+)\]\.", RegexOptions.CultureInvariant)]
    private static partial Regex InstancePattern();
}
