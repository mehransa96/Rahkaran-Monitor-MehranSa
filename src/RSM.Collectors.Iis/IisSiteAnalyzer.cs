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

    public IReadOnlySet<string> InputCollectors { get; } = new HashSet<string>(StringComparer.Ordinal) { "iis.config", "iis.runtime", "iis.logs" };

    /// <summary>Below this many requests in the window the measured distribution is not judged.</summary>
    public const long MinRequestsForDistribution = 200;

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

        Distribution(site, factsByNode, result);
        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<Fact>)p.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// ARR-003: requests each farm server's node logged in the last window (IIS logs of the App nodes) against the
    /// farm's configured weights. Judged only when every enabled server maps to a monitored node by host or name.
    /// </summary>
    private static void Distribution(SiteProfile site, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> factsByNode, Dictionary<string, List<Fact>> result)
    {
        foreach (var (node, facts) in factsByNode)
        {
            foreach (var (key, value) in facts)
            {
                if (!key.StartsWith("farm[", StringComparison.Ordinal) || !key.EndsWith("].weights", StringComparison.Ordinal) || value is not string weights || weights.Length == 0)
                {
                    continue;
                }

                var servers = weights.Split('|').Select(w => w.Split('=')).Where(p => p.Length == 2)
                    .Select(p => (Address: p[0], Weight: double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0)).ToList();
                var measured = new List<(double Weight, long Requests)>();
                foreach (var (address, weight) in servers)
                {
                    var target = site.Nodes.FirstOrDefault(n => string.Equals(n.Host, address, StringComparison.OrdinalIgnoreCase) || string.Equals(n.Name, address, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(n.Host.Split('.')[0], address.Split('.')[0], StringComparison.OrdinalIgnoreCase));
                    if (target is null || !factsByNode.TryGetValue(target.Id, out var targetFacts) || targetFacts.GetValueOrDefault("iis.requests_15m") is not { } r)
                    {
                        measured.Clear();
                        break;
                    }

                    measured.Add((weight, Convert.ToInt64(r, CultureInfo.InvariantCulture)));
                }

                var total = measured.Sum(m => m.Requests);
                var totalWeight = measured.Sum(m => m.Weight);
                if (measured.Count < 2 || total < MinRequestsForDistribution || totalWeight <= 0)
                {
                    continue;
                }

                var deviation = measured.Max(m => Math.Abs((double)m.Requests / total - m.Weight / totalWeight) / (m.Weight / totalWeight) * 100);
                result[node].Add(new Fact(key[..^".weights".Length] + ".distribution_deviation_pct", Math.Round(deviation, 1)));
            }
        }
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
