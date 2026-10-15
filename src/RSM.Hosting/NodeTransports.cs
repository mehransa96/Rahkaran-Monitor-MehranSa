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

        if (matrix.Has(CapabilityIds.WinCim))
        {
            var password = string.IsNullOrWhiteSpace(node.Windows.UserName) ? null
                : await secrets.GetAsync(site.Id, node.Id, SecretKind.WindowsPassword, cancellationToken).ConfigureAwait(false);
            if (WindowsTransportFactory.TryOpen(node, password).Transport is { } windows)
            {
                list.Add(windows);
            }
        }

        return new NodeTransports(list);
    }
}
