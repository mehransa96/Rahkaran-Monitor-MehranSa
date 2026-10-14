using System.Text.Json;
using RSM.Core.Model;
using RSM.Safety.Files;
using RSM.Storage.Profiles;

namespace RSM.Storage.Settings;

/// <summary>Per-user UI preferences in <c>data/settings.json</c> (human-readable).</summary>
public sealed record AppSettings
{
    public UiLanguage Language { get; init; } = UiLanguage.Fa;

    public string Theme { get; init; } = "Light";

    public bool PersianDigits { get; init; }
}

public sealed class AppSettingsStore(IDataFolder dataFolder)
{
    private const string FileName = "settings.json";

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!dataFolder.Exists(FileName))
        {
            return new AppSettings();
        }

        try
        {
            var json = await dataFolder.ReadAllTextAsync(FileName, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<AppSettings>(json, ProfileStore.JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings(); // a corrupt settings file must never stop RSM from starting
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) =>
        dataFolder.WriteAllTextAsync(FileName, JsonSerializer.Serialize(settings, ProfileStore.JsonOptions), cancellationToken);
}
