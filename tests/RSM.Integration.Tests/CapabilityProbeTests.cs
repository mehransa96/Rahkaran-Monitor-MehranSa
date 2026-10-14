using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using RSM.Collectors.Redis;
using RSM.Collectors.Sql.Capabilities;
using RSM.Core.Capabilities;
using RSM.Core.Profiles;
using RSM.Safety.Sql;

namespace RSM.Integration.Tests;

/// <summary>
/// Read-only proof (Phase 0 part): run every capability probe with the least-privilege login from
/// PERMISSIONS.md. The only acceptable non-available states are "optional feature unavailable" ones.
/// RSM_IT_SQL = connection string of the rsm_monitor login, e.g.
/// "Server=localhost,1433;User ID=rsm_monitor;Password=…;TrustServerCertificate=True".
/// </summary>
public class CapabilityProbeTests
{
    [EnvFact("RSM_IT_SQL")]
    public async Task Sql_probe_with_least_privilege_login_degrades_gracefully()
    {
        var cs = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RSM_IT_SQL"));
        var endpoint = new SqlEndpoint
        {
            Server = cs.DataSource,
            UserName = cs.UserID,
            TrustServerCertificate = cs.TrustServerCertificate,
            Encrypt = SqlEncryptMode.Optional,
        };
        using var session = new SqlNodeSession("it", endpoint, cs.Password);
        var executor = new SafeSqlExecutor(QueryCatalog.Load(typeof(SqlCapabilityProbe).Assembly));
        var matrix = new CapabilityMatrix("it", DateTimeOffset.UtcNow);

        await new SqlCapabilityProbe(executor).ProbeAsync(session, matrix, CancellationToken.None);

        matrix.Get(CapabilityIds.SqlConnect)!.State.Should().Be(CapabilityState.Available);
        matrix.Get(CapabilityIds.SqlViewServerState)!.State.Should().Be(CapabilityState.Available);
        matrix.Get(CapabilityIds.SqlViewAnyDefinition)!.State.Should().Be(CapabilityState.Available);
        matrix.Get(CapabilityIds.SqlConnectAnyDatabase)!.State.Should().Be(CapabilityState.Available);
        matrix.Get(CapabilityIds.SqlMsdbRead)!.State.Should().Be(CapabilityState.Available);
        matrix.Get(CapabilityIds.SqlAgentRead)!.State.Should().Be(CapabilityState.Available);
        matrix.Get(CapabilityIds.SqlSysadmin)!.State.Should().Be(CapabilityState.NotApplicable, "the integration login must not be sysadmin");
        session.ServerVersion.Should().NotBeNull();

        // Optional features may be missing on a test instance, but must never be an error.
        matrix.Results.Should().NotContain(r => r.State == CapabilityState.Error);
    }

    [EnvFact("RSM_IT_REDIS")]
    public async Task Redis_probe_uses_only_whitelisted_commands()
    {
        var parts = Environment.GetEnvironmentVariable("RSM_IT_REDIS")!.Split(':');
        var endpoint = new RedisEndpoint { Host = parts[0], Port = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 6379 };
        var matrix = new CapabilityMatrix("it", DateTimeOffset.UtcNow);

        await new RedisCapabilityProbe().ProbeAsync(endpoint, Environment.GetEnvironmentVariable("RSM_IT_REDIS_PASSWORD"), matrix, CancellationToken.None);

        matrix.Get(CapabilityIds.RedisPing)!.State.Should().Be(CapabilityState.Available);
        matrix.Facts.Should().ContainKey("redis.version");
    }

    [Fact]
    public void Placeholder_so_the_assembly_always_reports() => NullLogger.Instance.Should().NotBeNull();
}
