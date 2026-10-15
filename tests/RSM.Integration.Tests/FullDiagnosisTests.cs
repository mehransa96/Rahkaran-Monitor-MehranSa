using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Snapshots;
using RSM.Hosting;
using RSM.Reporting;
using RSM.Reporting.Model;
using RSM.Safety.Files;
using RSM.Storage.Profiles;
using Xunit.Abstractions;

namespace RSM.Integration.Tests;

/// <summary>
/// Read-only proof (Phase 1 part): a full diagnosis with the least-privilege login against a SQL Server that CI
/// seeded with known deviations (tests/integration/seed-deviations.sql). CI fingerprints the server as sa before and
/// after this test and fails if anything changed.
/// </summary>
public sealed class FullDiagnosisTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rsm-it-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [EnvFact("RSM_IT_SEEDED")]
    public async Task Full_diagnosis_reports_the_seeded_deviations_without_errors()
    {
        var cs = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RSM_IT_SQL"));
        var node = new NodeProfile
        {
            Id = "it-node",
            Name = "IT-SQL",
            Host = "localhost",
            Roles = NodeRoles.Data,
            Sql = new SqlEndpoint { Server = cs.DataSource, UserName = cs.UserID, TrustServerCertificate = cs.TrustServerCertificate, Encrypt = SqlEncryptMode.Optional },
        };
        var site = new SiteProfile { Id = "it-site", Name = "CI", Nodes = [node], Settings = new SiteSettings { RahkaranDatabases = ["Rahkaran"] } };

        var services = new ServiceCollection().AddLogging();
        RsmHost.AddRsmServices(services, new DataFolder(_root, isPortable: true));
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<SecretStore>().SetAsync(site.Id, node.Id, SecretKind.SqlPassword, cs.Password, remember: false, CancellationToken.None);
        var diagnosis = provider.GetRequiredService<SnapshotService>();
        diagnosis.RuleOverrideDirectory = null;

        var snapshot = await diagnosis.RunAsync(site, new SnapshotOptions(IncludeHeavy: true), null, CancellationToken.None);

        var result = snapshot.Nodes.Single();
        foreach (var c in result.Collectors)
        {
            output.WriteLine($"collector {c.CollectorId}: {c.Status.Kind} {c.Status.Reason} {c.Status.Detail}");
        }

        foreach (var f in result.Findings.OrderBy(f => f.RuleId, StringComparer.Ordinal))
        {
            output.WriteLine($"finding {f.RuleId} [{f.Instance}] {f.Severity}: {f.Title.En}");
        }

        foreach (var i in result.Indeterminate)
        {
            output.WriteLine($"indeterminate {i.RuleId} [{i.Instance}] missing {i.MissingFact}");
        }

        // P1 snapshot collectors + P2 live SQL collectors (Windows collectors need a Windows host and are skipped here).
        result.Collectors.Select(c => c.CollectorId).Should().BeEquivalentTo(
        [
            "sql.inventory", "sql.configuration", "sql.storage", "sql.errorlog", "sql.agent", "sql.security", "sql.databases",
            "sql.perf", "sql.cpu", "sql.activity", "sql.deadlocks", "sql.querystats", "sql.ha", "rsm.overhead.sql", "sql.querystore",
        ]);
        result.Collectors.Should().NotContain(c => c.Status.Kind == CollectorStatusKind.Error, "missing permissions must degrade, never fail");
        result.RuleErrors.Should().BeEmpty();
        result.Facts["rk.detected_by"].Should().Be("setting");
        result.Facts["db[Rahkaran].recovery_model"].Should().Be("SIMPLE");

        var fired = result.Findings.Select(f => f.Instance is null ? f.RuleId : $"{f.RuleId}[{f.Instance}]").ToList();
        fired.Should().Contain(
        [
            "SQL-CFG-004", "SQL-CFG-005", "SQL-CFG-007", "SQL-AGT-003",
            "DB-RM-001[Rahkaran]", "DB-OPT-001[Rahkaran]", "DB-OPT-003[Rahkaran]", "DB-FILE-001[Rahkaran]", "DB-BKP-001[Rahkaran]",
        ]);
        fired.Should().NotContain("SQL-SEC-009", "the integration login is not sysadmin");

        var rules = provider.GetRequiredService<RuleRepository>().Load(null).Rules.Select(r => r.Definition).ToList();
        foreach (var language in new[] { UiLanguage.Fa, UiLanguage.En })
        {
            var html = ReportExporter.RenderHtml(snapshot, rules, new ReportOptions(language, Mask: false, DateTimeOffset.UtcNow));
            html.Should().Contain("DB-RM-001").And.Contain("SQL-CFG-005").And.NotContain("<script");
        }

        var exported = await provider.GetRequiredService<ReportService>().ExportBundleAsync(snapshot, mask: true, CancellationToken.None);
        File.Exists(exported).Should().BeTrue();
    }
}
