using GitManager.Models;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private async Task OpenCoreAsync(string path)
    {
        string root = await Git.OpenAsync(path);
        var bookmark = Repositories.FirstOrDefault(item => string.Equals(item.Path, root, StringComparison.OrdinalIgnoreCase));
        if (bookmark is null) { bookmark = new(root); Repositories.Add(bookmark); }
        Repository = bookmark;
        CommitMessage = string.Empty;
        Settings.Value.LastRepository = root;
        SaveBookmarks();
        await RefreshCoreAsync();
    }

    private void SaveBookmarks()
    {
        Settings.Value.Repositories = Repositories.Select(item => item.Path).ToList();
        Settings.Save();
    }

    public Task OpenRepositoryAsync(string path) => ExecuteAsync("正在打开仓库", () => OpenCoreAsync(path));
    public Task CreateRepositoryAsync(string path) => ExecuteAsync("正在建立仓库", async () => await OpenCoreAsync(await Git.InitializeAsync(path)));
    public Task CloneRepositoryAsync(string url, string destination) => ExecuteAsync("正在克隆仓库", async () => await OpenCoreAsync(await Git.CloneAsync(url, destination)));
    public Task RefreshAsync() => !HasRepository ? Task.CompletedTask : ExecuteAsync("正在刷新", RefreshCoreAsync);

    private async Task RefreshCoreAsync()
    {
        bool sameRepository = string.Equals(_snapshotRoot, Root, StringComparison.OrdinalIgnoreCase);
        string? selectedPath = sameRepository ? SelectedChange?.Path : null, selectedHash = sameRepository ? SelectedCommit?.Hash : null;
        var selectedPaths = sameRepository ? Changes.Where(item => item.Selected).Select(item => item.Path).ToHashSet(StringComparer.Ordinal) : [];
        var previousChanges = sameRepository ? Changes.ToDictionary(item => item.Path, StringComparer.Ordinal) : new Dictionary<string, ChangedFile>();
        var snapshot = await Git.ReadAsync(Root);
        _snapshotRoot = Root;
        _hasHead = snapshot.HasHead;
        Replace(Branches, snapshot.Branches);
        Branch = snapshot.Branch;
        Remote = snapshot.Remote;
        UserName = snapshot.UserName; UserEmail = snapshot.UserEmail;
        Replace(Changes, snapshot.Changes.Select(item => previousChanges.TryGetValue(item.Path, out var previous) && previous.OriginalPath == item.OriginalPath && previous.IndexState == item.IndexState && previous.WorktreeState == item.WorktreeState ? previous : item));
        foreach (var file in Changes) file.Selected = selectedPaths.Contains(file.Path);
        Replace(History, snapshot.History);
        SelectedChange = Changes.FirstOrDefault(item => item.Path == selectedPath) ?? Changes.FirstOrDefault();
        SelectedCommit = History.FirstOrDefault(item => item.Hash == selectedHash) ?? History.FirstOrDefault();
        await LoadHistoryFilesCoreAsync();
        await LoadComparisonCoreAsync();
        Notify(nameof(ChangesTitle)); Notify(nameof(HistoryTitle));
    }

    public Task RemoveRepositoryAsync(RepositoryBookmark bookmark) => ExecuteAsync("正在移除仓库记录", async () =>
    {
        Repositories.Remove(bookmark);
        if (Repository == bookmark)
        {
            Repository = null;
            Changes.Clear(); History.Clear(); HistoryFiles.Clear(); Branches.Clear();
            Comparison = null; Branch = ""; Remote = "";
            Settings.Value.LastRepository = null;
            if (Repositories.FirstOrDefault() is { } next) await OpenCoreAsync(next.Path);
        }
        SaveBookmarks();
        Notify(nameof(ChangesTitle)); Notify(nameof(HistoryTitle));
    });

    public Task SwitchBranchAsync(string branch, bool create = false) => ExecuteAsync("正在切换分支", async () =>
    {
        try { await Git.SwitchBranchAsync(Root, branch, create); }
        finally { await RefreshCoreAsync(); }
    });

    public Task CommitAsync() => ExecuteAsync("正在保存本地版本", async () =>
    {
        await Git.CommitAsync(Root, Changes.Where(item => item.Selected).ToArray(), CommitMessage);
        CommitMessage = "";
        await RefreshCoreAsync();
        Status = "已保存到本地";
    });

    public Task SynchronizeAsync(string operation) => ExecuteAsync(operation switch { "push" => "正在推送", "pull" => "正在拉取", _ => "正在获取远程记录" }, async () =>
    {
        try { await Git.SynchronizeAsync(Root, operation); }
        finally { await RefreshCoreAsync(); }
    });

    public Task SaveRemoteAsync(string url) => ExecuteAsync("正在保存远程地址", async () =>
    {
        await Git.SaveRemoteAsync(Root, url);
        await RefreshCoreAsync();
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
