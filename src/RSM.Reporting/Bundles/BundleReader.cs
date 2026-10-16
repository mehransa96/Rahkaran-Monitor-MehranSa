using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RSM.Core.Rules;
using RSM.Core.Snapshots;

namespace RSM.Reporting.Bundles;

public sealed class BundleFormatException(string message) : Exception(message);

/// <summary>An RSM JSON bundle read back: its manifest values and the snapshot it carries.</summary>
public sealed record ImportedBundle(string FileName, string SnapshotId, string SiteName, DateTimeOffset StartedUtc, string RsmVersion, bool Masked, SiteSnapshot Snapshot);

/// <summary>
/// Reads bundles written by <see cref="ReportExporter.WriteBundleAsync"/> (format <c>rsm-bundle</c> v1). Only the two
/// known entries are read, each capped in size, so a crafted zip cannot exhaust memory. Fact values come back as plain
/// CLR values (number → double, string, bool) so the rules engine can evaluate them again.
/// </summary>
public static class BundleReader
{
    public const long MaxEntryBytes = 256L * 1024 * 1024;

    public static ImportedBundle Read(string fileName, byte[] zipBytes)
    {
        string manifestJson, snapshotJson;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            manifestJson = Entry(zip, "manifest.json");
            snapshotJson = Entry(zip, "snapshot.json");
        }
        catch (InvalidDataException ex)
        {
            throw new BundleFormatException($"{fileName}: not a zip file ({ex.Message}).");
        }

        using var manifest = Parse(fileName, manifestJson);
        var m = manifest.RootElement;
        if (Str(m, "format") != "rsm-bundle" || !m.TryGetProperty("formatVersion", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
        {
            throw new BundleFormatException($"{fileName}: not an RSM bundle (format rsm-bundle, version 1).");
        }

        SiteSnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<SiteSnapshot>(snapshotJson, ReportExporter.JsonOptions) ?? throw new BundleFormatException($"{fileName}: empty snapshot.");
        }
        catch (JsonException ex)
        {
            throw new BundleFormatException($"{fileName}: snapshot.json is not a valid snapshot ({ex.Message}).");
        }

        snapshot = FactValues.Plain(snapshot);
        return new ImportedBundle(fileName, Str(m, "snapshotId") ?? snapshot.Id, Str(m, "site") ?? snapshot.Site.Name,
            m.TryGetProperty("startedUtc", out var s) && s.TryGetDateTimeOffset(out var started) ? started : snapshot.StartedUtc,
            Str(m, "rsmVersion") ?? snapshot.RsmVersion, m.TryGetProperty("masked", out var masked) && masked.ValueKind == JsonValueKind.True, snapshot);
    }

    private static string Entry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new BundleFormatException($"Bundle has no {name}.");
        if (entry.Length > MaxEntryBytes)
        {
            throw new BundleFormatException($"{name} is larger than {MaxEntryBytes / 1048576} MB.");
        }

        using var stream = entry.Open();
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (limited.Length + read > MaxEntryBytes)
            {
                throw new BundleFormatException($"{name} is larger than {MaxEntryBytes / 1048576} MB.");
            }

            limited.Write(buffer, 0, read);
        }

        return new UTF8Encoding(false).GetString(limited.ToArray());
    }

    private static JsonDocument Parse(string fileName, string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BundleFormatException($"{fileName}: manifest.json is not valid JSON ({ex.Message}).");
        }
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
