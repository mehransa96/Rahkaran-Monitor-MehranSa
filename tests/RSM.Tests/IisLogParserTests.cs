using RSM.Parsers.Iis;

namespace RSM.Tests;

/// <summary>
/// W3C / HTTPERR / stdout / FREB parsing against hand-built fixtures. The expected percentiles were computed by hand
/// with the nearest-rank definition (value at rank ⌈p/100 · n⌉ of the sorted time-taken values).
/// </summary>
public class IisLogParserTests
{
    private static string FixturePath(params string[] path) => Path.Combine([AppContext.BaseDirectory, "Fixtures", .. path]);

    private static W3CSummary Analyze(DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var analyzer = new W3CAnalyzer(from, to);
        foreach (var line in File.ReadLines(FixturePath("iis", "u_ex261015.log")))
        {
            analyzer.AddLine(line);
        }

        return analyzer.Summarize();
    }

    [Fact]
    public void Percentiles_by_url_stem_match_the_hand_verified_sample()
    {
        var summary = Analyze();
        var voucher = summary.UrlStems.Single(u => u.UriStem == "/api/voucher");
        var login = summary.UrlStems.Single(u => u.UriStem == "/login");

        // /api/voucher sorted: 80 90 95 100 110 120 150 300 2000 5000 → p50 = 5th = 110, p95 = 10th, p99 = 10th.
        voucher.Should().Be(new UrlStemStats("/api/voucher", 10, 110, 5000, 5000, 5000, 0, false));
        // /login sorted (after the header change adds 60 and 70): 30 40 50 60 60 70 70 → p50 = 4th = 60, p95 = p99 = 7th = 70.
        login.Should().Be(new UrlStemStats("/login", 7, 60, 70, 70, 70, 0, false));
        summary.Slowest!.UriStem.Should().Be("/api/report", "a 40 s request (client disconnect) dominates its p95");
    }

    [Fact]
    public void Status_distribution_errors_disconnects_bursts_and_clients()
    {
        var summary = Analyze();

        summary.Requests.Should().Be(33);
        summary.LinesSkipped.Should().Be(1, "the malformed line is counted, not parsed");
        summary.HasTimeTaken.Should().BeTrue();
        summary.Errors5xx.Should().Be(14);
        summary.Errors4xx.Should().Be(1);
        summary.ClientDisconnects.Should().Be(1, "win32 status 64");
        summary.Statuses.ToDictionary(s => s.Status, s => s.Count).Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["200"] = 18, ["503"] = 12, ["502.5"] = 1, ["500.30"] = 1, ["404"] = 1,
        });
        summary.Statuses.Single(s => s.Status == "502.5").Meaning!.En.Should().Contain("failed to start");
        summary.Statuses.Single(s => s.Status == "500.30").Meaning!.En.Should().Contain("in-process");
        summary.Bursts.Should().ContainSingle().Which.Should().Be(new MinuteBucket(new DateTimeOffset(2026, 10, 15, 6, 5, 0, TimeSpan.Zero), 12, 12, 12));
        summary.TopClients[0].Should().Be(new ClientCount("10.0.0.21", 14));
        summary.TopClients.Should().Contain(new ClientCount("192.168.5.40", 2), "X-Forwarded-For wins over the proxy's c-ip");
        summary.ErrorRate5xx.Should().BeApproximately(100.0 * 14 / 33, 1e-9);
    }

    [Fact]
    public void Time_range_filters_requests()
    {
        var summary = Analyze(new DateTimeOffset(2026, 10, 15, 6, 5, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 15, 6, 5, 59, TimeSpan.Zero));

        summary.Requests.Should().Be(12);
        summary.UrlStems.Should().ContainSingle(u => u.UriStem == "/health");
    }

    [Theory]
    [InlineData(new[] { 5 }, 50, 5)]
    [InlineData(new[] { 1, 2, 3, 4 }, 50, 2)]
    [InlineData(new[] { 1, 2, 3, 4 }, 75, 3)]
    [InlineData(new[] { 1, 2, 3, 4 }, 100, 4)]
    [InlineData(new int[0], 95, 0)]
    public void Nearest_rank_percentile(int[] sorted, double p, int expected) => W3CAnalyzer.Percentile(sorted, p).Should().Be(expected);

    [Fact]
    public void Httperr_counts_reasons_by_queue_and_hour()
    {
        var analyzer = new HttpErrAnalyzer();
        foreach (var line in File.ReadLines(FixturePath("iis", "httperr1.log")))
        {
            analyzer.AddLine(line);
        }

        var reasons = analyzer.Reasons();
        reasons.Select(r => (r.Reason, r.Count)).Should().Equal(("QueueFull", 4), ("Timer_ConnectionIdle", 3), ("AppOffline", 1));
        reasons[0].TopQueue.Should().Be("AuxPool1");
        reasons[0].Meaning!.En.Should().Contain("queue is full");
        HttpErrAnalyzer.IsServerSide("QueueFull").Should().BeTrue();
        HttpErrAnalyzer.IsServerSide("Timer_ConnectionIdle").Should().BeFalse();
        analyzer.Hours().Should().Contain(new HttpErrHour(new DateTimeOffset(2026, 10, 15, 7, 0, 0, TimeSpan.Zero), "Timer_ConnectionIdle", 3));
    }

    [Fact]
    public void Stdout_tail_counts_errors_and_keeps_the_last_ones()
    {
        const string text = "info: Started\r\nwarn: slow\r\nfail: Microsoft.AspNetCore.Server.IIS[2]\r\nUnhandled exception. System.InvalidOperationException\r\ninfo: ok\r\n";

        var summary = StdoutLog.Summarize("stdout_1.log", text, truncated: true, keep: 1);

        summary.ErrorLines.Should().Be(2);
        summary.WarningLines.Should().Be(1);
        summary.LastErrors.Should().Equal("Unhandled exception. System.InvalidOperationException");
        summary.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Existing_freb_files_are_read_not_enabled()
    {
        var entry = FrebLog.Parse("fr000001.xml", File.ReadAllText(FixturePath("iis", "fr000001.xml")))!;

        entry.Should().Be(new FrebEntry("fr000001.xml", "http://rk.example.local:8081/api/voucher", "POST", 200, null, 15032, "AuxPool1", "TIME_TAKEN",
            new DateTimeOffset(2026, 10, 15, 6, 20, 1, 123, TimeSpan.Zero)));
        FrebLog.Parse("x.xml", "<html/>").Should().BeNull();
        FrebLog.Parse("x.xml", "not xml").Should().BeNull();
    }
}
