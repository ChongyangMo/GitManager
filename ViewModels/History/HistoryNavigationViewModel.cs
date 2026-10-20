using GitManager.Models;
using GitManager.Services;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private long _historyNavigationGeneration, _historyWarmupGeneration;
    private string? _localHistoryCommit, _remoteHistoryCommit;
    private bool _historyNavigationLoading, _applyingHistorySelection;
    public bool IsHistoryLoading => _historyNavigationLoading;
    public bool IsUpdatingHistorySelection => _applyingHistorySelection;

    private void InvalidateHistoryNavigation()
    {
        _historyNavigationGeneration++;
        _historyWarmupGeneration++;
        _historyNavigationLoading = false;
    }

    private bool IsCurrentHistoryRequest(long generation, string root, string hash, bool remote)
        => !_disposed && !IsBusy && generation == _historyNavigationGeneration && Repository?.Path == root
            && ActiveTab == (remote ? 2 : 1) && (remote ? SelectedRemoteCommit : SelectedCommit)?.Hash == hash;

    private async Task NavigateHistoryAsync(bool remote, bool loadFiles)
    {
        if (_disposed || IsBusy || !HasRepository || _workspaceLoading) return;
        string root = Root;
        var commit = remote ? SelectedRemoteCommit : SelectedCommit;
        if (commit is null) return;
        InvalidateHistoryNavigation();
        long generation = _historyNavigationGeneration;
        bool resumeWorkspace = _workspaceRefreshing;
        // 历史点击无需等待工作区校验；旧读取完成后按代次丢弃。
        _workspaceGeneration++;
        _workspaceRefreshing = false;
        _workspaceRefreshTask = null;
        _historyNavigationLoading = true;
        Status = "正在读取版本";
        try
        {
            var files = remote ? RemoteFiles : HistoryFiles;
            string? previousPath = (remote ? SelectedRemoteFile : SelectedHistoryFile)?.Path;
            bool sameCommit = (remote ? _remoteHistoryCommit : _localHistoryCommit) == root + "\0" + commit.Hash;
            if (loadFiles || !sameCommit)
            {
                var read = Git.ReadHistoryFilesAsync(root, commit);
                if (!sameCommit && !read.IsCompletedSuccessfully)
                {
                    _applyingHistorySelection = true;
                    try
                    {
                        files.Clear();
                        if (remote) SelectedRemoteFile = null; else SelectedHistoryFile = null;
                        Comparison = null;
                    }
                    finally { _applyingHistorySelection = false; }
                }
                var items = await read;
                if (!IsCurrentHistoryRequest(generation, root, commit.Hash, remote)) return;
                _applyingHistorySelection = true;
                try
                {
                    Replace(files, items);
                    var selected = files.FirstOrDefault(item => item.Path == previousPath) ?? files.FirstOrDefault();
                    if (remote) { SelectedRemoteFile = selected; _remoteHistoryCommit = root + "\0" + commit.Hash; }
                    else { SelectedHistoryFile = selected; _localHistoryCommit = root + "\0" + commit.Hash; }
                }
                finally { _applyingHistorySelection = false; }
            }
            var file = remote ? SelectedRemoteFile : SelectedHistoryFile;
            var previewTask = file is null ? null : Git.ReadHistoryPreviewAsync(root, commit, file);
            if (previewTask?.IsCompletedSuccessfully != true) Comparison = null;
            var preview = previewTask is null ? null : await previewTask;
            if (!IsCurrentHistoryRequest(generation, root, commit.Hash, remote)
                || (remote ? SelectedRemoteFile : SelectedHistoryFile)?.Path != file?.Path) return;
            if (_comparison != preview?.Comparison)
            {
                _comparison = preview?.Comparison;
                Notify(nameof(Comparison));
                DiffRows = preview?.Rows ?? [];
            }
            Status = "就绪";
            CacheCurrentWorkspace();
            _ = WarmNearbyHistoryAsync(root, remote ? RemoteHistory.ToArray() : History.ToArray(), commit, files.ToArray(), file?.Path);
        }
        catch (Exception ex)
        {
            if (!IsCurrentHistoryRequest(generation, root, commit.Hash, remote)) return;
            Status = AppLog.Clean(ex.Message);
            AppLog.Write("读取历史版本失败", ex);
        }
        finally
        {
            if (generation == _historyNavigationGeneration)
            {
                _historyNavigationLoading = false;
                if (resumeWorkspace && !_disposed && Repository?.Path == root) StartWorkspaceRefresh(fetchAfter: false);
            }
        }
    }
}
