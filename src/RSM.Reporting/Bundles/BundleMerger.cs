using RSM.Core.Capabilities;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;
using RSM.Core.Snapshots;

namespace RSM.Reporting.Bundles;

/// <summary>A node of the merged site: which bundles supplied it and which capabilities are still missing.</summary>
public sealed record MergedNode(string NodeId, string NodeName, IReadOnlyList<string> FromBundles, IReadOnlyList<CapabilityResult> Gaps);

public sealed record MergeResult(SiteSnapshot Snapshot, IReadOnlyList<MergedNode> Nodes, IReadOnlyList<string> Warnings);

/// <summary>
/// Merges bundles taken on different machines of one site (e.g. RSM run locally on each App server for the IIS
/// collectors that need local access, plus once against SQL Server) into one site snapshot:
/// <list type="bullet">
/// <item>nodes are matched by id, host, name or the machine name the node reported (<c>os.dns_host_name</c>/<c>os.computer_name</c>);</item>
/// <item>per node and capability the best state wins; per collector the best result (Ok &gt; Partial &gt; Error &gt; Unavailable, then newest);</item>
/// <item>facts = the best-covered node snapshot, overlaid with the facts of every chosen collector result;</item>
/// <item>site analyzers and the rules are evaluated again on the merged facts, so cross-node findings appear.</item>
/// </list>
/// Capabilities still unavailable after the merge are reported per node as gaps.
/// </summary>
public static class BundleMerger
{
    public static MergeResult Merge(IReadOnlyList<ImportedBundle> bundles, RulesEngine engine, IEnumerable<ISiteAnalyzer> analyzers, DateTimeOffset nowUtc)
    {
        if (bundles.Count == 0)
        {
            throw new ArgumentException("Nothing to merge.", nameof(bundles));
        }

        var warnings = new List<string>();
        var sites = bundles.Select(b => b.Snapshot.Site.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sites.Count > 1)
        {
            warnings.Add($"The bundles name different sites ({string.Join(", ", sites)}); nodes were matched by id, host and machine name.");
        }

        if (bundles.Any(b => b.Masked) && bundles.Any(b => !b.Masked))
        {
            warnings.Add("Masked and unmasked bundles were mixed; masked names cannot be matched by host or name.");
        }

        // Group node snapshots that describe the same machine.
        var groups = new List<List<(ImportedBundle Bundle, NodeSnapshot Node)>>();
        foreach (var bundle in bundles)
        {
            foreach (var node in bundle.Snapshot.Nodes)
            {
                var keys = Keys(node);
                var group = groups.FirstOrDefault(g => g.Any(m => Keys(m.Node).Overlaps(keys)));
                if (group is null)
                {
                    groups.Add(group = []);
                }

                group.Add((bundle, node));
            }
        }

        var first = bundles[0].Snapshot;
        var merged = new List<(NodeProfile Profile, CapabilityMatrix Matrix, List<CollectorResult> Results, FactStore Facts, List<string> From)>();
        foreach (var group in groups)
        {
            var best = group.MaxBy(m => (m.Node.Collectors.Count(c => c.Status.Kind == CollectorStatusKind.Ok), m.Node.Capabilities.Results.Count(r => r.IsAvailable)));
            var profile = best.Node.Node with { Roles = group.Aggregate(NodeRoles.None, (r, m) => r | m.Node.Node.Roles) };
            var matrix = new CapabilityMatrix(profile.Id, group.Max(m => m.Node.Capabilities.ProbedAtUtc));
            foreach (var capability in group.SelectMany(m => m.Node.Capabilities.Results).GroupBy(r => r.Id))
            {
                matrix.Set(capability.OrderBy(r => Rank(r.State)).First());
            }

            foreach (var banner in group.SelectMany(m => m.Node.Capabilities.Banners).DistinctBy(b => b.En))
            {
                matrix.Banners.Add(banner);
            }

            var results = group.SelectMany(m => m.Node.Collectors).GroupBy(c => c.CollectorId)
                .Select(g => g.OrderBy(c => Rank(c.Status.Kind)).ThenByDescending(c => c.CollectedAtUtc).First() with { NodeId = profile.Id })
                .OrderBy(c => c.CollectorId, StringComparer.Ordinal)
                .ToList();
            var facts = new FactStore();
            foreach (var (key, value) in best.Node.Facts)
            {
                facts.Set(key, value);
            }

            foreach (var result in results.Where(r => r.Status.Kind is CollectorStatusKind.Ok or CollectorStatusKind.Partial))
            {
                facts.SetAll(result.Facts.Select(f => f with { Value = FactValues.Plain(f.Value) }));
            }

            merged.Add((profile, matrix, results, facts, group.Select(m => m.Bundle.FileName).Distinct().ToList()));
        }

        var site = first.Site with { Nodes = merged.Select(m => m.Profile).ToList() };
        var byNode = merged.ToDictionary(m => m.Profile.Id, m => m.Facts.Snapshot(), StringComparer.Ordinal);
        foreach (var analyzer in analyzers)
        {
            foreach (var (nodeId, extra) in analyzer.Analyze(site, byNode))
            {
                merged.FirstOrDefault(m => m.Profile.Id == nodeId).Facts?.SetAll(extra);
            }
        }

        var nodes = merged.Select(m =>
        {
            var evaluation = engine.Evaluate(m.Profile.Id, m.Profile.Roles, m.Facts, nowUtc);
            return new NodeSnapshot(m.Profile, m.Matrix, m.Results, m.Facts.Snapshot(), evaluation.Findings, evaluation.Indeterminate, evaluation.Errors);
        }).ToList();

        var snapshot = new SiteSnapshot("merged-" + Guid.NewGuid().ToString("N")[..20], site, bundles.Min(b => b.Snapshot.StartedUtc), bundles.Max(b => b.Snapshot.FinishedUtc),
            bundles.Any(b => b.Snapshot.IncludedHeavy), first.RsmVersion, engine.Rules.Count, nodes)
        {
            Sources = bundles.Select(b => new SnapshotSource(b.SnapshotId, b.SiteName, b.StartedUtc, b.RsmVersion, b.Masked,
                merged.Where(m => m.From.Contains(b.FileName)).Select(m => m.Profile.Id).ToList())).ToList(),
        };

        var info = merged.Select(m => new MergedNode(m.Profile.Id, m.Profile.Name, m.From,
            m.Matrix.Results.Where(r => r.State is not (CapabilityState.Available or CapabilityState.NotApplicable or CapabilityState.NotImplemented)).OrderBy(r => r.Id, StringComparer.Ordinal).ToList())).ToList();
        return new MergeResult(snapshot, info, warnings);
    }

    /// <summary>Identity keys of a node; two node snapshots are the same machine when any key matches.</summary>
    private static HashSet<string> Keys(NodeSnapshot node)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id:" + node.Node.Id };
        void Add(string kind, object? value)
        {
            if (value is string s && !string.IsNullOrWhiteSpace(s))
            {
                keys.Add(kind + ":" + s.Trim().Split('.')[0]);
            }
        }

        Add("host", node.Node.Host);
        Add("host", node.Node.Name);
        Add("host", node.Facts.GetValueOrDefault("os.dns_host_name"));
        Add("host", node.Facts.GetValueOrDefault("os.computer_name"));
        return keys;
    }

    private static int Rank(CapabilityState state) => state switch
    {
        CapabilityState.Available => 0,
        CapabilityState.NotApplicable => 1,
        CapabilityState.NeedsElevation => 2,
        CapabilityState.Denied => 3,
        CapabilityState.NotConfigured => 4,
        CapabilityState.NotInstalled => 5,
        CapabilityState.Unreachable => 6,
        CapabilityState.Error => 7,
        _ => 8,
    };

    private static int Rank(CollectorStatusKind kind) => kind switch
    {
        CollectorStatusKind.Ok => 0,
        CollectorStatusKind.Partial => 1,
        CollectorStatusKind.Error => 2,
        _ => 3,
    };
}
