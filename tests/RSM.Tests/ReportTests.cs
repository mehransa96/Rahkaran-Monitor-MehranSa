using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RSM.Core.Model;
using RSM.Core.Rules;
using RSM.Core.Snapshots;
using RSM.Hosting;
using RSM.Reporting;
using RSM.Reporting.Masking;
using RSM.Reporting.Model;
using RSM.Safety.Files;
using static VerifyXunit.Verifier;

namespace RSM.Tests;

public sealed partial class ReportTests : IDisposable
{
    private static readonly TimeZoneInfo Tehran = TimeZoneInfo.CreateCustomTimeZone("Tehran-test", TimeSpan.FromHours(3.5), "Tehran", "Tehran");
    private static readonly DateTimeOffset Generated = new(2026, 10, 14, 7, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rsm-report-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ReportOptions Options(UiLanguage language, bool mask = false) => new(language, mask, Generated, Tehran);

    private static string Html(SiteSnapshot snapshot, UiLanguage language, bool mask = false) =>
        ReportExporter.RenderHtml(snapshot, LabSnapshot.RuleDefinitions, Options(language, mask));

    [Fact]
    public void Lab_snapshot_produces_exactly_the_seeded_findings()
    {
        var node = LabSnapshot.Build().Nodes.Single();

        node.Findings.Select(f => $"{f.RuleId}{(f.Instance is null ? string.Empty : $"[{f.Instance}]")}:{f.Severity}").Order(StringComparer.Ordinal).Should().Equal(
            "DB-BKP-001[RK_Tehran1405]:Critical",
            "DB-FILE-001[RK_Tehran1405]:Warning",
            "DB-FILE-002[RK_Tehran1405]:Info",
            "DB-INT-001[RK_Tehran1405]:Critical",
            "DB-LOG-004[RK_Tehran1405]:Info",
            "DB-OPT-001[RK_Tehran1405]:Warning",
            "DB-OPT-003[RK_Tehran1405]:Warning",
            "DB-RM-001[RK_Tehran1405]:Critical",
            "SQL-AGT-003:Warning",
            "SQL-CFG-004:Warning",
            "SQL-CFG-005:Warning",
            "SQL-CFG-007:Warning",
            "SQL-MEM-001:Critical",
            "SQL-SEC-002:Info",
            "SQL-SEC-005[RK_Tehran1405]:Info");
        node.Indeterminate.Should().BeEmpty("a complete lab fact set leaves no rule undecided");
        node.RuleErrors.Should().BeEmpty();
        node.Findings.SelectMany(f => new[] { f.Title.Fa, f.Title.En, f.Explanation.Fa, f.Explanation.En })
            .Should().NotContain(t => t.Contains("System.", StringComparison.Ordinal), "list facts must render as text, not as a .NET type name");
        node.Findings.Single(f => f.RuleId == "SQL-SEC-005").Explanation.En.Should().Contain("olduser");
    }

    [Theory]
    [InlineData(UiLanguage.Fa)]
    [InlineData(UiLanguage.En)]
    public Task Html_report_matches_the_approved_rendering(UiLanguage language) =>
        Verify(ScrubFonts(Html(LabSnapshot.Build(), language)), "html").UseParameters(language);

    [Theory]
    [InlineData(UiLanguage.Fa, "rtl", "fa")]
    [InlineData(UiLanguage.En, "ltr", "en")]
    public void Html_report_is_self_contained_and_directional(UiLanguage language, string dir, string lang)
    {
        var html = Html(LabSnapshot.Build(), language);

        html.Should().Contain($"<html lang=\"{lang}\" dir=\"{dir}\">");
        html.Should().Contain("@font-face { font-family:Vazirmatn; font-weight:400; src:url(data:font/ttf;base64,");
        html.Should().NotContainAny(["<script", "http://", "https://", "<link", "<iframe", "<form", "<button", "<input"]);
        html.Should().Contain("Content-Security-Policy");
        html.Should().Contain("1405/07/22").And.Contain("2026-10-14", "timestamps are Jalali + Gregorian");
    }

    [Fact]
    public void Compliance_table_marks_deviations_unknowns_and_not_applicable_rows()
    {
        var snapshot = LabSnapshot.Build();
        var lab = snapshot.Nodes[0];
        snapshot = snapshot with { Nodes = [lab with { Facts = lab.Facts.Where(p => p.Key != "sql.config.xp_cmdshell").ToDictionary(p => p.Key, p => p.Value) }] };
        var rows = new ReportBuilder(LabSnapshot.RuleDefinitions).Build(snapshot, Options(UiLanguage.En)).Nodes.Single().Compliance;

        string Status(string ruleId, string instance = "") => rows.Single(r => r.RuleId == ruleId && r.Instance == instance).StatusClass;
        Status("SQL-CFG-005").Should().Be("warning");                 // MAXDOP 0 vs 4
        Status("SQL-CFG-001").Should().Be("ok");                      // priority boost off
        Status("SQL-CFG-009").Should().Be("unknown");                 // fact missing
        Status("SQL-MEM-002").Should().Be("muted");                   // shared-box rule on a dedicated server
        Status("DB-RM-001", LabSnapshot.RahkaranDb).Should().Be("critical");
        Status("DB-OPT-002", LabSnapshot.RahkaranDb).Should().Be("ok");
        rows.Single(r => r.RuleId == "SQL-CFG-005").Actual.Should().Be("0");
        rows.Single(r => r.RuleId == "SQL-CFG-005").Expected.Should().Be("4");
        rows.Should().NotContain(r => r.RuleId == "DB-RM-003", "there is no ReportServer database in the lab");
    }

    [Theory]
    [InlineData(UiLanguage.Fa)]
    [InlineData(UiLanguage.En)]
    public void Masked_html_contains_no_names_or_addresses(UiLanguage language)
    {
        var html = Html(LabSnapshot.Build(), language, mask: true);

        foreach (var token in LabSnapshot.SensitiveTokens)
        {
            html.Should().NotContainEquivalentOf(token);
            html.Should().NotContainEquivalentOf(System.Net.WebUtility.HtmlEncode(token));
        }

        html.Should().Contain("DB-1").And.Contain("SITE-1");
    }

    [Fact]
    public void Masked_bundle_contains_no_names_addresses_payloads_or_notes()
    {
        var (manifest, snapshotJson) = ReadBundle(ReportExporter.BuildBundle(LabSnapshot.Build(), mask: true, Generated));

        foreach (var token in LabSnapshot.SensitiveTokens)
        {
            manifest.Should().NotContainEquivalentOf(token);
            snapshotJson.Should().NotContainEquivalentOf(token);
            snapshotJson.Should().NotContainEquivalentOf(JsonEncodedText.Encode(token).ToString());
        }

        var root = JsonNode.Parse(snapshotJson)!;
        root["nodes"]![0]!["collectors"]!.AsArray().Should().OnlyContain(c => c!["payload"] == null);
        root["nodes"]![0]!["facts"]!.AsObject().Select(p => p.Key).Should().Contain("db[DB-1].recovery_model");
        JsonNode.Parse(manifest)!["masked"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Unmasked_bundle_keeps_the_full_snapshot()
    {
        var (manifest, snapshotJson) = ReadBundle(ReportExporter.BuildBundle(LabSnapshot.Build(), mask: false, Generated));

        JsonNode.Parse(manifest)!["format"]!.GetValue<string>().Should().Be("rsm-bundle");
        snapshotJson.Should().Contain(LabSnapshot.RahkaranDb).And.Contain("secret.person").And.Contain("\"payload\"");
        var back = JsonSerializer.Deserialize<JsonElement>(snapshotJson);
        back.GetProperty("nodes")[0].GetProperty("findings").GetArrayLength().Should().Be(15);
    }

    [Fact]
    public void Server_supplied_text_is_html_encoded()
    {
        const string evil = "<script>alert(1)</script>";
        var snapshot = LabSnapshot.Build();
        snapshot = snapshot with { Site = snapshot.Site with { Name = "Lab \"<u>\" & Co" } };
        var node = snapshot.Nodes[0];
        var facts = node.Facts.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        facts["db[" + evil + "].state"] = "ONLINE<img src=x onerror=alert(1)>";
        snapshot = snapshot with { Nodes = [node with { Facts = facts }] };

        foreach (var mask in new[] { false, true })
        {
            var html = Html(snapshot, UiLanguage.En, mask);

            html.Should().NotContain("<script").And.NotContain("<img").And.NotContain("<u>");
            if (!mask)
            {
                html.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;").And.Contain("Lab &quot;&lt;u&gt;&quot; &amp; Co");
            }
        }
    }

    [Fact]
    public async Task Exporter_writes_reports_into_the_data_folder()
    {
        var folder = new DataFolder(_root, isPortable: true);
        var exporter = new ReportExporter(folder);
        var snapshot = LabSnapshot.Build();

        var html = await exporter.WriteHtmlAsync(snapshot, LabSnapshot.RuleDefinitions, Options(UiLanguage.Fa), CancellationToken.None);
        var masked = await exporter.WriteBundleAsync(snapshot, mask: true, Generated, CancellationToken.None);

        Path.GetFileName(html).Should().Be("RSM_Tehran_HQ_Lab_20261014-0630_fa.html");
        Path.GetFileName(masked).Should().Be("RSM_SITE-1_20261014-0630_masked.zip");
        Path.GetDirectoryName(html).Should().Be(Path.Combine(_root, "reports"));
        File.Exists(html).Should().BeTrue();
        File.Exists(masked).Should().BeTrue();
    }

    [Fact]
    public void Masker_handles_instances_domains_ips_and_versions()
    {
        var snapshot = LabSnapshot.Build();
        var masker = Masker.From(snapshot);

        masker.Apply(@"Server LAB-SQL01\RK,1433 runs 16.0.4305.5 at 10.20.30.40; backup to \\nas01\backups").Should()
            .NotContain("LAB-SQL01").And.NotContain(@"\RK").And.Contain("16.0.4305.5").And.Contain("10.x.x.40").And.Contain(@"\\HOST-X\backups");
        masker.Apply(@"login LAB\former.employee and former.employee").Should().NotContain("former.employee").And.NotContain("LAB\\");
        masker.Apply("master, msdb, tempdb, sa, 127.0.0.1").Should().Be("master, msdb, tempdb, sa, 127.0.0.1");
        Masker.None.Apply(LabSnapshot.SiteName).Should().Be(LabSnapshot.SiteName);
    }

    [Fact]
    public void Every_rule_fact_is_emitted_by_a_collector()
    {
        var sources = string.Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains("RSM.Collectors.", StringComparison.Ordinal) || f.Contains("RSM.Hosting", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        var missing = new List<string>();
        foreach (var rule in LabSnapshot.RuleDefinitions)
        {
            var text = QuotedText().Replace(string.Join(" ", rule.Escalate.Select(e => e.When).Prepend(rule.When)), string.Empty);
            foreach (var key in FactKey().Matches(text).Select(m => m.Value).Where(k => !k.StartsWith("params.", StringComparison.Ordinal)).Distinct())
            {
                var (prefix, rest) = (key[..key.IndexOf('.')], key[(key.IndexOf('.') + 1)..]);
                var found = prefix is "db" or "volume" or "net" or "ag" ? sources.Contains($".{rest}\"", StringComparison.Ordinal)
                    : key.StartsWith("sql.config.", StringComparison.Ordinal) ? sources.Contains("$\"sql.config.{key}\"", StringComparison.Ordinal)
                    : key.StartsWith("sql.errorlog.e", StringComparison.Ordinal) ? sources.Contains($"\"{key["sql.errorlog.e".Length..]}\"", StringComparison.Ordinal)
                    : sources.Contains($"\"{key}\"", StringComparison.Ordinal);
                if (!found)
                {
                    missing.Add($"{rule.Id}: {key}");
                }
            }
        }

        missing.Should().BeEmpty("a fact no collector emits would leave the rule permanently indeterminate");
    }

    [Fact]
    public void Every_catalog_key_a_collector_uses_is_in_the_embedded_catalog()
    {
        // Guards against a query file that exists on a developer machine but never reached the build
        // (e.g. hidden by a .gitignore pattern): CI would load a catalog without it.
        var keys = RSM.Safety.Sql.QueryCatalog.Load(RsmHost.CatalogAssemblies).All.Select(q => q.Header.Key.Value).ToHashSet(StringComparer.Ordinal);
        var used = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains("RSM.Collectors.", StringComparison.Ordinal))
            .SelectMany(f => CatalogKeyUse().Matches(File.ReadAllText(f)).Select(m => m.Groups["key"].Value))
            .Distinct()
            .ToList();

        used.Should().NotBeEmpty();
        used.Should().OnlyContain(k => keys.Contains(k), "every key passed to the query runner must be an embedded catalog file");
    }

    private static (string Manifest, string Snapshot) ReadBundle(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        archive.Entries.Select(e => e.FullName).Should().BeEquivalentTo("manifest.json", "snapshot.json");
        string Read(string name)
        {
            using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        return (Read("manifest.json"), Read("snapshot.json"));
    }

    private static string ScrubFonts(string html) => FontData().Replace(html, "base64,{font}");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RSM.sln")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    [GeneratedRegex(@"Query(?:Optional)?Async<\w+>\(""(?<key>[a-z0-9_.]+)""")]
    private static partial Regex CatalogKeyUse();

    [GeneratedRegex("base64,[A-Za-z0-9+/=]+")]
    private static partial Regex FontData();

    [GeneratedRegex("'[^']*'")]
    private static partial Regex QuotedText();

    [GeneratedRegex(@"[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+")]
    private static partial Regex FactKey();
}
