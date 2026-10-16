using RSM.Collectors.Iis;
using RSM.Collectors.Rahkaran;
using RSM.Collectors.Sql.Live;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Parsers.Iis;
using RSM.Safety.Files;

namespace RSM.Tests;

/// <summary>§6 topology on the P4 acceptance lab: ARR → 2 App → Data/Redis (+ a separate Report node).</summary>
public class TopologyTests
{
    private static readonly SiteProfile Lab = new()
    {
        Id = "lab", Name = "lab",
        Nodes =
        [
            new NodeProfile { Id = "arr", Name = "arr01", Host = "arr01.corp.local", Roles = NodeRoles.Arr },
            new NodeProfile { Id = "app1", Name = "app01", Host = "app01.corp.local", Roles = NodeRoles.App },
            new NodeProfile { Id = "app2", Name = "app02", Host = "10.0.0.22", Roles = NodeRoles.App },
            new NodeProfile { Id = "db", Name = "db01", Host = "db01.corp.local", Roles = NodeRoles.Data | NodeRoles.Redis },
            new NodeProfile { Id = "rpt", Name = "rpt01", Host = "rpt01.corp.local", Roles = NodeRoles.Report },
        ],
    };

    private static Dictionary<string, IReadOnlyDictionary<string, object?>> Facts(int app1Sessions = 30, int app2Sessions = 10, string rptComputer = "RPT01") => new(StringComparer.Ordinal)
    {
        ["arr"] = new Dictionary<string, object?>
        {
            ["farm[RahkaranAppFarm_Tehran].weights"] = "app01.corp.local=100|10.0.0.22=100",
            ["os.computer_name"] = "ARR01",
        },
        ["app1"] = new Dictionary<string, object?> { ["os.computer_name"] = "APP01", ["os.ip_addresses"] = "10.0.0.21|fe80::1", ["site[AuxWebsite1].role"] = "Aux", ["site[AuxWebsite2].role"] = "Aux", ["os.numa_nodes"] = 2, ["rk.app_segments"] = "Rahkaran73" },
        ["app2"] = new Dictionary<string, object?> { ["os.computer_name"] = "APP02", ["os.ip_addresses"] = "10.0.0.22", ["rk.app_segments"] = "Rahkaran72" },
        ["db"] = new Dictionary<string, object?>
        {
            ["os.computer_name"] = "DB01", ["sql.machine_name"] = "DB01", ["os.ip_addresses"] = "10.0.0.30",
            ["topo.sql_sessions_total"] = app1Sessions + app2Sessions + 6,
            ["sqlclient[APP01].sessions"] = app1Sessions, ["sqlclient[APP01].addresses"] = "10.0.0.21", ["sqlclient[APP01].programs"] = "Core Microsoft SqlClient Data Provider",
            ["sqlclient[10.0.0.22].sessions"] = app2Sessions, ["sqlclient[10.0.0.22].addresses"] = "10.0.0.22",
            ["sqlclient[DBA-LAPTOP].sessions"] = 4, ["sqlclient[DBA-LAPTOP].addresses"] = "10.0.5.7",
            ["sqlclient[DB01].sessions"] = 2,
            ["redisclient[10.0.0.21].connections"] = 8, ["redisclient[10.0.0.22].connections"] = 7,
        },
        ["rpt"] = new Dictionary<string, object?> { ["os.computer_name"] = rptComputer, ["os.ip_addresses"] = rptComputer == "DB01" ? "10.0.0.30" : "10.0.0.40" },
    };

    [Fact]
    public void Map_of_the_three_tier_lab_has_arr_app_sql_and_redis_edges()
    {
        var map = TopologyBuilder.Build(Lab, Facts());

        map.Edges.Should().ContainSingle(e => e.Kind == TopologyEdgeKind.Http).Which.To.Should().Be("arr", "users reach the ARR tier");
        map.Edges.Where(e => e.Kind == TopologyEdgeKind.ArrFarm).Select(e => (e.From, e.To, e.Label, e.Count))
            .Should().BeEquivalentTo([("arr", "app1", "RahkaranAppFarm_Tehran", (double?)100), ("arr", "app2", "RahkaranAppFarm_Tehran", (double?)100)]);
        map.Edges.Where(e => e.Kind == TopologyEdgeKind.Sql).Select(e => (e.From, e.To, e.Count))
            .Should().BeEquivalentTo([("app1", "db", (double?)30), ("app2", "db", (double?)10), ("@host:dba-laptop", "db", (double?)4)],
                "host_name and client address both resolve; the server's own sessions are not an edge");
        map.Edges.Where(e => e.Kind == TopologyEdgeKind.Redis).Select(e => (e.From, e.To, e.Count))
            .Should().BeEquivalentTo([("app1", "db", (double?)8), ("app2", "db", (double?)7)]);
        map.Edges.Where(e => e.Kind == TopologyEdgeKind.Declared).Should().OnlyContain(e => e.To == "rpt" && e.Label == "SSRS" && !e.Observed).And.HaveCount(2);
        map.Nodes.Should().Contain(n => n.Id == "@host:dba-laptop" && !n.Profiled);
        map.Nodes.Single(n => n.Id == "app1").Details.Should().Contain(["AuxWebsite1, AuxWebsite2", "NUMA × 2"]);
    }

    [Fact]
    public void Without_an_arr_tier_users_reach_the_app_nodes()
    {
        var site = Lab with { Nodes = Lab.Nodes.Where(n => n.Id != "arr").ToList() };
        TopologyBuilder.Build(site, Facts()).Edges.Where(e => e.Kind == TopologyEdgeKind.Http).Select(e => e.To).Should().BeEquivalentTo(["app1", "app2"]);
    }

    private static Dictionary<string, object?> Analyze(string node, Dictionary<string, IReadOnlyDictionary<string, object?>>? facts = null) =>
        new RahkaranSiteAnalyzer().Analyze(Lab, facts ?? Facts())[node].ToDictionary(f => f.Key, f => f.Value);

    [Fact]
    public void Session_imbalance_is_measured_against_the_arr_weights()
    {
        var db = Analyze("db");

        db["apphost[app01].sql_share_pct"].Should().Be(75.0);
        db["apphost[app02].sql_share_pct"].Should().Be(25.0);
        db["apphost[app02].expected_share_pct"].Should().Be(50.0);
        db["apphost[app02].share_deviation_pct"].Should().Be(50.0, "RK-002 fires above 30 %");
        db["apphost[app02].share_basis"].Should().Be("ARR weights");
        Analyze("db", Facts(app1Sessions: 21, app2Sessions: 19))["apphost[app02].share_deviation_pct"].Should().Be(5.0);
        Analyze("db", Facts(app1Sessions: 5, app2Sessions: 3)).Should().NotContainKey("apphost[app02].share_deviation_pct", "too few sessions to judge");
    }

    [Fact]
    public void Path_segment_drift_and_same_machine_are_detected()
    {
        var app = Analyze("app1");
        app["rk.version_drift"].Should().Be(true);
        app["rk.version_source"].Should().Be("path segment");
        app["rk.version_drift_detail"].Should().Be("app01: Rahkaran73; app02: Rahkaran72");

        Analyze("rpt")["rk.report_data_same_machine"].Should().Be(false);
        var same = Analyze("rpt", Facts(rptComputer: "DB01"));
        same["rk.report_data_same_machine"].Should().Be(true);
        same["rk.report_data_same_machine_with"].Should().Be("db01");
    }

    [Fact]
    public void Version_file_fingerprint_wins_over_path_segments()
    {
        var facts = Facts();
        facts["app1"] = new Dictionary<string, object?>(facts["app1"]) { ["rk.version_fingerprint"] = "AAAA", ["rk.app_segments"] = "Rahkaran73" };
        facts["app2"] = new Dictionary<string, object?>(facts["app2"]) { ["rk.version_fingerprint"] = "AAAA", ["rk.app_segments"] = "Rahkaran73" };

        var app = Analyze("app2", facts);
        app["rk.version_drift"].Should().Be(false);
        app["rk.version_source"].Should().Be("file");
    }

    [Fact]
    public void Sql_clients_are_grouped_by_host_or_address()
    {
        var hosts = SqlClientsCollector.Group(
        [
            new SqlClientRow { HostName = "APP01", ProgramName = "w3wp", DatabaseName = "RK", ClientAddress = "10.0.0.21", Sessions = 20, Running = 2 },
            new SqlClientRow { HostName = "app01", ProgramName = "SQLAgent", DatabaseName = "msdb", ClientAddress = "10.0.0.21", Sessions = 3 },
            new SqlClientRow { HostName = "", ProgramName = "", ClientAddress = "10.0.0.22", Sessions = 5 },
        ]);

        hosts.Should().HaveCount(2);
        hosts[0].Should().BeEquivalentTo(new SqlClientHost("APP01", 23, 2, ["10.0.0.21"], ["w3wp", "SQLAgent"], ["RK", "msdb"]));
        hosts[1].Host.Should().Be("10.0.0.22");
    }

    private sealed class HashFiles(Dictionary<string, string> files) : IServerFiles
    {
        public string? Map(string? serverPath) => ServerPaths.ToAccessPath(serverPath, null);

        public Task<string> ReadAllTextAsync(string accessPath, CancellationToken cancellationToken) => Task.FromResult(files[accessPath]);

        public Task<(string Text, bool Truncated)> ReadTailAsync(string accessPath, long maxBytes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<(string Text, long NextOffset)> ReadFromAsync(string accessPath, long offset, long maxBytes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IReadOnlyList<ServerFileInfo> List(string accessDirectory, string pattern, int max = 1000) => [];

        public bool Exists(string accessPath) => files.ContainsKey(accessPath);

        public Task<string?> Sha256Async(string accessPath, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(files[accessPath]))));
    }

    [Fact]
    public async Task Version_file_is_fingerprinted_under_the_application_folder_and_traversal_is_refused()
    {
        var config = ApplicationHostConfigParser.Parse("""
            <configuration><system.applicationHost><sites>
              <site name="AuxWebsite1" id="2"><application path="/"><virtualDirectory path="/" physicalPath="C:\inetpub\aux1" /></application>
                <application path="/Rahkaran73" applicationPool="RK"><virtualDirectory path="/" physicalPath="D:\Rahkaran\73" /></application></site>
            </sites></system.applicationHost></configuration>
            """);
        var files = new HashFiles(new(StringComparer.OrdinalIgnoreCase) { [@"D:\Rahkaran\73\bin\Main.dll"] = "build-7.3.1" });

        var output = new LiveOutput();
        await IisConfigCollector.FingerprintVersionFileAsync(@"bin/Main.dll", config, files, output, [], CancellationToken.None);
        output.Facts.Should().Contain(new Fact("rk.version_file_found", true));
        output.Facts.Single(f => f.Key == "rk.version_fingerprint").Value.Should().BeOfType<string>().Which.Should().HaveLength(16);

        var problems = new List<string>();
        var refused = new LiveOutput();
        await IisConfigCollector.FingerprintVersionFileAsync(@"..\..\Windows\win.ini", config, files, refused, problems, CancellationToken.None);
        refused.Facts.Should().BeEmpty();
        problems.Should().ContainSingle().Which.Should().Contain("relative path");
    }
}
