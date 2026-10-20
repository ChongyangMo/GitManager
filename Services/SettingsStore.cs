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
}

public sealed class SettingsStore
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitManager");
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
