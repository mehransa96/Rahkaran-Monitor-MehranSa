using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using RSM.Collectors.Iis;
using RSM.Collectors.Redis;
using RSM.Collectors.Sql.Capabilities;
using RSM.Collectors.Windows;
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

        // Windows: CIM queries, counters (PDH locally, CIM remotely) and whitelisted registry values.
        var windowsPassword = string.IsNullOrWhiteSpace(node.Windows.UserName) ? null
            : await secrets.GetAsync(site.Id, node.Id, SecretKind.WindowsPassword, cancellationToken).ConfigureAwait(false);
        var (windows, windowsFailure) = WindowsTransportFactory.TryOpen(node, windowsPassword);
        using (windows)
        {
            await WindowsCapabilityProbe.ProbeAsync(windows, windowsFailure, matrix, cancellationToken).ConfigureAwait(false);

            // IIS (App and ARR nodes): configuration and logs from files, live requests locally.
            if ((node.Roles & (NodeRoles.App | NodeRoles.Arr)) != 0)
            {
                var elevated = Elevation.IsElevated();
                var (iis, iisFailure) = await IisTransportFactory.TryOpenAsync(node, windows, elevated, cancellationToken).ConfigureAwait(false);
                using (iis)
                {
                    await IisCapabilityProbe.ProbeAsync(iis, iisFailure, elevated, matrix, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                foreach (var id in new[] { CapabilityIds.IisConfig, CapabilityIds.IisRequests, CapabilityIds.IisLogs })
                {
                    matrix.Set(id, CapabilityState.NotApplicable);
                }
            }
        }

        matrix.Set(CapabilityIds.RabbitApi, node.RabbitMq is null ? CapabilityState.NotConfigured : CapabilityState.NotImplemented, "Phase 4");
        matrix.Set(CapabilityIds.DotNetEventPipe, CapabilityState.NotImplemented, "Phase 4 (optional, off by default)");
        matrix.Set(CapabilityIds.RsmElevated, Elevation.IsElevated() ? CapabilityState.Available : CapabilityState.NeedsElevation);

        logger.LogInformation("Node {NodeName}: {Available}/{Total} capabilities available", node.Name, matrix.Results.Count(r => r.IsAvailable), matrix.Results.Count);
        return matrix;
    }

}

/// <summary>Whether RSM itself runs elevated (it never elevates or restarts itself).</summary>
public static class Elevation
{
    public static bool IsElevated() => OperatingSystem.IsWindows() && IsAdministrator();

    [SupportedOSPlatform("windows")]
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
