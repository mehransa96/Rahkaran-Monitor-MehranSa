using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using RSM.Collectors.Redis;
using RSM.Collectors.Sql.Capabilities;
using RSM.Core.Capabilities;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Safety.Sql;
using RSM.Storage.Profiles;

namespace RSM.Hosting;

/// <summary>
/// "Test connection": runs every capability probe for a node and returns its Capability Matrix.
/// Probes that arrive in later phases report <see cref="CapabilityState.NotImplemented"/> with the phase.
/// </summary>
public sealed class ConnectionTester(
    SqlCapabilityProbe sqlProbe,
    RedisCapabilityProbe redisProbe,
    SecretStore secrets,
    ILogger<ConnectionTester> logger)
{
    public async Task<CapabilityMatrix> TestAsync(SiteProfile site, NodeProfile node, CancellationToken cancellationToken)
    {
        var matrix = new CapabilityMatrix(node.Id, DateTimeOffset.UtcNow);
        logger.LogInformation("Testing node {NodeName} ({Roles})", node.Name, node.Roles);

        if (node.Sql is { } sql)
        {
            var password = sql.Auth == SqlAuthKind.Sql ? await secrets.GetAsync(site.Id, node.Id, SecretKind.SqlPassword, cancellationToken).ConfigureAwait(false) : null;
            using var session = new SqlNodeSession(node.Id, sql, password);
            await sqlProbe.ProbeAsync(session, matrix, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            foreach (var id in new[] { CapabilityIds.SqlConnect, CapabilityIds.SqlViewServerState, CapabilityIds.SqlViewAnyDefinition, CapabilityIds.SqlConnectAnyDatabase,
                         CapabilityIds.SqlMsdbRead, CapabilityIds.SqlAgentRead, CapabilityIds.SqlErrorLog, CapabilityIds.SqlSsrsLog, CapabilityIds.SqlSysadmin })
            {
                matrix.Set(id, (node.Roles & (NodeRoles.Data | NodeRoles.Report)) != 0 ? CapabilityState.NotConfigured : CapabilityState.NotApplicable);
            }
        }

        if (node.Redis is { } redis)
        {
            var password = redis.HasPassword ? await secrets.GetAsync(site.Id, node.Id, SecretKind.RedisPassword, cancellationToken).ConfigureAwait(false) : null;
            await redisProbe.ProbeAsync(redis, password, matrix, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            foreach (var id in new[] { CapabilityIds.RedisPing, CapabilityIds.RedisInfo, CapabilityIds.RedisSlowlog, CapabilityIds.RedisClientList })
            {
                matrix.Set(id, (node.Roles & NodeRoles.Redis) != 0 ? CapabilityState.NotConfigured : CapabilityState.NotApplicable);
            }
        }

        // Windows / IIS / RabbitMQ / .NET probes arrive with their collectors.
        foreach (var id in new[] { CapabilityIds.WinCim, CapabilityIds.WinPerf, CapabilityIds.WinEventLog, CapabilityIds.WinServices, CapabilityIds.WinRegistry })
        {
            matrix.Set(id, CapabilityState.NotImplemented, "Phase 2");
        }

        foreach (var id in new[] { CapabilityIds.IisConfig, CapabilityIds.IisRequests, CapabilityIds.IisLogs })
        {
            matrix.Set(id, (node.Roles & (NodeRoles.App | NodeRoles.Arr)) != 0 ? CapabilityState.NotImplemented : CapabilityState.NotApplicable, "Phase 3");
        }

        matrix.Set(CapabilityIds.RabbitApi, node.RabbitMq is null ? CapabilityState.NotConfigured : CapabilityState.NotImplemented, "Phase 4");
        matrix.Set(CapabilityIds.DotNetEventPipe, CapabilityState.NotImplemented, "Phase 4 (optional, off by default)");
        matrix.Set(CapabilityIds.RsmElevated, OperatingSystem.IsWindows() && IsElevated() ? CapabilityState.Available : CapabilityState.NeedsElevation);

        logger.LogInformation("Node {NodeName}: {Available}/{Total} capabilities available", node.Name, matrix.Results.Count(r => r.IsAvailable), matrix.Results.Count);
        return matrix;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
