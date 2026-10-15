using System.Text.Json;
using RSM.Parsers.Iis;

namespace RSM.Tests;

public class IisConfigParserTests
{
    private static string Fixture(params string[] path) => File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "Fixtures", .. path]));

    private static ApplicationHostConfig AppHost() => ApplicationHostConfigParser.Parse(Fixture("iis", "applicationHost.config"));

    [Fact]
    public void Sites_carry_bindings_applications_log_fields_and_rahkaran_roles()
    {
        var config = AppHost();

        config.Sites.Select(s => s.Name).Should().Equal("Default Web Site", "AuxWebsite1", "AuxWebsite2", "AppRouterWebsite");
        config.Sites.Select(s => s.RahkaranRole).Should().Equal(null, "Aux", "Aux", "AppRouter");

        var defaultSite = config.Sites[0];
        defaultSite.AutoStart.Should().BeFalse();
        defaultSite.LogFields.Should().Equal(ApplicationHostConfigParser.DefaultLogFields, "no logExtFileFlags means the IIS default fields");
        defaultSite.LogsTimeTaken.Should().BeTrue();
        defaultSite.LogDirectory.Should().Be(@"%SystemDrive%\inetpub\logs\LogFiles");
        defaultSite.AppPools.Should().Equal("DefaultAppPool");

        config.Sites[1].LogDirectory.Should().Be(@"D:\IISLogs");
        config.Sites[1].LogsTimeTaken.Should().BeTrue();
        config.Sites[2].LogsTimeTaken.Should().BeFalse("AuxWebsite2 logs without time-taken");

        var router = config.Sites[3];
        router.Applications.Select(a => a.Path).Should().Equal("/", "/Rahkaran73");
        router.RootPhysicalPath.Should().Be(@"D:\Rahkaran\Router");
        var https = router.Bindings.Should().ContainSingle(b => b.Protocol == "https").Subject;
        https.Should().Be(new IisBinding("https", "10.0.0.5:443:rk.example.local", "10.0.0.5", 443, "rk.example.local", "AB12CD34", "My"));
        router.Bindings.Should().Contain(b => b.Protocol == "net.tcp" && b.Port == null);
        config.Sites[1].Bindings.Single().Should().Match<IisBinding>(b => b.IpAddress == null && b.Port == 8081 && b.Host == null);
    }

    [Fact]
    public void Pools_apply_iis_defaults_and_read_numa_settings_without_secrets()
    {
        var pools = AppHost().AppPools.ToDictionary(p => p.Name);

        var def = pools["DefaultAppPool"];
        def.ManagedRuntimeVersion.Should().Be("v4.0");
        def.PipelineMode.Should().Be("Integrated");
        def.IdleTimeout.Should().Be(TimeSpan.FromMinutes(20));
        def.PeriodicRestart.Should().Be(TimeSpan.FromMinutes(1740), "the IIS default recycle is every 29 h");
        def.QueueLength.Should().Be(1000);
        def.MaxProcesses.Should().Be(1);
        def.RapidFailProtection.Should().BeTrue();
        def.NumaNodeAssignment.Should().Be("MostAvailableMemory");

        var aux = pools["AuxPool1"];
        aux.NoManagedCode.Should().BeTrue();
        aux.StartMode.Should().Be("AlwaysRunning");
        aux.IdleTimeout.Should().Be(TimeSpan.Zero);
        aux.PeriodicRestart.Should().Be(TimeSpan.Zero);
        aux.RestartSchedule.Should().Equal(TimeSpan.FromHours(3));
        aux.NumaNodeAssignment.Should().Be("WindowsScheduling");
        aux.NumaNodeAffinityMode.Should().Be("Hard");
        aux.SmpAffinitized.Should().BeTrue();

        pools["AuxPool2"].IdentityType.Should().Be("SpecificUser");
        pools["AuxPool2"].IdentityUserName.Should().Be(@"CORP\svc_rk");
        pools["RouterPool"].Should().Match<IisAppPool>(p => p.Enable32Bit && p.MaxProcesses == 2 && p.QueueLength == 5000 && !p.NoManagedCode);

        JsonSerializer.Serialize(AppHost()).Should().NotContain("secretBlob").And.NotContain("[enc:", "encrypted pool passwords are never read");
    }

    [Fact]
    public void Drift_signature_differs_only_where_pools_differ()
    {
        var pools = AppHost().AppPools.ToDictionary(p => p.Name);
        var a = pools["AuxPool1"].DriftSignature;
        var b = pools["AuxPool2"].DriftSignature;

        a.Where(kv => b[kv.Key] != kv.Value).Select(kv => kv.Key).Should().BeEquivalentTo(["identityType", "restartSchedule"]);
    }

    [Fact]
    public void Arr_farms_and_rewrite_rules_are_read_with_targets_and_path_segments()
    {
        var config = AppHost();

        config.ArrProxyEnabled.Should().BeTrue();
        var farm = config.WebFarms.Should().Contain(f => f.Name == "RahkaranFarm").Subject;
        farm.LoadBalancing.Should().Be("WeightedRoundRobin");
        farm.HealthCheckUrl.Should().Be("http://rk.example.local/health");
        farm.Servers.Should().HaveCount(3);
        farm.Servers.Should().ContainSingle(s => !s.Enabled).Which.Weight.Should().Be(50);
        config.WebFarms.Should().Contain(f => f.Name == "EmptyFarm" && f.Servers.Count == 0);

        var global = config.RewriteRules.Where(r => r.Scope == "global").ToList();
        global.Should().HaveCount(2);
        global[0].TargetHost.Should().Be("RahkaranFarm");
        global[0].TargetPathSegment.Should().BeNull("{R:0} is not a literal segment");
        global[0].Conditions.Should().Equal("{HTTP_HOST} ~ rk.example.local");
        global[1].TargetPathSegment.Should().Be("Rahkaran72", "the rule still routes to the old application path");
        config.RewriteRules.Should().ContainSingle(r => r.Scope == "AuxWebsite1" && r.ActionType == "Redirect");
    }

    [Fact]
    public void Web_config_shows_hosting_targets_and_limits_but_no_secret()
    {
        var summary = WebConfigParser.Parse(Fixture("iis", "web.config"));

        summary.HostingModel.Should().Be("inprocess");
        summary.ProcessPath.Should().Be("dotnet");
        summary.StdoutLogEnabled.Should().BeTrue();
        summary.MaxAllowedContentLength.Should().Be(104857600);
        summary.MaxRequestLengthKb.Should().Be(51200);
        summary.ExecutionTimeoutSeconds.Should().Be(300);
        summary.ConnectionStrings.Should().ContainEquivalentOf(new ConnectionTarget("Rahkaran", @"sql01\RK", "RK_Tehran1405", false, true));
        summary.ConnectionStrings.Should().ContainEquivalentOf(new ConnectionTarget("Reports", "sql02", "ReportServer", true, false));
        summary.RedisAddresses.Should().Equal("redis01:6379");
        summary.AppSettings.Single(a => a.Name == "Culture").Value.Should().Be("fa-IR");
        summary.AppSettings.Single(a => a.Name == "Jwt.SigningKey").Value.Should().Be(SecretMasker.Mask);
        summary.EnvironmentVariables.Single(e => e.Name == "ASPNETCORE_ENVIRONMENT").Value.Should().Be("Production");
        summary.RewriteRules.Single().TargetPathSegment.Should().Be("Rahkaran73");

        var everything = JsonSerializer.Serialize(summary);
        foreach (var secret in new[]
                 {
                     "Pa55-SQL-Secret!", "rk_app", "Redis-Secret-42", "jwt-key-material-123", "tok_ABCDEF987", "client-secret-xyz", "mail-pwd-555",
                     "url-pass-777", "Legacy-Pwd-1", "Arg-Token-999", "Env-CS-Secret", "env-jwt-secret", "MACHINE-KEY-VALIDATION", "MACHINE-KEY-DECRYPT",
                 })
        {
            everything.Should().NotContain(secret);
        }
    }

    [Theory]
    [InlineData("--token abc --mode x", "--token ••••••", "--mode x")]
    [InlineData("apiKey=12345&x=1", "apiKey=••••••", "x=1")]
    [InlineData("https://u:p4ss@host/x", "https://••••••@host/x", null)]
    public void Inline_secrets_are_masked(string text, string expected, string? kept)
    {
        var masked = SecretMasker.MaskInline(text)!;

        masked.Should().Contain(expected);
        if (kept is not null)
        {
            masked.Should().Contain(kept);
        }
    }

    [Fact]
    public void Hostile_config_xml_is_refused()
    {
        const string bomb = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\">]><configuration>&b;</configuration>";

        FluentActions.Invoking(() => ApplicationHostConfigParser.Parse(bomb)).Should().Throw<System.Xml.XmlException>();
        FluentActions.Invoking(() => WebConfigParser.Parse(bomb)).Should().Throw<System.Xml.XmlException>();
    }
}
