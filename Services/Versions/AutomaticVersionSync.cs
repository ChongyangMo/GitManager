using GitManager.Models;

namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    public async Task<SyncOutcome> SynchronizeAutomaticallyAsync(string root, bool upload, bool merge, CancellationToken token = default)
    {
        if (!upload && !merge) return new("自动同步已关闭");
        await EnsureNoOperationAsync(root);
        var before = await ReadVersionsAsync(root);
        if (!before.HasRemote || before.Target is null) throw new InvalidOperationException("请先设置远程仓库并选择分支。");
        if (before.LocalHead is null || before.LocalBranch == "分离 HEAD")
            throw new InvalidOperationException("请先提交代码并切换到本地分支，再自动同步。");

        await FetchVersionsAsync(root, before.RemoteName, token, createMissing: upload);
        await CheckVersionUnchangedAsync(root, before);
        var state = await ReadVersionsAsync(root, before.Target);
        if (state.HasCommonHistory == false) throw new InvalidOperationException("本地和远程没有共同历史，未执行自动合并或覆盖。");

        string message = "本地与远程提交已一致";
        if (state.Behind is > 0)
        {
            if (!merge) throw new InvalidOperationException("远程有新提交，请开启自动合并或手动合并远程。");
            var outcome = await SynchronizeVersionsAsync(root, state.Target, state.Diverged ? "merge" : "pull", fetch: false);
            if (outcome.HasConflicts) return outcome;
            message = state.Diverged ? "远程版本已自动合并到本地" : "本地已自动更新到远程版本";
        }

        // 合并遇到冲突时已返回；只在确有本地提交或远程尚无分支时推送。
        if (upload && (state.Ahead is > 0 || state.RemoteHead is null))
        {
            var pushed = await SynchronizeVersionsAsync(root, state.Target, "push", fetch: false);
            return new(state.Behind is > 0 ? message + "，" + pushed.Message : pushed.Message);
        }
        return new(state.Ahead is > 0 && state.Behind == 0 ? "本地提交待推送" : message);
    }
}
