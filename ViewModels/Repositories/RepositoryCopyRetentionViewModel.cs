using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Repositories.Copies;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    public void RetainRepositoryCopies(IReadOnlyList<RepositoryBookmark> bookmarks)
    {
        if (bookmarks.Count < 2 || bookmarks.Any(bookmark => !Repositories.Contains(bookmark))
            || bookmarks.Select(bookmark => bookmark.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
            throw new InvalidOperationException("仓库列表已变化，请重新打开同名仓库窗口。");
        SaveRepositoryCopyRetention(RepositoryCopyRetention.Remember(Settings.Value.RetainedRepositoryPairs, bookmarks.Select(bookmark => bookmark.Path)));
        Status = "已保留这些仓库，不再提醒";
        AppLog.Write("已保留同名仓库并关闭这些目录之间的提醒：" + string.Join("；", bookmarks.Select(bookmark => bookmark.Path)));
    }

    public bool HasRetainedRepositoryCopies(RepositoryBookmark bookmark)
        => GetSameNameRepositories(bookmark).Any(other => other != bookmark
            && RepositoryCopyRetention.Contains(Settings.Value.RetainedRepositoryPairs, bookmark.Path, other.Path));

    public void RestoreRepositoryCopyReminders(RepositoryBookmark bookmark)
    {
        var peers = GetSameNameRepositories(bookmark).Where(other => other != bookmark).ToArray();
        var retained = Settings.Value.RetainedRepositoryPairs.Where(pair => !peers.Any(other =>
            RepositoryCopyRetention.Contains([pair], bookmark.Path, other.Path))).ToList();
        SaveRepositoryCopyRetention(retained);
        Status = "已恢复同名仓库提醒";
        AppLog.Write("已恢复同名仓库提醒：" + bookmark.Path);
    }

    private void SaveRepositoryCopyRetention(List<RetainedRepositoryPair> retained)
    {
        var previous = Settings.Value.RetainedRepositoryPairs;
        Settings.Value.RetainedRepositoryPairs = retained;
        try { Settings.Save(); }
        catch { Settings.Value.RetainedRepositoryPairs = previous; throw; }
        UpdateRepositoryNameConflicts();
    }
}
