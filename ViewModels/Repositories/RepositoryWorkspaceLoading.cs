using System.IO;
using GitManager.Models;
using GitManager.Services;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private readonly Dictionary<string, (WorkspaceSelection Selection, Task<WorkspaceContent> Task)> _workspaceReads = new(StringComparer.OrdinalIgnoreCase);
    private Task? _workspaceRefreshTask;
    private Task? _workspaceWarmupTask;
    private long _workspaceGeneration;
    private bool _workspaceLoading, _workspaceRefreshing, _refreshAfterOperation, _warmingWorkspaces;
    public bool IsWorkspaceLoading => _workspaceLoading;
    public bool IsWorkspaceRefreshing => _workspaceRefreshing;

    private void SetWorkspaceLoading(bool value)
    {
        _workspaceLoading = value;
        Notify(nameof(IsWorkspaceLoading)); Notify(nameof(CanOperate)); UpdateBusyFeedback();
    }

    public Task OpenRepositoryAsync(string path)
    {
        if (IsBusy || _disposed) return Task.CompletedTask;
        CancelCommitSummary();
        InvalidateHistoryNavigation();
        var bookmark = Repositories.FirstOrDefault(item => string.Equals(item.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (bookmark is null) return ExecuteAsync("正在打开仓库", () => OpenCoreAsync(path));
        bookmark.RefreshPath();
        CacheCurrentWorkspace();
        _workspaceGeneration++;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        IsBusy = true;
        try
        {
            Repository = bookmark;
            if (_workspaceCache.TryGetValue(bookmark.Path, out var cached))
            {
                ApplyWorkspace(cached);
                SetWorkspaceLoading(false);
            }
            else
            {
                ClearWorkspace();
                SetWorkspaceLoading(true);
            }
            Settings.Value.LastRepository = bookmark.Path;
            SaveBookmarks();
            Status = _workspaceLoading ? "正在读取仓库" : "正在刷新本地状态";
        }
        catch (Exception ex)
        {
            SetWorkspaceLoading(true);
            Status = AppLog.Clean(ex.Message);
            AppLog.Write("切换仓库失败", ex);
            return Task.CompletedTask;
        }
        finally { IsBusy = false; }
        StartWorkspaceRefresh(fetchAfter: true);
        AppLog.Write($"仓库页面已切换：{bookmark.Name}，耗时 {timer.ElapsedMilliseconds} 毫秒");
        return Task.CompletedTask;
    }

    private Task<WorkspaceContent> GetWorkspaceRead(string root, WorkspaceSelection selection)
    {
        _workspaceReads.TryGetValue(root, out var previous);
        if (previous.Task is { IsCompleted: false } && previous.Selection == selection) return previous.Task;
        var task = Task.Run(async () =>
        {
            // 同一仓库的状态读取顺序执行，避免 Git 更新索引时争用锁。
            if (previous.Task is { IsCompleted: false }) { try { await previous.Task; } catch { } }
            return await ReadWorkspaceAsync(root, selection);
        });
        _workspaceReads[root] = (selection, task);
        _ = ReleaseWorkspaceReadAsync(root, task);
        return task;
    }

    private async Task ReleaseWorkspaceReadAsync(string root, Task<WorkspaceContent> task)
    {
        try { await task; }
        catch { /* 读取错误由当前页面或预读流程处理。 */ }
        finally
        {
            if (_workspaceReads.TryGetValue(root, out var current) && ReferenceEquals(current.Task, task))
                _workspaceReads.Remove(root);
        }
    }

    private void StartWorkspaceRefresh(bool fetchAfter)
    {
        if (_disposed || !HasRepository) return;
        long generation = ++_workspaceGeneration;
        string root = Root;
        var selection = _snapshotRoot == root ? CaptureWorkspaceSelection() : new WorkspaceSelection();
        _workspaceRefreshing = true;
        _workspaceRefreshTask = RefreshWorkspaceInBackgroundAsync(root, generation, GetWorkspaceRead(root, selection), fetchAfter);
    }

    private async Task RefreshWorkspaceInBackgroundAsync(string root, long generation, Task<WorkspaceContent> read, bool fetchAfter)
    {
        try
        {
            var content = await read;
            if (_disposed || generation != _workspaceGeneration || Repository?.Path != root || IsBusy) return;
            IsBusy = true;
            try
            {
                ApplyWorkspace(new(content, CommitMessage, CheckedChangePaths()));
                SetWorkspaceLoading(false);
                CacheCurrentWorkspace();
                Status = "就绪";
            }
            finally { IsBusy = false; }
            if (fetchAfter) StartBackgroundFetch();
        }
        catch (Exception ex)
        {
            if (_disposed || generation != _workspaceGeneration || Repository?.Path != root) return;
            SetWorkspaceLoading(true);
            Status = AppLog.Clean(ex.Message);
            AppLog.Write("后台读取仓库失败", ex);
        }
        finally
        {
            if (generation == _workspaceGeneration)
            {
                _workspaceRefreshing = false;
                _workspaceRefreshTask = null;
            }
        }
    }

    private async Task PauseWorkspaceRefreshAsync()
    {
        if (!_workspaceRefreshing) return;
        _refreshAfterOperation = true;
        _workspaceGeneration++;
        _workspaceRefreshing = false;
        if (_workspaceRefreshTask is { } task) await task;
        _workspaceRefreshTask = null;
    }

    public Task PreloadRepositoriesAsync()
    {
        if (_workspaceWarmupTask is { IsCompleted: false }) return _workspaceWarmupTask;
        return _workspaceWarmupTask = WarmOtherWorkspacesAsync();
    }

    private async Task WarmOtherWorkspacesAsync()
    {
        if (_warmingWorkspaces || _disposed) return;
        _warmingWorkspaces = true;
        try
        {
            await Task.Yield();
            foreach (var bookmark in Repositories.Take(8).ToArray())
            {
                if (_disposed) break;
                if (Repository?.Path == bookmark.Path || _workspaceCache.ContainsKey(bookmark.Path)) continue;
                try
                {
                    var content = await GetWorkspaceRead(bookmark.Path, new());
                    if (!_disposed && Repository?.Path != bookmark.Path && !_workspaceCache.ContainsKey(bookmark.Path))
                        StoreWorkspace(bookmark.Path, new(content, "", []));
                }
                catch (Exception ex) { AppLog.Write("预读本地仓库失败：" + bookmark.Name, ex); }
            }
        }
        finally { _warmingWorkspaces = false; }
    }
}
