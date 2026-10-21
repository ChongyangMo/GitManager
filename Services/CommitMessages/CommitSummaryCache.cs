using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitManager.Services.CommitMessages;

public sealed class CommitSummaryCache(string directory)
{
    public static string Key(string root, QwenOptions options, CommitContent content) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Root = Path.GetFullPath(root), Endpoint = options.Endpoint().AbsoluteUri,
            options.Model, options.MaxOutputTokens, Prompt = QwenPrompt.Version, QwenPrompt.System, content.Fingerprint, content.Text }))));
    public static string ClaudeKey(string root, ClaudeOptions options, CommitContent content) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Root = Path.GetFullPath(root), Provider = "claude-code",
            options.Model, Prompt = QwenPrompt.Version, QwenPrompt.System, content.Fingerprint, content.Text }))));

    public string? Read(string key)
    {
        try
        {
            string path = Path.Combine(directory, key + ".json");
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
            return entry is { Message.Length: > 0 } && entry.Key == key ? entry.Message : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { AppLog.Write("读取本地总结缓存失败", ex); return null; }
    }

    public void Save(string key, string message)
    {
        string temporary = Path.Combine(directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Entry(key, message)));
            File.Move(temporary, Path.Combine(directory, key + ".json"), overwrite: true);
            foreach (var old in new DirectoryInfo(directory).GetFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc).Skip(100)) old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Write("保存本地总结缓存失败", ex); }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }

    private sealed record Entry(string Key, string Message);
}
