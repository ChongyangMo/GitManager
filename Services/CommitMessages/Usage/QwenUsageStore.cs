using System.IO;
using System.Text.Json;

namespace GitManager.Services.CommitMessages.Usage;

public sealed class QwenUsageStore(string directory, IReadOnlyList<string>? sharedDataDirectories = null)
{
    public static QwenUsageStore Default { get; } = new(Path.Combine(SettingsStore.DataDirectory, "qwen-usage"), SettingsStore.PackagedDataDirectories());
    public string DirectoryPath { get; } = directory;
    // 其他启动方式留下的数据目录，只读取其中的用量记录和日志，新记录始终写入 DirectoryPath
    private readonly string[] _sharedDataDirectories = (sharedDataDirectories ?? [])
        .Where(root => !SamePath(Path.Combine(root, "qwen-usage"), directory)).ToArray();
    private IEnumerable<string> Directories => _sharedDataDirectories.Select(root => Path.Combine(root, "qwen-usage")).Prepend(DirectoryPath);

    public bool Contains(string id) => Directories.Any(path => File.Exists(Path.Combine(path, id + ".json")));

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
        int unreadable = 0;
        if (logsDirectory is not null)
            foreach (string logs in _sharedDataDirectories.Select(root => Path.Combine(root, "logs")).Prepend(logsDirectory))
                unreadable += QwenUsageLogImporter.Import(this, logs);
        Dictionary<string, QwenUsageEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directories.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(path, "*.json").ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; continue; }
            foreach (string file in files)
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<QwenUsageEntry>(File.ReadAllText(file));
                    if (entry is null || !ValidId(entry.Id) || entry.Id != Path.GetFileNameWithoutExtension(file)
                        || entry.Time == default || string.IsNullOrWhiteSpace(entry.Model) || entry.Cost is null)
                    { unreadable++; continue; }
                    entries.TryAdd(entry.Id, entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { unreadable++; }
            }
        }
        return new(entries.Values.OrderByDescending(entry => entry.Time).ToArray(), unreadable);
    }

    private static bool ValidId(string? id) => id is { Length: >= 16 and <= 64 } && id.All(char.IsAsciiHexDigit);
    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
}
