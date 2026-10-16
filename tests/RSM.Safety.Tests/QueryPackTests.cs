using RSM.Safety.Sql;

namespace RSM.Safety.Tests;

/// <summary>§8.7 query packs: every file is guarded with the query-pack profile at load time, or refused with a visible reason.</summary>
public class QueryPackTests
{
    private static string Manifest(string file = "users.sql", string database = "rk", string thresholds = "") => $$"""
        {
          "id": "team",
          "title": { "fa": "بسته تیم", "en": "Team pack" },
          "database": "{{database}}",
          "queries": [
            { "key": "users", "file": "{{file}}", "title": { "fa": "کاربران", "en": "Users" },
              "columns": [ { "name": "ActiveUsers" } ]{{thresholds}} }
          ]
        }
        """;

    private const string Threshold = """
        , "thresholds": [ { "column": "ActiveUsers", "op": ">", "value": 500, "severity": "Warning",
                            "message": { "fa": "کاربر زیاد", "en": "Many users" }, "source": "general" } ]
        """;

    private static (QueryPackManifest? Pack, IReadOnlyList<QueryPackQuery> Accepted, IReadOnlyList<QueryPackRefusal> Refused) Load(string sql, string? manifest = null) =>
        QueryPackCatalog.LoadPack("team", manifest ?? Manifest(), new Dictionary<string, string> { ["users.sql"] = sql });

    [Fact]
    public void A_read_only_select_on_target_tables_is_accepted_with_a_pack_key()
    {
        var (pack, accepted, refused) = Load("SELECT COUNT(*) AS ActiveUsers FROM dbo.SomeTeamConfirmedTable WITH (NOLOCK);", Manifest(thresholds: Threshold));

        refused.Should().BeEmpty();
        pack!.Id.Should().Be("team");
        accepted.Should().ContainSingle().Which.Key.Value.Should().Be("pack.team.users");
        accepted[0].TargetsRahkaran.Should().BeTrue();
        accepted[0].BatchText.Should().StartWith(HardenedBatch.Prefix);
    }

    [Fact]
    public void A_pack_with_update_is_refused_with_a_visible_reason_and_line()
    {
        var (_, accepted, refused) = Load("SELECT 1 AS ActiveUsers;\nUPDATE dbo.Users SET IsActive = 0;");

        accepted.Should().BeEmpty();
        var refusal = refused.Should().ContainSingle().Subject;
        refusal.File.Should().Be("users.sql");
        refusal.Reason.Should().StartWith("rejected by SqlGuard").And.Contain("SG010").And.Contain("line 2");
        refusal.Violations.Should().Contain(v => v.Rule.StartsWith("SG010", StringComparison.Ordinal) && v.Line == 2);
        refusal.ToString().Should().Be("team/users.sql: " + refusal.Reason);
    }

    [Theory]
    [InlineData("SELECT * FROM OtherDb.dbo.Users;", "SG063")]
    [InlineData("SELECT dbo.fn_Secret(1) AS ActiveUsers;", "SG067")]
    [InlineData("SELECT * FROM dbo.fn_Table(1);", "SG062")]
    [InlineData("EXEC master.sys.xp_readerrorlog 0, 1;", "SG050")]
    [InlineData("DELETE FROM dbo.Users;", "SG010")]
    [InlineData("SELECT * INTO dbo.Copy FROM dbo.Users;", "SG030")]
    [InlineData("SELECT 1; GO\nSELECT 2;", "SG00")]
    [InlineData("SELECT * FROM srv.db.dbo.t;", "SG064")]
    [InlineData("SELECT * FROM dbo.Users WITH (UPDLOCK);", "SG021")]
    [InlineData("SELECT * FROM #t;", "SG065")]
    public void Unsafe_or_out_of_scope_sql_is_refused(string sql, string rule)
    {
        var (_, accepted, refused) = Load(sql);

        accepted.Should().BeEmpty();
        refused.Should().ContainSingle().Which.Reason.Should().Contain(rule);
    }

    [Theory]
    [InlineData("../evil.sql", "plain *.sql name")]
    [InlineData("sub\\\\x.sql", "plain *.sql name")]
    [InlineData("missing.sql", "file not found")]
    public void Files_must_be_plain_names_inside_the_pack(string file, string reason)
    {
        var (_, accepted, refused) = Load("SELECT 1 AS ActiveUsers;", Manifest(file: file));

        accepted.Should().BeEmpty();
        refused.Should().ContainSingle().Which.Reason.Should().Contain(reason);
    }

    [Fact]
    public void Manifest_problems_are_refusals_not_exceptions()
    {
        Load("SELECT 1;", "{ not json").Refused.Should().ContainSingle().Which.Reason.Should().StartWith("invalid manifest");
        Load("SELECT 1;", """{ "id": "x" }""").Refused.Should().ContainSingle().Which.Reason.Should().Contain("needs id");
        Load("SELECT 1;", Manifest(database: "rk; DROP")).Refused.Should().ContainSingle().Which.Reason.Should().Contain("database");
        Load("SELECT 1;", Manifest(thresholds: Threshold.Replace("\"general\"", "\"my opinion\"", StringComparison.Ordinal)))
            .Refused.Should().ContainSingle().Which.Reason.Should().Contain("source");
        Load("SELECT 1;", Manifest(thresholds: Threshold.Replace("ActiveUsers\", \"op\"", "Other\", \"op\"", StringComparison.Ordinal)))
            .Refused.Should().ContainSingle().Which.Reason.Should().Contain("not declared");
        Load("SELECT 1;", Manifest(thresholds: Threshold.Replace("\">\"", "\"=~\"", StringComparison.Ordinal)))
            .Refused.Should().ContainSingle().Which.Reason.Should().Contain("op");
        Load("SELECT 1;", Manifest().Replace("\"key\": \"users\"", "\"key\": \"users\", \"maxRows\": 100000", StringComparison.Ordinal))
            .Refused.Should().ContainSingle().Which.Reason.Should().Contain("maxRows");
        Load("SELECT 1;", Manifest().Replace("\"key\": \"users\"", "\"key\": \"users\", \"shell\": \"cmd\"", StringComparison.Ordinal))
            .Refused.Should().ContainSingle("unknown manifest members are refused, not ignored");
    }

    [Theory]
    [InlineData(">", 501, true)]
    [InlineData(">", 500, false)]
    [InlineData(">=", 500, true)]
    [InlineData("<", 3, true)]
    [InlineData("==", 500, true)]
    [InlineData("!=", 500, false)]
    public void Thresholds_compare_numbers(string op, double value, bool breached)
    {
        var t = new QueryPackThreshold("c", op, 500, "Warning", new("x", "x"));
        QueryPackCatalog.Breaches(t, value).Should().Be(breached);
        QueryPackCatalog.Breaches(t, (long)value).Should().Be(breached);
    }

    [Fact]
    public void Non_numeric_values_never_breach()
    {
        var t = new QueryPackThreshold("c", ">", 1, "Info", new("x", "x"));
        QueryPackCatalog.Breaches(t, null).Should().BeFalse();
        QueryPackCatalog.Breaches(t, "abc").Should().BeFalse();
        QueryPackCatalog.Breaches(t, new DateTime(2026, 1, 1)).Should().BeFalse();
        QueryPackCatalog.Breaches(t, "5").Should().BeTrue();
    }

    [Fact]
    public async Task Folder_loading_reports_packs_and_refusals_and_the_documented_example_is_valid()
    {
        var root = Path.Combine(Path.GetTempPath(), "rsm-packs-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(Path.Combine(RepoRoot(), "docs", "query-packs", "example-table-sizes"), Path.Combine(root, "example-table-sizes"));
            Directory.CreateDirectory(Path.Combine(root, "bad"));
            await File.WriteAllTextAsync(Path.Combine(root, "bad", "manifest.json"), Manifest());
            await File.WriteAllTextAsync(Path.Combine(root, "bad", "users.sql"), "UPDATE dbo.Users SET IsActive = 0;");
            Directory.CreateDirectory(Path.Combine(root, "empty"));

            var catalog = await QueryPackCatalog.LoadAsync(root, CancellationToken.None);

            catalog.Queries.Should().ContainSingle().Which.Key.Value.Should().Be("pack.example-table-sizes.top-tables");
            catalog.Find("pack.example-table-sizes.top-tables").Should().NotBeNull();
            catalog.Find("pack.team.users").Should().BeNull("a refused file is never executable");
            catalog.Refusals.Should().HaveCount(2);
            catalog.Refusals.Should().Contain(r => r.Pack == "team" && r.Reason.Contains("SG010", StringComparison.Ordinal));
            catalog.Refusals.Should().Contain(r => r.Pack == "empty" && r.Reason.Contains("manifest.json is missing", StringComparison.Ordinal));
            catalog.Packs.Should().HaveCount(2);
            (await QueryPackCatalog.LoadAsync(Path.Combine(root, "nope"), CancellationToken.None)).Queries.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "RSM.sln")))
        {
            dir = Path.GetDirectoryName(dir)!;
        }

        return dir;
    }
}
