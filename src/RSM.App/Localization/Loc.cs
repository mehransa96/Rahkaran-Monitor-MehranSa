using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows;
using RSM.Core.Model;

namespace RSM.App.Localization;

/// <summary>
/// Runtime-switchable UI strings (resx) and layout direction. XAML binds with
/// <c>{Binding [Key], Source={x:Static loc:Loc.Instance}}</c>; switching language raises <c>Item[]</c> so every
/// binding refreshes without restarting.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new("RSM.App.Resources.Strings", typeof(Loc).Assembly);
    private static readonly CultureInfo Persian = CreatePersianCulture();
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private Loc()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static Loc Instance { get; } = new();

    public UiLanguage Language { get; private set; } = UiLanguage.Fa;

    public CultureInfo Culture => Language == UiLanguage.Fa ? Persian : English;

    public FlowDirection FlowDirection => Language == UiLanguage.Fa ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public bool UsePersianDigits { get; set; }

    public string this[string key] => Resources.GetString(key, Culture) ?? $"[{key}]";

    public string Format(string key, params object?[] args) => string.Format(Culture, this[key], args);

    public void SetLanguage(UiLanguage language)
    {
        if (Language == language)
        {
            return;
        }

        Language = language;
        CultureInfo.CurrentUICulture = Culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
    }

    /// <summary>Formats a timestamp as Jalali + Gregorian, local time.</summary>
    public string FormatTimestamp(DateTimeOffset utc) => RSM.Core.Localization.DateText.Format(utc, Language, UsePersianDigits);

    private static CultureInfo CreatePersianCulture()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("fa-IR").Clone();
        culture.DateTimeFormat.Calendar = new PersianCalendar();
        return culture;
    }
}
