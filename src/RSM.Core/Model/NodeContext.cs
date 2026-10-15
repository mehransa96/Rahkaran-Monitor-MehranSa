using RSM.Core.Profiles;

namespace RSM.Core.Model;

/// <summary>Default <see cref="INodeContext"/>: a node plus the transports the host opened for it.</summary>
public sealed class NodeContext(SiteProfile site, NodeProfile node, IEnumerable<object> transports) : INodeContext
{
    private readonly List<object> _transports = transports.ToList();

    public SiteProfile Site { get; } = site;

    public NodeProfile Node { get; } = node;

    public T? GetTransport<T>() where T : class => _transports.OfType<T>().FirstOrDefault();
}
