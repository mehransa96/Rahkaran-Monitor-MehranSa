using Microsoft.Extensions.Logging.Abstractions;
using RSM.Collectors.Redis;
using RSM.Collectors.Sql.Capabilities;
using RSM.Core.Capabilities;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;
using RSM.Core.Snapshots;
using RSM.Hosting;
using RSM.Safety.Files;
using RSM.Safety.Secrets;
using RSM.Safety.Sql;
using RSM.Storage.Profiles;
using RSM.Storage.Snapshots;

namespace RSM.Tests;

public sealed class SnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rsm-snap-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("2026-10-17 10:00", true)]   // Saturday morning
    [InlineData("2026-10-17 07:59", false)]  // before start
    [InlineData("2026-10-22 12:00", true)]   // Thursday before 13:00
    [InlineData("2026-10-22 14:00", false)]  // Thursday afternoon
    [InlineData("2026-10-23 10:00", false)]  // Friday
    public void Working_hours_follow_the_iranian_week(string local, bool expected) =>
        SnapshotService.IsWorkingTime(new SiteSettings(), DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(expected);

    [Fact]
    public void Node_facts_describe_roles_and_rsm_login()
    {
        var facts = new FactStore();
        var matrix = new CapabilityMatrix("n", DateTimeOffset.UtcNow);
        matrix.Set(CapabilityIds.SqlSysadmin, CapabilityState.NotApplicable);
        var node = new NodeProfile { Id = "n", Name = "N", Host = "h", Roles = NodeRoles.Data | NodeRoles.App };

        SnapshotService.AddNodeFacts(new SiteProfile { Id = "s", Name = "S" }, node, matrix, facts, new DateTime(2026, 10, 17, 10, 0, 0));

        facts.TryGet("node.shared_box", out var shared).Should().BeTrue();
        shared.Should().Be(true);
        facts.TryGet("rsm.login_is_sysadmin", out var sa).Should().BeTrue();
        sa.Should().Be(false);
    }

    [Fact]
    public void Embedded_rules_load_and_overrides_replace_by_id()
    {
        var overrides = Path.Combine(_root, "rules");
        Directory.CreateDirectory(overrides);
        var original = File.ReadAllText(Path.Combine(RepoRoot(), "rules", "SQL-CFG-005.json"));
        File.WriteAllText(Path.Combine(overrides, "SQL-CFG-005.json"), original.Replace("!= 4", "!= 8", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(overrides, "broken.json"), "{ not json");

        var set = new RuleRepository(NullLogger<RuleRepository>.Instance).Load(overrides);

        set.Rules.Should().HaveCount(Directory.GetFiles(Path.Combine(RepoRoot(), "rules"), "*.json").Length);
        set.OverriddenIds.Should().Equal("SQL-CFG-005");
        set.Rules.Single(r => r.Id == "SQL-CFG-005").Definition.When.Should().Contain("!= 8");
        set.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task Diagnosis_without_sql_runs_rules_and_is_stored()
    {
        var folder = new DataFolder(_root, isPortable: true);
        var secrets = new SecretStore(folder, new NoPersistenceSecretProtector());
        var executor = new SafeSqlExecutor(QueryCatalog.Load(RsmHost.CatalogAssemblies));
        var tester = new ConnectionTester(new SqlCapabilityProbe(executor), new RedisCapabilityProbe(), secrets, NullLogger<ConnectionTester>.Instance);
        var store = new SnapshotStore(folder);
        var service = new SnapshotService(tester, new NodeTransportOpener(secrets), [], [], new RuleRepository(NullLogger<RuleRepository>.Instance), store, NullLogger<SnapshotService>.Instance)
        {
            RuleOverrideDirectory = null,
        };
        var site = new SiteProfile { Id = "site1", Name = "Lab", Nodes = [new NodeProfile { Id = "app1", Name = "APP1", Host = "localhost", Roles = NodeRoles.App }] };

        var snapshot = await service.RunAsync(site, new SnapshotOptions(), null, CancellationToken.None);

        snapshot.Nodes.Should().ContainSingle();
        snapshot.RuleCount.Should().BeGreaterThan(60);
        snapshot.Nodes[0].Capabilities.Get(CapabilityIds.SqlConnect)!.State.Should().Be(CapabilityState.NotApplicable);
        var list = await store.ListAsync("site1", 10, CancellationToken.None);
        list.Should().ContainSingle().Which.Id.Should().Be(snapshot.Id);
        (await store.LoadJsonAsync(snapshot.Id, CancellationToken.None)).Should().Contain("\"name\":\"Lab\"").And.Contain("APP1").And.NotContain("allFindings");
    }

    [Fact]
    public async Task Snapshot_store_keeps_findings_counts()
    {
        var store = new SnapshotStore(new DataFolder(_root, isPortable: true));
        var finding = new Finding("X-1", Severity.Critical, Layer.Sql, "n1", null, new("ع", "t"), new("ع", "e"), new("ع", "r"), [], new SourceRef(SourceKind.General, "General guidance"), true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var node = new NodeSnapshot(new NodeProfile { Id = "n1", Name = "N", Host = "h" }, new CapabilityMatrix("n1", DateTimeOffset.UtcNow), [], new Dictionary<string, object?>(), [finding], [], []);
        var snapshot = new SiteSnapshot("abc", new SiteProfile { Id = "s1", Name = "S" }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, "test", 1, [node]);

        await store.SaveAsync(snapshot, CancellationToken.None);

        (await store.ListAsync(null, 5, CancellationToken.None)).Single().Critical.Should().Be(1);
        (await store.ListAsync("other-site", 5, CancellationToken.None)).Should().BeEmpty();
        (await store.LoadJsonAsync("missing", CancellationToken.None)).Should().BeNull();
        await FluentActions.Awaiting(() => store.LoadJsonAsync("../../etc/passwd", CancellationToken.None)).Should().ThrowAsync<ArgumentException>();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RSM.sln")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
