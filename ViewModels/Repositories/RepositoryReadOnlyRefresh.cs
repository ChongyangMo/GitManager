using GitManager.Models;
using GitManager.Services;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private Task? _initializationTask, _repositoryRefreshTask;
    private long _repositoryOpenGeneration;

    public Task InitializeAsync(string? repository) => _initializationTask ??= InitializeWorkspaceAsync(repository);

    private async Task InitializeWorkspaceAsync(string? repository)
    {
        // 首屏先完成已保存仓库的读取，避免目录扫描状态覆盖仓库加载状态；扫描随后在后台执行。
        string? path = repository ?? Settings.Value.LastRepository ?? Repositories.FirstOrDefault()?.Path;
        Task? workspace = null;
        if (path is not null && !HasRepository)
        {
            await OpenRepositoryAsync(path);
            workspace = _workspaceRefreshTask;
            if (workspace is not null) await workspace;
        }
        if (path is null)
        {
            await DiscoverRepositoriesAsync();
            if (!_disposed && !HasRepository && !IsBusy && Repositories.FirstOrDefault() is { } first)
            {
                await OpenRepositoryAsync(first.Path);
                workspace = _workspaceRefreshTask;
                if (workspace is not null) await workspace;
            }
        }
        if (_disposed) return;
        StartRepositoryPushMonitoring();
        StartAutoUploadMonitoring();
        _ = PreloadRepositoriesAsync();
        _ = EnsureCommitEmailAsync();
        // 有已有仓库时，首屏已经可用，再开始扫描，避免扫描期间显示半加载页面。
        if (path is not null) _ = DiscoverRepositoriesAsync(showStatus: false);
    }

    public Task RefreshAsync()
    {
        if (_disposed || IsBusy) return Task.CompletedTask;
        if (_repositoryRefreshTask is { IsCompleted: false }) return _repositoryRefreshTask;
        return _repositoryRefreshTask = RefreshReadOnlyStateAsync();
    }

    private async Task RefreshReadOnlyStateAsync()
    {
        var discovery = DiscoverRepositoriesAsync();
        if (HasRepository) StartWorkspaceRefresh(fetchAfter: false);
        await Task.WhenAll(discovery, _workspaceRefreshTask ?? Task.CompletedTask);
        if (_disposed) return;
        if (!HasRepository && !IsBusy && Repositories.FirstOrDefault() is { } first)
        {
            await OpenRepositoryAsync(first.Path);
            if (_workspaceRefreshTask is { } workspace) await workspace;
        }
        _ = RefreshRepositoryPushIndicatorsAsync();
        if (HasRepository && !IsBusy) ReloadGitHubVisibility();
    }

    private async Task RegisterRepositoryAsync(string path, long generation)
    {
        try
        {
            string root = await Git.OpenAsync(path, _lifetimeCancellation.Token);
            if (_disposed || IsBusy || generation != _repositoryOpenGeneration) return;
            var bookmark = Repositories.FirstOrDefault(item => string.Equals(item.Path, root, StringComparison.OrdinalIgnoreCase));
            if (bookmark is null) { bookmark = new RepositoryBookmark(root); Repositories.Add(bookmark); }
            Settings.Value.IgnoredRepositories.RemoveAll(item => string.Equals(item, root, StringComparison.OrdinalIgnoreCase));
            await OpenRepositoryAsync(root);
            _ = RefreshRepositoryNameAsync(bookmark);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            AppLog.Write("打开本地仓库失败", ex);
            if (!_disposed && !IsBusy && generation == _repositoryOpenGeneration) Status = AppLog.Clean(ex.Message);
        }
    }

    private async Task FetchCurrentRepositoryAsync()
    {
        if (_disposed || !CanOperate) return;
        StartBackgroundFetch();
        if (_backgroundFetchTask is { } fetch) await fetch;
    }
}
