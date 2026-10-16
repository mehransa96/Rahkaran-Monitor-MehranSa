using System.Globalization;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;

namespace RSM.Collectors.Rahkaran;

/// <summary>
/// Rahkaran site-level checks (RULES.md §10) over the topology:
/// RK-001 build differs across App nodes (version-file fingerprint ⚠U, else the app path segments),
/// RK-002 SQL sessions per App host vs an even (or ARR-weighted) share,
/// RK-003 Report and Data nodes are separate in the profile but resolve to the same machine.
/// </summary>
public sealed class RahkaranSiteAnalyzer : ISiteAnalyzer
{
    /// <summary>Below this many App-host sessions the balance is not judged.</summary>
    public const int MinSessionsForBalance = 20;

    public string Id => "rk.site";

    public IReadOnlySet<string> InputCollectors { get; } = new HashSet<string>(StringComparer.Ordinal) { "topo.sql", "iis.config", "redis", "win.inventory", "sql.inventory" };

    public IReadOnlyDictionary<string, IReadOnlyList<Fact>> Analyze(SiteProfile site, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> factsByNode)
    {
        var result = factsByNode.Keys.ToDictionary(k => k, _ => new List<Fact>(), StringComparer.Ordinal);
        var map = TopologyBuilder.Build(site, factsByNode);
        VersionDrift(site, factsByNode, result);
        SessionBalance(site, map, factsByNode, result);
        SameMachine(site, factsByNode, result);
        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<Fact>)p.Value, StringComparer.Ordinal);
    }

    private static void VersionDrift(SiteProfile site, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> factsByNode, Dictionary<string, List<Fact>> result)
    {
        var apps = site.Nodes.Where(n => n.Roles.HasFlag(NodeRoles.App) && factsByNode.ContainsKey(n.Id)).ToList();
        foreach (var (source, key) in new[] { ("file", "rk.version_fingerprint"), ("path segment", "rk.app_segments") })
        {
            var values = apps.Select(n => (Node: n, Value: factsByNode[n.Id].GetValueOrDefault(key) as string)).Where(v => !string.IsNullOrEmpty(v.Value)).ToList();
            if (values.Count < 2)
            {
                continue;
            }

            var drift = values.Select(v => v.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            var detail = string.Join("; ", values.Select(v => $"{v.Node.Name}: {v.Value}"));
            foreach (var (node, _) in values)
            {
                result[node.Id].Add(new Fact("rk.version_drift", drift));
                result[node.Id].Add(new Fact("rk.version_source", source));
                result[node.Id].Add(new Fact("rk.version_drift_detail", detail));
            }

            return; // the version file, when configured and found, wins over path segments
        }
    }

    private static void SessionBalance(SiteProfile site, TopologyMap map, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> factsByNode, Dictionary<string, List<Fact>> result)
    {
        var apps = site.Nodes.Where(n => n.Roles.HasFlag(NodeRoles.App)).ToList();
        if (apps.Count < 2)
        {
            return;
        }

        // ARR weights per App node when one farm covers all of them; otherwise an even share.
        var weights = map.Edges.Where(e => e.Kind == TopologyEdgeKind.ArrFarm && e.Count is > 0)
            .GroupBy(e => e.Label)
            .Select(g => g.GroupBy(e => e.To).ToDictionary(t => t.Key, t => t.Sum(e => e.Count!.Value), StringComparer.Ordinal))
            .FirstOrDefault(w => apps.All(a => w.ContainsKey(a.Id)));

        foreach (var data in site.Nodes.Where(n => n.Roles.HasFlag(NodeRoles.Data) && factsByNode.ContainsKey(n.Id)))
        {
            var sessions = apps.ToDictionary(a => a.Id, a => map.Edges.Where(e => e.Kind == TopologyEdgeKind.Sql && e.To == data.Id && e.From == a.Id).Sum(e => e.Count ?? 0), StringComparer.Ordinal);
            var total = sessions.Values.Sum();
            if (total < MinSessionsForBalance || !factsByNode[data.Id].ContainsKey("topo.sql_sessions_total"))
            {
                continue;
            }

            var totalWeight = weights?.Where(w => sessions.ContainsKey(w.Key)).Sum(w => w.Value) ?? apps.Count;
            foreach (var app in apps)
            {
                var expected = (weights?.GetValueOrDefault(app.Id) ?? 1) / totalWeight;
                var actual = sessions[app.Id] / total;
                var key = $"apphost[{app.Name}]";
                result[data.Id].Add(new Fact($"{key}.sql_sessions", sessions[app.Id]));
                result[data.Id].Add(new Fact($"{key}.sql_share_pct", Math.Round(actual * 100, 1)));
                result[data.Id].Add(new Fact($"{key}.expected_share_pct", Math.Round(expected * 100, 1)));
                result[data.Id].Add(new Fact($"{key}.share_deviation_pct", Math.Round(Math.Abs(actual - expected) / expected * 100, 1)));
                result[data.Id].Add(new Fact($"{key}.share_basis", weights is null ? "even" : "ARR weights"));
            }
        }
    }

    private static void SameMachine(SiteProfile site, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> factsByNode, Dictionary<string, List<Fact>> result)
    {
        var identities = new NodeIdentities(site, factsByNode);
        foreach (var report in site.Nodes.Where(n => n.Roles.HasFlag(NodeRoles.Report) && !n.Roles.HasFlag(NodeRoles.Data) && factsByNode.ContainsKey(n.Id)))
        {
            var reportMachine = Machine(report.Id, factsByNode);
            var reportAddresses = identities.AddressesOf(report.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? same = null;
            var judged = false;
            foreach (var data in site.Nodes.Where(n => n.Id != report.Id && n.Roles.HasFlag(NodeRoles.Data) && factsByNode.ContainsKey(n.Id)))
            {
                var dataMachine = Machine(data.Id, factsByNode);
                var dataAddresses = identities.AddressesOf(data.Id);
                if ((reportMachine is null || dataMachine is null) && (reportAddresses.Count == 0 || dataAddresses.Count == 0))
                {
                    continue;
                }

                judged = true;
                if ((reportMachine is not null && string.Equals(reportMachine, dataMachine, StringComparison.OrdinalIgnoreCase))
                    || dataAddresses.Any(a => reportAddresses.Contains(a) && !a.StartsWith("127.", StringComparison.Ordinal) && a != "::1"))
                {
                    same = data.Name;
                }
            }

            if (judged)
            {
                result[report.Id].Add(new Fact("rk.report_data_same_machine", same is not null));
                result[report.Id].Add(new Fact("rk.report_data_same_machine_with", same ?? string.Empty));
            }
        }
    }

    /// <summary>Machine name as the node reports it (Windows computer name, else SQL Server's MachineName).</summary>
    private static string? Machine(string nodeId, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> factsByNode)
    {
        var facts = factsByNode[nodeId];
        var name = facts.GetValueOrDefault("os.computer_name") as string ?? facts.GetValueOrDefault("sql.machine_name") as string;
        return string.IsNullOrWhiteSpace(name) ? null : name.Split('\\')[0].Split('.')[0].ToUpper(CultureInfo.InvariantCulture);
    }
}
