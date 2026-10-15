using RSM.Collectors.Sql.Capabilities;
using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Safety.Tests;

/// <summary>Every embedded catalog file must have a valid header and pass SqlGuard (fails the build otherwise).</summary>
public class SqlCatalogTests
{
    private static readonly System.Reflection.Assembly[] CatalogAssemblies = [typeof(SqlCapabilityProbe).Assembly, typeof(RSM.Collectors.Database.DatabaseCollector).Assembly];

    public static TheoryData<string> CatalogResources()
    {
        var data = new TheoryData<string>();
        foreach (var assembly in CatalogAssemblies)
        {
            foreach (var resource in QueryCatalog.EmbeddedQueryResources(assembly))
            {
                data.Add(resource);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CatalogResources))]
    public void SqlCatalogIsReadOnly(string resource)
    {
        var assembly = CatalogAssemblies.Single(a => a.GetManifestResourceNames().Contains(resource));
        var text = QueryCatalog.ReadResource(assembly, resource);

        var verdict = SqlGuard.Check(HardenedBatch.Prefix + text, SqlGuardProfile.Catalog);

        verdict.IsAllowed.Should().BeTrue($"{resource}: {verdict}");
    }

    private static readonly string[] SessionViews =
        ["sys.dm_exec_sessions", "sys.dm_exec_requests", "sys.dm_exec_connections", "sys.dm_os_waiting_tasks", "sys.dm_tran_session_transactions", "sys.dm_tran_locks"];

    /// <summary>
    /// CLAUDE.md: RSM's own sessions are excluded from every session/request view. The single declared exception
    /// (<c>own_sessions: true</c>) is the self-overhead query, which looks only AT our sessions.
    /// </summary>
    [Theory]
    [MemberData(nameof(CatalogResources))]
    public void Session_views_exclude_rsm_own_sessions(string resource)
    {
        var assembly = CatalogAssemblies.Single(a => a.GetManifestResourceNames().Contains(resource));
        var text = QueryCatalog.ReadResource(assembly, resource);
        var body = string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        if (!SessionViews.Any(v => body.Contains(v, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var key = QueryCatalog.ParseHeader(text, resource).Key.Value;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^--\s*own_sessions:\s*true\s*$", System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            key.Should().Be("cap.self_overhead", "only the self-overhead query may look at RSM's own sessions");
            body.Should().Contain($"= N'{HardenedBatch.ApplicationName}'");
            return;
        }

        body.Should().Contain($"<> N'{HardenedBatch.ApplicationName}'", $"{resource} reads a session/request view and must exclude RSM's own sessions");
    }

    [Fact]
    public void Whole_catalog_loads_and_keys_are_unique_per_version()
    {
        var catalog = QueryCatalog.Load(CatalogAssemblies);

        catalog.Keys.Should().Contain(["cap.version", "cap.server_perms", "cap.db_access", "cap.object_perms", "cap.agent_role", "cap.ssrs_access"]);
    }

    [Fact]
    public void Catalog_has_at_least_one_query() => CatalogResources().Count.Should().BeGreaterThan(0);

    [Fact]
    public void Version_variants_resolve_to_the_highest_satisfied_minimum()
    {
        var catalog = QueryCatalog.Load(CatalogAssemblies);

        catalog.Resolve("cap.server_perms", SqlServerVersion.Sql2019)!.Header.MinVersion.Should().Be(SqlServerVersion.Sql2016);
        catalog.Resolve("cap.server_perms", SqlServerVersion.Sql2022)!.Header.MinVersion.Should().Be(SqlServerVersion.Sql2022);
        catalog.Resolve("cap.server_perms", new SqlServerVersion(12))!.Should().BeNull();
    }

    [Fact]
    public void Unknown_key_is_an_error()
    {
        var catalog = QueryCatalog.Load(CatalogAssemblies);

        var act = () => catalog.Resolve("does.not.exist", SqlServerVersion.Sql2022);

        act.Should().Throw<QueryCatalogException>();
    }

    [Fact]
    public void Header_is_parsed()
    {
        const string text = """
            -- key:         test.example
            -- purpose:     Example
            -- min_version: 13.0.5026   -- 2016 SP2
            -- permission:  VIEW SERVER STATE
            -- cost:        medium
            -- timeout_s:   30
            -- database:    each
            SELECT 1 AS x;
            """;

        var header = QueryCatalog.ParseHeader(text, "inline");

        header.Key.Value.Should().Be("test.example");
        header.MinVersion.Should().Be(SqlServerVersion.Sql2016Sp2);
        header.Cost.Should().Be(CollectorCost.Medium);
        header.TimeoutSeconds.Should().Be(30);
        header.Database.Should().Be(QueryDatabase.Each);
    }

    [Theory]
    [InlineData("-- key: a.b\nSELECT 1", "missing")]
    [InlineData("-- key: a.b\n-- purpose: p\n-- min_version: x\n-- permission: p\n-- cost: cheap\n-- timeout_s: 10\n-- database: master\nSELECT 1", "min_version")]
    [InlineData("-- key: a.b\n-- purpose: p\n-- min_version: 13\n-- permission: p\n-- cost: free\n-- timeout_s: 10\n-- database: master\nSELECT 1", "cost")]
    [InlineData("-- key: a.b\n-- purpose: p\n-- min_version: 13\n-- permission: p\n-- cost: cheap\n-- timeout_s: 0\n-- database: master\nSELECT 1", "timeout_s")]
    [InlineData("-- key: a.b\n-- purpose: p\n-- min_version: 13\n-- permission: p\n-- cost: cheap\n-- timeout_s: 10\n-- database: master\nDELETE FROM dbo.t", "SqlGuard")]
    public void Invalid_files_are_refused(string text, string reason)
    {
        var act = () => QueryCatalog.Parse(text, "inline");

        act.Should().Throw<QueryCatalogException>().WithMessage($"*{reason}*");
    }
}
