using GitManager.Models;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private readonly Dictionary<string, WorkspaceView> _workspaceCache = new(StringComparer.OrdinalIgnoreCase);

    private void CacheCurrentWorkspace()
    {
        if (!HasRepository || _versions is null || _snapshotRoot != Root || _workspaceLoading) return;
        var snapshot = new RepositorySnapshot(Branch, Branches.ToList(), Changes.ToList(), History.ToList(), Remote, UserName, UserEmail, _hasHead);
        var content = new WorkspaceContent(snapshot, _versions, CaptureWorkspaceSelection(), HistoryFiles.ToList(),
            RemoteFiles.ToList(), VersionChoices.ToList(), VersionFiles.ToList(), Comparison, DiffRows, Readme);
        StoreWorkspace(Root, new(content, CommitMessage, CheckedChangePaths()));
    }

    private HashSet<string> CheckedChangePaths() => Changes.Where(item => item.Selected).Select(item => item.Path).ToHashSet(StringComparer.Ordinal);

    private void StoreWorkspace(string root, WorkspaceView view)
    {
        _workspaceCache.Remove(root);
        _workspaceCache.Add(root, view);
        // 已加入列表的仓库保留页面，避免仓库稍多时来回切换不断淘汰缓存。
        while (_workspaceCache.Count > Math.Max(8, Repositories.Count))
            _workspaceCache.Remove(_workspaceCache.Keys.First(path => !string.Equals(path, Repository?.Path, StringComparison.OrdinalIgnoreCase)));
    }

    private void ApplyWorkspace(WorkspaceView view)
    {
        var content = view.Content;
        var snapshot = content.Snapshot;
        var selection = content.Selection;
        var previousChanges = _snapshotRoot == Root ? Changes.ToDictionary(item => item.Path, StringComparer.Ordinal) : [];
        _snapshotRoot = _versionRoot = Root;
        _hasHead = snapshot.HasHead;
        Remote = snapshot.Remote;
        Repository?.UpdateDisplayName(Services.Repositories.RepositoryDisplayName.FromRemote(snapshot.Remote));
        UserName = snapshot.UserName; UserEmail = snapshot.UserEmail;
        CommitMessage = view.Message;
        Replace(Branches, snapshot.Branches);
        Branch = snapshot.Branch;
        Replace(Changes, snapshot.Changes.Select(item => previousChanges.TryGetValue(item.Path, out var previous)
            && previous.OriginalPath == item.OriginalPath && previous.IndexState == item.IndexState
            && previous.WorktreeState == item.WorktreeState ? previous : item));
        foreach (var file in Changes) file.Selected = view.CheckedPaths.Contains(file.Path);
        Replace(History, snapshot.History);
        SelectedChange = Changes.FirstOrDefault(item => item.Path == selection.Change);
        SelectedCommit = History.FirstOrDefault(item => item.Hash == selection.Commit);
        Replace(HistoryFiles, content.HistoryFiles);
        SelectedHistoryFile = HistoryFiles.FirstOrDefault(item => item.Path == selection.HistoryFile);
        _versions = content.Versions;
        Replace(RemoteBranches, _versions.Branches);
        SelectedRemoteBranch = _versions.Target;
        Replace(RemoteHistory, _versions.RemoteHistory);
        SelectedRemoteCommit = RemoteHistory.FirstOrDefault(item => item.Hash == selection.RemoteCommit);
        Replace(RemoteFiles, content.RemoteFiles);
        SelectedRemoteFile = RemoteFiles.FirstOrDefault(item => item.Path == selection.RemoteFile);
        Replace(VersionChoices, content.Choices);
        ComparisonBefore = selection.Before; ComparisonAfter = selection.After;
        _followHeads = selection.FollowHeads;
        Replace(VersionFiles, content.VersionFiles);
        SelectedVersionFile = VersionFiles.FirstOrDefault(item => item.Path == selection.VersionFile);
        Readme = content.Readme;
        ActiveTab = selection.Tab;
        if (_comparison != content.Comparison)
        {
            _comparison = content.Comparison;
            Notify(nameof(Comparison));
            DiffRows = content.Rows;
        }
        Notify(nameof(ChangesTitle)); Notify(nameof(HistoryTitle)); Notify(nameof(VersionComparisonState));
        NotifyVersionState();
    }

    private void ClearWorkspace()
    {
        _snapshotRoot = null; _hasHead = false;
        Branch = Remote = UserName = UserEmail = CommitMessage = "";
        Changes.Clear(); History.Clear(); HistoryFiles.Clear(); Branches.Clear();
        SelectedChange = null; SelectedCommit = null; SelectedHistoryFile = null;
        ClearVersions(); Comparison = null; Readme = null;
        ActiveTab = ReadmeTab;
        Notify(nameof(ChangesTitle)); Notify(nameof(HistoryTitle));
    }
}
