using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;

namespace RSM.Collectors.Windows;

/// <summary>
/// Same-node checks that need facts of two layers: OS-011 compares the SQL Server data/log folders (SQL layer) with
/// the Defender path exclusions (Windows registry) of the same machine.
/// </summary>
public sealed class WindowsNodeAnalyzer : ISiteAnalyzer
{
    public string Id => "win.node";

    public IReadOnlySet<string> InputCollectors { get; } = new HashSet<string>(StringComparer.Ordinal) { "win.inventory", "sql.storage" };

    public IReadOnlyDictionary<string, IReadOnlyList<Fact>> Analyze(SiteProfile site, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> factsByNode)
    {
        var result = new Dictionary<string, IReadOnlyList<Fact>>(StringComparer.Ordinal);
        foreach (var (node, facts) in factsByNode)
        {
            if (facts.GetValueOrDefault("os.defender_excluded_paths") is IEnumerable<object?> excluded && facts.GetValueOrDefault("sql.file_dirs") is IEnumerable<object?> dirs)
            {
                var missing = UnexcludedFolders(dirs.OfType<string>(), excluded.OfType<string>());
                result[node] = [new Fact("os.defender_unexcluded_sql_dirs", missing.Cast<object?>().ToList()), new Fact("os.defender_sql_dirs_excluded", missing.Count == 0)];
            }
        }

        return result;
    }

    /// <summary>SQL folders not covered by an excluded path (a folder is covered when an exclusion is it or a parent of it).</summary>
    internal static List<string> UnexcludedFolders(IEnumerable<string> sqlFolders, IEnumerable<string> exclusions)
    {
        var normalized = exclusions.Where(e => !e.Contains('*', StringComparison.Ordinal) && !e.Contains('%', StringComparison.Ordinal))
            .Select(e => e.TrimEnd('\\') + "\\").ToList();
        return sqlFolders.Where(f => !normalized.Any(e => (f.TrimEnd('\\') + "\\").StartsWith(e, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
