using System.IO;
using Microsoft.Win32;

namespace GitManager.Services;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GitManager";
    private static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "GitManager.exe");
    private static string Command => $"\"{ExecutablePath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled && !File.Exists(ExecutablePath))
            throw new FileNotFoundException("无法找到 GitManager 程序，请从完整的程序目录启动后重试。", ExecutablePath);

        using var key = Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new InvalidOperationException("无法访问当前用户的开机启动设置。");
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        AppLog.Write(enabled ? "已启用开机自启动" : "已关闭开机自启动");
    }
}
