using System.Globalization;
using RSM.Core.Model;

namespace RSM.Core.Localization;

/// <summary>Jalali + Gregorian timestamps and optional Persian digits, shared by the UI and reports.</summary>
public static class DateText
{
    private static readonly PersianCalendar Persian = new();

    /// <summary>"1405/07/22 23:10 — 2026-10-14 23:10" (fa) or "2026-10-14 23:10 (1405/07/22 23:10)" (en), local time.</summary>
    public static string Format(DateTimeOffset utc, UiLanguage language, bool persianDigits = false, TimeZoneInfo? zone = null)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone ?? TimeZoneInfo.Local).DateTime;
        var jalali = string.Create(CultureInfo.InvariantCulture,
            $"{Persian.GetYear(local):0000}/{Persian.GetMonth(local):00}/{Persian.GetDayOfMonth(local):00} {local:HH\\:mm}");
        var gregorian = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var text = language == UiLanguage.Fa ? $"{jalali} — {gregorian}" : $"{gregorian} ({jalali})";
        return persianDigits && language == UiLanguage.Fa ? ToPersianDigits(text) : text;
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
}
