using FluentAssertions;
using RSM.Core.Capabilities;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;
using RSM.Core.Snapshots;
using RSM.Reporting;
using RSM.Reporting.Bundles;
using RSM.Reporting.Model;

namespace RSM.Tests;

/// <summary>
/// P5 acceptance: RSM run on two machines of one site (each with its own profile, so different node ids) produces two
/// bundles; importing both merges them into one site snapshot/report with per-node capability gaps.
/// </summary>
public sealed class BundleImportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 6, 0, 0, TimeSpan.Zero);

    private static NodeSnapshot Node(string id, string name, string host, NodeRoles roles, Dictionary<string, CapabilityState> caps, IReadOnlyList<CollectorResult> collectors,
        Dictionary<string, object?> facts)
    {
        var matrix = new CapabilityMatrix(id, T0);
        foreach (var (cap, state) in caps)
        {
            matrix.Set(cap, state, state == CapabilityState.Available ? null : "from this machine");
        }

        return new NodeSnapshot(new NodeProfile { Id = id, Name = name, Host = host, Roles = roles }, matrix, collectors, facts, [], [], []);
    }

    private static CollectorResult Collector(string id, string node, CollectorStatus status, params Fact[] facts) =>
        new(id, node, T0, TimeSpan.FromSeconds(1), status, null, [], facts, []);

    /// <summary>Taken on DB01: SQL Server fully visible; the App server's IIS needs local access, so it is missing.</summary>
    private static SiteSnapshot FromDataServer() => new("snap-db", new SiteProfile { Id = "siteA", Name = "Lab site", Nodes = [] }, T0, T0.AddMinutes(3), false, "1.5.0", 0,
    [
        Node("a-db", "DB01", "db01.lab.local", NodeRoles.Data,
            new() { [CapabilityIds.SqlConnect] = CapabilityState.Available, [CapabilityIds.SqlViewServerState] = CapabilityState.Available, [CapabilityIds.WinEventLog] = CapabilityState.Available },
            [Collector("sql.waits", "a-db", CollectorStatus.Ok, new Fact("sql.waits.ms_per_sec", 500.0), new Fact("sql.waits.signal_pct", 40.0))],
            new() { ["node.role.data"] = true, ["os.dns_host_name"] = "db01.lab.local", ["sql.waits.ms_per_sec"] = 500.0, ["sql.waits.signal_pct"] = 40.0 }),
        Node("a-app", "APP01", "app01.lab.local", NodeRoles.App,
            new() { [CapabilityIds.IisConfig] = CapabilityState.NeedsElevation, [CapabilityIds.WinEventLog] = CapabilityState.Denied },
            [Collector("iis.config", "a-app", CollectorStatus.Unavailable("IIS configuration is read locally only", CapabilityIds.IisConfig))],
            new() { ["node.role.app"] = true }),
    ]);

    /// <summary>Taken on APP01 (elevated, local IIS): IIS visible; no SQL credentials on that machine.</summary>
    private static SiteSnapshot FromAppServer() => new("snap-app", new SiteProfile { Id = "siteB", Name = "Lab site", Nodes = [] }, T0.AddMinutes(10), T0.AddMinutes(12), false, "1.5.0", 0,
    [
        Node("b-app", "App 01", "APP01", NodeRoles.App,
            new() { [CapabilityIds.IisConfig] = CapabilityState.Available, [CapabilityIds.WinEventLog] = CapabilityState.Denied },
            [Collector("iis.config", "b-app", CollectorStatus.Ok, new Fact("iis.site_count", 3.0), new Fact("iis.rahkaran_site_count", 1.0))],
            new() { ["node.role.app"] = true, ["os.computer_name"] = "APP01", ["iis.site_count"] = 3.0 }),
        Node("b-db", "SQL", "db01", NodeRoles.Data,
            new() { [CapabilityIds.SqlConnect] = CapabilityState.NotConfigured },
            [Collector("sql.waits", "b-db", CollectorStatus.Unavailable("No SQL credentials on this machine", CapabilityIds.SqlConnect))],
            new() { ["node.role.data"] = true }),
    ]);

    private static ImportedBundle RoundTrip(string name, SiteSnapshot snapshot, bool mask = false) =>
        BundleReader.Read(name, ReportExporter.BuildBundle(snapshot, mask, T0.AddHours(1)));

    [Fact]
    public void Two_bundles_from_two_nodes_merge_into_one_site_report_with_per_node_capability_gaps()
    {
        var bundles = new[] { RoundTrip("db01.zip", FromDataServer()), RoundTrip("app01.zip", FromAppServer()) };

        var result = BundleMerger.Merge(bundles, new RulesEngine(LabSnapshot.Rules), [], T0.AddHours(2));

        result.Snapshot.Nodes.Should().HaveCount(2, "DB01 and APP01 were matched across bundles by host / machine name");
        var db = result.Snapshot.Nodes.Single(n => n.Node.Roles.HasFlag(NodeRoles.Data));
        var app = result.Snapshot.Nodes.Single(n => n.Node.Roles.HasFlag(NodeRoles.App));

        db.Capabilities.Get(CapabilityIds.SqlConnect)!.State.Should().Be(CapabilityState.Available);
        db.Collectors.Single(c => c.CollectorId == "sql.waits").Status.Kind.Should().Be(CollectorStatusKind.Ok);
        db.Findings.Should().Contain(f => f.RuleId == "SQL-CPU-001", "rules are evaluated again on the merged facts");
        app.Capabilities.Get(CapabilityIds.IisConfig)!.State.Should().Be(CapabilityState.Available);
        app.Facts.Should().ContainKey("iis.site_count").WhoseValue.Should().Be(3.0);

        result.Nodes.Single(n => n.NodeId == app.Node.Id).Gaps.Select(g => (g.Id, g.State)).Should().Equal((CapabilityIds.WinEventLog, CapabilityState.Denied));
        result.Nodes.Single(n => n.NodeId == db.Node.Id).Gaps.Should().BeEmpty();
        result.Nodes.Should().OnlyContain(n => n.FromBundles.Count == 2);
        result.Snapshot.StartedUtc.Should().Be(T0);
        result.Snapshot.FinishedUtc.Should().Be(T0.AddMinutes(12));
        result.Snapshot.Sources!.Select(s => s.SnapshotId).Should().Equal("snap-db", "snap-app");

        foreach (var language in new[] { UiLanguage.En, UiLanguage.Fa })
        {
            var html = ReportExporter.RenderHtml(result.Snapshot, LabSnapshot.RuleDefinitions, new ReportOptions(language, false, T0.AddHours(2), TimeZoneInfo.Utc));
            html.Should().Contain(ReportText.MergedFrom.Get(language)).And.Contain("snap-app").And.Contain(ReportText.CapabilityGaps.Get(language));
        }

        // The merged snapshot exports and imports again like any other.
        RoundTrip("merged.zip", result.Snapshot).Snapshot.Nodes.Should().HaveCount(2);
    }

    [Fact]
    public void Imported_facts_are_plain_values_and_capabilities_survive_the_round_trip()
    {
        var bundle = RoundTrip("db01.zip", FromDataServer());

        bundle.Snapshot.Nodes[0].Facts["sql.waits.ms_per_sec"].Should().Be(500.0);
        bundle.Snapshot.Nodes[0].Facts["node.role.data"].Should().Be(true);
        bundle.Snapshot.Nodes[1].Capabilities.Get(CapabilityIds.IisConfig)!.State.Should().Be(CapabilityState.NeedsElevation);
        bundle.Masked.Should().BeFalse();
        bundle.SiteName.Should().Be("Lab site");
    }

    [Fact]
    public void Lab_snapshot_bundle_imports_with_the_same_findings_when_evaluated_again()
    {
        var original = LabSnapshot.Build();
        var bundle = RoundTrip("lab.zip", original);

        var merged = BundleMerger.Merge([bundle], new RulesEngine(LabSnapshot.Rules), [], LabSnapshot.Started);

        merged.Snapshot.Nodes.Single().Findings.Select(f => (f.RuleId, f.Instance)).Should()
            .BeEquivalentTo(original.Nodes.Single().Findings.Select(f => (f.RuleId, f.Instance)));
    }

    [Theory]
    [InlineData("not a zip")]
    [InlineData("wrong format")]
    [InlineData("missing snapshot")]
    public void Foreign_files_are_refused_with_a_reason(string kind)
    {
        byte[] bytes = kind switch
        {
            "not a zip" => "hello"u8.ToArray(),
            "wrong format" => Zip(("manifest.json", "{\"format\":\"other\",\"formatVersion\":1}"), ("snapshot.json", "{}")),
            _ => Zip(("manifest.json", "{\"format\":\"rsm-bundle\",\"formatVersion\":1}")),
        };

        FluentActions.Invoking(() => BundleReader.Read("x.zip", bytes)).Should().Throw<BundleFormatException>().Which.Message.Should().NotBeNullOrWhiteSpace();
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(System.Text.Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }
}
