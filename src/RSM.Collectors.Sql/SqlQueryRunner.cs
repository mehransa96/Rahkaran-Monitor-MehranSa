using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql;

/// <summary>Outcome of one catalog query inside a collector run (kept for the report appendix and the bundle).</summary>
public sealed record QueryOutcome(string Key, string? Database, SqlQueryStatus Status, int Rows, TimeSpan Duration, string? Message);

/// <summary>
/// Runs catalog queries for one collector and remembers every outcome, so the collector can degrade gracefully:
/// all OK → Ok; some unavailable → Partial; nothing usable → Unavailable (with the reason); SQL errors → Error.
/// </summary>
public sealed class SqlQueryRunner(SafeSqlExecutor executor, SqlNodeSession session, CancellationToken cancellationToken)
{
    private readonly List<QueryOutcome> _outcomes = [];

    public IReadOnlyList<QueryOutcome> Outcomes => _outcomes;

    public SqlNodeSession Session { get; } = session;

    /// <summary>Runs a query; returns null (and records why) when it could not run.</summary>
    public async Task<IReadOnlyList<T>?> QueryAsync<T>(QueryKey key, object? parameters = null, string? database = null)
    {
        var result = await executor.QueryAsync<T>(Session, key, parameters, database, cancellationToken).ConfigureAwait(false);
        _outcomes.Add(new QueryOutcome(key.Value, database, result.Status, result.Rows.Count, result.Duration, result.Message));
        return result.IsOk ? result.Rows : null;
    }

    /// <summary>
    /// Runs a query whose failure is an expected outcome (e.g. "no more error-log archives"): failures are not
    /// recorded and do not degrade the collector status.
    /// </summary>
    public async Task<IReadOnlyList<T>?> QueryOptionalAsync<T>(QueryKey key, object? parameters = null, string? database = null)
    {
        var result = await executor.QueryAsync<T>(Session, key, parameters, database, cancellationToken).ConfigureAwait(false);
        if (result.IsOk)
        {
            _outcomes.Add(new QueryOutcome(key.Value, database, result.Status, result.Rows.Count, result.Duration, null));
        }

        return result.IsOk ? result.Rows : null;
    }

    public CollectorStatus ToStatus()
    {
        if (_outcomes.Count == 0)
        {
            return CollectorStatus.Unavailable("no queries were run");
        }

        var errors = _outcomes.Where(o => o.Status is SqlQueryStatus.Error).ToList();
        var failed = _outcomes.Where(o => o.Status != SqlQueryStatus.Ok).ToList();
        if (errors.Count > 0)
        {
            return CollectorStatus.Error("query error", string.Join("; ", errors.Select(e => $"{e.Key}: {e.Message}")));
        }

        if (failed.Count == 0)
        {
            return CollectorStatus.Ok;
        }

        var detail = string.Join("; ", failed.GroupBy(f => f.Status).Select(g => $"{g.Key}: {string.Join(", ", g.Select(f => f.Database is null ? f.Key : $"{f.Key}@{f.Database}").Distinct())}"));
        return failed.Count == _outcomes.Count
            ? CollectorStatus.Unavailable(failed[0].Status.ToString(), MissingCapability(failed[0].Status), detail)
            : CollectorStatus.Partial("some queries unavailable", detail);
    }

    private static string? MissingCapability(SqlQueryStatus status) => status switch
    {
        SqlQueryStatus.PermissionDenied => "permission",
        SqlQueryStatus.VersionTooLow => "version",
        SqlQueryStatus.Unreachable or SqlQueryStatus.LoginFailed => "sql.connect",
        _ => null,
    };
}

/// <summary>Base for SQL snapshot collectors: resolves the node's SQL session and builds the result.</summary>
public abstract class SqlCollectorBase(SafeSqlExecutor executor) : ICollector
{
    public abstract string Id { get; }

    public virtual Layer Layer => Layer.Sql;

    public virtual CollectorCost Cost => CollectorCost.Medium;

    public virtual NodeRoles AppliesTo => NodeRoles.Data | NodeRoles.Report;

    public virtual TimeSpan DefaultInterval => TimeSpan.FromMinutes(15);

    public async Task<CollectorResult> CollectAsync(INodeContext node, CollectContext context, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var session = node.GetTransport<SqlNodeSession>();
        if (session is null)
        {
            return CollectorResult.FromStatus(Id, node.Node.Id, CollectorStatus.Unavailable("SQL Server not configured for this node", "sql.connect"), TimeSpan.Zero);
        }

        var runner = new SqlQueryRunner(executor, session, cancellationToken);
        var facts = new List<Fact>();
        var events = new List<UnifiedEvent>();
        var payload = await CollectCoreAsync(node, context, runner, facts, events).ConfigureAwait(false);
        return new CollectorResult(Id, node.Node.Id, started, DateTimeOffset.UtcNow - started, runner.ToStatus(), new SqlCollectorPayload(payload, runner.Outcomes),
            [], facts, events);
    }

    /// <summary>Runs the queries, adds facts/events, returns the payload (typed rows) for reports and bundles.</summary>
    protected abstract Task<IReadOnlyDictionary<string, object?>> CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, List<Fact> facts, List<UnifiedEvent> events);

    protected static void Add(List<Fact> facts, string key, object? value)
    {
        if (value is not null)
        {
            facts.Add(new Fact(key, value));
        }
    }
}

public sealed record SqlCollectorPayload(IReadOnlyDictionary<string, object?> Data, IReadOnlyList<QueryOutcome> Queries);
