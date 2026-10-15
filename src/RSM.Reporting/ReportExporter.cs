using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RSM.Core.Model;
using RSM.Core.Rules;
using RSM.Core.Snapshots;
using RSM.Reporting.Html;
using RSM.Reporting.Masking;
using RSM.Reporting.Model;
using RSM.Safety.Files;

namespace RSM.Reporting;

/// <summary>
/// Writes reports into <c>&lt;data&gt;/reports/</c> through <see cref="IDataFolder"/> (the only writer): the HTML report
/// (fa or en) and the JSON bundle (zip with manifest + snapshot). Masked exports pseudonymise every name and address
/// and drop raw collector payloads and the site notes, which can hold arbitrary text.
/// </summary>
public sealed class ReportExporter(IDataFolder dataFolder)
{
    public const string Folder = "reports";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public static string RenderHtml(SiteSnapshot snapshot, IReadOnlyList<RuleDefinition> rules, ReportOptions options) =>
        HtmlReportRenderer.Render(new ReportBuilder(rules).Build(snapshot, options));

    /// <summary>Writes the HTML report and returns its absolute path.</summary>
    public async Task<string> WriteHtmlAsync(SiteSnapshot snapshot, IReadOnlyList<RuleDefinition> rules, ReportOptions options, CancellationToken cancellationToken)
    {
        var html = RenderHtml(snapshot, rules, options);
        var suffix = options.Language == UiLanguage.Fa ? "fa" : "en";
        var relative = $"{Folder}/{BaseName(snapshot, options.Mask)}_{suffix}.html";
        await dataFolder.WriteAllTextAsync(relative, html, cancellationToken).ConfigureAwait(false);
        return dataFolder.Resolve(relative);
    }

    /// <summary>Writes the JSON bundle (zip: manifest.json + snapshot.json) and returns its absolute path.</summary>
    public async Task<string> WriteBundleAsync(SiteSnapshot snapshot, bool mask, DateTimeOffset generatedUtc, CancellationToken cancellationToken)
    {
        var relative = $"{Folder}/{BaseName(snapshot, mask)}.zip";
        await dataFolder.WriteAllBytesAsync(relative, BuildBundle(snapshot, mask, generatedUtc), cancellationToken).ConfigureAwait(false);
        return dataFolder.Resolve(relative);
    }

    internal static byte[] BuildBundle(SiteSnapshot snapshot, bool mask, DateTimeOffset generatedUtc)
    {
        var masker = mask ? Masker.From(snapshot) : Masker.None;
        var json = SnapshotJson(snapshot, masker);
        var manifest = new JsonObject
        {
            ["format"] = "rsm-bundle",
            ["formatVersion"] = 1,
            ["snapshotId"] = snapshot.Id,
            ["site"] = masker.Apply(snapshot.Site.Name),
            ["startedUtc"] = snapshot.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["generatedUtc"] = generatedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["rsmVersion"] = snapshot.RsmVersion,
            ["masked"] = mask,
            ["files"] = new JsonArray("manifest.json", "snapshot.json"),
        };

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "manifest.json", manifest.ToJsonString(JsonOptions));
            AddEntry(zip, "snapshot.json", json);
        }

        return buffer.ToArray();
    }

    internal static string SnapshotJson(SiteSnapshot snapshot, Masker masker)
    {
        var node = JsonSerializer.SerializeToNode(snapshot, JsonOptions)!;
        if (masker.IsEnabled)
        {
            node["site"]?.AsObject().Remove("notes");
            foreach (var n in node["nodes"]?.AsArray() ?? [])
            {
                foreach (var collector in n?["collectors"]?.AsArray() ?? [])
                {
                    collector?.AsObject().Remove("payload");
                }
            }
        }

        return masker.Apply(node)!.ToJsonString(JsonOptions);
    }

    /// <summary><c>RSM_&lt;site&gt;_&lt;yyyyMMdd-HHmm&gt;[_masked]</c> (UTC start time of the snapshot).</summary>
    internal static string BaseName(SiteSnapshot snapshot, bool mask)
    {
        var site = mask ? Masker.From(snapshot).Apply(snapshot.Site.Name) : snapshot.Site.Name;
        var safe = new StringBuilder();
        foreach (var c in site.Trim())
        {
            safe.Append(char.IsLetterOrDigit(c) || c is '-' ? c : '_');
        }

        var name = safe.Length == 0 ? "site" : safe.ToString(0, Math.Min(safe.Length, 40));
        return $"RSM_{name}_{snapshot.StartedUtc.UtcDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}{(mask ? "_masked" : string.Empty)}";
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes);
    }
}
