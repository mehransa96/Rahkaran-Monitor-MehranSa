using System.Text.RegularExpressions;
using System.Xml.Linq;
using RSM.Collectors.Sql.Capabilities;
using RSM.Safety.Sql;

namespace RSM.Tests;

/// <summary>Keeps docs, resources and code from drifting apart.</summary>
public partial class RepositoryConsistencyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RSM.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("RSM.sln not found");
    }

    [Fact]
    public void Every_catalog_key_is_documented_in_SQL_CATALOG_md()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "SQL_CATALOG.md"));
        var catalog = QueryCatalog.Load(typeof(SqlCapabilityProbe).Assembly, typeof(RSM.Collectors.Database.DatabaseCollector).Assembly);

        foreach (var key in catalog.Keys)
        {
            doc.Should().Contain($"`{key}`", $"catalog key {key} must have a row in SQL_CATALOG.md");
        }
    }

    [Fact]
    public void English_and_Persian_resources_have_identical_keys_and_no_empty_values()
    {
        var dir = Path.Combine(RepoRoot(), "src", "RSM.App", "Resources");
        var en = ReadResx(Path.Combine(dir, "Strings.resx"));
        var fa = ReadResx(Path.Combine(dir, "Strings.fa.resx"));

        fa.Keys.Should().BeEquivalentTo(en.Keys);
        en.Values.Should().NotContain(string.Empty);
        fa.Values.Should().NotContain(string.Empty);
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        var dir = Path.Combine(RepoRoot(), "src", "RSM.App", "Resources");
        var en = ReadResx(Path.Combine(dir, "Strings.resx"));
        var fa = ReadResx(Path.Combine(dir, "Strings.fa.resx"));

        foreach (var (key, value) in en)
        {
            Placeholder().Matches(fa[key]).Select(m => m.Value).Should().BeEquivalentTo(Placeholder().Matches(value).Select(m => m.Value), key);
        }
    }

    [Fact]
    public void Every_resource_key_used_in_xaml_and_code_exists()
    {
        var app = Path.Combine(RepoRoot(), "src", "RSM.App");
        var keys = ReadResx(Path.Combine(app, "Resources", "Strings.resx")).Keys.ToHashSet();
        var used = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => UsedKey().Matches(File.ReadAllText(f)).Select(m => m.Groups["key"].Value))
            .ToHashSet();

        used.Should().NotBeEmpty();
        used.Except(keys).Should().BeEmpty("every key referenced from XAML/C# must exist in Strings.resx");
    }

    [Fact]
    public void Enum_driven_labels_exist_in_both_languages()
    {
        // The live pages build these keys from enum names ("WaitCategory_" + category, "PlanWarning_" + kind).
        var app = Path.Combine(RepoRoot(), "src", "RSM.App", "Resources");
        var en = ReadResx(Path.Combine(app, "Strings.resx"));
        var fa = ReadResx(Path.Combine(app, "Strings.fa.resx"));
        var keys = Enum.GetNames<RSM.Parsers.Sql.WaitCategory>().Select(n => "WaitCategory_" + n)
            .Concat(Enum.GetNames<RSM.Parsers.Showplan.PlanWarningKind>().Select(n => "PlanWarning_" + n))
            .Concat(Enum.GetNames<RSM.Core.Model.Severity>().Select(n => "Severity_" + n))
            .Concat(Enum.GetNames<RSM.Core.Model.Layer>().Select(n => "LayerName_" + n))
            .Concat(Enum.GetNames<RSM.Collectors.Windows.ServiceCategory>().Select(n => "ServiceCategory_" + n))
            .Concat(Enumerable.Range(1, 7).Select(n => "WasState_" + n));
        foreach (var key in keys)
        {
            en.Should().ContainKey(key);
            fa.Should().ContainKey(key);
        }
    }

    [Fact]
    public void Xaml_has_no_hard_coded_user_visible_text()
    {
        var app = Path.Combine(RepoRoot(), "src", "RSM.App");
        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains("obj", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in LiteralText().Matches(text))
            {
                // Only the product acronym "RSM" may appear as a literal.
                m.Groups["value"].Value.Should().Be("RSM", $"{Path.GetFileName(file)} must use Strings.resx for user-visible text");
            }
        }
    }

    private static Dictionary<string, string> ReadResx(string path) =>
        XDocument.Load(path).Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    // [Key] bindings in XAML, Loc.Instance["Key"] and Format("Key", …) in C#.
    [GeneratedRegex(@"(?:\[(?<key>[A-Z][A-Za-z]+_[A-Za-z_]+)\]|Instance\[""(?<key>[A-Za-z_]+)""\]|Format\(""(?<key>[A-Za-z_]+)""|ViewModel\(""(?<key>[A-Za-z_]+)""|NavItem\(""(?<key>[A-Za-z_]+)"")")]
    private static partial Regex UsedKey();

    [GeneratedRegex(@"\b(?:Text|Content|Header|Title|ToolTip)=""(?<value>[^""{][^""]*)""")]
    private static partial Regex LiteralText();
}
