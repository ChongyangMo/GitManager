using System.Collections.ObjectModel;
using GitManager.Models;
using GitManager.Services;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private long _historyNavigationGeneration, _historyWarmupGeneration;
    private string? _localHistoryCommit, _remoteHistoryCommit;
    private bool _historyNavigationLoading, _applyingHistorySelection;
    private ObservableCollection<HistoryFile>? _staleHistoryFiles;
    public bool IsHistoryLoading => _historyNavigationLoading;
    public bool IsUpdatingHistorySelection => _applyingHistorySelection;

    private void SetHistoryLoading(bool value)
    {
        if (_historyNavigationLoading == value) return;
        _historyNavigationLoading = value;
        Notify(nameof(IsHistoryLoading));
    }

    private void InvalidateHistoryNavigation(bool keepStaleContent = false)
    {
        _historyNavigationGeneration++;
        _historyWarmupGeneration++;
        SetHistoryLoading(false);
        if (!keepStaleContent) DiscardStaleHistory();
    }

    // 未读完就被放弃的历史读取，不再保留上一版本的内容，避免缓存或操作对应到错误的提交。
    private void DiscardStaleHistory()
    {
        if (_staleHistoryFiles is not { } files) return;
        _staleHistoryFiles = null;
        _applyingHistorySelection = true;
        try { files.Clear(); Comparison = null; }
        finally { _applyingHistorySelection = false; }
    }

    private bool IsCurrentHistoryRequest(long generation, string root, string hash, bool remote)
        => !_disposed && !IsBusy && generation == _historyNavigationGeneration && Repository?.Path == root
            && ActiveTab == (remote ? RemoteTab : HistoryTab) && (remote ? SelectedRemoteCommit : SelectedCommit)?.Hash == hash;

    private async Task NavigateHistoryAsync(bool remote, bool loadFiles)
    {
        if (_disposed || IsBusy || !HasRepository || _workspaceLoading) return;
        string root = Root;
        var commit = remote ? SelectedRemoteCommit : SelectedCommit;
        if (commit is null) return;
        InvalidateHistoryNavigation(keepStaleContent: true);
        var files = remote ? RemoteFiles : HistoryFiles;
        if (_staleHistoryFiles is not null && _staleHistoryFiles != files) DiscardStaleHistory();
        long generation = _historyNavigationGeneration;
        bool resumeWorkspace = _workspaceRefreshing;
        // 历史点击无需等待工作区校验；旧读取完成后按代次丢弃。
        _workspaceGeneration++;
        _workspaceRefreshing = false;
        _workspaceRefreshTask = null;
        SetHistoryLoading(true);
        Status = "正在读取版本";
        try
        {
            string? previousPath = (remote ? SelectedRemoteFile : SelectedHistoryFile)?.Path;
            bool sameCommit = (remote ? _remoteHistoryCommit : _localHistoryCommit) == root + "\0" + commit.Hash;
            if (loadFiles || !sameCommit)
            {
                var read = Git.ReadHistoryFilesAsync(root, commit);
                if (!sameCommit && !read.IsCompletedSuccessfully)
                {
                    // 读取期间保留上一版本的文件和差异，避免先闪空态；仅取消文件选择，防止对旧文件执行恢复。
                    _applyingHistorySelection = true;
                    try { if (remote) SelectedRemoteFile = null; else SelectedHistoryFile = null; }
                    finally { _applyingHistorySelection = false; }
                    _staleHistoryFiles = files;
                }
                var items = await read;
                if (!IsCurrentHistoryRequest(generation, root, commit.Hash, remote)) return;
                _applyingHistorySelection = true;
                try
                {
                    Replace(files, items);
                    _staleHistoryFiles = null;
                    var selected = files.FirstOrDefault(item => item.Path == previousPath) ?? files.FirstOrDefault();
                    if (remote) { SelectedRemoteFile = selected; _remoteHistoryCommit = root + "\0" + commit.Hash; }
                    else { SelectedHistoryFile = selected; _localHistoryCommit = root + "\0" + commit.Hash; }
                }
                finally { _applyingHistorySelection = false; }
            }
            var file = remote ? SelectedRemoteFile : SelectedHistoryFile;
            var previewTask = file is null ? null : Git.ReadHistoryPreviewAsync(root, commit, file);
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
            Comparison = null;
            Status = AppLog.Clean(ex.Message);
            AppLog.Write("读取历史版本失败", ex);
        }
        finally
        {
            if (generation == _historyNavigationGeneration)
            {
                SetHistoryLoading(false);
                if (resumeWorkspace && !_disposed && Repository?.Path == root) StartWorkspaceRefresh(fetchAfter: false);
            }
        }
    }
}
