using RSM.Collectors.Iis;
using RSM.Collectors.Redis;
using RSM.Collectors.Windows;
using RSM.Core.Capabilities;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Safety.Sql;
using RSM.Storage.Profiles;

namespace RSM.Hosting;

/// <summary>The read-only transports opened for one node (SQL session, Windows CIM/counters/registry).</summary>
public sealed class NodeTransports(IReadOnlyList<object> transports) : IDisposable
{
    public IReadOnlyList<object> All { get; } = transports;

    /// <summary>A collector runs only when the transport it needs was opened (the Capability Matrix says why not).</summary>
    public bool CanRun(ICollector collector) =>
        collector is not IRequiresTransport needs || All.Any(t => needs.TransportType.IsInstanceOfType(t));

    public void Dispose()
    {
        foreach (var t in All.OfType<IDisposable>())
        {
            t.Dispose();
        }
    }
}

/// <summary>Opens the transports a node's capabilities allow; shared by the diagnosis and the live view.</summary>
public sealed class NodeTransportOpener(SecretStore secrets)
{
    public async Task<NodeTransports> OpenAsync(SiteProfile site, NodeProfile node, CapabilityMatrix matrix, CancellationToken cancellationToken)
    {
        var list = new List<object>();
        if (node.Sql is { } sql && matrix.Has(CapabilityIds.SqlConnect))
        {
            var password = sql.Auth == SqlAuthKind.Sql ? await secrets.GetAsync(site.Id, node.Id, SecretKind.SqlPassword, cancellationToken).ConfigureAwait(false) : null;
            var session = new SqlNodeSession(node.Id, sql, password);
            if (matrix.Facts.TryGetValue("sql.version", out var v) && SqlServerVersion.TryParse(v, out var version))
            {
                session.ServerVersion = version;
            }

            list.Add(session);
        }

        // Redis is opened whenever it is configured: "unreachable / AUTH failed" is itself a finding (RDS-001).
        if (node.Redis is { } redis)
        {
            var password = redis.HasPassword ? await secrets.GetAsync(site.Id, node.Id, SecretKind.RedisPassword, cancellationToken).ConfigureAwait(false) : null;
            list.Add(new RedisNodeTransport(redis, password));
        }

        if (matrix.Has(CapabilityIds.WinCim))
        {
            var password = string.IsNullOrWhiteSpace(node.Windows.UserName) ? null
                : await secrets.GetAsync(site.Id, node.Id, SecretKind.WindowsPassword, cancellationToken).ConfigureAwait(false);
            if (WindowsTransportFactory.TryOpen(node, password).Transport is { } windows)
            {
                list.Add(windows);
            }
        }

        if ((node.Roles & (NodeRoles.App | NodeRoles.Arr)) != 0 && (matrix.Has(CapabilityIds.IisConfig) || matrix.Has(CapabilityIds.IisLogs)))
        {
            var (iis, _) = await IisTransportFactory.TryOpenAsync(node, list.OfType<WindowsNodeTransport>().FirstOrDefault(), Elevation.IsElevated(), cancellationToken).ConfigureAwait(false);
            if (iis is not null)
            {
                list.Add(iis);
            }
        }

        return new NodeTransports(list);
    }
}
