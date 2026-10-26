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
        // 先打开已保存的仓库，目录扫描、分组和状态识别在后台独立进行。
        string? path = repository ?? Settings.Value.LastRepository ?? Repositories.FirstOrDefault()?.Path;
        if (path is not null && !HasRepository) await OpenRepositoryAsync(path);
        var workspace = _workspaceRefreshTask;
        var discovery = DiscoverRepositoriesAsync();
        if (path is null)
        {
            await discovery;
            if (!_disposed && !HasRepository && !IsBusy && Repositories.FirstOrDefault() is { } first)
            {
                await OpenRepositoryAsync(first.Path);
                workspace = _workspaceRefreshTask;
            }
        }
        if (workspace is not null) await workspace;
        if (_disposed) return;
        StartRepositoryPushMonitoring();
        StartAutoUploadMonitoring();
        _ = PreloadRepositoriesAsync();
        _ = EnsureCommitEmailAsync();
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
