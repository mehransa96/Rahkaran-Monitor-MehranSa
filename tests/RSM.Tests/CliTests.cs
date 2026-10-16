using FluentAssertions;
using RSM.Cli;
using RSM.Reporting;
using RSM.Safety.Files;

namespace RSM.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rsm-cli-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("30m", 1800)]
    [InlineData("8h", 28800)]
    [InlineData("2h30m", 9000)]
    [InlineData("1d", 86400)]
    [InlineData("01:30:00", 5400)]
    public void Durations_are_parsed(string text, int seconds) => CliOptions.ParseDuration(text).Should().Be(TimeSpan.FromSeconds(seconds));

    [Theory]
    [InlineData("")]
    [InlineData("h")]
    [InlineData("8 hours")]
    public void Bad_durations_are_rejected(string text) => CliOptions.ParseDuration(text).Should().BeNull();

    [Fact]
    public void Commands_and_options_are_parsed_and_validated()
    {
        CliOptions.Parse(["--collect", "--site", "HQ", "--duration", "8h"]).Should().Match<CliOptions>(o =>
            o.Command == CliCommand.Collect && o.Site == "HQ" && o.Duration == TimeSpan.FromHours(8) && o.Error == null);
        CliOptions.Parse(["--snapshot", "--site", "HQ", "--heavy", "--portable"]).Should().Match<CliOptions>(o => o.Command == CliCommand.Snapshot && o.Heavy && o.Portable);
        CliOptions.Parse(["--export", "abc", "--format", "bundle", "--lang", "en", "--mask"]).Should().Match<CliOptions>(o =>
            o.Command == CliCommand.Export && o.SnapshotId == "abc" && o.Format == "bundle" && o.Language == "en" && o.Mask);
        CliOptions.Parse(["--export"]).SnapshotId.Should().Be("latest");
        CliOptions.Parse(["--import", "a.zip", "b.zip"]).Files.Should().Equal("a.zip", "b.zip");
        CliOptions.Parse([]).Should().Match<CliOptions>(o => o.Command == CliCommand.Help && o.Error == null);

        CliOptions.Parse(["--collect", "--site", "HQ"]).Error.Should().Contain("--duration");
        CliOptions.Parse(["--collect", "--site", "HQ", "--duration", "30d"]).Error.Should().Contain("between");
        CliOptions.Parse(["--snapshot"]).Error.Should().Contain("--site");
        CliOptions.Parse(["--import"]).Error.Should().Contain("bundle");
        CliOptions.Parse(["--snapshot", "--import", "a.zip"]).Error.Should().Contain("one command");
        CliOptions.Parse(["--format", "docx"]).Error.Should().Contain("html");
        CliOptions.Parse(["--kill"]).Error.Should().Contain("Unknown");
    }

    [Fact]
    public void Usage_offers_no_command_that_acts_on_a_server()
    {
        CliOptions.Usage.Should().NotContainAny("--kill", "--recycle", "--restart", "--stop", "--fix", "--apply", "--shrink", "--rebuild", "--execute", "--sql ");
        CliOptions.Usage.Should().Contain("never installs a service or a scheduled task");
    }

    [Fact]
    public async Task Import_then_list_and_export_work_headless()
    {
        var inbox = Path.Combine(_root, "inbox");
        Directory.CreateDirectory(inbox);
        var bundle = Path.Combine(inbox, "lab.zip");
        await File.WriteAllBytesAsync(bundle, ReportExporter.BuildBundle(LabSnapshot.Build(), mask: false, LabSnapshot.Started));
        var folder = new DataFolder(Path.Combine(_root, "data"), isPortable: true);

        async Task<(int Code, string Text)> Run(params string[] args)
        {
            var text = new StringWriter();
            var code = await new CliRunner(folder, text).RunAsync(CliOptions.Parse(args), CancellationToken.None);
            return (code, text.ToString());
        }

        var import = await Run("--import", bundle);
        import.Code.Should().Be(0, import.Text);
        import.Text.Should().Contain("Imported 1 bundle(s)").And.Contain("capability gaps");

        (await Run("--list")).Text.Should().Contain("merged-");

        var html = await Run("--export", "latest", "--format", "html", "--lang", "en");
        html.Code.Should().Be(0, html.Text);
        Directory.GetFiles(Path.Combine(folder.Root, "reports"), "*_en.html").Should().ContainSingle();

        var pdf = await Run("--export", "latest", "--format", "pdf");
        pdf.Text.Should().Contain("PDF not available").And.Contain("print to PDF");

        (await Run("--export", "nope-123")).Code.Should().Be(3);
        (await Run("--import", Path.Combine(inbox, "missing.zip"))).Code.Should().Be(2);
        (await Run("--bogus")).Code.Should().Be(1);
    }
}
