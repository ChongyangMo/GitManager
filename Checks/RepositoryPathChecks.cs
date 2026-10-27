using System.IO;
using System.Text.Json;
using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Repositories;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunRepositoryPathChecksAsync()
    {
        string settingsFile = Path.Combine(SettingsStore.DataDirectory, "settings.json");
        var original = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsFile))!;
        string actual = new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
            .EnumerateDirectories().Single(item => item.Name.Equals("AntoCopy", StringComparison.OrdinalIgnoreCase)).FullName;
        string legacy = actual.ToLowerInvariant();
        Check(RepositoryPaths.Actual(legacy + Path.DirectorySeparatorChar) == actual, "旧大小写及尾部分隔符恢复为真实磁盘目录名");
        Check(new RepositoryBookmark(legacy).Path == actual, "书签中的位置直接显示真实目录名");
        Check(await Git.OpenAsync(legacy) == actual, "Git 返回的根目录统一为磁盘实际名称");
        string theme = original.Theme;
        original.Repositories.Add(legacy);
        original.Repositories.Add(actual);
        original.LastRepository = legacy;
        string checkDirectory = CheckDirectories.Create("repository-paths");
        Directory.CreateDirectory(checkDirectory);
        string checkFile = Path.Combine(checkDirectory, "settings.json");
        File.WriteAllText(checkFile, JsonSerializer.Serialize(original));
        var migrated = new SettingsStore(checkFile);
        Check(migrated.Value.Repositories.Count(path => string.Equals(path, actual, StringComparison.OrdinalIgnoreCase)) == 1
            && migrated.Value.Repositories.Contains(actual, StringComparer.Ordinal), "同一目录的不同大小写记录合并并保存真实名称");
        var persisted = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(checkFile))!;
        Check(persisted.LastRepository == actual && persisted.Theme == theme, "最近使用目录同步更新，主题设置保留");
        using var model = new MainViewModel(migrated);
        var bookmark = model.Repositories.Single(item => item.Path == actual);
        Check(!bookmark.HasNameConflict, "相同物理目录不会误报为两份同名仓库");
        Console.WriteLine($"仓库路径检查通过，共 {_count} 项。实际目录：{actual}");
    }
}
