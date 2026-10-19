using System.IO;
using System.Text.RegularExpressions;

namespace GitManager.Services;

public static partial class AppLog
{
    private static readonly object Gate = new();
    public static string Clean(string value) => SecretUrl().Replace(value, "$1[已隐藏]@");
    public static void Write(string message, Exception? error = null)
    {
        try
        {
            lock (Gate)
            {
                string directory = Path.Combine(SettingsStore.DataDirectory, "logs");
                Directory.CreateDirectory(directory);
                string detail = error is null ? message : message + "：" + error.Message;
                File.AppendAllText(Path.Combine(directory, $"gitmanager-{DateTime.Now:yyyyMMdd}.log"), $"[{DateTime.Now:HH:mm:ss}] {Clean(detail)}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    [GeneratedRegex(@"(https?://)[^\s/@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex SecretUrl();
}
