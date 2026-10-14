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

    /// <summary>Formats a timestamp as Jalali + Gregorian, local time (e.g. "1405/07/22 23:10 — 2026-10-14 23:10").</summary>
    public string FormatTimestamp(DateTimeOffset utc)
    {
        var local = utc.ToLocalTime().DateTime;
        var pc = new PersianCalendar();
        var jalali = $"{pc.GetYear(local):0000}/{pc.GetMonth(local):00}/{pc.GetDayOfMonth(local):00} {local:HH\\:mm}";
        var gregorian = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var text = Language == UiLanguage.Fa ? $"{jalali} — {gregorian}" : $"{gregorian} ({jalali})";
        return UsePersianDigits && Language == UiLanguage.Fa ? ToPersianDigits(text) : text;
    }

    public static string ToPersianDigits(string text) =>
        string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c is >= '0' and <= '9' ? (char)('۰' + (c - '0')) : c;
            }
        });

    private static CultureInfo CreatePersianCulture()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("fa-IR").Clone();
        culture.DateTimeFormat.Calendar = new PersianCalendar();
        return culture;
    }
}
