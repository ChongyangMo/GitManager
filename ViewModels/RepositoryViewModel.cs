using GitManager.Models;
using GitManager.Services;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private async Task OpenCoreAsync(string path)
    {
        CancelCommitSummary();
        CacheCurrentWorkspace();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        string root = await Git.OpenAsync(path);
        var bookmark = Repositories.FirstOrDefault(item => string.Equals(item.Path, root, StringComparison.OrdinalIgnoreCase));
        if (bookmark is null) { bookmark = new(root); Repositories.Add(bookmark); }
        else bookmark.RefreshPath();
        await RefreshRepositoryNameAsync(bookmark);
        Repository = bookmark;
        Settings.Value.IgnoredRepositories.RemoveAll(path => string.Equals(path, root, StringComparison.OrdinalIgnoreCase));
        CommitMessage = string.Empty;
        Settings.Value.LastRepository = root;
        SaveBookmarks();
        await RefreshCoreAsync();
        SetWorkspaceLoading(false);
        _fetchAfterOperation = true;
        _ = PreloadRepositoriesAsync();
        AppLog.Write($"仓库本地加载完成：{bookmark.Name}，耗时 {timer.ElapsedMilliseconds} 毫秒");
    }

    private void SaveBookmarks()
    {
        Settings.Value.Repositories = Repositories.Select(item => item.Path).ToList();
        Settings.Save();
    }

    public Task CreateRepositoryAsync(string path) => ExecuteAsync("正在建立仓库", async () => await OpenCoreAsync(await Git.InitializeAsync(path)));
    public Task CloneRepositoryAsync(string url, string destination) => ExecuteAsync("正在克隆仓库", async () => await OpenCoreAsync(await Git.CloneAsync(url, destination)));
    public Task RefreshAsync() => ExecuteAsync("正在刷新", async () =>
    {
        await DiscoverRepositoriesCoreAsync();
        await RefreshRepositoryNamesAsync();
        if (HasRepository) await RefreshCoreAsync();
        else if (Repositories.FirstOrDefault() is { } first) await OpenCoreAsync(first.Path);
        _ = RefreshRepositoryPushIndicatorsAsync();
    });

    private async Task RefreshCoreAsync()
    {
        bool sameRepository = string.Equals(_snapshotRoot, Root, StringComparison.OrdinalIgnoreCase);
        var content = await GetWorkspaceRead(Root, sameRepository ? CaptureWorkspaceSelection() : new());
        ApplyWorkspace(new(content, CommitMessage, sameRepository ? CheckedChangePaths() : []));
        SetWorkspaceLoading(false);
        _refreshAfterOperation = false;
    }

    public Task RemoveRepositoryAsync(RepositoryBookmark bookmark) => RemoveRepositoryCopyAsync(bookmark, false);

    public Task SwitchBranchAsync(string branch, bool create = false) => ExecuteAsync("正在切换分支", async () =>
    {
        try { await Git.SwitchBranchAsync(Root, branch, create); }
        finally { await RefreshCoreAsync(); }
    });

    public Task SynchronizeAsync(string operation) => RunVersionSyncAsync(operation);

    public Task SaveRemoteAsync(string url) => ExecuteAsync("正在保存远程地址", async () =>
    {
        await Git.SaveRemoteAsync(Root, url, _versions?.RemoteName is { Length: > 0 } name ? name : "origin");
        await RefreshCoreAsync();
        await FetchRemoteCoreAsync(tolerateFailure: true);
        await RefreshVersionsCoreAsync();
        await LoadComparisonCoreAsync();
    });

    public Task SaveSettingsAsync(string gitPath, string theme, string name, string email) => ExecuteAsync("正在保存设置", async () =>
    {
        if (!System.IO.File.Exists(gitPath)) throw new InvalidOperationException("请选择有效的 git.exe。");
        await new GitManager.Services.GitRunner(() => gitPath).RunAsync(null, ["--version"]);
        Settings.Value.GitExecutablePath = gitPath;
        Settings.Value.Theme = theme;
        Settings.Save();
        GitManager.Services.ThemeService.Apply(theme);
        if (HasRepository)
        {
            await Git.SaveIdentityAsync(Root, name, email);
            await RefreshCoreAsync();
        }
    });
}
