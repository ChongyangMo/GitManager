using System.IO;
using System.Text.Json;

namespace GitManager.Services;

public sealed class AppSettings
{
    public string GitExecutablePath { get; set; } = @"C:\Program Files\Git\cmd\git.exe";
    public List<string> Repositories { get; set; } = [];
    public string? LastRepository { get; set; }
    public string Theme { get; set; } = "System";
    public List<string> IgnoredRepositories { get; set; } = [];
    public Repositories.RepositoryDiscoveryOptions RepositoryDiscovery { get; set; } = new();
    public ServiceCloud.ServiceCloudOptions ServiceCloud { get; set; } = null!;
    public CommitMessages.QwenOptions Qwen { get; set; } = new();
    public CommitMessages.ClaudeOptions Claude { get; set; } = new();
}

public sealed class SettingsStore
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitManager");

    // 从打包应用（如 Codex）启动时，Windows 会把 LocalAppData 的写入重定向到 Packages\<包名>\LocalCache\Local
    public static IReadOnlyList<string> PackagedDataDirectories()
    {
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        try
        {
            return Directory.EnumerateDirectories(packages)
                .Select(package => Path.Combine(package, "LocalCache", "Local", "GitManager"))
                .Where(Directory.Exists).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
    public string FilePath { get; }
    public AppSettings Value { get; }

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(DataDirectory, "settings.json");
        string source = File.Exists(FilePath) ? FilePath : Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Value = File.Exists(source) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(source)) ?? throw new InvalidDataException("配置文件内容为空。") : new();
        Value.Repositories ??= [];
        Value.IgnoredRepositories ??= [];
        Value.RepositoryDiscovery ??= new();
        Value.RepositoryDiscovery.Roots ??= [];
        Value.Qwen ??= new();
        Value.Claude ??= new();
        Value.Qwen.PublicGitHosts ??= [];
        if (Value.ServiceCloud is null)
        {
            string defaults = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            Value.ServiceCloud = File.Exists(defaults)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(defaults))?.ServiceCloud ?? new()
                : new();
        }
        if (Repositories.RepositoryPaths.Refresh(Value) && File.Exists(FilePath))
        {
            Save();
            AppLog.Write("已按磁盘实际目录名更新仓库路径记录");
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(Value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
