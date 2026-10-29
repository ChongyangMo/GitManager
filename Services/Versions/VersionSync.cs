using GitManager.Models;

namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    public async Task<SyncOutcome> SynchronizeVersionsAsync(string root, RemoteBranch? target, string operation, bool fetch = true)
    {
        await EnsureNoOperationAsync(root);
        var state = await ReadVersionsAsync(root, target);
        if (!state.HasRemote || state.Target is null) throw new InvalidOperationException("请先设置远程仓库并选择分支。");
        if (fetch) await FetchVersionsAsync(root, state.RemoteName, createMissing: operation is "push" or "sync");
        state = await ReadVersionsAsync(root, state.Target);
        var destination = state.Target ?? throw new InvalidOperationException("远程分支已变化，请重新选择。");
        if (operation == "fetch") return new("远程版本已更新");
        if (state.LocalHead is null) throw new InvalidOperationException("本地尚无提交，请先提交代码，或通过克隆获取远程仓库。");
        if (state.LocalBranch == "分离 HEAD") throw new InvalidOperationException("请先切换到本地分支。");
        if (operation == "sync")
        {
            if (state.HasCommonHistory == false) throw new InvalidOperationException("本地和远程没有共同历史，未执行同步或覆盖。");
            if (state.Diverged) throw new InvalidOperationException("本地和远程都有新提交，请先对比，再点击“合并远程”。");
            operation = state.RemoteHead is null || state.Ahead is > 0 ? "push" : state.Behind is > 0 ? "pull" : "none";
        }
        await CheckVersionUnchangedAsync(root, state);
        switch (operation)
        {
            case "none": return new("本地与远程提交已一致");
            case "push":
                if (state.Behind is > 0) throw new InvalidOperationException("远程存在本地尚未包含的提交，请先更新或合并后再推送。");
                await Run(root, "push", "--set-upstream", state.RemoteName, "HEAD:refs/heads/" + destination.Name);
                AppLog.Write("本地版本已推送到远程");
                return new("本地版本已推送");
            case "pull":
                if (state.RemoteHead is null) throw new InvalidOperationException("远程尚无此分支，请先推送本地版本。");
                if (state.Diverged || state.HasCommonHistory == false) throw new InvalidOperationException("无法快进更新，请先对比并合并远程版本。");
                await EnsureCleanForSyncAsync(root);
                await Run(root, "merge", "--ff-only", "--no-edit", "--no-overwrite-ignore", state.RemoteHead.Hash);
                await Run(root, "branch", "--set-upstream-to=" + destination.FullRef, state.LocalBranch);
                AppLog.Write("本地已更新到远程版本");
                return new("本地版本已更新");
            case "merge":
                if (state.RemoteHead is null) throw new InvalidOperationException("远程尚无可合并的版本。");
                if (state.HasCommonHistory == false) throw new InvalidOperationException("本地和远程没有共同历史，未执行合并或覆盖。");
                await EnsureCleanForSyncAsync(root);
                var merge = await RunWithCommitIdentityAsync(root, ["merge", "--no-edit", "--no-overwrite-ignore", state.RemoteHead.Hash], true, state.RemoteName);
                if (merge.ExitCode != 0)
                {
                    var latest = await ReadVersionsAsync(root, state.Target);
                    int conflicts = (await ReadChangesAsync(root)).Count(file => file.IsConflict);
                    if (latest.IsMerging && conflicts > 0) return new($"有 {conflicts} 个冲突，请在“改动”中处理", true);
                    throw new InvalidOperationException("合并未完成：" + merge.Error + merge.Output);
                }
                AppLog.Write("远程版本已合并到本地");
                return new("合并完成，可继续推送");
            default: throw new InvalidOperationException("不支持的同步操作。");
        }
    }

    private async Task EnsureCleanForSyncAsync(string root)
    {
        if ((await ReadChangesAsync(root)).Count > 0) throw new InvalidOperationException("请先提交或处理工作区修改，再更新或合并远程版本。");
    }

    private async Task CheckVersionUnchangedAsync(string root, VersionSnapshot state)
    {
        string hash = (await Run(root, "rev-parse", "HEAD")).Output.Trim();
        string branch = (await Run(root, "symbolic-ref", "--short", "HEAD")).Output.Trim();
        if (hash != state.LocalHead?.Hash || branch != state.LocalBranch) throw new InvalidOperationException("仓库版本已变化，请刷新后重试。");
    }
}
