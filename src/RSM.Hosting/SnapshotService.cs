using System.Reflection;
using Microsoft.Extensions.Logging;
using RSM.Core.Capabilities;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;
using RSM.Core.Snapshots;
using RSM.Safety.Sql;
using RSM.Storage.Profiles;
using RSM.Storage.Snapshots;

namespace RSM.Hosting;

/// <summary>
/// "Full diagnosis": probe capabilities, run every snapshot collector once per node (sequentially, to keep the load on
/// the customer's server low), flatten facts, evaluate rules, persist the snapshot.
/// </summary>
public sealed class SnapshotService(
    ConnectionTester tester,
    IEnumerable<ICollector> collectors,
    RuleRepository ruleRepository,
    SecretStore secrets,
    SnapshotStore store,
    ILogger<SnapshotService> logger)
{
    private static readonly string Version =
        typeof(SnapshotService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Folder next to the exe whose rule files override the embedded ones (null = embedded only).</summary>
    public string? RuleOverrideDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "rules");

    public async Task<SiteSnapshot> RunAsync(SiteProfile site, SnapshotOptions options, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var ruleSet = ruleRepository.Load(RuleOverrideDirectory);
        var engine = new RulesEngine(ruleSet.Rules);
        var nodes = new List<NodeSnapshot>();
        logger.LogInformation("Diagnosis of site {Site}: {Nodes} nodes, heavy={Heavy}", site.Name, site.Nodes.Count, options.IncludeHeavy);

        foreach (var node in site.Nodes)
        {
            progress?.Report(node.Name);
            nodes.Add(await RunNodeAsync(site, node, options, engine, progress, cancellationToken).ConfigureAwait(false));
        }

        var snapshot = new SiteSnapshot(Guid.NewGuid().ToString("N"), site, started, DateTimeOffset.UtcNow, options.IncludeHeavy, Version, ruleSet.Rules.Count, nodes);
        await store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Diagnosis of {Site} finished: {Findings} findings", site.Name, snapshot.AllFindings.Count());
        return snapshot;
    }

    private async Task<NodeSnapshot> RunNodeAsync(SiteProfile site, NodeProfile node, SnapshotOptions options, RulesEngine engine, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var matrix = await tester.TestAsync(site, node, cancellationToken).ConfigureAwait(false);
        var results = new List<CollectorResult>();
        var facts = new FactStore();
        AddNodeFacts(site, node, matrix, facts, DateTime.Now);

        if (node.Sql is { } sql && matrix.Has(CapabilityIds.SqlConnect))
        {
            var password = sql.Auth == SqlAuthKind.Sql ? await secrets.GetAsync(site.Id, node.Id, SecretKind.SqlPassword, cancellationToken).ConfigureAwait(false) : null;
            using var session = new SqlNodeSession(node.Id, sql, password);
            if (matrix.Facts.TryGetValue("sql.version", out var v) && SqlServerVersion.TryParse(v, out var version))
            {
                session.ServerVersion = version;
            }

            var context = new NodeContext(site, node, [session]);
            foreach (var collector in collectors.Where(c => (c.AppliesTo & node.Roles) != 0 || node.Roles == NodeRoles.None))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"{node.Name}: {collector.Id}");
                CollectorResult result;
                try
                {
                    result = await collector.CollectAsync(context, new CollectContext(IncludeHeavy: options.IncludeHeavy), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Collector {Collector} failed on {Node}", collector.Id, node.Name);
                    result = CollectorResult.FromStatus(collector.Id, node.Id, CollectorStatus.Error(ex.GetType().Name, ex.Message), TimeSpan.Zero);
                }

                results.Add(result);
                facts.SetAll(result.Facts);
            }
        }

        var evaluation = engine.Evaluate(node.Id, node.Roles, facts, DateTimeOffset.UtcNow);
        return new NodeSnapshot(node, matrix, results, facts.Snapshot(), evaluation.Findings, evaluation.Indeterminate, evaluation.Errors);
    }

    /// <summary>Facts that come from the profile and RSM itself rather than from a collector.</summary>
    internal static void AddNodeFacts(SiteProfile site, NodeProfile node, CapabilityMatrix matrix, FactStore facts, DateTime localNow)
    {
        facts.Set("node.role.data", node.Roles.HasFlag(NodeRoles.Data));
        facts.Set("node.role.app", node.Roles.HasFlag(NodeRoles.App));
        facts.Set("node.role.report", node.Roles.HasFlag(NodeRoles.Report));
        facts.Set("node.shared_box", node.Roles.HasFlag(NodeRoles.Data) && node.Roles.HasFlag(NodeRoles.App));
        facts.Set("rsm.in_working_hours", IsWorkingTime(site.Settings, localNow));
        if (matrix.Get(CapabilityIds.SqlSysadmin) is { } sysadmin && sysadmin.State is CapabilityState.Available or CapabilityState.NotApplicable)
        {
            facts.Set("rsm.login_is_sysadmin", sysadmin.IsAvailable);
        }
    }

    /// <summary>Working days are indexed Saturday = 0 … Friday = 6 (Iranian week); Thursday may end earlier.</summary>
    internal static bool IsWorkingTime(SiteSettings settings, DateTime localNow)
    {
        var day = ((int)localNow.DayOfWeek + 1) % 7;
        if (!settings.WorkingDays.Contains(day))
        {
            return false;
        }

        var time = TimeOnly.FromDateTime(localNow);
        var end = day == 5 ? settings.ThursdayEnd : settings.WorkdayEnd;
        return time >= settings.WorkdayStart && time < end;
    }
}
