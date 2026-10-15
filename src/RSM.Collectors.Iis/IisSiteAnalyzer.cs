using System.Globalization;
using System.Text.Json;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;

namespace RSM.Collectors.Iis;

/// <summary>
/// Cross-node IIS checks (spec §10.2, 10.8, 10.10): pool settings and web.config differing between the App nodes of
/// the farm (drift), rewrite rules whose application path segment is not deployed on any node ("X changes after an
/// update"), and worker processes that started recently outside any scheduled recycle.
/// </summary>
public sealed class IisSiteAnalyzer : ISiteAnalyzer
{
    public static readonly TimeSpan RecentStart = TimeSpan.FromHours(1);
    public static readonly TimeSpan ScheduleTolerance = TimeSpan.FromMinutes(15);

    public string Id => "iis.site";

    public IReadOnlySet<string> InputCollectors { get; } = new HashSet<string>(StringComparer.Ordinal) { "iis.config", "iis.runtime" };

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public IReadOnlyDictionary<string, IReadOnlyList<Fact>> Analyze(SiteProfile site, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> factsByNode)
    {
        var result = factsByNode.Keys.ToDictionary(k => k, _ => new List<Fact>(), StringComparer.Ordinal);
        var appNodes = site.Nodes.Where(n => n.Roles.HasFlag(NodeRoles.App) && factsByNode.ContainsKey(n.Id)).Select(n => n.Id).ToList();

        Drift(appNodes, factsByNode, result, "pool", ".signature", ".drift_keys");
        Drift(appNodes, factsByNode, result, "site", ".webconfig_signature", ".config_drift_keys");

        var deployed = factsByNode.Values
            .SelectMany(f => (f.GetValueOrDefault("iis.app_segments") as string)?.Split('|', StringSplitOptions.RemoveEmptyEntries) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anyConfig = factsByNode.Values.Any(f => f.ContainsKey("iis.app_segments"));
        foreach (var (node, facts) in factsByNode)
        {
            foreach (var (key, value) in facts)
            {
                if (anyConfig && key.StartsWith("rewrite[", StringComparison.Ordinal) && key.EndsWith("].target_segment", StringComparison.Ordinal) && value is string segment && segment.Length > 0)
                {
                    result[node].Add(new Fact(key[..^".target_segment".Length] + ".segment_deployed", deployed.Contains(segment)));
                }

                if (key.StartsWith("pool[", StringComparison.Ordinal) && key.EndsWith("].wp_youngest_started_utc", StringComparison.Ordinal) && value is string started
                    && DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                {
                    var prefix = key[..^".wp_youngest_started_utc".Length];
                    var schedule = facts.GetValueOrDefault(prefix + ".restart_schedule") as string;
                    result[node].Add(new Fact(prefix + ".recent_unplanned_start", IsRecentUnplanned(at, schedule, Clock.GetUtcNow())));
                }
            }
        }

        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<Fact>)p.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// A worker process younger than <see cref="RecentStart"/> whose start (in local time) is not within
    /// <see cref="ScheduleTolerance"/> of a configured recycle time ("03:00, 15:00").
    /// </summary>
    internal static bool IsRecentUnplanned(DateTimeOffset startedUtc, string? schedule, DateTimeOffset nowUtc)
    {
        if (nowUtc - startedUtc > RecentStart || startedUtc > nowUtc + TimeSpan.FromMinutes(1))
        {
            return false;
        }

        var local = startedUtc.ToLocalTime().TimeOfDay;
        foreach (var entry in (schedule ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TimeSpan.TryParse(entry, CultureInfo.InvariantCulture, out var t))
            {
                var diff = Math.Abs((local - t).TotalMinutes);
                if (Math.Min(diff, 1440 - diff) <= ScheduleTolerance.TotalMinutes)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// For every instance (pool or site) present on at least two App nodes, compares the JSON signatures and writes
    /// the differing setting names on each of those nodes ("" = identical).
    /// </summary>
    private static void Drift(IReadOnlyList<string> appNodes, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> factsByNode,
        Dictionary<string, List<Fact>> result, string prefix, string signatureSuffix, string outputSuffix)
    {
        if (appNodes.Count < 2)
        {
            return;
        }

        var byInstance = new Dictionary<string, List<(string Node, Dictionary<string, string> Signature)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in appNodes)
        {
            foreach (var (key, value) in factsByNode[node])
            {
                if (key.StartsWith(prefix + "[", StringComparison.Ordinal) && key.EndsWith("]" + signatureSuffix, StringComparison.Ordinal) && value is string json)
                {
                    var instance = key[(prefix.Length + 1)..^(signatureSuffix.Length + 1)];
                    try
                    {
                        if (JsonSerializer.Deserialize<Dictionary<string, string>>(json) is { } signature)
                        {
                            (byInstance.TryGetValue(instance, out var list) ? list : byInstance[instance] = []).Add((node, signature));
                        }
                    }
                    catch (JsonException)
                    {
                        // A malformed signature is not compared.
                    }
                }
            }
        }

        foreach (var (instance, entries) in byInstance.Where(e => e.Value.Count >= 2))
        {
            var keys = entries.SelectMany(e => e.Signature.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var differing = keys.Where(k => entries.Select(e => e.Signature.GetValueOrDefault(k) ?? "\u0000").Distinct(StringComparer.Ordinal).Count() > 1).ToList();
            foreach (var (node, _) in entries)
            {
                result[node].Add(new Fact($"{prefix}[{instance}]{outputSuffix}", string.Join(", ", differing)));
                result[node].Add(new Fact($"{prefix}[{instance}]{outputSuffix.Replace("_keys", "_nodes", StringComparison.Ordinal)}", entries.Count));
            }
        }
    }
}
