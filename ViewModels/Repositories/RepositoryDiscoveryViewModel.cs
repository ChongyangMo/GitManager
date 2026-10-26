using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Repositories;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;
    private Task? _repositoryDiscoveryTask;
    private bool _discoveringRepositories;
    public bool IsDiscoveringRepositories => _discoveringRepositories;

    private async Task DiscoverRepositoriesCoreAsync()
    {
        if (!Settings.Value.RepositoryDiscovery.Enabled) return;
        _lastDiscovery = DateTimeOffset.UtcNow;
        var discovery = new RepositoryDiscovery(Git);
        var roots = RepositoryDiscovery.GetRoots(Settings.Value.RepositoryDiscovery, Repositories.Select(item => item.Path));
        var result = await discovery.DiscoverAsync(roots, Settings.Value.RepositoryDiscovery, Settings.Value.IgnoredRepositories, _lifetimeCancellation.Token);
        // 扫描不占用前台操作锁；仅在提交列表变更时避开正在进行的写操作。
        while (IsBusy && !_disposed) await Task.Delay(50, _lifetimeCancellation.Token);
        if (_disposed) return;
        var known = Repositories.Select(item => RepositoryDiscovery.Normalize(item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ignored = Settings.Value.IgnoredRepositories.Select(RepositoryDiscovery.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] additions = result.Repositories.Where(path => !ignored.Contains(RepositoryDiscovery.Normalize(path)) && known.Add(RepositoryDiscovery.Normalize(path))).ToArray();
        if (additions.Length == 0) return;
        List<string> previous = Settings.Value.Repositories;
        Settings.Value.Repositories = Repositories.Select(item => item.Path).Concat(additions).ToList();
        try { Settings.Save(); }
        catch { Settings.Value.Repositories = previous; throw; }
        foreach (string path in additions) Repositories.Add(new RepositoryBookmark(path));
        AppLog.Write($"已自动加入 {additions.Length} 个本地仓库");
    }

    public Task DiscoverRepositoriesAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_repositoryDiscoveryTask is { IsCompleted: false }) return _repositoryDiscoveryTask;
        return _repositoryDiscoveryTask = DiscoverInBackgroundAsync();
    }

    private async Task DiscoverInBackgroundAsync()
    {
        _discoveringRepositories = true;
        Notify(nameof(IsDiscoveringRepositories));
        const string message = "正在识别本地仓库";
        if (!IsBusy) Status = message;
        try
        {
            await DiscoverRepositoriesCoreAsync();
            if (_disposed) return;
            await RefreshRepositoryNamesAsync();
            if (_disposed) return;
            _ = RefreshRepositoryPushIndicatorsAsync();
            _ = PreloadRepositoriesAsync();
            if (!IsBusy && Status == message) Status = "就绪";
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            AppLog.Write("后台识别本地仓库失败", ex);
            if (!_disposed && !IsBusy && Status == message) Status = AppLog.Clean(ex.Message);
        }
        finally { _discoveringRepositories = false; Notify(nameof(IsDiscoveringRepositories)); }
    }

    public Task DiscoverOnActivationAsync() => DateTimeOffset.UtcNow - _lastDiscovery < TimeSpan.FromMinutes(1)
        ? Task.CompletedTask : DiscoverRepositoriesAsync();
}
