using System.Globalization;
using System.Net;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;

namespace RSM.Collectors.Rahkaran;

public enum TopologyEdgeKind
{
    /// <summary>End users → ARR / App (from the profile).</summary>
    Http,

    /// <summary>ARR server farm → App server (applicationHost.config, label = farm, count = weight).</summary>
    ArrFarm,

    /// <summary>Client host → SQL Server (sys.dm_exec_sessions, count = sessions).</summary>
    Sql,

    /// <summary>Client host → Redis (CLIENT LIST, count = connections).</summary>
    Redis,

    /// <summary>App → Report / Lock / Workflow node (declared by the site profile, not observed).</summary>
    Declared,
}

/// <param name="Profiled">False for a host seen in another node's data but not in the site profile.</param>
public sealed record TopologyNode(string Id, string Label, NodeRoles Roles, bool Profiled, IReadOnlyList<string> Details);

/// <param name="Observed">True when read from the servers; false when only declared in the site profile.</param>
public sealed record TopologyEdge(string From, string To, TopologyEdgeKind Kind, double? Count, string? Label, bool Observed);

public sealed record TopologyMap(IReadOnlyList<TopologyNode> Nodes, IReadOnlyList<TopologyEdge> Edges)
{
    public const string ClientsId = "@clients";

    public IEnumerable<TopologyEdge> EdgesTo(string id, TopologyEdgeKind kind) => Edges.Where(e => e.To == id && e.Kind == kind);
}

/// <summary>
/// §6 topology auto-discovery from facts already collected (read-only): ARR farms and rewrite targets
/// (<c>farm[…].weights</c>), SQL sessions per client host (<c>sqlclient[…]</c>), Redis connections per address
/// (<c>redisclient[…]</c>) and the identities of the profiled nodes (host, computer name, DNS name, SQL machine name,
/// IP addresses). Pure: the live view and the snapshot report build the same map.
/// </summary>
public static class TopologyBuilder
{
    public const int MaxUnprofiled = 12;

    public static TopologyMap Build(SiteProfile site, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> factsByNode)
    {
        var identities = new NodeIdentities(site, factsByNode);
        var nodes = site.Nodes.Select(n => new TopologyNode(n.Id, n.Name, n.Roles, true, Details(n, factsByNode.GetValueOrDefault(n.Id)))).ToList();
        var edges = new List<TopologyEdge>();
        var unprofiled = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        string Endpoint(string address, double weight)
        {
            if (identities.Resolve(address) is { } id)
            {
                return id;
            }

            var key = "@host:" + address.ToLowerInvariant();
            unprofiled[key] = unprofiled.GetValueOrDefault(key) + weight;
            return key;
        }

        // Users reach the ARR nodes, or the App nodes directly when there is no ARR tier.
        var front = site.Nodes.Where(n => n.Roles.HasFlag(NodeRoles.Arr)).ToList();
        if (front.Count == 0)
        {
            front = site.Nodes.Where(n => n.Roles.HasFlag(NodeRoles.App)).ToList();
        }

        edges.AddRange(front.Select(n => new TopologyEdge(TopologyMap.ClientsId, n.Id, TopologyEdgeKind.Http, null, null, false)));

        foreach (var node in site.Nodes)
        {
            var facts = factsByNode.GetValueOrDefault(node.Id);
            if (facts is null)
            {
                continue;
            }

            foreach (var (key, value) in facts)
            {
                if (Instance(key, "farm", ".weights") is { } farm && value is string weights)
                {
                    foreach (var server in weights.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = server.Split('=');
                        var weight = parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : (double?)null;
                        var target = Endpoint(parts[0], weight ?? 1);
                        if (target != node.Id)
                        {
                            edges.Add(new TopologyEdge(node.Id, target, TopologyEdgeKind.ArrFarm, weight, farm, true));
                        }
                    }
                }
                else if (Instance(key, "sqlclient", ".sessions") is { } host && Number(value) is { } sessions && sessions > 0)
                {
                    var addresses = (facts.GetValueOrDefault($"sqlclient[{host}].addresses") as string)?.Split('|', StringSplitOptions.RemoveEmptyEntries) ?? [];
                    var source = identities.Resolve(host) ?? addresses.Select(identities.Resolve).FirstOrDefault(r => r is not null) ?? Endpoint(host, sessions);
                    if (source != node.Id)
                    {
                        edges.Add(new TopologyEdge(source, node.Id, TopologyEdgeKind.Sql, sessions, facts.GetValueOrDefault($"sqlclient[{host}].programs") as string, true));
                    }
                }
                else if (Instance(key, "redisclient", ".connections") is { } address && Number(value) is { } connections && connections > 0)
                {
                    var source = Endpoint(address, connections);
                    if (source != node.Id)
                    {
                        edges.Add(new TopologyEdge(source, node.Id, TopologyEdgeKind.Redis, connections, null, true));
                    }
                }
            }
        }

        // Declared dependencies of the App tier: SSRS, CodeMeter and RabbitMQ nodes.
        foreach (var app in site.Nodes.Where(n => n.Roles.HasFlag(NodeRoles.App)))
        {
            foreach (var other in site.Nodes.Where(n => n.Id != app.Id && (n.Roles & (NodeRoles.Report | NodeRoles.Lock | NodeRoles.Workflow)) != 0))
            {
                var label = string.Join(", ", new[] { (NodeRoles.Report, "SSRS"), (NodeRoles.Lock, "CodeMeter"), (NodeRoles.Workflow, "RabbitMQ") }
                    .Where(r => other.Roles.HasFlag(r.Item1)).Select(r => r.Item2));
                edges.Add(new TopologyEdge(app.Id, other.Id, TopologyEdgeKind.Declared, null, label, false));
            }
        }

        // Keep the busiest unprofiled hosts; fold the rest into one "other clients" node.
        var keep = unprofiled.OrderByDescending(u => u.Value).Take(MaxUnprofiled).Select(u => u.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        const string others = "@others";
        var folded = edges.Select(e => e with { From = Fold(e.From), To = Fold(e.To) })
            .GroupBy(e => (e.From, e.To, e.Kind, e.Label))
            .Select(g => g.First() with { Count = g.Any(e => e.Count is not null) ? g.Sum(e => e.Count ?? 0) : null })
            .ToList();
        nodes.AddRange(keep.Order(StringComparer.OrdinalIgnoreCase).Select(k => new TopologyNode(k, k["@host:".Length..], NodeRoles.None, false, [])));
        if (unprofiled.Count > keep.Count)
        {
            nodes.Add(new TopologyNode(others, $"+{unprofiled.Count - keep.Count}", NodeRoles.None, false, []));
        }

        nodes.Insert(0, new TopologyNode(TopologyMap.ClientsId, "Users", NodeRoles.None, false, []));
        return new TopologyMap(nodes, folded);

        string Fold(string id) => id.StartsWith("@host:", StringComparison.Ordinal) && !keep.Contains(id) ? others : id;
    }

    private static IReadOnlyList<string> Details(NodeProfile node, IReadOnlyDictionary<string, object?>? facts)
    {
        if (facts is null)
        {
            return [node.Host];
        }

        var details = new List<string> { node.Host };
        var aux = facts.Where(f => Instance(f.Key, "site", ".role") is not null && f.Value is "Aux").Select(f => Instance(f.Key, "site", ".role")!).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (aux.Count > 0)
        {
            details.Add(string.Join(", ", aux));
        }

        if (facts.GetValueOrDefault("os.numa_nodes") is { } numa && Number(numa) is > 1)
        {
            details.Add($"NUMA × {numa}");
        }

        return details;
    }

    /// <summary>"farm[X].weights" → "X" for prefix "farm" and suffix ".weights".</summary>
    internal static string? Instance(string key, string prefix, string suffix) =>
        key.Length > prefix.Length + suffix.Length + 2 && key.StartsWith(prefix + "[", StringComparison.Ordinal) && key.EndsWith("]" + suffix, StringComparison.Ordinal)
            ? key[(prefix.Length + 1)..^(suffix.Length + 1)]
            : null;

    internal static double? Number(object? value) => value switch
    {
        null => null,
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
        _ => null,
    };
}

/// <summary>Every name and address by which a profiled node may appear in another node's data.</summary>
internal sealed class NodeIdentities
{
    private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byAddress = new(StringComparer.OrdinalIgnoreCase);

    public NodeIdentities(SiteProfile site, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> factsByNode)
    {
        foreach (var node in site.Nodes)
        {
            var facts = factsByNode.GetValueOrDefault(node.Id);
            var names = new[] { node.Host, node.Name, facts?.GetValueOrDefault("os.computer_name") as string, facts?.GetValueOrDefault("os.dns_host_name") as string, facts?.GetValueOrDefault("sql.machine_name") as string };
            foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                if (IPAddress.TryParse(name, out _))
                {
                    _byAddress.TryAdd(name!, node.Id);
                }
                else
                {
                    _byName.TryAdd(name!, node.Id);
                    _byName.TryAdd(ShortName(name!), node.Id);
                }
            }

            foreach (var address in (facts?.GetValueOrDefault("os.ip_addresses") as string)?.Split('|', StringSplitOptions.RemoveEmptyEntries) ?? [])
            {
                _byAddress.TryAdd(address, node.Id);
            }
        }
    }

    public IReadOnlyCollection<string> AddressesOf(string nodeId) => _byAddress.Where(p => p.Value == nodeId).Select(p => p.Key).ToList();

    public IReadOnlyCollection<string> NamesOf(string nodeId) => _byName.Where(p => p.Value == nodeId).Select(p => p.Key).ToList();

    /// <summary>Node id for a host name, FQDN, IP address (optionally with :port) or null.</summary>
    public string? Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        if (IPEndPoint.TryParse(v, out var endpoint) && (v.Contains('.', StringComparison.Ordinal) || v.Contains(':', StringComparison.Ordinal)))
        {
            var address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
            return _byAddress.GetValueOrDefault(address.ToString());
        }

        return _byName.GetValueOrDefault(v) ?? _byName.GetValueOrDefault(ShortName(v));
    }

    private static string ShortName(string name) => name.Split('.')[0];
}
