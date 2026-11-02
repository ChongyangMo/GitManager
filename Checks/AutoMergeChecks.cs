using System.IO;
using GitManager.Services;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunAutoMergeChecksAsync()
    {
        string directory = CheckDirectories.Create("AutoMergeChecks");
        string local = Path.Combine(directory, "本地"), remote = Path.Combine(directory, "远程.git"), other = Path.Combine(directory, "另一端");
        Directory.CreateDirectory(local);
        await Git.InitializeAsync(local);
        await Git.SaveIdentityAsync(local, "自动合并检查", ChecksCommitEmail);
        await File.WriteAllTextAsync(Path.Combine(local, "文档.txt"), "初始内容\n");
        await CommitAllAsync(local, "初始版本");
        await Runner.RunAsync(null, ["init", "--bare", "--initial-branch=main", remote]);
        await Git.SaveRemoteAsync(local, remote);
        await Git.SynchronizeVersionsAsync(local, null, "push");
        await Git.CloneAsync(remote, other);
        await Git.SaveIdentityAsync(other, "另一端检查", ChecksCommitEmail);

        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        settings.Value.RepositoryDiscovery.Enabled = false;
        settings.Value.Repositories = [local];
        settings.Value.AutoUpload.DisabledRepositories = [local];
        settings.Value.AutoUpload.QuietSeconds = 0;
        using var model = new MainViewModel(settings);
        await model.InitializeAsync(local);
        await model.RunAutoUploadAsync();
        Check(model.IsAutoMergeEnabled && !model.IsAutoUploadEnabled, "自动合并默认开启，可独立于自动上传运行");
        model.IsAutoMergeEnabled = false;
        Check(new SettingsStore(settings.FilePath).Value.AutoMerge.DisabledRepositories.Single() == model.Root, "关闭自动合并后记住仓库选择");

        await File.WriteAllTextAsync(Path.Combine(other, "远程新增.txt"), "远程更新\n");
        await CommitAllAsync(other, "另一端新增文件");
        await Git.SynchronizeVersionsAsync(other, null, "push");
        await model.RunAutoUploadAsync();
        Check(!File.Exists(Path.Combine(local, "远程新增.txt")), "关闭两个开关时不自动更新");
        model.CommitMessage = "自动合并期间保留的草稿";
        model.IsAutoMergeEnabled = true;
        await model.RunAutoUploadAsync();
        await WaitWorkspaceReadsAsync(model);
        var state = await Git.ReadVersionsAsync(local);
        Check(state.LocalHead?.Hash == state.RemoteHead?.Hash && File.Exists(Path.Combine(local, "远程新增.txt")), "没有本地新提交时自动获取并快进远程更新");
        Check(model.CommitMessage == "自动合并期间保留的草稿" && model.CanWriteRepository && model.AutoUploadStatus.Contains("自动更新"), "自动更新后保留草稿并显示结果");
        Check(new SettingsStore(settings.FilePath).Value.AutoMerge.DisabledRepositories.Count == 0, "重新开启自动合并后保存配置");

        await File.WriteAllTextAsync(Path.Combine(other, "远程独立.txt"), "另一端的新文件\n");
        await CommitAllAsync(other, "另一端独立提交");
        await Git.SynchronizeVersionsAsync(other, null, "push");
        await File.WriteAllTextAsync(Path.Combine(local, "本地独立.txt"), "本地尚未提交\n");
        string originalHead = state.LocalHead!.Hash;
        await model.RunAutoUploadAsync();
        Check((await Git.ReadVersionsAsync(local)).LocalHead!.Hash == originalHead && (await Git.ReadChangesAsync(local)).Count == 1,
            "只开自动合并时保留本地未提交修改并等待");
        await CommitAllAsync(local, "本地独立提交");
        string remoteBeforeMerge = (await Git.ReadVersionsAsync(other)).LocalHead!.Hash;
        await model.RunAutoUploadAsync();
        state = await Git.ReadVersionsAsync(local);
        Check(state.Behind == 0 && state.Ahead is > 0 && state.LocalHead!.Parents.Split(' ').Length == 2
            && state.RemoteHead!.Hash == remoteBeforeMerge, "双方都有提交时自动合并，只开合并时不推送");

        model.IsAutoUploadEnabled = true;
        await model.RunAutoUploadAsync();
        state = await Git.ReadVersionsAsync(local);
        Check(state.Ahead == 0 && state.LocalHead?.Hash == state.RemoteHead?.Hash, "同时开启自动上传后推送合并结果");
        await Git.SynchronizeVersionsAsync(other, null, "pull");
        await File.WriteAllTextAsync(Path.Combine(other, "远程后续.txt"), "后续远程提交\n");
        await CommitAllAsync(other, "后续远程更新");
        await Git.SynchronizeVersionsAsync(other, null, "push");
        await File.WriteAllTextAsync(Path.Combine(local, "本地后续.txt"), "本地待上传的新文件\n");
        await model.RunAutoUploadAsync();
        state = await Git.ReadVersionsAsync(local);
        Check(state.Ahead == 0 && state.Behind == 0 && (await Git.ReadChangesAsync(local)).Count == 0
            && File.Exists(Path.Combine(local, "远程后续.txt")), "自动上传先提交本地改动，再合并远程并推送");

        await Git.SynchronizeVersionsAsync(other, null, "pull");
        await File.WriteAllTextAsync(Path.Combine(other, "文档.txt"), "远程冲突内容\n");
        await CommitAllAsync(other, "远程修改同一行");
        await Git.SynchronizeVersionsAsync(other, null, "push");
        string conflictRemote = (await Git.ReadVersionsAsync(other)).LocalHead!.Hash;
        await File.WriteAllTextAsync(Path.Combine(local, "文档.txt"), "本地冲突内容\n");
        await CommitAllAsync(local, "本地修改同一行");
        string conflictLocal = (await Git.ReadVersionsAsync(local)).LocalHead!.Hash;
        await model.RunAutoUploadAsync();
        await WaitWorkspaceReadsAsync(model);
        state = await Git.ReadVersionsAsync(local);
        Check(state.IsMerging && state.LocalHead!.Hash == conflictLocal && state.RemoteHead!.Hash == conflictRemote
            && (await Git.ReadChangesAsync(local)).Single().IsConflict && model.AutoUploadStatus.Contains("冲突"), "冲突时保留合并现场并提示，不推送或覆盖任何一端");
        await model.RunAutoUploadAsync();
        Check((await Git.ReadVersionsAsync(local)).IsMerging, "后续扫描保留未处理冲突");
        Console.WriteLine("自动合并状态：" + model.AutoUploadStatus);
    }
}
