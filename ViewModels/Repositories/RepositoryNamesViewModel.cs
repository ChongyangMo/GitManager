using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Repositories;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private async Task RefreshRepositoryNameAsync(RepositoryBookmark bookmark)
    {
        try { bookmark.UpdateDisplayName(await RepositoryDisplayName.ReadAsync(Git, bookmark.Path)); }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        { AppLog.Write("读取仓库显示名称失败：" + bookmark.Path, ex); }
    }

    private Task RefreshRepositoryNamesAsync() => Task.WhenAll(Repositories.ToArray().Select(RefreshRepositoryNameAsync));
}
