using RSM.Collectors.Windows;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Parsers.Events;

namespace RSM.Tests;

public class EventsAndServicesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RSM.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("RSM.sln not found");
    }

    private static object? FactValue(LiveOutput output, string key) => output.Facts.LastOrDefault(f => f.Key == key)?.Value;

    // ---------- knowledge base ----------

    /// <summary>Every event named in spec §9.8, as (provider as Windows logs it, id, level).</summary>
    public static TheoryData<string, int, int> SpecEvents => new()
    {
        { "Microsoft-Windows-Kernel-Power", 41, 1 },
        { "EventLog", 6008, 2 },
        { "User32", 1074, 4 },
        { "Microsoft-Windows-Resource-Exhaustion-Detector", 2004, 3 },
        { "srv", 2013, 3 },
        { "Service Control Manager", 7031, 2 },
        { "Service Control Manager", 7034, 2 },
        { "Service Control Manager", 7000, 2 },
        { "Service Control Manager", 7009, 2 },
        { "Service Control Manager", 7023, 2 },
        { "Application Error", 1000, 2 },
        { ".NET Runtime", 1026, 2 },
        { "Windows Error Reporting", 1001, 4 },
        { "Microsoft-Windows-WAS", 5002, 2 },
        { "Microsoft-Windows-WAS", 5009, 3 },
        { "Microsoft-Windows-WAS", 5010, 3 },
        { "Microsoft-Windows-WAS", 5011, 3 },
        { "Microsoft-Windows-WAS", 5013, 3 },
        { "WAS", 5117, 4 },
        { "ASP.NET 4.0.30319.0", 1309, 3 },
        { "IIS AspNetCore Module V2", 1005, 2 },
        { "disk", 7, 2 },
        { "disk", 11, 2 },
        { "disk", 51, 3 },
        { "disk", 153, 3 },
        { "Ntfs", 55, 2 },
        { "Microsoft-Windows-Ntfs", 98, 2 },
        { "MSSQLSERVER", 823, 2 },
        { "MSSQL$RK", 824, 2 },
        { "MSSQLSERVER", 833, 4 },
        { "MSSQLSERVER", 17883, 2 },
        { "MSSQLSERVER", 701, 2 },
        { "MSSQLSERVER", 9002, 2 },
        { "MSSQLSERVER", 18456, 0 },
        { "Schannel", 36887, 2 },
        { "Schannel", 36874, 2 },
        { "Microsoft-Windows-Time-Service", 129, 3 },
    };

    [Theory]
    [MemberData(nameof(SpecEvents))]
    public void Knowledge_base_covers_every_event_named_in_the_spec(string provider, int id, int level)
    {
        var entry = EventKnowledgeBase.Find(provider, id, level);

        entry.Should().NotBeNull($"{provider} {id} is listed in spec §9.8");
    }

    [Fact]
    public void Every_kb_entry_is_bilingual_and_its_rule_exists()
    {
        var rulesDoc = File.ReadAllText(Path.Combine(RepoRoot(), "RULES.md"));
        EventKnowledgeBase.All.Select(e => e.Key).Should().OnlyHaveUniqueItems();
        foreach (var e in EventKnowledgeBase.All)
        {
            foreach (var text in new[] { e.Title, e.Explanation, e.NextStep })
            {
                text.Fa.Should().NotBeNullOrWhiteSpace(e.Key);
                text.En.Should().NotBeNullOrWhiteSpace(e.Key);
            }

            if (e.RuleId is not null)
            {
                rulesDoc.Should().Contain($"| {e.RuleId} |", $"{e.Key} links to a rule");
            }
        }
    }

    [Fact]
    public void Healthy_and_information_events_of_problem_only_entries_are_not_matched()
    {
        EventKnowledgeBase.Find("Microsoft-Windows-Ntfs", 98, 4).Should().BeNull("'volume is healthy' is information");
        EventKnowledgeBase.Find("IIS AspNetCore Module V2", 1001, 4).Should().BeNull("ANCM start-up information is not a failure");
        EventKnowledgeBase.Find("Microsoft-Windows-Time-Service", 37, 4).Should().BeNull();
        EventKnowledgeBase.Find("MSSQL$RK", 18456, 0)!.Key.Should().Be("mssql-18456", "failed logins are audit events at level 0");
    }

    [Theory]
    [InlineData("Faulting application name: w3wp.exe, version: 10.0", true)]
    [InlineData("Application: dotnet.exe CoreCLR Version: 8.0", true)]
    [InlineData("Faulting application name: sqlservr.exe", false)]
    [InlineData("dotnetfx installer", false)]
    public void Web_worker_processes_are_recognised(string message, bool expected) => EventKnowledgeBase.NamesWebWorker(message).Should().Be(expected);

    [Fact]
    public void Messages_are_masked_collapsed_and_truncated()
    {
        EventDigest.CleanMessage("Login failed.\r\n  Password=Hunter2;  user=sa")!.Should().Be("Login failed. Password=••••••; user=sa");
        EventDigest.CleanMessage(new string('x', 5000))!.Length.Should().Be(EventDigest.MaxMessageLength + 1);
        EventDigest.CleanMessage("  ").Should().BeNull();
    }

    // ---------- the query ----------

    [Fact]
    public void Event_query_is_built_from_integers_and_a_validated_log_name()
    {
        var xpath = new EventQuery("System", new DateTimeOffset(2026, 10, 15, 6, 0, 0, TimeSpan.Zero), [1074, 41], true, 100).ToXPath();

        xpath.Should().Be("*[System[(Level=1 or Level=2 or Level=3 or EventID=41 or EventID=1074) and TimeCreated[@SystemTime>'2026-10-15T06:00:00.0000000Z']]]");
        new EventQuery("Microsoft-Windows-IIS-Configuration/Operational", Now, [1], false, 1).ToXPath().Should().Contain("EventID=1");
    }

    [Theory]
    [InlineData("System']] or [x", false)]
    [InlineData("Sys\\tem", false)]
    [InlineData("", false)]
    [InlineData("System", true)]
    public void Event_query_rejects_anything_but_a_channel_name(string log, bool valid)
    {
        var build = () => new EventQuery(log, Now, [1], false, 10).ToXPath();

        if (valid)
        {
            build.Should().NotThrow();
        }
        else
        {
            build.Should().Throw<ArgumentException>();
        }
    }

    // ---------- the collector ----------

    private static EventRecordData Ev(string log, string provider, int id, int level, double hoursAgo, string? message = null) =>
        new(log, provider, id, level, Now.AddHours(-hoursAgo), message);

    [Fact]
    public void Facts_count_kb_events_in_their_windows_and_web_worker_crashes_only()
    {
        var state = new EventLogState();
        state.LogsRead.Add("System");
        state.LogsRead.Add("Application");
        var output = new LiveOutput();

        WindowsEventLogCollector.Apply(state,
        [
            Ev("System", "Microsoft-Windows-Kernel-Power", 41, 1, 72),
            Ev("System", "EventLog", 6008, 2, 72),
            Ev("System", "Microsoft-Windows-Kernel-Power", 41, 1, 24 * 8), // older than 7 days: dropped
            Ev("System", "Microsoft-Windows-WAS", 5002, 2, 2),
            Ev("System", "Microsoft-Windows-WAS", 5009, 3, 2),
            Ev("System", "Microsoft-Windows-WAS", 5009, 3, 30), // older than 24 h: not counted
            Ev("System", "Service Control Manager", 7031, 2, 1),
            Ev("Application", "Application Error", 1000, 2, 1, "Faulting application name: w3wp.exe"),
            Ev("Application", "Application Error", 1000, 2, 1, "Faulting application name: notepad.exe"),
            Ev("Application", ".NET Runtime", 1026, 2, 3, "Application: w3wp.exe"),
            Ev("Application", "IIS AspNetCore Module V2", 1005, 2, 3, "Failed to start application"),
            Ev("Application", "MSSQLSERVER", 18456, 0, 1, "Login failed for user 'rk'."),
        ], Now, "n1", output);

        FactValue(output, "evt.unexpected_shutdowns_7d").Should().Be(1L);
        FactValue(output, "evt.kernel_power_41_7d").Should().Be(1L);
        FactValue(output, "evt.was_failures_24h").Should().Be(2L);
        FactValue(output, "evt.was_rapid_fail_24h").Should().Be(1L);
        FactValue(output, "evt.scm_unexpected_24h").Should().Be(1L);
        FactValue(output, "evt.web_worker_crash_24h").Should().Be(2L, "notepad crashes are not web workers");
        FactValue(output, "evt.ancm_errors_24h").Should().Be(1L);
        FactValue(output, "evt.resource_exhaustion_24h").Should().Be(0L);
        state.Window.Should().HaveCount(10, "the 8-day-old kernel event and the 30-hour-old WAS event are pruned");

        output.Events.Should().Contain(e => e.Source == "Microsoft-Windows-WAS" && e.Code == "5009" && e.Layer == Layer.Iis && e.Count == 2);
        output.Events.Should().Contain(e => e.Source == "MSSQLSERVER" && e.Layer == Layer.Sql && e.Severity == Severity.Info);
        output.Events.Should().Contain(e => e.Source == "Service Control Manager" && e.Layer == Layer.Services);
    }

    [Fact]
    public void Facts_of_an_unread_log_stay_unknown()
    {
        var state = new EventLogState();
        state.LogsRead.Add("System");
        var output = new LiveOutput();

        WindowsEventLogCollector.Apply(state, [], Now, "n1", output);

        output.Facts.Should().Contain(f => f.Key == "evt.resource_exhaustion_24h");
        output.Facts.Should().NotContain(f => f.Key == "evt.web_worker_crash_24h", "Application was not readable: unknown, not zero");
    }

    private sealed class FakeEvents(Func<EventQuery, IReadOnlyList<EventRecordData>> handler) : IEventLogSource
    {
        public List<EventQuery> Queries { get; } = [];

        public Task<(IReadOnlyList<EventRecordData> Events, bool Truncated)> ReadAsync(EventQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult((handler(query), false));
        }
    }

    private sealed class FakeCim(Func<CimQuery, IReadOnlyList<CimRow>> handler) : ICimQuery
    {
        public string Protocol => "fake";

        public Task<IReadOnlyList<CimRow>> QueryAsync(CimQuery query, CancellationToken cancellationToken) => Task.FromResult(handler(query));
    }

    private sealed class NoCounters : IWindowsCounters
    {
        public string Transport => "test";

        public Task<IReadOnlyList<MetricReading>> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricReading>>([]);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static NodeContext Node(SiteSettings? settings, params object[] transports) =>
        new(new SiteProfile { Id = "s", Name = "s", Settings = settings ?? new SiteSettings() },
            new NodeProfile { Id = "n1", Name = "N1", Host = "srv", Roles = NodeRoles.App }, transports);

    [Fact]
    public async Task Reads_24_hours_then_incrementally_and_reports_an_unreadable_log()
    {
        var clock = new FixedClock(Now);
        var events = new FakeEvents(q => q.Log == "Application"
            ? throw new UnauthorizedAccessException("denied")
            : q.IncludeProblems ? [Ev("System", "srv", 2013, 3, 1)] : [Ev("System", "EventLog", 6008, 2, 100)]);
        using var transport = new WindowsNodeTransport(new FakeCim(_ => []), new NoCounters(), null, false, events);
        var collector = new WindowsEventLogCollector { Clock = clock };

        var first = await collector.CollectAsync(Node(null, transport), new CollectContext(), CancellationToken.None);

        first.Status.Kind.Should().Be(CollectorStatusKind.Partial);
        first.Status.Detail.Should().Contain("Application: UnauthorizedAccessException");
        events.Queries.Should().Contain(q => q.Log == "System" && q.IncludeProblems && q.SinceUtc == Now.AddHours(-24));
        events.Queries.Should().Contain(q => q.Log == "System" && !q.IncludeProblems && q.SinceUtc == Now.AddDays(-7) && q.Ids.Contains(41));
        first.Facts.Should().Contain(new Fact("evt.low_disk_24h", 1L)).And.Contain(new Fact("evt.unexpected_shutdowns_7d", 1L));
        first.Facts.Should().NotContain(f => f.Key == "evt.web_worker_crash_24h");
        var payload = (EventLogPayload)((IReadOnlyDictionary<string, object?>)first.Payload!)["events"]!;
        payload.LogsFailed.Should().Equal("Application");
        payload.Groups.Should().Contain(g => g.KbKey == "srv-2013");

        events.Queries.Clear();
        clock.Now = Now.AddMinutes(2);
        await collector.CollectAsync(Node(null, transport), new CollectContext(), CancellationToken.None);

        events.Queries.Should().ContainSingle(q => q.Log == "System").Which.SinceUtc.Should().Be(Now.AddHours(-1), "the next read starts at the newest event seen");
    }

    [Fact]
    public async Task Missing_event_log_session_degrades_with_the_reason()
    {
        using var transport = new WindowsNodeTransport(new FakeCim(_ => []), new NoCounters(), null, false);

        var result = await new WindowsEventLogCollector().CollectAsync(Node(null, transport), new CollectContext(), CancellationToken.None);

        result.Status.Kind.Should().Be(CollectorStatusKind.Unavailable);
        result.Status.Reason.Should().Contain("Event Log Readers");
    }

    // ---------- services ----------

    private static CimRow Svc(string name, string display, string state, string mode, int pid = 0) =>
        CimRow.Of(("Name", name), ("DisplayName", display), ("State", state), ("StartMode", mode), ("StartName", "LocalSystem"), ("ProcessId", (uint)pid));

    private static readonly CimRow[] Services =
    [
        Svc("MSSQLSERVER", "SQL Server (MSSQLSERVER)", "Running", "Auto", 1200),
        Svc("SQLSERVERAGENT", "SQL Server Agent (MSSQLSERVER)", "Stopped", "Auto"),
        Svc("SQLBrowser", "SQL Server Browser", "Running", "Auto", 1300),
        Svc("W3SVC", "World Wide Web Publishing Service", "Running", "Auto", 900),
        Svc("CodeMeter.exe", "CodeMeter Runtime Server", "Stopped", "Auto"),
        Svc("wuauserv", "Windows Update", "Stopped", "Manual"),
        Svc("RkAttendance", "Rahkaran Attendance", "Stopped", "Auto"),
        Svc("Spooler", "Print Spooler", "Running", "Auto", 700),
    ];

    [Fact]
    public void Watched_services_are_matched_by_name_or_display_name_and_settings()
    {
        var settings = new SiteSettings { AttendanceServicePattern = "*Attendance*" };

        var watched = WindowsServicesCollector.Match(Services, WindowsServicesCollector.WatchList(settings));

        watched.Select(s => s.Name).Should().BeEquivalentTo(["MSSQLSERVER", "SQLSERVERAGENT", "SQLBrowser", "W3SVC", "CodeMeter.exe", "wuauserv", "RkAttendance"]);
        watched.Single(s => s.Name == "RkAttendance").Should().Match<ServiceInfo>(s => s.Category == ServiceCategory.Attendance && s.Unverified);
        watched.Single(s => s.Name == "SQLSERVERAGENT").ProcessId.Should().BeNull();
        WindowsServicesCollector.Match(Services, WindowsServicesCollector.WatchList(new SiteSettings())).Should().NotContain(s => s.Category == ServiceCategory.Attendance,
            "the attendance pattern is unverified and empty by default");
    }

    [Fact]
    public async Task Service_facts_flag_automatic_but_stopped_and_compute_uptime()
    {
        var cim = new FakeCim(q => q.ClassName == "Win32_Service" ? Services
            : [CimRow.Of(("ProcessId", 1200u), ("CreationDate", Now.AddMinutes(-30).UtcDateTime)), CimRow.Of(("ProcessId", 4u), ("CreationDate", Now.AddDays(-9).UtcDateTime))]);
        using var transport = new WindowsNodeTransport(cim, new NoCounters(), null, false);
        var collector = new WindowsServicesCollector { Clock = new FixedClock(Now) };

        var result = await collector.CollectAsync(Node(new SiteSettings { AttendanceServicePattern = "*Attendance*" }, transport), new CollectContext(), CancellationToken.None);

        result.Status.Kind.Should().Be(CollectorStatusKind.Ok);
        result.Facts.Should().Contain(new Fact("svc[SQLSERVERAGENT].auto_stopped", true));
        result.Facts.Should().Contain(new Fact("svc[MSSQLSERVER].uptime_s", 1800.0));
        result.Facts.Should().Contain(new Fact("svc.auto_stopped_count", 2), "Agent and CodeMeter; attendance has its own rule and wuauserv is Manual");
        result.Facts.Should().Contain(new Fact("svc.codemeter_present", true)).And.Contain(new Fact("svc.codemeter_running", false));
        result.Facts.Should().Contain(new Fact("svc.attendance_stopped", true));
        result.Facts.Should().Contain(new Fact("svc.sqlbrowser_running", true)).And.Contain(new Fact("svc.sql_instance_count", 1));
        result.Facts.Should().Contain(new Fact("svc.rabbitmq_present", false));
    }
}
