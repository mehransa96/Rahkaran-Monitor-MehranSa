using Microsoft.Extensions.Logging;
using RSM.Core.Live;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Rules;

namespace RSM.Hosting;

/// <summary>
/// Starts and stops the live view for a site: probes each node, opens its read-only transports, and schedules every
/// live collector that can run there. One live session at a time.
/// </summary>
public sealed class LiveMonitorService(
    ConnectionTester tester,
    NodeTransportOpener transportOpener,
    IEnumerable<ICollector> collectors,
    IEnumerable<ISiteAnalyzer> analyzers,
    RuleRepository ruleRepository,
    ILogger<LiveMonitorService> logger,
    RSM.Storage.History.HistoryRecorder? recorder = null) : IAsyncDisposable
{
    private readonly List<NodeTransports> _transports = [];
    private IAsyncDisposable? _recording;

    public LiveSession? Current { get; private set; }

    public SiteProfile? Site { get; private set; }

    public string? RuleOverrideDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "rules");

    public async Task<LiveSession> StartAsync(SiteProfile site, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);
        var engine = new RulesEngine(ruleRepository.Load(RuleOverrideDirectory).Rules);
        var nodes = new List<LiveNode>();
        foreach (var node in site.Nodes)
        {
            progress?.Report(node.Name);
            var matrix = await tester.TestAsync(site, node, cancellationToken).ConfigureAwait(false);
            var transports = await transportOpener.OpenAsync(site, node, matrix, cancellationToken).ConfigureAwait(false);
            _transports.Add(transports);
            var live = collectors.OfType<ILiveCollector>()
                .Where(c => transports.CanRun(c) && ((c.AppliesTo & node.Roles) != 0 || node.Roles == NodeRoles.None))
                .Cast<ICollector>()
                .ToList();
            var facts = new FactStore();
            SnapshotService.AddNodeFacts(site, node, matrix, facts, DateTime.Now);
            nodes.Add(new LiveNode(new NodeContext(site, node, transports.All), live, facts.Snapshot()));
            logger.LogInformation("Live view: node {Node} with {Count} collectors", node.Name, live.Count);
        }

        Site = site;
        Current = new LiveSession(nodes, engine, analyzers: analyzers.ToList());
        _recording = recorder?.Attach(site.Id, Current);
        Current.Start();
        return Current;
    }

    public async Task StopAsync()
    {
        if (_recording is { } recording)
        {
            _recording = null;
            await recording.DisposeAsync().ConfigureAwait(false);
        }

        if (Current is { } session)
        {
            Current = null;
            await session.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var t in _transports)
        {
            t.Dispose();
        }

        _transports.Clear();
        Site = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
