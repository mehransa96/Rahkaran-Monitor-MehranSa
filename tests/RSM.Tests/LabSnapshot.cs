using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RSM.Core.Capabilities;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;
using RSM.Core.Snapshots;
using RSM.Hosting;

namespace RSM.Tests;

/// <summary>
/// A complete snapshot built from <c>Fixtures/lab/lab-facts.json</c> (the facts a SQL Server 2022 lab with the CI
/// seeded deviations produces) evaluated by the shipped rules. Names are deliberately distinctive so masking tests
/// can prove none of them survive.
/// </summary>
internal static class LabSnapshot
{
    public const string SiteName = "Tehran HQ Lab";
    public const string NodeName = "DB-Node-Tehran";
    public const string Host = "lab-sql01.corp.example";
    public const string SqlServer = @"LAB-SQL01\RK,1433";
    public const string RahkaranDb = "RK_Tehran1405";
    public const string PayloadOnlySecret = @"LAB\secret.person";
    public const string Notes = "Contact: Mr. Example 0912-000-0000";

    /// <summary>Every name/address in the lab that must disappear from masked exports.</summary>
    public static readonly string[] SensitiveTokens =
    [
        SiteName, NodeName, Host, "LAB-SQL01", "lab-sql01", RahkaranDb, "rsm_monitor", "svc_sql", "former.employee",
        "dba.admin", "olduser", "nightly shrink", "192.168.10.44", PayloadOnlySecret, "secret.person", "0912-000-0000",
    ];

    public static readonly DateTimeOffset Started = new(2026, 10, 14, 6, 30, 0, TimeSpan.Zero);

    public static IReadOnlyList<CompiledRule> Rules { get; } = new RuleRepository(NullLogger<RuleRepository>.Instance).Load(null).Rules;

    public static IReadOnlyList<RuleDefinition> RuleDefinitions { get; } = Rules.Select(r => r.Definition).ToList();

    public static SiteSnapshot Build(Action<FactStore>? tweak = null)
    {
        var node = new NodeProfile
        {
            Id = "node-1",
            Name = NodeName,
            Host = Host,
            Roles = NodeRoles.Data,
            Sql = new SqlEndpoint { Server = SqlServer, UserName = "rsm_monitor" },
        };
        var site = new SiteProfile
        {
            Id = "site-1",
            Name = SiteName,
            Notes = Notes,
            Nodes = [node],
            Settings = new SiteSettings { RahkaranDatabases = [RahkaranDb] },
        };

        var matrix = new CapabilityMatrix(node.Id, Started);
        matrix.Set(CapabilityIds.SqlConnect, CapabilityState.Available);
        matrix.Set(CapabilityIds.SqlViewServerState, CapabilityState.Available);
        matrix.Set(CapabilityIds.SqlMsdbRead, CapabilityState.Available);
        matrix.Set(CapabilityIds.SqlErrorLog, CapabilityState.Denied, "Login rsm_monitor lacks securityadmin; read via \\\\lab-sql01.corp.example\\D$ instead");
        matrix.Set(CapabilityIds.SqlSysadmin, CapabilityState.NotApplicable);
        matrix.Set(CapabilityIds.WinCim, CapabilityState.NotImplemented);
        matrix.Facts["sql.server_name"] = @"LAB-SQL01\RK";
        matrix.Facts["sql.login"] = "rsm_monitor";

        var facts = new FactStore();
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lab", "lab-facts.json"))))
        {
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                facts.Set(p.Name, ToClr(p.Value));
            }
        }

        SnapshotService.AddNodeFacts(site, node, matrix, facts, new DateTime(2026, 10, 17, 10, 0, 0)); // Saturday, working hours
        tweak?.Invoke(facts);

        var evaluation = new RulesEngine(Rules).Evaluate(node.Id, node.Roles, facts, Started.AddMinutes(2));
        var collectors = new List<CollectorResult>
        {
            new("sql.inventory", node.Id, Started, TimeSpan.FromSeconds(1.2), CollectorStatus.Ok, null, [], [], []),
            new("sql.errorlog", node.Id, Started, TimeSpan.FromSeconds(0.4), CollectorStatus.Partial("Read the archived logs over \\\\lab-sql01.corp.example\\D$"), null, [], [], []),
            new("sql.security", node.Id, Started, TimeSpan.FromSeconds(0.3), CollectorStatus.Ok,
                new Dictionary<string, object?> { ["logins"] = new[] { PayloadOnlySecret } }, [], [], []),
        };
        var nodeSnapshot = new NodeSnapshot(node, matrix, collectors, facts.Snapshot(), evaluation.Findings, evaluation.Indeterminate, evaluation.Errors);
        return new SiteSnapshot("lab-snapshot", site, Started, Started.AddMinutes(2), false, "1.0.0-test", Rules.Count, [nodeSnapshot]);
    }

    private static object? ToClr(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Array => e.EnumerateArray().Select(ToClr).ToList(),
        _ => null,
    };
}
