using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using RSM.Core.Profiles;

namespace RSM.Safety.Sql;

public enum SqlQueryStatus
{
    Ok,
    /// <summary>The server is older than the query's min_version.</summary>
    VersionTooLow,
    PermissionDenied,
    /// <summary>Object or procedure does not exist (feature not installed, e.g. no ReportServer).</summary>
    ObjectMissing,
    LoginFailed,
    Unreachable,
    Timeout,
    Error,
}

public sealed record SqlQueryResult<T>(SqlQueryStatus Status, IReadOnlyList<T> Rows, TimeSpan Duration, string? Message = null, int? ErrorNumber = null)
{
    public bool IsOk => Status == SqlQueryStatus.Ok;
}

/// <summary>
/// Per-node SQL access: hardened connection string, concurrency cap and the detected server version.
/// The password lives only in memory for the session lifetime.
/// </summary>
public sealed class SqlNodeSession : IDisposable
{
    private readonly SqlEndpoint _endpoint;
    private readonly string? _password;

    public SqlNodeSession(string nodeId, SqlEndpoint endpoint, string? password)
    {
        NodeId = nodeId;
        _endpoint = endpoint;
        _password = password;
        Gate = new SemaphoreSlim(Math.Max(1, endpoint.MaxConcurrentQueries));
    }

    public string NodeId { get; }

    public SqlEndpoint Endpoint => _endpoint;

    /// <summary>Set after the <c>cap.version</c> probe; null until then (the lowest variant is used).</summary>
    public SqlServerVersion? ServerVersion { get; set; }

    internal SemaphoreSlim Gate { get; }

    /// <summary>Builds the hardened connection string. Never logged (contains the password).</summary>
    internal string BuildConnectionString(string? database)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = _endpoint.Server,
            InitialCatalog = string.IsNullOrEmpty(database) ? "master" : database,
            ApplicationName = HardenedBatch.ApplicationName,
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ConnectTimeout = 5,
            CommandTimeout = Math.Clamp(_endpoint.CommandTimeoutSeconds, 1, 600),
            Encrypt = _endpoint.Encrypt switch
            {
                SqlEncryptMode.Optional => SqlConnectionEncryptOption.Optional,
                SqlEncryptMode.Strict => SqlConnectionEncryptOption.Strict,
                _ => SqlConnectionEncryptOption.Mandatory,
            },
            TrustServerCertificate = _endpoint.TrustServerCertificate,
            MultipleActiveResultSets = false,
            Pooling = true,
            MaxPoolSize = Math.Max(2, _endpoint.MaxConcurrentQueries + 1),
            PersistSecurityInfo = false,
        };

        if (_endpoint.Auth == SqlAuthKind.Windows)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = _endpoint.UserName ?? string.Empty;
            builder.Password = _password ?? string.Empty;
        }

        return builder.ConnectionString;
    }

    public void Dispose() => Gate.Dispose();
}

/// <summary>
/// The ONLY way RSM runs SQL: by catalog key, with scalar parameters, after SqlGuard, with the hardening prefix,
/// a per-command timeout and the per-node concurrency cap.
/// </summary>
public sealed class SafeSqlExecutor(QueryCatalog catalog)
{
    private static readonly HashSet<Type> ScalarParameterTypes =
    [
        typeof(string), typeof(int), typeof(long), typeof(short), typeof(byte), typeof(bool), typeof(decimal), typeof(double),
        typeof(DateTime), typeof(DateTimeOffset), typeof(Guid), typeof(byte[]),
    ];

    private readonly ConcurrentDictionary<string, bool> _guardCache = new(StringComparer.Ordinal);

    public QueryCatalog Catalog { get; } = catalog;

    public async Task<SqlQueryResult<T>> QueryAsync<T>(SqlNodeSession session, QueryKey key, object? parameters = null, string? database = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateParameters(parameters);

        var started = Stopwatch.GetTimestamp();
        var query = session.ServerVersion is { } version
            ? Catalog.Resolve(key, version)
            : Catalog.All.Where(q => q.Header.Key == key).MinBy(q => q.Header.MinVersion);
        if (query is null)
        {
            return new SqlQueryResult<T>(SqlQueryStatus.VersionTooLow, [], TimeSpan.Zero,
                $"requires {Catalog.MinimumVersion(key).ProductName} or later");
        }

        var batch = query.BatchText;
        if (!_guardCache.GetOrAdd(batch, static text => SqlGuard.Check(text, SqlGuardProfile.Catalog).IsAllowed))
        {
            // Unreachable for a loaded catalog; defence in depth.
            throw new InvalidOperationException($"SqlGuard rejected catalog query '{key}'.");
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqlConnection(session.BuildConnectionString(database));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = new CommandDefinition(batch, parameters, commandTimeout: query.Header.TimeoutSeconds,
                commandType: CommandType.Text, cancellationToken: cancellationToken);
            var rows = (await connection.QueryAsync<T>(command).ConfigureAwait(false)).AsList();
            return new SqlQueryResult<T>(SqlQueryStatus.Ok, rows, Stopwatch.GetElapsedTime(started));
        }
        catch (SqlException ex)
        {
            return new SqlQueryResult<T>(Classify(ex.Number), [], Stopwatch.GetElapsedTime(started), ex.Message, ex.Number);
        }
        catch (InvalidOperationException ex) when (!cancellationToken.IsCancellationRequested && ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return new SqlQueryResult<T>(SqlQueryStatus.Timeout, [], Stopwatch.GetElapsedTime(started), ex.Message);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>Maps SQL Server error numbers to outcomes that degrade gracefully (Capability Matrix).</summary>
    public static SqlQueryStatus Classify(int errorNumber) => errorNumber switch
    {
        229 or 230 or 262 or 297 or 300 or 916 or 4060 or 15247 or 15151 or 27203 => SqlQueryStatus.PermissionDenied,
        18456 or 18452 or 18486 or 18487 or 18488 => SqlQueryStatus.LoginFailed,
        208 or 2812 or 4121 or 911 => SqlQueryStatus.ObjectMissing,
        -2 or 1222 => SqlQueryStatus.Timeout,
        -1 or 2 or 53 or 40 or 64 or 121 or 233 or 10053 or 10054 or 10060 or 10061 or 11001 or 258 => SqlQueryStatus.Unreachable,
        _ => SqlQueryStatus.Error,
    };

    /// <summary>
    /// Only scalar parameters are allowed: Dapper expands enumerable parameters by rewriting the SQL text,
    /// which would bypass SqlGuard.
    /// </summary>
    internal static void ValidateParameters(object? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (var property in parameters.GetType().GetProperties())
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!ScalarParameterTypes.Contains(type))
            {
                throw new ArgumentException($"Parameter '{property.Name}' has non-scalar type {type.Name}; only scalar SQL parameters are allowed.", nameof(parameters));
            }
        }
    }
}
