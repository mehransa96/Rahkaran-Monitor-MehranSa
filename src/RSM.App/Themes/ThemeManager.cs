using System.Windows;

namespace RSM.App.Themes;

public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>Swaps the colour dictionary (Light.xaml / Dark.xaml) at runtime. Styles reference colours via DynamicResource.</summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static void Apply(AppTheme theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var uri = new Uri($"pack://application:,,,/RSM;component/Themes/{theme}.xaml", UriKind.Absolute);
        var existing = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Themes/Light.xaml", StringComparison.Ordinal) == true
            || d.Source?.OriginalString.Contains("/Themes/Dark.xaml", StringComparison.Ordinal) == true);
        var replacement = new ResourceDictionary { Source = uri };
        if (existing is null)
        {
            dictionaries.Insert(0, replacement);
        }
        else
        {
            dictionaries[dictionaries.IndexOf(existing)] = replacement;
        }

        Current = theme;
    }
}
