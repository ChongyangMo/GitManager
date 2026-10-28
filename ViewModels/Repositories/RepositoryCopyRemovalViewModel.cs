using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Repositories.Copies;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    public async Task<bool> RemoveRepositoryCopyAsync(RepositoryBookmark bookmark, bool deleteLocal,
        Func<IReadOnlyList<DirectoryLockHolder>, bool>? confirmRelease = null)
    {
        if (string.Equals(_autoUploadWriteRoot, bookmark.Path, StringComparison.OrdinalIgnoreCase))
        {
            Status = "此仓库正在自动上传，请完成后再移除";
            return false;
        }
        bool removed = false;
        string? result = null;
        await ExecuteAsync(deleteLocal ? "正在删除本地仓库" : "正在移除仓库记录", async () =>
        {
            if (!Repositories.Contains(bookmark)) return;
            // 等预读退出，避免删除时仍有本程序打开的 Git 进程或索引文件。
            if (_workspaceWarmupTask is { } warmup) await warmup;
            var previousRepositories = Settings.Value.Repositories;
            var previousIgnored = Settings.Value.IgnoredRepositories;
            string? previousLast = Settings.Value.LastRepository;
            var remaining = Repositories.Where(item => item != bookmark).ToArray();
            Settings.Value.Repositories = remaining.Select(item => item.Path).ToList();
            Settings.Value.IgnoredRepositories = previousIgnored.Append(bookmark.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (Repository == bookmark) Settings.Value.LastRepository = remaining.FirstOrDefault()?.Path;
            try { Settings.Save(); }
            catch
            {
                Settings.Value.Repositories = previousRepositories;
                Settings.Value.IgnoredRepositories = previousIgnored;
                Settings.Value.LastRepository = previousLast;
                throw;
            }
            try
            {
                if (deleteLocal) result = await new RepositoryLocalDeletion(Git).RecycleAsync(bookmark, remaining.Select(item => item.Path).ToArray(), confirmRelease);
            }
            catch
            {
                Settings.Value.Repositories = previousRepositories;
                Settings.Value.IgnoredRepositories = previousIgnored;
                Settings.Value.LastRepository = previousLast;
                Settings.Save();
                throw;
            }
            _workspaceCache.Remove(bookmark.Path);
            _workspaceReads.Remove(bookmark.Path);
            if (Repository == bookmark)
            {
                CancelCommitSummary();
                Repository = null;
                ClearWorkspace();
                _fetchAfterOperation = _refreshAfterOperation = false;
            }
            Repositories.Remove(bookmark);
            removed = true;
            AppLog.Write((deleteLocal ? "已删除本地仓库并移除记录：" : "已移除仓库记录：") + bookmark.Path);
            if (!HasRepository && remaining.FirstOrDefault() is { } next) await OpenCoreAsync(next.Path);
            if (result is not null) Status = result;
        });
        return removed;
    }
}
