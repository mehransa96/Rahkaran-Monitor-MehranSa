using RSM.Collectors.Sql.Builds;
using RSM.Collectors.Sql.Instance;
using RSM.Safety.Sql;

namespace RSM.Tests;

public class SqlCollectorLogicTests
{
    [Theory]
    [InlineData("16.0.4305.5", "CU", true, false, false)]           // latest CU + GDR
    [InlineData("16.0.4295.3", "CU", false, false, true)]           // latest CU, security GDR missing
    [InlineData("16.0.4135.4", "CU", false, true, false)]           // CU14: behind
    [InlineData("16.0.1210.3", "GDR", true, false, false)]          // GDR branch, latest
    [InlineData("16.0.1000.6", "GDR", false, true, false)]          // RTM
    [InlineData("15.0.4430.1", "CU", false, false, true)]
    [InlineData("14.0.3456.2", "CU", false, false, true)]
    public void Assesses_builds_against_the_catalogue(string build, string branch, bool latest, bool behind, bool securityOnly)
    {
        var a = KnownBuilds.Assess(SqlServerVersion.Parse(build), KnownBuilds.Embedded);

        a.Branch.Should().Be(branch);
        a.IsLatest.Should().Be(latest);
        (a.IsBehind && !a.MissingSecurityUpdateOnly).Should().Be(behind);
        a.MissingSecurityUpdateOnly.Should().Be(securityOnly);
    }

    [Fact]
    public void Sql2016_below_sp3_is_below_baseline_and_out_of_support()
    {
        var a = KnownBuilds.Assess(SqlServerVersion.Parse("13.0.5026.0"), KnownBuilds.Embedded);

        a.BelowBaseline.Should().BeTrue();
        a.IsBehind.Should().BeTrue();
        a.ExtendedSupportEnd.Should().Be(new DateOnly(2026, 7, 14));
    }

    [Fact]
    public void Newer_build_than_catalogue_is_flagged_not_reported_as_behind()
    {
        var a = KnownBuilds.Assess(SqlServerVersion.Parse("16.0.4400.1"), KnownBuilds.Embedded);

        a.NewerThanCatalog.Should().BeTrue();
        a.IsBehind.Should().BeFalse();
    }

    [Fact]
    public void Catalogue_is_dated() => KnownBuilds.Embedded.AsOf.Should().Be(new DateOnly(2026, 10, 13));

    [Theory]
    [InlineData("max server memory (MB)", "max_server_memory_mb")]
    [InlineData("max degree of parallelism", "max_degree_of_parallelism")]
    [InlineData("optimize for ad hoc workloads", "optimize_for_ad_hoc_workloads")]
    [InlineData("Ole Automation Procedures", "ole_automation_procedures")]
    [InlineData("xp_cmdshell", "xp_cmdshell")]
    public void Normalises_configuration_names(string name, string key) => SqlConfigurationCollector.Normalize(name).Should().Be(key);

    [Theory]
    [InlineData("DBCC SHRINKDATABASE(N'Rahkaran', 10)", true)]
    [InlineData("DBCC SHRINKFILE (N'Rahkaran_Data', 1024)", true)]
    [InlineData("DBCC SHRINKFILE (N'Rahkaran_log', 1024)", false)]
    [InlineData("BACKUP LOG Rahkaran TO DISK = N'x'", false)]
    [InlineData(null, false)]
    public void Detects_data_file_shrink_steps(string? command, bool shrink) => SqlAgentCollector.IsDataShrink(command).Should().Be(shrink);

    [Fact]
    public void Decodes_agent_history_time_and_duration()
    {
        var row = new SqlAgentCollector.HistoryRow { RunDate = 20261014, RunTime = 223015, RunDuration = 13005 };

        row.RunAt.Should().Be(new DateTime(2026, 10, 14, 22, 30, 15));
        row.DurationSeconds.Should().Be(3600 + 30 * 60 + 5);
    }

    [Theory]
    [InlineData(@"NT SERVICE\MSSQLSERVER", true)]
    [InlineData(@"NT AUTHORITY\SYSTEM", true)]
    [InlineData("##MS_PolicyTsqlExecutionLogin##", true)]
    [InlineData(@"CORP\dba", false)]
    [InlineData("sa", false)]
    public void Ignores_built_in_sysadmins(string name, bool builtIn) => SqlSecurityCollector.IsBuiltInPrincipal(name).Should().Be(builtIn);

    [Theory]
    [InlineData("localhost", true)]
    [InlineData(".", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("db01.corp.local", false)]
    public void Recognises_local_hosts(string host, bool local) => SqlErrorLogCollector.IsLocal(host).Should().Be(local);
}
