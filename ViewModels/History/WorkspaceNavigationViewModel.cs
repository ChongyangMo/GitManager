using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Repositories;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private async Task NavigateWorkspaceAsync(bool loadFiles)
    {
        if (_disposed || !CanOperate) return;
        InvalidateHistoryNavigation();
        long generation = _historyNavigationGeneration;
        string root = Root;
        var selection = CaptureWorkspaceSelection();
        var change = SelectedChange;
        bool hasHead = _hasHead, resumeWorkspace = _workspaceRefreshing;
        // 前台切换选择无需等待后台校验，旧结果由代次校验丢弃。
        _workspaceGeneration++;
        _workspaceRefreshing = false;
        _workspaceRefreshTask = null;
        bool Current() => !_disposed && !IsBusy && generation == _historyNavigationGeneration
            && Repository?.Path == root && ActiveTab == selection.Tab;
        SetHistoryLoading(true);
        Status = "正在读取版本";
        try
        {
            ReadmeDocument? readme = null;
            List<HistoryFile>? files = null;
            HistoryFile? file = SelectedVersionFile;
            if (selection.Tab == ReadmeTab) readme = await ReadmeReader.ReadAsync(root, _lifetimeCancellation.Token);
            if (selection.Tab == VersionTab && loadFiles)
            {
                files = selection.Before is null || selection.After is null ? []
                    : await Git.ReadVersionDifferencesAsync(root, selection.Before.Commit.Hash, selection.After.Commit.Hash);
                file = files.FirstOrDefault(item => item.Path == selection.VersionFile) ?? files.FirstOrDefault();
            }
            FileComparison? comparison = selection.Tab switch
            {
                ReadmeTab => null,
                ChangesTab => change is null ? null : await Git.CompareWorkingAsync(root, change, hasHead),
                VersionTab => selection.Before is null || selection.After is null ? null : file is null
                    ? new("", "", "", selection.Before.Label, selection.After.Label,
                        (files?.Count ?? VersionFiles.Count) == 0 ? "两个版本内容一致。" : "选择文件查看差异。")
                    : await Git.CompareVersionsAsync(root, selection.Before, selection.After, file),
                _ => null
            };
            if (!Current()) return;
            var rows = comparison is null ? [] : await Task.Run(() => DiffBuilder.Build(comparison), _lifetimeCancellation.Token);
            if (!Current()) return;
            // 仅同步更新绑定时抑制选择回调，不在任何异步读取期间持有全局锁。
            IsBusy = true;
            try
            {
                if (selection.Tab == ReadmeTab) Readme = readme;
                if (files is not null)
                {
                    Replace(VersionFiles, files);
                    SelectedVersionFile = file;
                    Notify(nameof(VersionComparisonState));
                }
                if (_comparison != comparison)
                {
                    _comparison = comparison;
                    Notify(nameof(Comparison));
                    DiffRows = rows;
                }
                Status = "就绪";
                CacheCurrentWorkspace();
            }
            finally { IsBusy = false; }
        }
        catch (Exception ex)
        {
            if (!Current()) return;
            Status = AppLog.Clean(ex.Message);
            AppLog.Write("后台读取页面内容失败", ex);
        }
        finally
        {
            if (generation == _historyNavigationGeneration)
            {
                SetHistoryLoading(false);
                if (resumeWorkspace && !_disposed && Repository?.Path == root && !IsBusy)
                    StartWorkspaceRefresh(fetchAfter: false);
            }
        }
    }
}
