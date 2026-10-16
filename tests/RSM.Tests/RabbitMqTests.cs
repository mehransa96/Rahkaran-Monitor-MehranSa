using System.Net;
using RSM.Collectors.Rahkaran;
using RSM.Collectors.Windows;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Safety.Http;

namespace RSM.Tests;

public class RabbitMqTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    // Captured from RabbitMQ 3.13 management API (trimmed to the fields RSM reads plus some it ignores).
    private const string Overview = """
        {"management_version":"3.13.7","rabbitmq_version":"3.13.7","erlang_version":"26.2.5","cluster_name":"rabbit@wf01",
         "object_totals":{"channels":12,"connections":6,"consumers":9,"exchanges":14,"queues":5},
         "queue_totals":{"messages":1230,"messages_ready":1100,"messages_unacknowledged":130}}
        """;

    private const string Nodes = """
        [{"name":"rabbit@wf01","running":true,"mem_used":3221225472,"mem_limit":3435973836,"mem_alarm":true,"disk_free":53687091200,
          "disk_free_limit":50000000,"disk_free_alarm":false,"fd_used":120,"fd_total":1048576,"sockets_used":10,"sockets_total":943629,"uptime":86400000},
         {"name":"rabbit@wf02","running":true,"mem_used":1073741824,"mem_limit":3435973836,"mem_alarm":false,"disk_free":53687091200,
          "disk_free_limit":50000000,"disk_free_alarm":false,"fd_used":950,"fd_total":1000,"sockets_used":800,"sockets_total":900,"uptime":3600000}]
        """;

    private const string Queues = """
        [{"name":"wf.tasks","vhost":"/","messages":540,"messages_ready":540,"messages_unacknowledged":0,"consumers":0,"state":"running"},
         {"name":"wf.events","vhost":"/","messages":690,"messages_ready":560,"messages_unacknowledged":130,"consumers":4,"state":"running"},
         {"name":"wf.small","vhost":"/","messages":50,"messages_ready":50,"messages_unacknowledged":0,"consumers":0,"state":"running"},
         {"name":"no.counters.yet","vhost":"rk","state":"running"}]
        """;

    private static object? FactValue(LiveOutput output, string key) => output.Facts.LastOrDefault(f => f.Key == key)?.Value;

    [Fact]
    public void Management_api_json_is_parsed()
    {
        var overview = RabbitMqCollector.ParseOverview(Overview);
        overview.Version.Should().Be("3.13.7");
        overview.Connections.Should().Be(6);
        overview.Queues.Should().Be(5);

        var nodes = RabbitMqCollector.ParseNodes(Nodes);
        nodes.Should().HaveCount(2);
        nodes[0].MemAlarm.Should().BeTrue();
        nodes[1].FdPct.Should().Be(95);
        nodes[1].SocketsPct.Should().BeApproximately(88.9, 0.1);

        var queues = RabbitMqCollector.ParseQueues(Queues);
        queues.Should().HaveCount(4);
        queues[3].Should().Be(new RabbitQueue("no.counters.yet", "rk", 0, 0, 0, 0, "running"), "a queue without stats yet has no counters");
    }

    [Fact]
    public void Alarms_fd_usage_and_stuck_queues_become_facts()
    {
        var output = new LiveOutput();
        RabbitMqCollector.Apply(new RabbitState(), RabbitMqCollector.ParseOverview(Overview), RabbitMqCollector.ParseNodes(Nodes),
            RabbitMqCollector.ParseQueues(Queues), Now, output);

        FactValue(output, "rmq.reachable").Should().Be(true);
        FactValue(output, "rmq.alarm").Should().Be(true);
        FactValue(output, "rmq.alarm_nodes").Should().Be("rabbit@wf01 (memory)");
        FactValue(output, "rmq.fd_max_pct").Should().Be(95.0);
        FactValue(output, "rmq.stuck_queues").Should().Be(1, "wf.small has fewer than 100 messages and wf.events has consumers");
        FactValue(output, "rmq.stuck_queue_names").Should().Be("wf.tasks (540)");
        FactValue(output, "rmq.unacked").Should().Be(130L);
        output.Facts.Should().NotContain(f => f.Key == "rmq.unacked_growing_15m", "no 15-minute history yet");
    }

    [Theory]
    [InlineData(new long[] { 10, 20, 20, 40, 60, 80, 100, 130, 150, 170, 200, 220, 240, 260, 280, 300 }, true)]
    [InlineData(new long[] { 10, 20, 30, 40, 50, 60, 70, 5, 90, 100, 110, 120, 130, 140, 150, 160 }, false)]
    [InlineData(new long[] { 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10 }, false)]
    public void Unacked_growth_needs_fifteen_minutes_without_a_drop(long[] unacked, bool growing)
    {
        var state = new RabbitState();
        LiveOutput last = new();
        for (var i = 0; i < unacked.Length; i++)
        {
            last = new LiveOutput();
            var queue = new RabbitQueue("wf.events", "/", unacked[i], 0, unacked[i], 2, "running");
            RabbitMqCollector.Apply(state, new RabbitOverview("3.13.7", null, null, null, null, null), [], [queue], Now.AddMinutes(i), last);
        }

        FactValue(last, "rmq.unacked_growing_15m").Should().Be(growing);
    }

    private sealed class FakeRabbit(Func<string, string> respond) : IRabbitApi
    {
        public List<string> Paths { get; } = [];

        public Task<string> GetAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult(respond(path));
        }
    }

    private static NodeContext Context(params object[] transports)
    {
        var node = new NodeProfile { Id = "wf", Name = "wf01", Host = "wf01", Roles = NodeRoles.Workflow, RabbitMq = new RabbitEndpoint { ManagementUrl = new Uri("http://wf01:15672/") } };
        return new NodeContext(new SiteProfile { Id = "s", Name = "s", Nodes = [node] }, node, transports);
    }

    [Fact]
    public async Task Collector_uses_only_the_three_whitelisted_paths()
    {
        var api = new FakeRabbit(path => path switch { "api/overview" => Overview, "api/nodes" => Nodes, "api/queues" => Queues, _ => throw new InvalidOperationException(path) });

        var result = await new RabbitMqCollector { Clock = new FixedClock(Now) }.CollectAsync(Context(api), new CollectContext(), CancellationToken.None);

        result.Status.Kind.Should().Be(CollectorStatusKind.Ok);
        api.Paths.Should().BeEquivalentTo(["api/overview", "api/nodes", "api/queues"]);
        api.Paths.Should().OnlyContain(p => RabbitGetClient.AllowedPaths.Contains(p));
        result.Facts.Should().Contain(new Fact("rmq.alarm", true));
    }

    [Fact]
    public async Task Unreachable_api_or_denied_user_is_a_fact_not_a_crash()
    {
        var api = new FakeRabbit(_ => throw new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).", null, HttpStatusCode.Unauthorized));

        var result = await new RabbitMqCollector().CollectAsync(Context(api), new CollectContext(), CancellationToken.None);

        result.Status.Kind.Should().Be(CollectorStatusKind.Unavailable);
        result.Facts.Should().Contain(new Fact("rmq.reachable", false));
        (await new RabbitMqCollector().CollectAsync(Context(), new CollectContext(), CancellationToken.None)).Status.Reason.Should().Contain("off by default");
    }

    [Fact]
    public void CodeMeter_listen_query_is_a_constant_filter_and_ports_become_facts()
    {
        RSM.Collectors.Windows.WindowsServicesCollector.ListenQuery(4242).ToWql()
            .Should().Be("SELECT LocalAddress, LocalPort FROM MSFT_NetTCPConnection WHERE State = 2 AND OwningProcess = 4242");

        var output = new LiveOutput();
        WindowsServicesCollector.ApplyCodeMeterPorts(["0.0.0.0:22350", "::1:22350", "0.0.0.0:22350"], output);
        FactValue(output, "svc.codemeter_listening").Should().Be(true);
        FactValue(output, "svc.codemeter_listen_ports").Should().Be("0.0.0.0:22350, ::1:22350");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
