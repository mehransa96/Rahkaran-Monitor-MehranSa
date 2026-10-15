using RSM.Core.Profiles;

namespace RSM.Core.Model;

/// <summary>
/// Read-only access to one server node. Transports (SQL executor, CIM, files, Redis…) are provided by the host;
/// collectors never construct connections, sockets or processes themselves.
/// </summary>
public interface INodeContext
{
    SiteProfile Site { get; }

    NodeProfile Node { get; }

    /// <summary>Returns a transport registered for this node, or <c>null</c> if it is not configured.</summary>
    T? GetTransport<T>() where T : class;
}

public sealed record CollectContext(string? CaptureId = null, bool IncludeHeavy = false);

public interface ICollector
{
    /// <summary>Stable id, e.g. <c>sql.waits</c>.</summary>
    string Id { get; }

    Layer Layer { get; }

    CollectorCost Cost { get; }

    NodeRoles AppliesTo { get; }

    TimeSpan DefaultInterval { get; }

    Task<CollectorResult> CollectAsync(INodeContext node, CollectContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Marker for collectors the live view schedules at <see cref="ICollector.DefaultInterval"/> (P2). Every collector
/// still runs once in a full diagnosis; snapshot-only collectors are not repeated by the live view.
/// </summary>
public interface ILiveCollector : ICollector;
