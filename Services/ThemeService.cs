using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GitManager.Services;

public static class ThemeService
{
    private static bool _listening;
    public static string SelectedTheme { get; private set; } = "System";
    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    public static void Apply(string theme)
    {
        SelectedTheme = theme is "Dark" or "Light" ? theme : "System";
        if (!_listening)
        {
            SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
            Application.Current.Exit += (_, _) => { SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged; _listening = false; };
            _listening = true;
        }
        Refresh();
    }

    public static void Attach(Window window) => window.SourceInitialized += (_, _) => ApplyTitleBar(window);

    private static void Refresh()
    {
        IsDark = SelectedTheme == "Dark" || SelectedTheme == "System" && IsSystemDark();
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries[0] = new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
        Changed?.Invoke();
    }

    private static bool IsSystemDark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            AppLog.Write("读取系统主题失败，使用白天模式", ex);
            return false;
        }
    }

    private static void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(new Action(() => { if (SelectedTheme == "System") Refresh(); }));
    }

    private static void ApplyTitleBar(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}
