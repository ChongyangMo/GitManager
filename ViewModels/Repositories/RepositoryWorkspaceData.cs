using GitManager.Models;
using GitManager.Services.Repositories;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private sealed record WorkspaceSelection(
        int Tab = ReadmeTab, string? Change = null, string? Commit = null, string? HistoryFile = null,
        RemoteBranch? RemoteBranch = null, string? RemoteCommit = null, string? RemoteFile = null,
        VersionChoice? Before = null, VersionChoice? After = null, bool FollowHeads = true, string? VersionFile = null,
        string? LocalBranch = null);

    private sealed record WorkspaceContent(
        RepositorySnapshot Snapshot, VersionSnapshot Versions, WorkspaceSelection Selection,
        List<HistoryFile> HistoryFiles, List<HistoryFile> RemoteFiles, List<VersionChoice> Choices,
        List<HistoryFile> VersionFiles, FileComparison? Comparison, List<DiffRow> Rows, ReadmeDocument? Readme);

    private sealed record WorkspaceView(WorkspaceContent Content, string Message, HashSet<string> CheckedPaths);

    private WorkspaceSelection CaptureWorkspaceSelection() => new(
        ActiveTab, SelectedChange?.Path, SelectedCommit?.Hash, SelectedHistoryFile?.Path,
        SelectedRemoteBranch, SelectedRemoteCommit?.Hash, SelectedRemoteFile?.Path,
        ComparisonBefore, ComparisonAfter, _followHeads, SelectedVersionFile?.Path, _versions?.LocalBranch);
}
