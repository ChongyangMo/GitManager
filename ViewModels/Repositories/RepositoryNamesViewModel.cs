using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Repositories;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private async Task RefreshRepositoryNameAsync(RepositoryBookmark bookmark)
    {
        try
        {
            var (address, group) = await RepositoryRemoteGroup.ReadAsync(Git, bookmark.Path, Settings.Value.ServiceCloud.BaseUrl);
            bookmark.UpdateDisplayName(RepositoryDisplayName.FromRemote(address));
            bookmark.UpdateRemoteGroup(group);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        { AppLog.Write("读取仓库显示名称失败：" + bookmark.Path, ex); }
    }

    private Task RefreshRepositoryNamesAsync()
    {
        bool changed = false;
        foreach (var bookmark in Repositories) changed |= bookmark.RefreshPath();
        if (changed)
        {
            if (Settings.Value.LastRepository is { } last) Settings.Value.LastRepository = RepositoryPaths.Actual(last);
            SaveBookmarks();
        }
        return Task.WhenAll(Repositories.ToArray().Select(RefreshRepositoryNameAsync));
    }
}
