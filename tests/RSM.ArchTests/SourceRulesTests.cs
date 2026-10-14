using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RSM.ArchTests;

/// <summary>Source-level rules that keep the read-only guarantees from being weakened.</summary>
public partial class SourceRulesTests
{
    private static readonly string AuditedFolder = Path.Combine("src", "RSM.Safety", "Audited") + Path.DirectorySeparatorChar;

    /// <summary>Must match ARCHITECTURE.md §6.4. Phase 0: no audited exception exists yet.</summary>
    private static readonly string[] DocumentedAuditedTypes = [];

    [Fact]
    public void RS0030_is_never_suppressed_outside_the_audited_folder()
    {
        var offenders = Repo.SourceFiles("*.cs")
            .Where(f => !Repo.Relative(f).StartsWith(AuditedFolder, StringComparison.Ordinal))
            .Where(f => SuppressRs0030().IsMatch(File.ReadAllText(f)))
            .Select(Repo.Relative)
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void RS0030_is_never_disabled_in_build_files()
    {
        var buildFiles = Directory.EnumerateFiles(Repo.Root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".props", StringComparison.Ordinal) || f.EndsWith(".targets", StringComparison.Ordinal)
                        || Path.GetFileName(f) is ".editorconfig" or ".globalconfig")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        foreach (var file in buildFiles)
        {
            var text = File.ReadAllText(file);
            NoWarnRs0030().IsMatch(text).Should().BeFalse($"{Repo.Relative(file)} must not disable RS0030");
            text.Should().NotContain("dotnet_diagnostic.RS0030.severity", Repo.Relative(file));
        }
    }

    [Fact]
    public void Every_production_project_gets_the_banned_api_analyzer()
    {
        var props = File.ReadAllText(Path.Combine(Repo.Src, "Directory.Build.props"));
        var targets = File.ReadAllText(Path.Combine(Repo.Src, "Directory.Build.targets"));

        props.Should().Contain("Microsoft.CodeAnalysis.BannedApiAnalyzers");
        props.Should().Contain("RS0030");
        targets.Should().Contain("BannedSymbols.txt").And.Contain("BannedSymbols.NonSafety.txt");

        foreach (var csproj in Repo.SourceFiles("*.csproj"))
        {
            var text = File.ReadAllText(csproj);
            text.Should().NotContain("ImportDirectoryBuildProps", Repo.Relative(csproj));
            text.Should().NotContain("<AdditionalFiles Remove", Repo.Relative(csproj));
            text.Should().NotContain("BannedApiAnalyzers", $"{Repo.Relative(csproj)} must not override the analyzer reference");
        }
    }

    [Fact]
    public void Only_RSM_Safety_is_the_safety_boundary()
    {
        var boundaries = Repo.SourceFiles("*.csproj")
            .Where(f => XDocument.Load(f).Descendants("RsmSafetyBoundary").Any(e => e.Value.Trim() == "true"))
            .Select(Path.GetFileName)
            .ToList();

        boundaries.Should().Equal("RSM.Safety.csproj");
    }

    [Fact]
    public void Audited_exceptions_match_the_documented_list()
    {
        var folder = Path.Combine(Repo.Root, AuditedFolder);
        var types = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.cs").Select(Path.GetFileNameWithoutExtension).ToList()
            : [];

        types.Should().BeEquivalentTo(DocumentedAuditedTypes);
    }

    [Fact]
    public void No_sql_text_is_built_in_csharp()
    {
        var offenders = Repo.SourceFiles("*.cs")
            .SelectMany(f => SqlLiteral().Matches(File.ReadAllText(f)).Select(m => $"{Repo.Relative(f)}: {m.Value.Trim()}"))
            .ToList();

        offenders.Should().BeEmpty("all T-SQL lives in embedded Queries/**/*.sql catalog files");
    }

    [Theory]
    [InlineData("var q = \"SELECT name FROM sys.objects\";", true)]
    [InlineData("var q = \"delete from dbo.t\";", true)]
    [InlineData("var q = \"INSERT INTO dbo.t VALUES (1)\";", true)]
    [InlineData("var q = \"EXEC sp_who2\";", true)]
    [InlineData("var q = \"UPDATE t SET a = 1\";", true)]
    [InlineData("var q = \"DBCC CHECKDB\";", true)]
    [InlineData("var q = \"WITH c AS (SELECT 1 x) SELECT x FROM c\";", true)]
    [InlineData("Reject(\"INSERT is only allowed into a table variable\")", false)]
    [InlineData("\"SELECT on ReportServer.dbo.ExecutionLog3\"", false)]
    [InlineData("\"EXECUTE AS / AT linked server is not allowed\"", false)]
    public void Sql_literal_detector_itself_works(string code, bool isSql) => SqlLiteral().IsMatch(code).Should().Be(isSql);

    [Fact]
    public void Sql_files_exist_only_in_query_catalog_folders()
    {
        var misplaced = Repo.SourceFiles("*.sql")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Queries{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(Repo.Relative)
            .ToList();

        misplaced.Should().BeEmpty();
    }

    [Fact]
    public void Ui_has_no_server_action_commands()
    {
        var commands = Repo.SourceFiles("*.xaml")
            .SelectMany(f => CommandBinding().Matches(File.ReadAllText(f)).Select(m => m.Groups["name"].Value))
            .ToList();

        commands.Should().NotBeEmpty();
        commands.Should().NotContain(c => ForbiddenAction().IsMatch(c), "RSM never offers kill/recycle/restart/stop/shrink/rebuild actions — not even disabled ones");
    }

    [Fact]
    public void No_free_text_sql_editor_exists()
    {
        foreach (var xaml in Repo.SourceFiles("*.xaml"))
        {
            File.ReadAllText(xaml).Should().NotContain("AvalonEdit", "the read-only SQL viewer (P2) must be IsReadOnly and is reviewed when introduced");
        }
    }

    [GeneratedRegex(@"#pragma\s+warning\s+disable[^\n]*RS0030|SuppressMessage\([^)]*RS0030", RegexOptions.IgnoreCase)]
    private static partial Regex SuppressRs0030();

    [GeneratedRegex(@"<(NoWarn|WarningsNotAsErrors)>[^<]*RS0030", RegexOptions.IgnoreCase)]
    private static partial Regex NoWarnRs0030();

    // A string literal that starts like a T-SQL statement (prose such as "SELECT on dbo.X" in messages is not matched).
    [GeneratedRegex(@"""\s*(SELECT\s+[^""]*\bFROM\b|INSERT\s+(INTO\s+[\w@#\[]|[@#\[]|\w+\.\w)|UPDATE\s+[\w\[]+\s+SET\b|DELETE\s+FROM\b|MERGE\s+[\w\[]|EXEC(UTE)?\s+(\w+\.|\[|\(|@|sp_|xp_)|DBCC\s+\w|(ALTER|CREATE|DROP)\s+(TABLE|DATABASE|LOGIN|USER|PROCEDURE|PROC|VIEW|INDEX|EVENT|TRIGGER|FUNCTION)\b|TRUNCATE\s+TABLE\b|WITH\s+\w+\s+AS\s*\()", RegexOptions.IgnoreCase)]
    private static partial Regex SqlLiteral();

    [GeneratedRegex(@"Command=""\{Binding\s+(?:[\w.]*\.)?(?<name>\w+)Command")]
    private static partial Regex CommandBinding();

    [GeneratedRegex("Kill|Recycle|Restart|Stop|Start|Shrink|Rebuild|Reorganize|Execute|Run|Fix|Apply", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenAction();
}
