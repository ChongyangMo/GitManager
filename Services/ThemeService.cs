using System.Windows;
using Microsoft.Win32;

namespace GitManager.Services;

public static class ThemeService
{
    public static void Apply(string theme)
    {
        bool dark = theme == "Dark" || theme == "System" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries[0] = new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
    }
}
