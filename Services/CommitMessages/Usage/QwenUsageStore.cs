using System.IO;
using System.Text.Json;

namespace GitManager.Services.CommitMessages.Usage;

public sealed class QwenUsageStore(string directory)
{
    public static QwenUsageStore Default { get; } = new(Path.Combine(SettingsStore.DataDirectory, "qwen-usage"));
    public string DirectoryPath { get; } = directory;

    public void Save(QwenUsageEntry entry, bool overwrite = true)
    {
        if (!ValidId(entry.Id)) throw new InvalidOperationException("千问用量记录编号无效。");
        Directory.CreateDirectory(DirectoryPath);
        string destination = Path.Combine(DirectoryPath, entry.Id + ".json");
        if (!overwrite && File.Exists(destination)) return;
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entry));
            try { File.Move(temporary, destination, overwrite); }
            catch (IOException) when (!overwrite && File.Exists(destination)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void TrySave(QwenUsageEntry entry)
    {
        try { Save(entry); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppLog.Write("保存千问用量记录失败", ex); }
    }

    public QwenUsageReadResult Read(string? logsDirectory = null)
    {
        int unreadable = logsDirectory is null ? 0 : QwenUsageLogImporter.Import(this, logsDirectory);
        if (!Directory.Exists(DirectoryPath)) return new([], unreadable);
        List<QwenUsageEntry> entries = [];
        foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<QwenUsageEntry>(File.ReadAllText(file));
                if (entry is null || !ValidId(entry.Id) || entry.Id != Path.GetFileNameWithoutExtension(file)
                    || entry.Time == default || string.IsNullOrWhiteSpace(entry.Model) || entry.Cost is null)
                { unreadable++; continue; }
                entries.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { unreadable++; }
        }
        return new(entries.OrderByDescending(entry => entry.Time).ToArray(), unreadable);
    }

    private static bool ValidId(string? id) => id is { Length: >= 16 and <= 64 } && id.All(char.IsAsciiHexDigit);
}
