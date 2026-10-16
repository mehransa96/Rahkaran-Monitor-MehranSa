using RSM.Collectors.Sql.Live;
using RSM.Core.Model;
using RSM.Parsers.Ssrs;

namespace RSM.Tests;

public class SsrsTests
{
    private static ExecutionLogRow Run(string report, int ms, int data, int processing, int rendering, string status = "rsSuccess", string user = @"CORP\a", int hour = 9) =>
        new() { ItemPath = report, DurationMs = ms, DataMs = data, ProcessingMs = processing, RenderingMs = rendering, Status = status, UserName = user, HourOfDay = hour, Source = "Live" };

    private static readonly ExecutionLogRow[] Rows =
    [
        // Ledger: 10 runs, data-bound; sorted totals 10..100 s → p50 = 5th = 50 s, p95 = 10th = 100 s.
        .. Enumerable.Range(1, 10).Select(i => Run("/Finance/Ledger", i * 10_000, i * 9_000, 500, 500, user: i % 3 == 0 ? @"CORP\b" : @"CORP\a")),
        // Invoice: 4 runs, render-bound, one failure.
        Run("/Sales/Invoice", 2000, 200, 300, 1500), Run("/Sales/Invoice", 3000, 200, 300, 2500, hour: 10),
        Run("/Sales/Invoice", 4000, 200, 300, 3500, hour: 10), Run("/Sales/Invoice", 500, 0, 0, 0, status: "rsProcessingAborted", hour: 11),
    ];

    [Fact]
    public void Execution_log_is_aggregated_per_report_with_phase_and_percentiles()
    {
        var summary = ExecutionLogAggregator.Aggregate(Rows);

        summary.Executions.Should().Be(14);
        summary.Failures.Should().Be(1);
        summary.FailurePct.Should().BeApproximately(7.14, 0.01);
        var ledger = summary.Reports.Single(r => r.ItemPath == "/Finance/Ledger");
        ledger.Should().Match<ReportStats>(r => r.Executions == 10 && r.P50Ms == 50_000 && r.P95Ms == 100_000 && r.MaxMs == 100_000 && r.Dominant == ReportPhase.Data && r.TopUser == @"CORP\a");
        var invoice = summary.Reports.Single(r => r.ItemPath == "/Sales/Invoice");
        invoice.Dominant.Should().Be(ReportPhase.Rendering);
        invoice.Failures.Should().Be(1);
        summary.Reports[0].ItemPath.Should().Be("/Finance/Ledger", "sorted by p95, slowest first");
        summary.ByHour.Should().Equal(new CountBy("09", 11), new CountBy("10", 2), new CountBy("11", 1));
        summary.FailureStatuses.Should().Equal(new CountBy("rsProcessingAborted", 1));
    }

    [Theory]
    [InlineData("Failure sending mail: The SMTP server rejected the address.", true)]
    [InlineData("Error: the path is not valid", true)]
    [InlineData("Mail sent to finance@example.local", false)]
    [InlineData("New Subscription", false)]
    [InlineData(null, false)]
    public void Subscription_failures_are_recognised(string? status, bool failed) => ExecutionLogAggregator.IsFailedSubscriptionStatus(status).Should().Be(failed);

    [Fact]
    public void Facts_count_slow_data_bound_reports_and_failed_subscriptions()
    {
        var output = new LiveOutput();
        SsrsCollector.Apply("ReportServer", Rows,
            [new SubscriptionRow { Description = "Daily sales", LastStatus = "Failure sending mail: x" }, new SubscriptionRow { Description = "Old", LastStatus = "Error", InactiveFlags = 1 }],
            output);
        var facts = output.Facts.ToDictionary(f => f.Key, f => f.Value);

        facts["ssrs.slow_reports"].Should().Be(1, "only the ledger has ≥ 5 runs and p95 > 60 s");
        facts["ssrs.slow_reports_data_bound"].Should().Be(1);
        facts["ssrs.subscriptions_failed"].Should().Be(1, "inactive subscriptions are ignored");
        facts["ssrs.subscriptions_failed_names"].Should().Be("Daily sales");
        ((string)facts["ssrs.slow_reports_top"]!).Should().Contain("/Finance/Ledger p95 100 s (Data)");
    }
}
