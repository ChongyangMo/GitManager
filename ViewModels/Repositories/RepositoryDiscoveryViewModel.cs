using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Repositories;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;

    private async Task DiscoverRepositoriesCoreAsync()
    {
        if (!Settings.Value.RepositoryDiscovery.Enabled) return;
        _lastDiscovery = DateTimeOffset.UtcNow;
        var discovery = new RepositoryDiscovery(Git);
        var roots = RepositoryDiscovery.GetRoots(Settings.Value.RepositoryDiscovery, Repositories.Select(item => item.Path));
        var result = await discovery.DiscoverAsync(roots, Settings.Value.RepositoryDiscovery, Settings.Value.IgnoredRepositories);
        var known = Repositories.Select(item => RepositoryDiscovery.Normalize(item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] additions = result.Repositories.Where(path => known.Add(RepositoryDiscovery.Normalize(path))).ToArray();
        if (additions.Length == 0) return;
        List<string> previous = Settings.Value.Repositories;
        Settings.Value.Repositories = Repositories.Select(item => item.Path).Concat(additions).ToList();
        try { Settings.Save(); }
        catch { Settings.Value.Repositories = previous; throw; }
        foreach (string path in additions) Repositories.Add(new RepositoryBookmark(path));
        AppLog.Write($"已自动加入 {additions.Length} 个本地仓库");
    }

    public Task DiscoverRepositoriesAsync() => ExecuteAsync("正在识别本地仓库", DiscoverRepositoriesCoreAsync);

    public Task DiscoverOnActivationAsync() => IsBusy || DateTimeOffset.UtcNow - _lastDiscovery < TimeSpan.FromMinutes(1)
        ? Task.CompletedTask : DiscoverRepositoriesAsync();
}
