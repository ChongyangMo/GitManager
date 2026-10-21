using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Repositories;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    // 所有异步读取只使用传入的仓库和选择，切换过程中不读取当前界面状态。
    private async Task<WorkspaceContent> ReadWorkspaceAsync(string root, WorkspaceSelection selection)
    {
        var readmeTask = ReadmeReader.ReadAsync(root);
        var snapshot = await Git.ReadAsync(root);
        var versions = await Git.ReadVersionsAsync(root, selection.LocalBranch == snapshot.Branch ? selection.RemoteBranch : null, snapshot);
        var commit = snapshot.History.FirstOrDefault(item => item.Hash == selection.Commit) ?? snapshot.History.FirstOrDefault();
        var remoteCommit = versions.RemoteHistory.FirstOrDefault(item => item.Hash == selection.RemoteCommit) ?? versions.RemoteHistory.FirstOrDefault();
        var historyTask = commit is null ? Task.FromResult(new List<HistoryFile>()) : Git.ReadHistoryFilesAsync(root, commit);
        var remoteTask = remoteCommit is null ? Task.FromResult(new List<HistoryFile>()) : Git.ReadHistoryFilesAsync(root, remoteCommit);
        await Task.WhenAll(historyTask, remoteTask);
        var historyFiles = await historyTask;
        var remoteFiles = await remoteTask;
        var historyFile = historyFiles.FirstOrDefault(item => item.Path == selection.HistoryFile) ?? historyFiles.FirstOrDefault();
        var remoteFile = remoteFiles.FirstOrDefault(item => item.Path == selection.RemoteFile) ?? remoteFiles.FirstOrDefault();
        var change = snapshot.Changes.FirstOrDefault(item => item.Path == selection.Change) ?? snapshot.Changes.FirstOrDefault();
        string localSource = "本地 " + versions.LocalBranch;
        string remoteSource = versions.SourceLabel + " " + versions.Target?.Display;
        var choices = snapshot.History.Select(item => new VersionChoice(localSource, item))
            .Concat(versions.RemoteHistory.Select(item => new VersionChoice(remoteSource, item))).ToList();
        var before = selection.FollowHeads ? null : choices.FirstOrDefault(item => item == selection.Before);
        var after = selection.FollowHeads ? null : choices.FirstOrDefault(item => item == selection.After);
        before ??= choices.FirstOrDefault(item => item.Source == remoteSource) ?? choices.Where(item => item.Source == localSource).Skip(1).FirstOrDefault() ?? choices.FirstOrDefault();
        after ??= choices.FirstOrDefault(item => item.Source == localSource);
        var versionFiles = selection.Tab == 3 && before is not null && after is not null
            ? await Git.ReadVersionDifferencesAsync(root, before.Commit.Hash, after.Commit.Hash) : [];
        var versionFile = versionFiles.FirstOrDefault(item => item.Path == selection.VersionFile) ?? versionFiles.FirstOrDefault();
        FileComparison? comparison = selection.Tab switch
        {
            0 => change is null ? null : await Git.CompareWorkingAsync(root, change, snapshot.HasHead),
            1 => commit is null || historyFile is null ? null : await Git.CompareHistoryAsync(root, commit, historyFile),
            2 => remoteCommit is null || remoteFile is null ? null : await Git.CompareHistoryAsync(root, remoteCommit, remoteFile),
            ReadmeTab => null,
            _ => before is null || after is null ? null : versionFile is null
                ? new("", "", "", before.Label, after.Label, versionFiles.Count == 0 ? "两个版本内容一致。" : "选择文件查看差异。")
                : await Git.CompareVersionsAsync(root, before, after, versionFile)
        };
        selection = selection with
        {
            Change = change?.Path, Commit = commit?.Hash, HistoryFile = historyFile?.Path,
            RemoteBranch = versions.Target, RemoteCommit = remoteCommit?.Hash, RemoteFile = remoteFile?.Path,
            Before = before, After = after, VersionFile = versionFile?.Path, LocalBranch = snapshot.Branch
        };
        return new(snapshot, versions, selection, historyFiles, remoteFiles, choices, versionFiles,
            comparison, comparison is null ? [] : DiffBuilder.Build(comparison), await readmeTask);
    }
}
