using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using RSM.App.Localization;
using RSM.App.Themes;
using RSM.Core.Model;
using RSM.Safety.Files;
using RSM.Storage.Settings;

namespace RSM.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsStore _store;
    private bool _loading;

    public SettingsViewModel(AppSettingsStore store, IDataFolder dataFolder)
    {
        _store = store;
        DataFolder = dataFolder.Root;
        IsPortable = dataFolder.IsPortable;
        Version = typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        Loc.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(AboutText));
    }

    public string DataFolder { get; }

    public bool IsPortable { get; }

    public string Version { get; }

    public string AboutText => Loc.Instance.Format("About_Text", Version);

    [ObservableProperty]
    private bool _isPersian = true;

    [ObservableProperty]
    private bool _isDark;

    [ObservableProperty]
    private bool _persianDigits;

    public bool IsEnglish
    {
        get => !IsPersian;
        set => IsPersian = !value;
    }

    public bool IsLight
    {
        get => !IsDark;
        set => IsDark = !value;
    }

    public async Task LoadAsync()
    {
        var settings = await _store.LoadAsync(CancellationToken.None);
        _loading = true;
        IsPersian = settings.Language == UiLanguage.Fa;
        IsDark = string.Equals(settings.Theme, nameof(AppTheme.Dark), StringComparison.OrdinalIgnoreCase);
        PersianDigits = settings.PersianDigits;
        _loading = false;
        Apply();
    }

    partial void OnIsPersianChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEnglish));
        ApplyAndSave();
    }

    partial void OnIsDarkChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLight));
        ApplyAndSave();
    }

    partial void OnPersianDigitsChanged(bool value) => ApplyAndSave();

    private void ApplyAndSave()
    {
        if (_loading)
        {
            return;
        }

        Apply();
        _ = _store.SaveAsync(new AppSettings
        {
            Language = IsPersian ? UiLanguage.Fa : UiLanguage.En,
            Theme = IsDark ? nameof(AppTheme.Dark) : nameof(AppTheme.Light),
            PersianDigits = PersianDigits,
        }, CancellationToken.None);
    }

    private void Apply()
    {
        Loc.Instance.UsePersianDigits = PersianDigits;
        Loc.Instance.SetLanguage(IsPersian ? UiLanguage.Fa : UiLanguage.En);
        ThemeManager.Apply(IsDark ? AppTheme.Dark : AppTheme.Light);
    }
}
