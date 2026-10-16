using System.Globalization;
using RSM.Collectors.Sql;
using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Database;

/// <summary>Result of one query-pack query in one database, as shown on the Query packs page and in the report.</summary>
public sealed record QueryPackResult(
    string PackId,
    LocalizedText PackTitle,
    string Key,
    LocalizedText Title,
    string Database,
    SqlQueryStatus Status,
    string? Message,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    IReadOnlyList<QueryPackBreach> Breaches);

public sealed record QueryPackBreach(QueryPackThreshold Threshold, int Row, object? Value);

/// <summary>
/// §8.7 custom query packs: runs every accepted pack query (user-supplied, labelled *unverified*) in its target
/// database — <c>rk</c> = the configured/detected Rahkaran database(s) — and evaluates the pack's thresholds.
/// Facts: <c>pack[&lt;pack&gt;.&lt;key&gt;].*</c> (one instance per database when several Rahkaran databases exist).
/// </summary>
public sealed class QueryPackCollector(SafeSqlExecutor executor, QueryPackCatalog packs) : SqlCollectorBase(executor)
{
    public override string Id => "sql.querypacks";

    public override Layer Layer => Layer.Database;

    public override NodeRoles AppliesTo => NodeRoles.Data;

    protected override async Task<IReadOnlyDictionary<string, object?>> CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, List<Fact> facts, List<UnifiedEvent> events)
    {
        var results = new List<QueryPackResult>();
        Add(facts, "pack.loaded", packs.Queries.Count);
        Add(facts, "pack.refused", packs.Refusals.Count);
        if (packs.Queries.Count == 0)
        {
            return new Dictionary<string, object?> { ["querypacks"] = results, ["refusals"] = packs.Refusals };
        }

        IReadOnlyList<string>? rahkaran = null;
        foreach (var query in packs.Queries)
        {
            IReadOnlyList<string> databases;
            if (query.TargetsRahkaran)
            {
                rahkaran ??= await DetectRahkaranAsync(node, runner).ConfigureAwait(false);
                databases = rahkaran;
            }
            else
            {
                databases = [query.Database];
            }

            foreach (var database in databases)
            {
                var result = await runner.QueryPackAsync(packs, query.Key, database).ConfigureAwait(false);
                var evaluated = Evaluate(query, database, result);
                results.Add(evaluated);
                Apply(evaluated, databases.Count > 1, facts);
            }
        }

        return new Dictionary<string, object?> { ["querypacks"] = results, ["refusals"] = packs.Refusals };
    }

    private static async Task<IReadOnlyList<string>> DetectRahkaranAsync(INodeContext node, SqlQueryRunner runner)
    {
        if (node.Site.Settings.RahkaranDatabases.Count > 0)
        {
            return node.Site.Settings.RahkaranDatabases;
        }

        var databases = await runner.QueryAsync<DatabaseCollector.DatabaseRow>("db.list").ConfigureAwait(false);
        if (databases is null)
        {
            return [];
        }

        var activity = (await runner.QueryAsync<DatabaseCollector.ActivityRow>("db.detect.activity").ConfigureAwait(false))
            ?.Where(a => a.DatabaseName is not null).ToDictionary(a => a.DatabaseName!, a => a.Sessions, StringComparer.OrdinalIgnoreCase) ?? [];
        return RahkaranDatabaseDetector.Detect(
            databases.Select(d => new DatabaseCandidate(d.Name, d.DatabaseId > 4, d.StateDesc == "ONLINE", d.DataSizeMb ?? 0, activity.GetValueOrDefault(d.Name))).ToList(),
            []).Databases;
    }

    internal static QueryPackResult Evaluate(QueryPackQuery query, string database, SqlQueryResult<IReadOnlyDictionary<string, object?>> result)
    {
        var columns = result.Rows.Count > 0 ? result.Rows[0].Keys.ToList()
            : (query.Definition.Columns ?? []).Select(c => c.Name).ToList();
        var breaches = new List<QueryPackBreach>();
        for (var i = 0; i < result.Rows.Count; i++)
        {
            foreach (var threshold in query.Definition.Thresholds ?? [])
            {
                var value = result.Rows[i].GetValueOrDefault(threshold.Column);
                if (QueryPackCatalog.Breaches(threshold, value))
                {
                    breaches.Add(new QueryPackBreach(threshold, i, value));
                }
            }
        }

        return new QueryPackResult(query.PackId, query.PackTitle, query.Definition.Key, query.Definition.Title, database, result.Status, result.Message, columns, result.Rows, breaches);
    }

    private static int Rank(string severity) => severity switch { "Critical" => 3, "Warning" => 2, _ => 1 };

    internal static void Apply(QueryPackResult r, bool perDatabase, List<Fact> facts)
    {
        var key = perDatabase ? $"pack[{r.PackId}.{r.Key}@{r.Database}]" : $"pack[{r.PackId}.{r.Key}]";
        Add(facts, $"{key}.title_fa", $"{r.PackTitle.Fa} — {r.Title.Fa}");
        Add(facts, $"{key}.title_en", $"{r.PackTitle.En} — {r.Title.En}");
        Add(facts, $"{key}.database", r.Database);
        Add(facts, $"{key}.ok", r.Status == SqlQueryStatus.Ok);
        Add(facts, $"{key}.status", r.Status.ToString());
        Add(facts, $"{key}.rows", r.Rows.Count);
        Add(facts, $"{key}.breached", r.Breaches.Count > 0);
        Add(facts, $"{key}.breach_count", r.Breaches.Count);
        if (r.Breaches.OrderByDescending(b => Rank(b.Threshold.Severity)).FirstOrDefault() is { } worst)
        {
            var value = Convert.ToString(worst.Value, CultureInfo.InvariantCulture);
            Add(facts, $"{key}.breach_severity", worst.Threshold.Severity);
            Add(facts, $"{key}.breach_message_fa", $"{worst.Threshold.Message.Fa} ({worst.Threshold.Column} = {value})");
            Add(facts, $"{key}.breach_message_en", $"{worst.Threshold.Message.En} ({worst.Threshold.Column} = {value})");
            Add(facts, $"{key}.source", worst.Threshold.Source);
        }
    }
}
