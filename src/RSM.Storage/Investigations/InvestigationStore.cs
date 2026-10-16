using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RSM.Core.Slowness;
using RSM.Safety.Files;

namespace RSM.Storage.Investigations;

/// <summary>A saved slowness investigation: the raw capture (re-scorable) and the result shown to the user.</summary>
public sealed record SavedInvestigation(SlownessCapture Capture, InvestigationResult Result, string SiteName);

public sealed record InvestigationInfo(string CaptureId, string SiteId, string SiteName, DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? TopCauseId, double? TopConfidence);

/// <summary>Investigations as JSON under <c>investigations/</c> in the data folder (written only through <see cref="IDataFolder"/>).</summary>
public sealed partial class InvestigationStore(IDataFolder folder)
{
    private const string Folder = "investigations";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task SaveAsync(SavedInvestigation investigation, CancellationToken cancellationToken)
    {
        var id = CheckId(investigation.Capture.CaptureId);
        var top = investigation.Result.Causes.FirstOrDefault();
        var info = new InvestigationInfo(id, investigation.Capture.SiteId, investigation.SiteName, investigation.Capture.FromUtc, investigation.Capture.ToUtc, top?.Cause.Id, top?.Confidence);
        await folder.WriteAllTextAsync($"{Folder}/{id}.json", JsonSerializer.Serialize(investigation, Json), cancellationToken).ConfigureAwait(false);
        await folder.WriteAllTextAsync($"{Folder}/{id}.meta.json", JsonSerializer.Serialize(info, Json), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Newest first; <paramref name="siteId"/> null = all sites.</summary>
    public async Task<IReadOnlyList<InvestigationInfo>> ListAsync(string? siteId, CancellationToken cancellationToken)
    {
        var list = new List<InvestigationInfo>();
        foreach (var file in folder.ListFiles(Folder, "*.meta.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<InvestigationInfo>(await folder.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false), Json) is { } info
                    && (siteId is null || info.SiteId == siteId))
                {
                    list.Add(info);
                }
            }
            catch (JsonException)
            {
                // a damaged index entry is skipped; the investigation file itself stays
            }
        }

        return list.OrderByDescending(i => i.FromUtc).ToList();
    }

    /// <summary>The saved JSON (for export and re-scoring); null when unknown.</summary>
    public async Task<string?> LoadJsonAsync(string captureId, CancellationToken cancellationToken)
    {
        var path = $"{Folder}/{CheckId(captureId)}.json";
        return folder.Exists(path) ? await folder.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    private static string CheckId(string id) =>
        SafeId().IsMatch(id) ? id : throw new ArgumentException("Capture ids are letters, digits, '-' and '_' only.", nameof(id));

    [GeneratedRegex("^[A-Za-z0-9_-]{1,80}$")]
    private static partial Regex SafeId();
}
