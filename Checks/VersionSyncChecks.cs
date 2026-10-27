using System.IO;
using GitManager.Models;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunVersionChecksAsync()
    {
        string directory = CheckDirectories.Create("VersionChecks");
        string local = Path.Combine(directory, "本地"), remote = Path.Combine(directory, "远程.git"), other = Path.Combine(directory, "另一台电脑");
        Directory.CreateDirectory(local);
        await Git.InitializeAsync(local);
        await Git.SaveIdentityAsync(local, "版本校验", "version-checks@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(local, "文档.txt"), "初始内容\n");
        await CommitAllAsync(local, "初始版本");
        await Runner.RunAsync(null, ["init", "--bare", "--initial-branch=main", remote]);
        await Git.SaveRemoteAsync(local, remote);
        var state = await Git.ReadVersionsAsync(local);
        Check(state.RemoteHead is null && state.Ahead is null && state.Behind is null, "未获取的远程版本不填充虚假计数");
        await Git.SynchronizeVersionsAsync(local, null, "push");
        state = await Git.ReadVersionsAsync(local);
        string baseline = state.LocalHead!.Hash;
        Check(state.RemoteHead?.Hash == baseline && state.Ahead == 0 && state.Behind == 0, "首次推送后本地和远程版本一致");
        await Git.CloneAsync(remote, other);
        await Git.SaveIdentityAsync(other, "另一端校验", "other-checks@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(other, "远程新增.txt"), "另一端新增的内容\n");
        await CommitAllAsync(other, "远程新增文件");
        await Git.SynchronizeVersionsAsync(other, null, "push");
        Check((await Git.ReadVersionsAsync(local)).RemoteHead!.Hash == baseline, "未获取时保留本地远程缓存");
        await Git.FetchVersionsAsync(local, "origin");
        state = await Git.ReadVersionsAsync(local);
        Check(state.Ahead == 0 && state.Behind == 1 && state.RemoteHistory.Count == 2, "远程提交列表和待拉取数量准确");
        var files = await Git.ReadVersionDifferencesAsync(local, state.LocalHead!.Hash, state.RemoteHead!.Hash);
        var diff = await Git.CompareVersionsAsync(local, new("本地 main", state.LocalHead), new("远程 origin/main", state.RemoteHead), files.Single());
        Check(files.Single().Path == "远程新增.txt" && files.Single().State == "新增" && diff.Before == "" && diff.After.Contains("另一端新增"), "两版本文件差异和新增文件预览准确");
        await Git.SynchronizeVersionsAsync(local, null, "sync");
        state = await Git.ReadVersionsAsync(local);
        Check(state.LocalHead!.Hash == state.RemoteHead!.Hash && File.Exists(Path.Combine(local, "远程新增.txt")), "一键同步可快进更新本地版本");
        await File.WriteAllTextAsync(Path.Combine(local, "本地新增.txt"), "本地提交的内容\n");
        await CommitAllAsync(local, "本地新增文件");
        Check((await Git.ReadVersionsAsync(local)).Ahead == 1, "待推送数量准确");
        await Git.SynchronizeVersionsAsync(local, null, "sync");
        state = await Git.ReadVersionsAsync(local);
        Check(state.Ahead == 0 && state.Behind == 0 && state.LocalHead!.Hash == state.RemoteHead!.Hash, "一键同步可推送本地版本");

        await Git.SynchronizeVersionsAsync(other, null, "sync");
        await Runner.RunAsync(other, ["push", "origin", "HEAD:refs/heads/stable"]);
        await Git.FetchVersionsAsync(local, "origin");
        await Runner.RunAsync(local, ["branch", "--set-upstream-to=origin/stable", "main"]);
        await Runner.RunAsync(other, ["switch", "-c", "stable", "--track", "origin/stable"]);
        Check((await Git.ReadVersionsAsync(local)).Target?.Name == "stable", "本地与远程分支名称不同时读取实际上游");
        string mainBefore = (await Runner.RunAsync(local, ["ls-remote", "origin", "refs/heads/main"])).Output.Split('\t')[0];
        await File.WriteAllTextAsync(Path.Combine(other, "稳定分支.txt"), "稳定分支的远程内容\n");
        await CommitAllAsync(other, "稳定分支提交");
        await Git.SynchronizeVersionsAsync(other, null, "push");
        await Git.SynchronizeVersionsAsync(local, null, "sync");
        Check(File.Exists(Path.Combine(local, "稳定分支.txt")) && (await Git.ReadVersionsAsync(local)).Target?.Name == "stable", "同步更新到正确的远程上游分支");
        await File.WriteAllTextAsync(Path.Combine(local, "稳定本地.txt"), "推送到稳定分支\n");
        await CommitAllAsync(local, "稳定本地提交");
        await Git.SynchronizeVersionsAsync(local, null, "sync");
        Check((await Runner.RunAsync(local, ["ls-remote", "origin", "refs/heads/main"])).Output.Split('\t')[0] == mainBefore, "同步不会误推到同名 main 分支");
        await Git.SynchronizeVersionsAsync(other, null, "sync");

        await File.WriteAllTextAsync(Path.Combine(local, "文档.txt"), "本地冲突内容\n");
        await CommitAllAsync(local, "本地独立修改");
        string localBeforeMerge = (await Git.ReadVersionsAsync(local)).LocalHead!.Hash;
        await File.WriteAllTextAsync(Path.Combine(other, "文档.txt"), "远程冲突内容\n");
        await CommitAllAsync(other, "远程独立修改");
        await Git.SynchronizeVersionsAsync(other, null, "push");
        await Git.FetchVersionsAsync(local, "origin");
        state = await Git.ReadVersionsAsync(local);
        Check(state.Diverged && state.Ahead == 1 && state.Behind == 1, "分叉时准确显示两端独有提交");
        bool blocked = false;
        try { await Git.SynchronizeVersionsAsync(local, null, "sync"); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked && (await Git.ReadVersionsAsync(local)).LocalHead!.Hash == localBeforeMerge, "一键同步不强制覆盖分叉历史");
        var merge = await Git.SynchronizeVersionsAsync(local, null, "merge");
        Check(merge.HasConflicts && (await Git.ReadVersionsAsync(local)).IsMerging && (await Git.ReadChangesAsync(local)).Any(file => file.IsConflict), "合并分叉可返回真实冲突状态");
        blocked = false;
        try { await Git.ResolveConflictAsync(local, "文档.txt", "edited"); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked, "未清理冲突标记时不能标记为已解决");
        await Git.ResolveConflictAsync(local, "文档.txt", "ours");
        await Git.FinishMergeAsync(local, "");
        state = await Git.ReadVersionsAsync(local);
        Check(!state.IsMerging && state.LocalHead!.Parents.Split(' ').Length == 2 && File.ReadAllText(Path.Combine(local, "文档.txt")).Contains("本地冲突"), "采用本地后可完成保留双亲的合并提交");
        await Git.SynchronizeVersionsAsync(local, null, "sync");
        await Git.SynchronizeVersionsAsync(other, null, "sync");

        await File.WriteAllTextAsync(Path.Combine(local, "文档.txt"), "本地第二次冲突\n");
        await CommitAllAsync(local, "本地第二次修改");
        localBeforeMerge = (await Git.ReadVersionsAsync(local)).LocalHead!.Hash;
        await File.WriteAllTextAsync(Path.Combine(other, "文档.txt"), "远程第二次冲突\n");
        await CommitAllAsync(other, "远程第二次修改");
        await Git.SynchronizeVersionsAsync(other, null, "push");
        await Git.SynchronizeVersionsAsync(local, null, "merge");
        await Git.AbortMergeAsync(local);
        Check(!(await Git.ReadVersionsAsync(local)).IsMerging && (await Git.ReadVersionsAsync(local)).LocalHead!.Hash == localBeforeMerge && File.ReadAllText(Path.Combine(local, "文档.txt")).Contains("本地第二次"), "取消合并可回到合并前版本");
        await Git.SynchronizeVersionsAsync(local, null, "merge");
        await Git.ResolveConflictAsync(local, "文档.txt", "theirs");
        await Git.FinishMergeAsync(local, "接受远程冲突内容");
        Check(File.ReadAllText(Path.Combine(local, "文档.txt")).Contains("远程第二次"), "冲突可采用远程文件并完成合并");
        await Git.SynchronizeVersionsAsync(local, null, "sync");
        await Git.SynchronizeVersionsAsync(other, null, "sync");
        await File.WriteAllTextAsync(Path.Combine(other, "远程后续.txt"), "后续提交\n");
        await CommitAllAsync(other, "远程后续提交");
        await Git.SynchronizeVersionsAsync(other, null, "push");
        await File.WriteAllTextAsync(Path.Combine(local, "未提交.txt"), "本地尚未提交的内容\n");
        string unchangedHead = (await Git.ReadVersionsAsync(local)).LocalHead!.Hash;
        blocked = false;
        try { await Git.SynchronizeVersionsAsync(local, null, "sync"); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked && (await Git.ReadVersionsAsync(local)).LocalHead!.Hash == unchangedHead && File.ReadAllText(Path.Combine(local, "未提交.txt")).Contains("尚未提交"), "存在工作区修改时不会被远程同步覆盖");
        Console.WriteLine($"版本检查通过，共 {_count} 项。真实双端仓库：{directory}");
    }

    private static async Task CommitAllAsync(string root, string message) => await Git.CommitAsync(root, await Git.ReadChangesAsync(root), message);
}
