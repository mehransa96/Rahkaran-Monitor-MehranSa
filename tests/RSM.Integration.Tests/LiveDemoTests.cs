using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using RSM.Collectors.Sql;
using RSM.Collectors.Sql.Live;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Hosting;
using RSM.Parsers.Sql;
using RSM.Safety.Files;
using RSM.Safety.Sql;
using RSM.Storage.Profiles;
using Xunit.Abstractions;

namespace RSM.Integration.Tests;

/// <summary>
/// P2 acceptance demos against the CI SQL Server. The "application" side (two sessions that block or deadlock on
/// <c>Rahkaran.dbo.RsmDemoAccounts</c>, seeded by tests/integration/seed-deviations.sql) uses the admin connection in
/// this test project and always rolls back; RSM observes with the least-privilege login only. CI's fingerprint step
/// afterwards proves nothing changed on the server.
/// </summary>
public sealed class LiveDemoTests(ITestOutputHelper output) : IDisposable
{
    private const string DemoApp = "Rahkaran demo app (CI)";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rsm-it-live-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Admin(string database = "Rahkaran") =>
        new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RSM_IT_SQL_ADMIN")) { InitialCatalog = database, ApplicationName = DemoApp, Pooling = false }.ConnectionString;

    private static (NodeProfile Node, SiteProfile Site, string Password) Profiles()
    {
        var cs = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RSM_IT_SQL"));
        var node = new NodeProfile
        {
            Id = "it-live",
            Name = "IT-SQL",
            Host = "localhost",
            Roles = NodeRoles.Data,
            Sql = new SqlEndpoint { Server = cs.DataSource, UserName = cs.UserID, TrustServerCertificate = cs.TrustServerCertificate, Encrypt = SqlEncryptMode.Optional },
        };
        return (node, new SiteProfile { Id = "it-live-site", Name = "CI", Nodes = [node], Settings = new SiteSettings { RahkaranDatabases = ["Rahkaran"] } }, cs.Password);
    }

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(Admin());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<int> SpidAsync(SqlConnection connection, SqlTransaction? transaction = null)
    {
        await using var command = new SqlCommand("SELECT @@SPID;", connection, transaction);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static SqlCommand Update(SqlConnection connection, SqlTransaction transaction, int id) =>
        new($"UPDATE dbo.RsmDemoAccounts SET Balance = Balance + 1 WHERE Id = {id};", connection, transaction) { CommandTimeout = 180 };

    private static async Task<CollectorResult> CollectAsync(ICollector collector)
    {
        var (node, site, password) = Profiles();
        using var session = new SqlNodeSession(node.Id, node.Sql!, password);
        return await collector.CollectAsync(new NodeContext(site, node, [session]), new CollectContext(), CancellationToken.None);
    }

    private static T? Data<T>(CollectorResult result, string key) where T : class => (result.Payload as SqlCollectorPayload)?.Data.GetValueOrDefault(key) as T;

    [EnvFact("RSM_IT_SQL_ADMIN")]
    public async Task Blocking_demo_shows_head_blocker_chain_statement_and_transaction_age()
    {
        await using var head = await OpenAsync();
        await using var waiter = await OpenAsync();
        await using var headTran = (SqlTransaction)await head.BeginTransactionAsync();
        await using var waiterTran = (SqlTransaction)await waiter.BeginTransactionAsync();
        var headSpid = await SpidAsync(head, headTran);
        var waiterSpid = await SpidAsync(waiter, waiterTran);
        await using (var lockRow = Update(head, headTran, 1))
        {
            await lockRow.ExecuteNonQueryAsync(); // head keeps the row lock and goes idle ("forgot to commit")
        }

        await using var blocked = Update(waiter, waiterTran, 1);
        var blockedTask = blocked.ExecuteNonQueryAsync();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            var result = await CollectAsync(new SqlActivityCollector(new SafeSqlExecutor(QueryCatalog.Load(RsmHost.CatalogAssemblies))));
            output.WriteLine($"status {result.Status.Kind} {result.Status.Detail}");
            foreach (var fact in result.Facts.Where(f => f.Key.StartsWith("sql.blocking.", StringComparison.Ordinal)))
            {
                output.WriteLine($"{fact.Key} = {fact.Value}");
            }

            result.Status.Kind.Should().Be(CollectorStatusKind.Ok);
            var chain = Data<IReadOnlyList<BlockingChain>>(result, "blockingChains")!.Should().ContainSingle(c => c.Head.SessionId == headSpid).Subject;
            chain.HeadIsIdle.Should().BeTrue("the head holds the lock from an open transaction while sleeping");
            chain.Head.Program.Should().Be(DemoApp);
            chain.Head.Statement.Should().Contain("UPDATE dbo.RsmDemoAccounts", "an idle head shows its last statement");
            chain.Head.TranAgeS.Should().BeGreaterThanOrEqualTo(3);
            chain.Blocked.Should().ContainSingle(b => b.SessionId == waiterSpid).Which.Should().Match<BlockedSession>(b =>
                b.BlockingSessionId == headSpid && b.Level == 1 && b.WaitType!.StartsWith("LCK_M_") && b.WaitMs >= 3000 && b.Statement!.Contains("RsmDemoAccounts"));
            result.Facts.Should().Contain(f => f.Key == "sql.blocking.head_session" && Equals(f.Value, headSpid));
            ((double)result.Facts.Single(f => f.Key == "sql.blocking.max_wait_s").Value!).Should().BeGreaterThanOrEqualTo(3);
            ((string)result.Facts.Single(f => f.Key == "sql.blocking.head_summary").Value!).Should().Contain($"session {headSpid}");
            Data<IReadOnlyList<SqlActivityCollector.RequestRow>>(result, "requests")!.Should().Contain(r => r.SessionId == waiterSpid && r.BlockingSessionId == headSpid);
            Data<IReadOnlyList<SqlActivityCollector.TranRow>>(result, "transactions")!.Should().Contain(t => t.SessionId == headSpid);
        }
        finally
        {
            await headTran.RollbackAsync();
            await blockedTask;
            await waiterTran.RollbackAsync();
        }
    }

    [EnvFact("RSM_IT_SQL_ADMIN")]
    public async Task Deadlock_demo_is_read_from_system_health_and_parsed()
    {
        await using var a = await OpenAsync();
        await using var b = await OpenAsync();
        await using var ta = (SqlTransaction)await a.BeginTransactionAsync();
        await using var tb = (SqlTransaction)await b.BeginTransactionAsync();
        var spidA = await SpidAsync(a, ta);
        var spidB = await SpidAsync(b, tb);
        await using (var c = Update(a, ta, 1))
        {
            await c.ExecuteNonQueryAsync();
        }

        await using (var c = Update(b, tb, 2))
        {
            await c.ExecuteNonQueryAsync();
        }

        await using var aWantsRow2 = Update(a, ta, 2);
        await using var bWantsRow1 = Update(b, tb, 1);
        var first = aWantsRow2.ExecuteNonQueryAsync();
        await Task.Delay(500);
        var second = bWantsRow1.ExecuteNonQueryAsync();
        var victims = 0;
        foreach (var task in new[] { first, second })
        {
            try
            {
                await task;
            }
            catch (SqlException ex) when (ex.Number == 1205)
            {
                victims++;
            }
        }

        victims.Should().Be(1, "SQL Server picks exactly one deadlock victim");
        foreach (var t in new[] { ta, tb })
        {
            try
            {
                await t.RollbackAsync(); // the victim's transaction is already gone; the survivor's is undone too
            }
            catch (InvalidOperationException)
            {
            }
        }

        // system_health dispatches events lazily (up to ~2 min); poll with the least-privilege login.
        var collector = new SqlDeadlockCollector(new SafeSqlExecutor(QueryCatalog.Load(RsmHost.CatalogAssemblies)));
        DeadlockGraph? graph = null;
        CollectorResult? result = null;
        for (var attempt = 0; attempt < 40 && graph is null; attempt++)
        {
            result = await CollectAsync(collector);
            graph = Data<IReadOnlyList<DeadlockGraph>>(result, "deadlocks")?
                .FirstOrDefault(g => g.Processes.Select(p => p.Spid).ToHashSet().SetEquals([spidA, spidB]) && g.Resources.Any(r => r.ObjectName?.Contains("RsmDemoAccounts", StringComparison.Ordinal) == true));
            if (graph is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        output.WriteLine($"status {result!.Status.Kind} {result.Status.Detail}; source {result.Facts.FirstOrDefault(f => f.Key == "sql.deadlocks.source")?.Value}");
        graph.Should().NotBeNull("the deadlock must appear in system_health within ~3 minutes");
        output.WriteLine(SqlDeadlockCollector.Describe(graph!));
        graph!.Victims.Should().ContainSingle();
        graph.Processes.Should().HaveCount(2).And.OnlyContain(p => p.Statement!.Contains("UPDATE dbo.RsmDemoAccounts") && p.Application == DemoApp && p.Database == "Rahkaran");
        graph.Resources.Should().OnlyContain(r => r.Kind == "keylock" && r.ObjectName!.EndsWith("dbo.RsmDemoAccounts", StringComparison.Ordinal));
        graph.Resources.SelectMany(r => r.Owners).Select(o => o.Mode).Should().Contain("X");
        graph.Resources.SelectMany(r => r.Waiters).Should().NotBeEmpty();
        result.Facts.Should().Contain(f => f.Key == "sql.deadlocks.count_24h" && (int)f.Value! >= 1);
        result.Events.Should().Contain(e => e.Code == "xml_deadlock_report");
    }

    [EnvFact("RSM_IT_SQL_ADMIN")]
    public async Task Live_view_soak_stays_within_the_overhead_budget()
    {
        var (node, site, password) = Profiles();
        var services = new ServiceCollection().AddLogging();
        RsmHost.AddRsmServices(services, new DataFolder(_root, isPortable: true));
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<SecretStore>().SetAsync(site.Id, node.Id, SecretKind.SqlPassword, password, remember: false, CancellationToken.None);
        var live = provider.GetRequiredService<LiveMonitorService>();

        var session = await live.StartAsync(site, null, CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(75));
            var stats = session.Stats();
            var load = stats.Nodes[node.Id];
            output.WriteLine($"client CPU {stats.ClientCpuPercent:0.0} %, working set {stats.ClientWorkingSetMb:0} MB, heap {stats.ManagedHeapMb:0} MB, series {stats.Series}, points {stats.Points}");
            output.WriteLine($"node: {load.RunsPerMinute} runs/min, {load.CollectorSecondsPerMinute:0.00} s/min, RSM SQL CPU {load.SqlCpuPercent:0.000} %, RTT {load.SqlRoundTripMs:0.0} ms, backed off {stats.BackedOffCollectors}");
            foreach (var result in session.LatestResults(node.Id))
            {
                output.WriteLine($"collector {result.CollectorId}: {result.Status.Kind} {result.Status.Reason} {result.Status.Detail} ({result.Duration.TotalMilliseconds:0} ms)");
            }

            foreach (var finding in session.Findings(node.Id))
            {
                output.WriteLine($"live finding {finding.RuleId} [{finding.Instance}] {finding.Severity}");
            }

            session.LatestResults(node.Id).Select(r => r.CollectorId).Should().Contain(["sql.perf", "sql.cpu", "sql.activity", "sql.deadlocks", "sql.querystats", "rsm.overhead.sql"]);
            session.LatestResults(node.Id).Should().NotContain(r => r.Status.Kind == CollectorStatusKind.Error);
            load.SqlCpuPercent.Should().NotBeNull("RSM measures its own SQL Server CPU");
            load.SqlCpuPercent!.Value.Should().BeLessThan(2, "P2 acceptance: RSM overhead < 2 % CPU");
            stats.ClientWorkingSetMb.Should().BeLessThan(500, "P2 acceptance: process < 500 MB");
            session.Engine.Rules.Should().NotBeEmpty();
            session.Facts(node.Id).Should().ContainKeys("sql.perf.batch_requests_sec", "sql.waits.ms_per_sec", "sql.cpu.sql_pct", "rsm.sql_cpu_pct");
            session.Metrics.Series(node.Id, "sql.cpu.sql_pct", null, DateTimeOffset.UtcNow.AddHours(-5)).Should().NotBeEmpty("the ring buffer back-fills the CPU chart");
        }
        finally
        {
            await live.StopAsync();
        }
    }
}
