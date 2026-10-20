using GitManager.Models;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private sealed record WorkspaceSelection(
        int Tab = 0, string? Change = null, string? Commit = null, string? HistoryFile = null,
        RemoteBranch? RemoteBranch = null, string? RemoteCommit = null, string? RemoteFile = null,
        VersionChoice? Before = null, VersionChoice? After = null, bool FollowHeads = true, string? VersionFile = null,
        string? LocalBranch = null);

    private sealed record WorkspaceContent(
        RepositorySnapshot Snapshot, VersionSnapshot Versions, WorkspaceSelection Selection,
        List<HistoryFile> HistoryFiles, List<HistoryFile> RemoteFiles, List<VersionChoice> Choices,
        List<HistoryFile> VersionFiles, FileComparison? Comparison, List<DiffRow> Rows);

    private sealed record WorkspaceView(WorkspaceContent Content, string Message, HashSet<string> CheckedPaths);

    private WorkspaceSelection CaptureWorkspaceSelection() => new(
        ActiveTab, SelectedChange?.Path, SelectedCommit?.Hash, SelectedHistoryFile?.Path,
        SelectedRemoteBranch, SelectedRemoteCommit?.Hash, SelectedRemoteFile?.Path,
        ComparisonBefore, ComparisonAfter, _followHeads, SelectedVersionFile?.Path, _versions?.LocalBranch);
}
