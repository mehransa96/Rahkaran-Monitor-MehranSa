using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RSM.Core.Model;
using RSM.Core.Snapshots;
using RSM.Safety.Files;

namespace RSM.Storage.Snapshots;

public sealed record SnapshotInfo(string Id, string SiteId, string SiteName, DateTimeOffset StartedUtc, int Critical, int Warning, int Info);

/// <summary>
/// Diagnosis history in the data folder, written only through <see cref="IDataFolder"/>: per snapshot a small
/// <c>snapshots/&lt;id&gt;.meta.json</c> (for listing) and the full snapshot as Brotli-compressed JSON
/// (<c>snapshots/&lt;id&gt;.json.br</c>). No SQL anywhere: all SQL in <c>src/</c> is the read-only server catalog.
/// </summary>
public sealed partial class SnapshotStore(IDataFolder dataFolder)
{
    private const string Folder = "snapshots";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public async Task SaveAsync(SiteSnapshot snapshot, CancellationToken cancellationToken)
    {
        var id = CheckId(snapshot.Id);
        var findings = snapshot.AllFindings.ToList();
        var info = new SnapshotInfo(id, snapshot.Site.Id, snapshot.Site.Name, snapshot.StartedUtc,
            findings.Count(f => f.Severity == Severity.Critical), findings.Count(f => f.Severity == Severity.Warning), findings.Count(f => f.Severity == Severity.Info));

        await dataFolder.WriteAllBytesAsync($"{Folder}/{id}.json.br", Compress(JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions)), cancellationToken).ConfigureAwait(false);
        await dataFolder.WriteAllTextAsync($"{Folder}/{id}.meta.json", JsonSerializer.Serialize(info, JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Newest first; <paramref name="siteId"/> null = all sites.</summary>
    public async Task<IReadOnlyList<SnapshotInfo>> ListAsync(string? siteId, int limit, CancellationToken cancellationToken)
    {
        var list = new List<SnapshotInfo>();
        foreach (var file in dataFolder.ListFiles(Folder, "*.meta.json"))
        {
            try
            {
                var info = JsonSerializer.Deserialize<SnapshotInfo>(await dataFolder.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false), JsonOptions);
                if (info is not null && (siteId is null || info.SiteId == siteId))
                {
                    list.Add(info);
                }
            }
            catch (JsonException)
            {
                // A damaged index file hides one history entry; it never breaks the list.
            }
        }

        return list.OrderByDescending(i => i.StartedUtc).Take(limit).ToList();
    }

    /// <summary>The stored snapshot as JSON (for bundles and comparisons); null when unknown.</summary>
    public async Task<string?> LoadJsonAsync(string snapshotId, CancellationToken cancellationToken)
    {
        var path = $"{Folder}/{CheckId(snapshotId)}.json.br";
        return dataFolder.Exists(path)
            ? System.Text.Encoding.UTF8.GetString(Decompress(await dataFolder.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)))
            : null;
    }

    private static string CheckId(string id) =>
        SafeId().IsMatch(id) ? id : throw new ArgumentException("Snapshot ids are letters, digits, '-' and '_' only.", nameof(id));

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeId();
}
