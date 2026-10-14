using System.Text.Json;
using System.Text.Json.Serialization;
using RSM.Core.Profiles;
using RSM.Safety.Files;

namespace RSM.Storage.Profiles;

/// <summary>Site profiles as human-readable JSON in <c>data/sites/&lt;id&gt;.json</c>. Never contains secrets.</summary>
public sealed class ProfileStore(IDataFolder dataFolder)
{
    private const string Folder = "sites";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<IReadOnlyList<SiteProfile>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var sites = new List<SiteProfile>();
        foreach (var file in dataFolder.ListFiles(Folder, "*.json"))
        {
            var json = await dataFolder.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var site = JsonSerializer.Deserialize<SiteProfile>(json, JsonOptions);
            if (site is not null)
            {
                sites.Add(site);
            }
        }

        return sites.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task SaveAsync(SiteProfile site, CancellationToken cancellationToken)
    {
        ValidateId(site.Id);
        return dataFolder.WriteAllTextAsync(PathFor(site.Id), JsonSerializer.Serialize(site, JsonOptions), cancellationToken);
    }

    public void Delete(string siteId)
    {
        ValidateId(siteId);
        dataFolder.Delete(PathFor(siteId));
    }

    private static string PathFor(string siteId) => Path.Combine(Folder, siteId + ".json");

    internal static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException($"Invalid profile id '{id}'.", nameof(id));
        }
    }
}
