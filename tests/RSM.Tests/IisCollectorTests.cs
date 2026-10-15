using System.Text;
using RSM.Collectors.Iis;
using RSM.Collectors.Windows;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Parsers.Iis;
using RSM.Safety.Files;

namespace RSM.Tests;

public class IisCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 6, 20, 0, TimeSpan.Zero);

    private static string Fixture(params string[] path) => File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "Fixtures", .. path]));

    private static object? FactValue(LiveOutput output, string key) => output.Facts.LastOrDefault(f => f.Key == key)?.Value;

    // ---------- paths ----------

    [Theory]
    [InlineData(@"%SystemDrive%\inetpub\logs\LogFiles", null, @"C:\inetpub\logs\LogFiles")]
    [InlineData(@"%SystemDrive%\inetpub\logs\LogFiles", "app01", @"\\app01\C$\inetpub\logs\LogFiles")]
    [InlineData(@"%windir%\System32\inetsrv\config\applicationHost.config", "app01", @"\\app01\C$\Windows\System32\inetsrv\config\applicationHost.config")]
    [InlineData(@"d:\IISLogs\W3SVC2", "app01", @"\\app01\D$\IISLogs\W3SVC2")]
    [InlineData(@"\\files\logs", "app01", @"\\files\logs")]
    [InlineData(@"D:\a\..\..\Windows", null, null)]
    [InlineData(@"%TEMP%\x", null, null)]
    [InlineData("relative\\path", null, null)]
    public void Server_paths_map_to_local_or_admin_share(string serverPath, string? host, string? expected) =>
        ServerPaths.ToAccessPath(serverPath, host).Should().Be(expected);

    [Theory]
    [InlineData(@"D:\Rahkaran\Aux1", @".\logs\stdout", @"D:\Rahkaran\Aux1\logs\stdout")]
    [InlineData(@"D:\Rahkaran\Aux1\", "web.config", @"D:\Rahkaran\Aux1\web.config")]
    [InlineData(@"D:\Rahkaran\Aux1", @"E:\logs\stdout", @"E:\logs\stdout")]
    [InlineData(@"D:\Rahkaran\Aux1", @"..\..\secret", null)]
    public void Relative_paths_combine_with_the_site_root(string root, string relative, string? expected) => ServerPaths.Combine(root, relative).Should().Be(expected);

    // ---------- in-memory server files ----------

    private sealed class MemoryFiles : IServerFiles
    {
        public Dictionary<string, (string Text, DateTimeOffset Written)> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Map(string? serverPath) => ServerPaths.ToAccessPath(serverPath, null);

        public void Put(string path, string text, DateTimeOffset? written = null) => Files[path] = (text, written ?? Now);

        public Task<string> ReadAllTextAsync(string accessPath, CancellationToken cancellationToken) =>
            Files.TryGetValue(accessPath, out var f) ? Task.FromResult(f.Text) : throw new FileNotFoundException(accessPath);

        public Task<(string Text, bool Truncated)> ReadTailAsync(string accessPath, long maxBytes, CancellationToken cancellationToken)
        {
            var text = Files[accessPath].Text;
            return Task.FromResult(text.Length > maxBytes ? (text[^(int)maxBytes..], true) : (text, false));
        }

        public Task<(string Text, long NextOffset)> ReadFromAsync(string accessPath, long offset, long maxBytes, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(Files[accessPath].Text);
            offset = offset > bytes.Length ? 0 : offset;
            var take = (int)Math.Min(maxBytes, bytes.Length - offset);
            var end = take;
            while (end > 0 && bytes[offset + end - 1] != (byte)'\n')
            {
                end--;
            }

            return Task.FromResult((Encoding.UTF8.GetString(bytes, (int)offset, end), offset + end));
        }

        public IReadOnlyList<ServerFileInfo> List(string accessDirectory, string pattern, int max = 1000) =>
            Files.Where(f => string.Equals(Path.GetDirectoryName(f.Key.Replace('\\', '/'))?.Replace('/', '\\'), accessDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, f.Key.AsSpan(f.Key.LastIndexOf('\\') + 1), ignoreCase: true))
                .Select(f => new ServerFileInfo(f.Key, f.Key[(f.Key.LastIndexOf('\\') + 1)..], Encoding.UTF8.GetByteCount(f.Value.Text), f.Value.Written))
                .OrderByDescending(f => f.LastWriteUtc).Take(max).ToList();

        public bool Exists(string accessPath) => Files.ContainsKey(accessPath);
    }

    private sealed class FixedCertificates(CertificateInfo info) : ICertificateSource
    {
        public CertificateInfo? Find(string thumbprint, string? storeName) => thumbprint == info.Thumbprint ? info : null;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private const string AppHostPath = @"C:\Windows\System32\inetsrv\config\applicationHost.config";

    private static NodeContext Node(params object[] transports) =>
        new(new SiteProfile { Id = "s", Name = "s" }, new NodeProfile { Id = "n1", Name = "N1", Host = "app01", Roles = NodeRoles.App }, transports);

    // ---------- configuration ----------

    [Fact]
    public async Task Config_collector_reads_sites_pools_web_configs_and_certificates()
    {
        var files = new MemoryFiles();
        files.Put(AppHostPath, Fixture("iis", "applicationHost.config"));
        files.Put(@"D:\Rahkaran\Aux1\web.config", Fixture("iis", "web.config"));
        var cert = new CertificateInfo("AB12CD34", "CN=rk.example.local", "CN=Corp CA", Now.AddYears(-1), Now.AddDays(20.5), 2048, "RSA", true, null);
        using var transport = new IisNodeTransport(files, null, new FixedCertificates(cert), isLocal: true, "app01");

        var result = await new IisConfigCollector { Clock = new FixedClock(Now) }.CollectAsync(Node(transport), new CollectContext(), CancellationToken.None);

        result.Status.Kind.Should().Be(CollectorStatusKind.Ok, result.Status.Detail);
        var facts = result.Facts.ToDictionary(f => f.Key, f => f.Value);
        facts["iis.aux_site_count"].Should().Be(2);
        facts["site[AuxWebsite2].logs_time_taken"].Should().Be(false);
        facts["site[AuxWebsite1].is_rahkaran"].Should().Be(true);
        facts["site[AuxWebsite1].aspnetcore"].Should().Be(true);
        facts["site[AuxWebsite1].stdout_log_enabled"].Should().Be(true);
        facts["pool[AuxPool1].serves_aspnetcore"].Should().Be(true);
        facts["pool[AuxPool1].no_managed_code"].Should().Be(true);
        facts["pool[DefaultAppPool].periodic_restart_min"].Should().Be(1740.0);
        facts["pool[RouterPool].enable32bit"].Should().Be(true);
        facts["pool[RouterPool].max_processes"].Should().Be(2);
        facts["pool[AuxPool1].restart_schedule"].Should().Be("03:00");
        facts["farm[RahkaranFarm].enabled_servers"].Should().Be(2);
        facts["farm[EmptyFarm].servers"].Should().Be(0);
        facts["iis.app_segments"].Should().Be("Rahkaran73");
        facts.Where(f => f.Key.EndsWith(".target_segment", StringComparison.Ordinal)).Select(f => f.Value).Should().Contain(["Rahkaran72", "Rahkaran73"]);
        facts["cert[AppRouterWebsite|10.0.0.5:443:rk.example.local].days_left"].Should().Be(20.0);
        facts["cert[AppRouterWebsite|10.0.0.5:443:rk.example.local].expired"].Should().Be(false);

        var payload = (IisConfigPayload)((IReadOnlyDictionary<string, object?>)result.Payload!)["iis"]!;
        payload.WebConfigs.Should().Contain(w => w.Site == "AuxWebsite2" && w.Problem == "no web.config");
        System.Text.Json.JsonSerializer.Serialize(payload).Should().NotContain("Pa55-SQL-Secret!").And.NotContain("Redis-Secret-42");
    }

    [Fact]
    public async Task Unreadable_application_host_config_degrades_with_the_reason()
    {
        using var transport = new IisNodeTransport(new MemoryFiles(), null, null, isLocal: false, "app01");

        var result = await new IisConfigCollector().CollectAsync(Node(transport), new CollectContext(), CancellationToken.None);

        result.Status.Kind.Should().Be(CollectorStatusKind.Unavailable);
        result.Status.Reason.Should().Contain("applicationHost.config");
        result.Status.MissingCapability.Should().Be("iis.config");
    }

    [Fact]
    public void Rewrite_targets_that_name_no_farm_are_flagged_only_for_bare_names()
    {
        IisConfigCollector.LooksLikeFarmName("RahkaranFarm").Should().BeTrue();
        IisConfigCollector.LooksLikeFarmName("rk.example.local").Should().BeFalse();
        IisConfigCollector.LooksLikeFarmName("10.0.0.5").Should().BeFalse();
        IisConfigCollector.LooksLikeFarmName("localhost").Should().BeFalse();
    }

    [Fact]
    public void Numa_nodes_come_from_processor_information_instances()
    {
        IisConfigCollector.NumaNodes(["_Total", "0,_Total", "0,0", "0,1", "1,_Total", "1,0", "1,1"]).Should().Be(2);
        IisConfigCollector.NumaNodes(["_Total"]).Should().BeNull();
    }

    // ---------- cross-node analysis ----------

    private static IReadOnlyDictionary<string, object?> ConfigFacts(string appHostXml, DateTimeOffset now)
    {
        var output = new LiveOutput();
        IisConfigCollector.Apply(ApplicationHostConfigParser.Parse(appHostXml), [], [], false, now, output);
        return output.Facts.ToDictionary(f => f.Key, f => f.Value);
    }

    [Fact]
    public void Drift_names_only_the_settings_that_differ_between_app_nodes()
    {
        var xml = Fixture("iis", "applicationHost.config");
        var a = ConfigFacts(xml, Now);
        var b = ConfigFacts(xml.Replace("startMode=\"AlwaysRunning\"", "startMode=\"OnDemand\"", StringComparison.Ordinal), Now);
        var site = new SiteProfile
        {
            Id = "s", Name = "s",
            Nodes = [new NodeProfile { Id = "a", Name = "A", Host = "a", Roles = NodeRoles.App }, new NodeProfile { Id = "b", Name = "B", Host = "b", Roles = NodeRoles.App },
                new NodeProfile { Id = "arr", Name = "ARR", Host = "arr", Roles = NodeRoles.Arr }],
        };
        var arr = new Dictionary<string, object?> { ["rewrite[global/ToOld].target_segment"] = "Rahkaran72", ["rewrite[global/ToNew].target_segment"] = "Rahkaran73" };

        var extra = new IisSiteAnalyzer().Analyze(site, new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["a"] = a, ["b"] = b, ["arr"] = arr });

        extra["a"].Should().Contain(new Fact("pool[AuxPool1].drift_keys", "startMode"));
        extra["b"].Should().Contain(new Fact("pool[AuxPool1].drift_keys", "startMode"));
        extra["a"].Should().Contain(new Fact("pool[DefaultAppPool].drift_keys", ""));
        extra["arr"].Should().NotContain(f => f.Key.EndsWith(".drift_keys", StringComparison.Ordinal), "the ARR node is not part of the App farm");
        extra["arr"].Should().Contain(new Fact("rewrite[global/ToOld].segment_deployed", false), "no node deploys /Rahkaran72 any more");
        extra["arr"].Should().Contain(new Fact("rewrite[global/ToNew].segment_deployed", true));
    }

    [Theory]
    [InlineData(-30, "", true)]
    [InlineData(-90, "", false)]
    [InlineData(-30, "SCHEDULED", false)]
    public void Recent_worker_starts_outside_the_recycle_schedule_are_unplanned(int minutesAgo, string schedule, bool expected)
    {
        var started = Now.AddMinutes(minutesAgo);
        var s = schedule == "SCHEDULED" ? started.ToLocalTime().AddMinutes(5).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : schedule;

        IisSiteAnalyzer.IsRecentUnplanned(started, s, Now).Should().Be(expected);
    }

    // ---------- runtime ----------

    [Fact]
    public void Runtime_facts_cover_pool_state_worker_age_and_slow_requests()
    {
        var output = new LiveOutput();
        IisRuntimeCollector.Apply(
            [new PoolRuntime("AuxPool1", "Started"), new PoolRuntime("AuxPool2", "Stopped")],
            [new SiteRuntime("AuxWebsite1", 2, "Started")],
            [new WorkerProcessRow(4100, "AuxPool1", "Running", Now.AddMinutes(-20)), new WorkerProcessRow(4200, "AuxPool1", "Running", Now.AddHours(-5))],
            [new LiveRequest(4100, "AuxPool1", 2, "/api/voucher", "POST", "10.0.0.21", "rk", 42_000, "ExecuteRequestHandler", "AspNetCoreModuleV2"),
             new LiveRequest(4100, "AuxPool1", 2, "/login", "GET", "10.0.0.22", "rk", 1_500, "ExecuteRequestHandler", "AspNetCoreModuleV2")],
            null, Now, output);

        FactValue(output, "pool[AuxPool2].running").Should().Be(false);
        FactValue(output, "pool[AuxPool1].wp_count").Should().Be(2);
        FactValue(output, "pool[AuxPool1].wp_youngest_age_s").Should().Be(1200.0);
        FactValue(output, "iis.requests.over30s").Should().Be(1);
        FactValue(output, "iis.requests.max_elapsed_s").Should().Be(42.0);
        FactValue(output, "iis.requests.slowest_url").Should().Be("/api/voucher");
    }

    [Fact]
    public async Task Runtime_on_a_remote_node_is_reported_as_local_only()
    {
        using var transport = new IisNodeTransport(new MemoryFiles(), null, null, isLocal: false, "app01");

        var result = await new IisRuntimeCollector().CollectAsync(Node(transport), new CollectContext(), CancellationToken.None);

        result.Status.Kind.Should().Be(CollectorStatusKind.Unavailable);
        result.Status.Reason.Should().Contain("local only");
        result.Status.MissingCapability.Should().Be("iis.requests");
    }

    // ---------- logs ----------

    [Fact]
    public async Task Log_reads_are_incremental_and_never_take_half_a_line()
    {
        var files = new MemoryFiles();
        const string dir = @"D:\IISLogs\W3SVC2";
        var header = "#Software: Microsoft Internet Information Services 10.0\r\n#Fields: date time cs-method cs-uri-stem sc-status sc-substatus sc-win32-status time-taken c-ip\r\n";
        files.Put(dir + @"\u_ex261015.log", header + "2026-10-15 06:10:00 GET /a 200 0 0 100 10.0.0.1\r\n2026-10-15 06:10:01 GET /b 200 0 0 2");
        var state = new SiteLogState();

        (await IisLogCollector.ReadNewAsync(files, dir, "u_ex*.log", state, CancellationToken.None)).Should().BeNull();
        state.Recent.Should().ContainSingle().Which.UriStem.Should().Be("/a");

        files.Put(dir + @"\u_ex261015.log", header + "2026-10-15 06:10:00 GET /a 200 0 0 100 10.0.0.1\r\n2026-10-15 06:10:01 GET /b 200 0 0 250 10.0.0.1\r\n");
        await IisLogCollector.ReadNewAsync(files, dir, "u_ex*.log", state, CancellationToken.None);

        state.Recent.Select(r => (r.UriStem, r.TimeTakenMs)).Should().Equal(("/a", 100), ("/b", 250));
    }

    [Fact]
    public void Latency_regressions_compare_a_stem_with_its_own_earlier_windows()
    {
        var state = new SiteLogState { File = "u_ex.log" };
        DateTimeOffset at = Now;
        void Window(int ms)
        {
            at = at.Add(IisLogCollector.Window);
            for (var i = 0; i < 30; i++)
            {
                state.Recent.Add(new W3CRecord(at.AddSeconds(-i - 1), "GET", "/api/voucher", 200, 0, 0, ms, "10.0.0.1", null, 80));
            }
        }

        foreach (var ms in new[] { 200, 220, 210 })
        {
            Window(ms);
            IisLogCollector.Analyze("AuxWebsite1", state, at, null, [], null).Regressions.Should().BeEmpty();
        }

        Window(900);
        var summary = IisLogCollector.Analyze("AuxWebsite1", state, at, null, [], null);

        summary.Regressions.Should().ContainSingle().Which.Should().Be(new StemRegression("/api/voucher", 30, 900, 210));
    }

    [Theory]
    [InlineData("502.5", true)]
    [InlineData("502.3", true)]
    [InlineData("500.30", true)]
    [InlineData("500.37", true)]
    [InlineData("500.38", false)]
    [InlineData("500.19", false)]
    [InlineData("503", false)]
    public void Ancm_statuses_are_recognised(string status, bool expected) => IisLogCollector.IsAncmStatus(status).Should().Be(expected);

    [Fact]
    public async Task Log_collector_derives_error_rate_bursts_and_httperr_reasons()
    {
        var files = new MemoryFiles();
        files.Put(AppHostPath, Fixture("iis", "applicationHost.config"));
        files.Put(@"D:\IISLogs\W3SVC2\u_ex261015.log", Fixture("iis", "u_ex261015.log"));
        files.Put(@"C:\Windows\System32\LogFiles\HTTPERR\httperr1.log", Fixture("iis", "httperr1.log"));
        using var transport = new IisNodeTransport(files, null, null, isLocal: true, "app01");
        var collector = new IisLogCollector { Clock = new FixedClock(new DateTimeOffset(2026, 10, 15, 6, 15, 0, TimeSpan.Zero)) };

        var result = await collector.CollectAsync(Node(transport), new CollectContext(), CancellationToken.None);

        var facts = result.Facts.ToDictionary(f => f.Key, f => f.Value);
        facts["site[AuxWebsite1].burst_503"].Should().Be(true);
        facts["site[AuxWebsite1].status503_15m"].Should().Be(12L);
        facts["site[AuxWebsite1].ancm_errors_15m"].Should().Be(2L);
        ((double)facts["site[AuxWebsite1].err5xx_pct_15m"]!).Should().BeGreaterThan(5);
        facts["iis.httperr.queue_full_60m"].Should().Be(4L);
        facts["iis.httperr.server_side_60m"].Should().Be(5L, "QueueFull ×4 and AppOffline ×1 count; Timer_ConnectionIdle is client side");
    }

    // ---------- IIS counters (WindowsPerfCollector) ----------

    [Fact]
    public void Iis_counters_become_pool_facts_with_a_sustained_queue()
    {
        var state = new WindowsPerfState();
        LiveOutput Tick(int seconds, double queue, double rejected)
        {
            var output = new LiveOutput();
            WindowsPerfCollector.Apply(state,
            [
                new MetricReading("win.iis.queue.size", "AuxPool1", queue),
                new MetricReading("win.iis.queue.rejected", "AuxPool1", rejected),
                new MetricReading("win.iis.w3wp.active_requests", "4100_AuxPool1", 7),
                new MetricReading("win.iis.w3wp.active_requests", "4200_AuxPool1", 5),
                new MetricReading("win.iis.pool.state", "AuxPool2", 5),
            ], Now.AddSeconds(seconds), output);
            return output;
        }

        Tick(0, 3, 10);
        Tick(15, 4, 10);
        var output = Tick(30, 2, 12);

        FactValue(output, "pool[AuxPool1].queue_size_min30").Should().Be(2.0);
        FactValue(output, "pool[AuxPool1].rejected_increasing").Should().Be(true);
        FactValue(output, "pool[AuxPool1].active_requests").Should().Be(12.0);
        FactValue(output, "pool[AuxPool2].running").Should().Be(false);
        WindowsMetricCatalog.PoolOfW3wpInstance("4100_Aux_Pool_1").Should().Be("Aux_Pool_1");
    }
}
