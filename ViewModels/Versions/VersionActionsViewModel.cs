namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    public Task RunVersionSyncAsync(string operation) => operation == "fetch" ? FetchCurrentRepositoryAsync() : ExecuteAsync(operation switch { "push" => "正在推送本地版本", "pull" => "正在更新本地版本", "merge" => "正在合并远程版本", "sync" => "正在同步版本", _ => "正在获取远程版本" }, async () =>
    {
        if (_versions?.HasRemote != true) throw new InvalidOperationException("请先设置远程仓库。");
        try
        {
            await FetchRemoteCoreAsync(tolerateFailure: false, createMissing: operation is "push" or "sync");
            var outcome = await Git.SynchronizeVersionsAsync(Root, SelectedRemoteBranch, operation, fetch: false);
            if (outcome.HasConflicts) ActiveTab = ChangesTab;
            await RefreshCoreAsync();
            Status = outcome.Message;
        }
        catch { await RefreshCoreAsync(); throw; }
    });

    public Task ResolveSelectedConflictAsync(string side) => ExecuteAsync("正在处理冲突", async () =>
    {
        if (SelectedChange is null) throw new InvalidOperationException("请先选择冲突文件。");
        try { await Git.ResolveConflictAsync(Root, SelectedChange.Path, side); }
        finally { await RefreshCoreAsync(); }
    });

    public Task CancelMergeAsync() => ExecuteAsync("正在取消合并", async () =>
    {
        try { await Git.AbortMergeAsync(Root); }
        finally { await RefreshCoreAsync(); }
    });

    public Task RestoreRemoteVersionAsync() => ExecuteAsync("正在恢复远程版本", async () =>
    {
        if (SelectedRemoteCommit is null) throw new InvalidOperationException("请先选择远程版本。");
        string? checkpoint = await Git.RestoreVersionAsync(Root, SelectedRemoteCommit.Hash);
        ActiveTab = ChangesTab;
        await RefreshCoreAsync();
        Status = checkpoint is null ? "已恢复远程版本，检查后可提交" : "已恢复远程版本，原修改已保存到恢复点";
    });
}
