using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RSM.Core.Model;
using RSM.Safety.Files;

namespace RSM.Safety.Sql;

/// <summary>One result column the pack shows (and may set a threshold on).</summary>
public sealed record QueryPackColumn(string Name, LocalizedText? Title = null);

/// <summary>
/// A threshold on one numeric column. Every threshold carries a source (CLAUDE.md rule 3): <c>stad-fanni:&lt;doc&gt;§&lt;section&gt;</c>
/// or <c>general</c> (shown as "general guidance — confirm with ستاد فنی").
/// </summary>
public sealed record QueryPackThreshold(string Column, string Op, double Value, string Severity, LocalizedText Message, string Source = "general");

/// <summary>One query of a pack, as declared in <c>manifest.json</c>.</summary>
public sealed record QueryPackQueryDefinition(
    string Key,
    string File,
    LocalizedText Title,
    IReadOnlyList<QueryPackColumn>? Columns = null,
    IReadOnlyList<QueryPackThreshold>? Thresholds = null,
    int TimeoutSeconds = 10,
    int MaxRows = 200,
    int MinVersion = 13);

/// <summary><c>QueryPacks/&lt;pack&gt;/manifest.json</c>. <c>database</c> is <c>rk</c> (the detected/configured Rahkaran DB) or a database name.</summary>
public sealed record QueryPackManifest(string Id, LocalizedText Title, string Database, IReadOnlyList<QueryPackQueryDefinition> Queries, LocalizedText? Description = null);

/// <summary>An accepted query: guarded at load time, executable only by its key (<c>pack.&lt;pack&gt;.&lt;key&gt;</c>).</summary>
public sealed record QueryPackQuery(string PackId, LocalizedText PackTitle, string Database, QueryPackQueryDefinition Definition, string Body, string Origin)
{
    public QueryKey Key => new($"pack.{PackId}.{Definition.Key}");

    public bool TargetsRahkaran => string.Equals(Database, QueryPackCatalog.RahkaranDatabase, StringComparison.OrdinalIgnoreCase);

    /// <summary>Text as sent to the server: hardening prefix + body.</summary>
    public string BatchText => HardenedBatch.Prefix + Body;
}

/// <summary>Why a pack or one of its files was not loaded (shown in Settings → Query packs).</summary>
public sealed record QueryPackRefusal(string Pack, string? File, string Reason, IReadOnlyList<SqlGuardViolation> Violations)
{
    public override string ToString() => File is null ? $"{Pack}: {Reason}" : $"{Pack}/{File}: {Reason}";
}

public sealed record QueryPackInfo(string Id, LocalizedText Title, string Database, string Directory, int Accepted, int Refused);

/// <summary>
/// User-supplied query packs (spec §8.7): <c>QueryPacks/&lt;pack&gt;/manifest.json</c> + <c>*.sql</c>. Every file passes
/// <see cref="SqlGuard"/> with the stricter <see cref="SqlGuardProfile.QueryPack"/> profile when it is loaded, or is
/// refused with the reason. RSM ships no packs: Rahkaran-specific table names are only added by the team once confirmed.
/// </summary>
public sealed partial class QueryPackCatalog
{
    public const string RahkaranDatabase = "rk";
    public const string ManifestFileName = "manifest.json";
    public const int MaxFileBytes = 64 * 1024;
    public const int MaxRowsLimit = 1000;
    public const int MaxTimeoutSeconds = 60;

    private static readonly string[] Operators = [">", ">=", "<", "<=", "==", "!="];
    private static readonly string[] Severities = ["Info", "Warning", "Critical"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly Dictionary<string, QueryPackQuery> _byKey;

    private QueryPackCatalog(IReadOnlyList<QueryPackInfo> packs, IReadOnlyList<QueryPackQuery> queries, IReadOnlyList<QueryPackRefusal> refusals)
    {
        Packs = packs;
        Queries = queries;
        Refusals = refusals;
        _byKey = queries.ToDictionary(q => q.Key.Value, StringComparer.Ordinal);
    }

    public static QueryPackCatalog Empty { get; } = new([], [], []);

    public IReadOnlyList<QueryPackInfo> Packs { get; }

    public IReadOnlyList<QueryPackQuery> Queries { get; }

    public IReadOnlyList<QueryPackRefusal> Refusals { get; }

    public QueryPackQuery? Find(QueryKey key) => _byKey.GetValueOrDefault(key.Value);

    /// <summary>Loads every pack below <paramref name="directory"/> (one sub-folder per pack). A missing folder is an empty catalog.</summary>
    public static async Task<QueryPackCatalog> LoadAsync(string directory, CancellationToken cancellationToken)
    {
        if (!ReadOnlyFile.DirectoryExists(directory))
        {
            return Empty;
        }

        var packs = new List<QueryPackInfo>();
        var queries = new List<QueryPackQuery>();
        var refusals = new List<QueryPackRefusal>();
        foreach (var packDirectory in Directory.GetDirectories(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.GetFileName(packDirectory);
            var manifestPath = Path.Combine(packDirectory, ManifestFileName);
            if (!ReadOnlyFile.Exists(manifestPath))
            {
                refusals.Add(new QueryPackRefusal(folder, null, $"{ManifestFileName} is missing", []));
                continue;
            }

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in ReadOnlyFile.ListInfo(packDirectory, "*.sql", max: 200))
            {
                files[info.Name] = info.Length > MaxFileBytes ? string.Empty : await ReadOnlyFile.ReadAllTextAsync(info.Path, cancellationToken).ConfigureAwait(false);
            }

            var manifestText = await ReadOnlyFile.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var (pack, accepted, refused) = LoadPack(folder, manifestText, files);
            queries.AddRange(accepted);
            refusals.AddRange(refused);
            if (pack is not null)
            {
                packs.Add(new QueryPackInfo(pack.Id, pack.Title, pack.Database, packDirectory, accepted.Count, refused.Count));
            }
        }

        // Two folders declaring the same pack id: keep the first, refuse the rest.
        var duplicates = queries.GroupBy(q => q.Key.Value, StringComparer.Ordinal).Where(g => g.Count() > 1).SelectMany(g => g.Skip(1)).ToList();
        foreach (var d in duplicates)
        {
            queries.Remove(d);
            refusals.Add(new QueryPackRefusal(d.PackId, d.Definition.File, $"duplicate key {d.Key}", []));
        }

        return new QueryPackCatalog(packs, queries, refusals);
    }

    /// <summary>Validates one pack (manifest text + its .sql files by name). Pure; unit-tested.</summary>
    public static (QueryPackManifest? Pack, IReadOnlyList<QueryPackQuery> Accepted, IReadOnlyList<QueryPackRefusal> Refused) LoadPack(
        string folder, string manifestText, IReadOnlyDictionary<string, string> files)
    {
        var refused = new List<QueryPackRefusal>();
        QueryPackManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<QueryPackManifest>(manifestText, Json);
        }
        catch (JsonException ex)
        {
            return (null, [], [new QueryPackRefusal(folder, ManifestFileName, $"invalid manifest: {ex.Message}", [])]);
        }

        if (manifest is null || manifest.Queries is null || manifest.Title is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Database))
        {
            return (null, [], [new QueryPackRefusal(folder, ManifestFileName, "manifest needs id, title {fa, en}, database and queries", [])]);
        }

        if (!IdPattern().IsMatch(manifest.Id))
        {
            return (null, [], [new QueryPackRefusal(folder, ManifestFileName, "id may contain only letters, digits, '_' and '-' (max 64)", [])]);
        }

        if (!string.Equals(manifest.Database, RahkaranDatabase, StringComparison.OrdinalIgnoreCase) && !DatabasePattern().IsMatch(manifest.Database))
        {
            return (null, [], [new QueryPackRefusal(manifest.Id, ManifestFileName, $"database must be '{RahkaranDatabase}' or a plain database name", [])]);
        }

        var accepted = new List<QueryPackQuery>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in manifest.Queries)
        {
            var problem = Validate(q, keys);
            if (problem is not null)
            {
                refused.Add(new QueryPackRefusal(manifest.Id, q?.File, problem, []));
                continue;
            }

            if (!files.TryGetValue(q!.File, out var body))
            {
                refused.Add(new QueryPackRefusal(manifest.Id, q.File, "file not found in the pack folder", []));
                continue;
            }

            if (body.Length == 0)
            {
                refused.Add(new QueryPackRefusal(manifest.Id, q.File, $"file is empty or larger than {MaxFileBytes / 1024} KB", []));
                continue;
            }

            var query = new QueryPackQuery(manifest.Id, manifest.Title, manifest.Database, q, body, $"{folder}/{q.File}");
            var verdict = SqlGuard.Check(query.BatchText, SqlGuardProfile.QueryPack);
            if (!verdict.IsAllowed)
            {
                // Report positions relative to the user's file, not the hardened batch.
                var violations = verdict.Violations.Select(v => v with { Line = Math.Max(0, v.Line - 1) }).ToList();
                refused.Add(new QueryPackRefusal(manifest.Id, q.File, "rejected by SqlGuard: " + string.Join("; ", violations.Select(v => $"{v.Rule} at line {v.Line}: {v.Message}")), violations));
                continue;
            }

            keys.Add(q.Key);
            accepted.Add(query);
        }

        return (manifest, accepted, refused);
    }

    private static string? Validate(QueryPackQueryDefinition? q, HashSet<string> keys)
    {
        if (q is null || string.IsNullOrWhiteSpace(q.Key) || string.IsNullOrWhiteSpace(q.File) || q.Title is null)
        {
            return "each query needs key, file and title {fa, en}";
        }

        if (!IdPattern().IsMatch(q.Key))
        {
            return $"key '{q.Key}' may contain only letters, digits, '_' and '-'";
        }

        if (keys.Contains(q.Key))
        {
            return $"duplicate key '{q.Key}'";
        }

        if (!FilePattern().IsMatch(q.File))
        {
            return "file must be a plain *.sql name inside the pack folder";
        }

        if (q.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            return $"timeoutSeconds must be 1–{MaxTimeoutSeconds}";
        }

        if (q.MaxRows is < 1 or > MaxRowsLimit)
        {
            return $"maxRows must be 1–{MaxRowsLimit}";
        }

        if (q.MinVersion is < 10 or > 30)
        {
            return "minVersion is a SQL Server major version (e.g. 13 = 2016)";
        }

        foreach (var t in q.Thresholds ?? [])
        {
            if (t is null || t.Message is null || string.IsNullOrWhiteSpace(t.Column))
            {
                return "each threshold needs column, op, value, severity and message {fa, en}";
            }

            if (!Operators.Contains(t.Op))
            {
                return $"threshold op '{t.Op}' is not one of {string.Join(" ", Operators)}";
            }

            if (!Severities.Contains(t.Severity))
            {
                return $"threshold severity '{t.Severity}' is not Info, Warning or Critical";
            }

            if (q.Columns is { Count: > 0 } columns && !columns.Any(c => string.Equals(c.Name, t.Column, StringComparison.OrdinalIgnoreCase)))
            {
                return $"threshold column '{t.Column}' is not declared in columns";
            }

            if (string.IsNullOrWhiteSpace(t.Source) || !(t.Source == "general" || t.Source.StartsWith("stad-fanni:", StringComparison.Ordinal)))
            {
                return "threshold source must be 'general' or 'stad-fanni:<doc>§<section>'";
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="value"/> breaches the threshold. Non-numeric values never breach.</summary>
    public static bool Breaches(QueryPackThreshold threshold, object? value)
    {
        double? number = value switch
        {
            null => null,
            bool b => b ? 1 : 0,
            string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
            IConvertible c when c is not DateTime => c.ToDouble(CultureInfo.InvariantCulture),
            _ => null,
        };
        return number is { } n && threshold.Op switch
        {
            ">" => n > threshold.Value,
            ">=" => n >= threshold.Value,
            "<" => n < threshold.Value,
            "<=" => n <= threshold.Value,
            "==" => n == threshold.Value,
            "!=" => n != threshold.Value,
            _ => false,
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}\.sql$", RegexOptions.IgnoreCase)]
    private static partial Regex FilePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]{1,128}$")]
    private static partial Regex DatabasePattern();
}
