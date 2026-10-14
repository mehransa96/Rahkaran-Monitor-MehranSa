using System.Globalization;
using System.Reflection;
using System.Text;
using RSM.Core.Model;

namespace RSM.Safety.Sql;

/// <summary>Identifies a catalog query, e.g. <c>cap.version</c>. The only way code can ask for SQL to run.</summary>
public readonly record struct QueryKey(string Value)
{
    public override string ToString() => Value;

    public static implicit operator QueryKey(string value) => new(value);
}

/// <summary>Which database a query must run in (the executor sets Initial Catalog; queries never contain USE).</summary>
public enum QueryDatabase
{
    Master,
    Msdb,
    Tempdb,
    /// <summary>Run once per accessible user database.</summary>
    Each,
    /// <summary>Run in the detected/selected Rahkaran database(s).</summary>
    Rahkaran,
    /// <summary>Run in a ReportServer database.</summary>
    ReportServer,
}

/// <summary>Parsed header of a catalog .sql file (see ARCHITECTURE.md §6.3).</summary>
public sealed record QueryHeader(
    QueryKey Key,
    string Purpose,
    SqlServerVersion MinVersion,
    string Permission,
    CollectorCost Cost,
    int TimeoutSeconds,
    QueryDatabase Database);

public sealed record CatalogQuery(QueryHeader Header, string Body, string ResourceName)
{
    /// <summary>Text as sent to the server: hardening prefix + body.</summary>
    public string BatchText => HardenedBatch.Prefix + Body;
}

public sealed class QueryCatalogException(string message) : Exception(message);

/// <summary>
/// Loads every embedded <c>Queries/**/*.sql</c> resource, validates its header and runs SqlGuard on the exact
/// batch text. A file that fails is never executable. Version variants: <c>x.sql</c>, <c>x.v14.sql</c> …
/// the executor picks the highest variant whose minimum version the server satisfies.
/// </summary>
public sealed class QueryCatalog
{
    private static readonly string[] RequiredFields = ["key", "purpose", "min_version", "permission", "cost", "timeout_s", "database"];

    private readonly Dictionary<string, List<CatalogQuery>> _byKey = new(StringComparer.Ordinal);

    private QueryCatalog(IEnumerable<CatalogQuery> queries)
    {
        foreach (var q in queries)
        {
            if (!_byKey.TryGetValue(q.Header.Key.Value, out var list))
            {
                _byKey[q.Header.Key.Value] = list = [];
            }

            if (list.Any(x => x.Header.MinVersion == q.Header.MinVersion))
            {
                throw new QueryCatalogException($"Duplicate catalog key '{q.Header.Key}' for min_version {q.Header.MinVersion} ({q.ResourceName}).");
            }

            list.Add(q);
            list.Sort((a, b) => b.Header.MinVersion.CompareTo(a.Header.MinVersion));
        }
    }

    public IReadOnlyCollection<string> Keys => _byKey.Keys;

    public IEnumerable<CatalogQuery> All => _byKey.Values.SelectMany(v => v);

    /// <summary>Loads and guards all embedded queries of the given assemblies. Throws on the first invalid file.</summary>
    public static QueryCatalog Load(params Assembly[] assemblies)
    {
        var queries = new List<CatalogQuery>();
        foreach (var assembly in assemblies)
        {
            foreach (var resource in EmbeddedQueryResources(assembly))
            {
                queries.Add(Parse(ReadResource(assembly, resource), resource));
            }
        }

        return new QueryCatalog(queries);
    }

    public static IEnumerable<string> EmbeddedQueryResources(Assembly assembly) =>
        assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Queries.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

    public static string ReadResource(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new QueryCatalogException($"Resource {resource} not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Parses a catalog file and runs SqlGuard on the hardened batch. Throws <see cref="QueryCatalogException"/>.</summary>
    public static CatalogQuery Parse(string text, string origin)
    {
        var header = ParseHeader(text, origin);
        var query = new CatalogQuery(header, text, origin);
        var verdict = SqlGuard.Check(query.BatchText, SqlGuardProfile.Catalog);
        if (!verdict.IsAllowed)
        {
            throw new QueryCatalogException($"{origin}: rejected by SqlGuard: {verdict}");
        }

        return query;
    }

    public static QueryHeader ParseHeader(string text, string origin)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = new StringReader(text))
        {
            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("--", StringComparison.Ordinal))
                {
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }

                    break; // header ends at the first non-comment line
                }

                var content = trimmed[2..].Trim();
                var colon = content.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }

                var name = content[..colon].Trim();
                var value = content[(colon + 1)..];
                var comment = value.IndexOf("--", StringComparison.Ordinal);
                if (comment >= 0)
                {
                    value = value[..comment];
                }

                fields.TryAdd(name, value.Trim());
            }
        }

        var missing = RequiredFields.Where(f => !fields.TryGetValue(f, out var v) || v.Length == 0).ToList();
        if (missing.Count > 0)
        {
            throw new QueryCatalogException($"{origin}: header is missing {string.Join(", ", missing)}.");
        }

        if (!SqlServerVersion.TryParse(fields["min_version"], out var minVersion))
        {
            throw new QueryCatalogException($"{origin}: invalid min_version '{fields["min_version"]}'.");
        }

        CollectorCost cost = fields["cost"].ToLowerInvariant() switch
        {
            "cheap" or "c" => CollectorCost.Cheap,
            "medium" or "m" => CollectorCost.Medium,
            "heavy" or "h" => CollectorCost.Heavy,
            var other => throw new QueryCatalogException($"{origin}: invalid cost '{other}'."),
        };

        if (!int.TryParse(fields["timeout_s"], NumberStyles.None, CultureInfo.InvariantCulture, out var timeout) || timeout is < 1 or > 600)
        {
            throw new QueryCatalogException($"{origin}: timeout_s must be 1..600.");
        }

        QueryDatabase database = fields["database"].ToLowerInvariant() switch
        {
            "master" => QueryDatabase.Master,
            "msdb" => QueryDatabase.Msdb,
            "tempdb" => QueryDatabase.Tempdb,
            "each" or "each-user-db" => QueryDatabase.Each,
            "rk" or "rahkaran-db" => QueryDatabase.Rahkaran,
            "rs" or "reportserver" => QueryDatabase.ReportServer,
            var other => throw new QueryCatalogException($"{origin}: invalid database '{other}'."),
        };

        var key = fields["key"];
        if (!key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_'))
        {
            throw new QueryCatalogException($"{origin}: invalid key '{key}'.");
        }

        return new QueryHeader(new QueryKey(key), fields["purpose"], minVersion, fields["permission"], cost, timeout, database);
    }

    /// <summary>Returns the best variant for the server version, or null if the server is too old.</summary>
    public CatalogQuery? Resolve(QueryKey key, SqlServerVersion serverVersion)
    {
        if (!_byKey.TryGetValue(key.Value, out var variants))
        {
            throw new QueryCatalogException($"Unknown catalog key '{key}'.");
        }

        return variants.FirstOrDefault(v => serverVersion >= v.Header.MinVersion);
    }

    /// <summary>Lowest minimum version across variants (for "requires …" messages).</summary>
    public SqlServerVersion MinimumVersion(QueryKey key) =>
        _byKey.TryGetValue(key.Value, out var variants) ? variants[^1].Header.MinVersion : throw new QueryCatalogException($"Unknown catalog key '{key}'.");
}

/// <summary>The fixed session settings prepended to every batch so RSM can never block Rahkaran.</summary>
public static class HardenedBatch
{
    public const string Prefix =
        "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT 2000; SET DEADLOCK_PRIORITY LOW; SET NOCOUNT ON;\n";

    /// <summary>Program name used to exclude RSM's own sessions from every session/request view.</summary>
    public const string ApplicationName = "RSM-ReadOnlyMonitor";
}
