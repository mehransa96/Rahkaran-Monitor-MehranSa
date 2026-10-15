using RSM.Core.Model;
using RSM.Parsers.ErrorLog;

namespace RSM.Tests;

public class ErrorLogParserTests
{
    private static ErrorLogSummary Fixture(DateTime? since = null) =>
        ErrorLogParser.Analyze(ErrorLogParser.ParseFileText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "errorlog", "ERRORLOG.txt"))), since);

    [Theory]
    [InlineData("823", ErrorLogCategory.IoCorruption, Severity.Critical)]
    [InlineData("824", ErrorLogCategory.IoCorruption, Severity.Critical)]
    [InlineData("825", ErrorLogCategory.IoRetry, Severity.Warning)]
    [InlineData("833", ErrorLogCategory.SlowIo, Severity.Warning)]
    [InlineData("17883", ErrorLogCategory.NonYielding, Severity.Critical)]
    [InlineData("stackdump", ErrorLogCategory.StackDump, Severity.Critical)]
    [InlineData("9002", ErrorLogCategory.LogFull, Severity.Critical)]
    [InlineData("701", ErrorLogCategory.Memory, Severity.Critical)]
    [InlineData("3041", ErrorLogCategory.BackupFailure, Severity.Critical)]
    [InlineData("18456", ErrorLogCategory.FailedLogin, Severity.Warning)]
    [InlineData("cacheflush", ErrorLogCategory.CacheFlush, Severity.Info)]
    public void Classifies_known_errors(string code, ErrorLogCategory category, Severity severity)
    {
        var e = Fixture().Events.First(x => x.Code == code);

        e.Category.Should().Be(category);
        e.Severity.Should().Be(severity);
    }

    [Fact]
    public void Unknown_errors_are_not_reported() => Fixture().CountsByCode.Should().NotContainKey("50000");

    [Fact]
    public void Header_and_message_are_paired_and_details_extracted()
    {
        var summary = Fixture();

        summary.Events.Single(e => e.Code == "823").Details["file"].Should().Be(@"E:\Data\Rahkaran.mdf");
        summary.Events.Single(e => e.Code == "9002").Details.Should().Contain("database", "Rahkaran").And.Contain("log_reuse_wait", "LOG_BACKUP");
        summary.Events.Single(e => e.Code == "833").Details.Should().Contain("occurrences", "3").And.Contain("file", @"E:\Log\Rahkaran_log.ldf");
        summary.Events.Single(e => e.Code == "cacheflush").Details["reason"].Should().Be("DBCC FREEPROCCACHE");
    }

    [Fact]
    public void Failed_logins_are_grouped_by_login_state_and_client()
    {
        var groups = Fixture().FailedLogins;

        groups.Should().HaveCount(2);
        groups[0].Should().BeEquivalentTo(new { Login = "sa", State = 8, Client = "10.0.0.25", Count = 2 });
        groups[1].Should().BeEquivalentTo(new { Login = "olduser", State = 5, Client = "<local machine>", Count = 1 });
    }

    [Fact]
    public void Trace_flags_from_startup_parameters_and_dbcc_traceon() =>
        Fixture().TraceFlags.Should().Equal(1117, 1222, 3226);

    [Fact]
    public void Window_filter_drops_older_events_but_keeps_startup_trace_flags()
    {
        var summary = Fixture(since: new DateTime(2026, 10, 14));

        summary.Events.Select(e => e.Code).Should().Equal("cacheflush");
        summary.TraceFlags.Should().Contain(1117);
        summary.OldestEntry.Should().Be(new DateTime(2026, 10, 12, 8, 0, 1, 120));
    }

    [Fact]
    public void Continuation_lines_join_the_previous_entry()
    {
        var entries = ErrorLogParser.ParseFileText("2026-01-01 00:00:00.00 Server      line one\r\n\tline two\r\n");

        entries.Should().ContainSingle().Which.Text.Should().Be("line one\nline two");
    }

    [Fact]
    public void Works_on_xp_readerrorlog_rows()
    {
        var at = new DateTime(2026, 10, 14, 9, 0, 0);
        var rows = new[]
        {
            new ErrorLogEntry(at, "spid5", "Error: 9002, Severity: 17, State: 2."),
            new ErrorLogEntry(at, "spid5", "The transaction log for database 'Rk' is full due to 'ACTIVE_TRANSACTION'."),
        };

        ErrorLogParser.Analyze(rows).Events.Single().Details["log_reuse_wait"].Should().Be("ACTIVE_TRANSACTION");
    }
}
