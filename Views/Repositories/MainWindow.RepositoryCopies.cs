using System.Windows;
using System.Windows.Controls;
using GitManager.Models;
using GitManager.Views.Repositories;

namespace GitManager;

public partial class MainWindow
{
    private void RepositoryContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: RepositoryBookmark bookmark, ContextMenu: { } menu }) return;
        menu.Items.Clear();
        var compare = new MenuItem { Header = "比较同名仓库", CommandParameter = bookmark,
            IsEnabled = ViewModel.GetSameNameRepositories(bookmark).Count > 1 };
        compare.Click += CompareRepositoryCopiesClicked;
        var remove = new MenuItem { Header = "移除记录", CommandParameter = bookmark };
        remove.Click += RemoveRepositoryRecordClicked;
        menu.Items.Add(compare);
        if (ViewModel.HasRetainedRepositoryCopies(bookmark))
        {
            var restore = new MenuItem { Header = "恢复同名提醒", CommandParameter = bookmark };
            restore.Click += RestoreRepositoryCopyRemindersClicked;
            menu.Items.Add(restore);
        }
        menu.Items.Add(remove);
        var delete = new MenuItem { Header = "删除仓库（移到回收站）…", CommandParameter = bookmark };
        delete.SetResourceReference(ForegroundProperty, "DangerBrush");
        delete.Click += DeleteRepositoryClicked;
        menu.Items.Add(delete);
    }

    private async void DeleteRepositoryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: RepositoryBookmark bookmark }) await DeleteRepositoryAsync(bookmark);
    }

    private async void DeleteCurrentRepositoryClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Repository is { } bookmark) await DeleteRepositoryAsync(bookmark);
    }

    // 删除本地仓库：整个目录（源码、文件、.git 历史、忽略文件）移到回收站并移除记录；远程仓库不受影响。
    private async Task DeleteRepositoryAsync(RepositoryBookmark bookmark)
    {
        if (ViewModel.IsBusy) return;
        var risks = new List<string>();
        try
        {
            string root = await ViewModel.Git.OpenAsync(bookmark.Path);
            int changes = (await ViewModel.Git.ReadChangesAsync(root)).Count;
            if (changes > 0) risks.Add($"· 有 {changes} 个未提交的改动，删除后只能从回收站找回");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        { risks.Add("· 无法读取改动状态：" + Services.AppLog.Clean(ex.Message)); }
        if (bookmark.PendingPushCount is > 0) risks.Add($"· 有 {bookmark.PendingPushCount} 个本地提交尚未推送，远程没有这些提交");
        string message = $"删除仓库 {bookmark.Name}？\n\n{bookmark.Path}\n\n整个目录（源码、所有文件、.git 历史、忽略文件）会移到回收站，并从列表移除。"
            + (risks.Count > 0 ? "\n\n注意：\n" + string.Join("\n", risks) : "")
            + "\n\n远程仓库不受影响。";
        if (MessageBox.Show(this, message, "删除仓库", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await ViewModel.RemoveRepositoryCopyAsync(bookmark, true, ConfirmDirectoryRelease);
    }

    // 编译服务、资源管理器窗口等安全占用会自动解除；这里只询问需要结束的其他程序。
    private bool ConfirmDirectoryRelease(IReadOnlyList<Services.Repositories.Copies.DirectoryLockHolder> holders) => Dispatcher.Invoke(() => MessageBox.Show(this,
        "以下程序正在占用该目录：\n\n" + string.Join("\n", holders.Select(holder => "· " + holder))
        + "\n\n结束这些程序后继续删除？会先请求正常关闭，未响应再强制结束，未保存的内容可能丢失。",
        "目录被占用", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);

    private void RestoreRepositoryCopyRemindersClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: RepositoryBookmark bookmark }) return;
        try { ViewModel.RestoreRepositoryCopyReminders(bookmark); }
        catch (Exception ex)
        {
            Services.AppLog.Write("恢复同名仓库提醒失败", ex);
            MessageBox.Show(this, Services.AppLog.Clean(ex.Message), "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CompareRepositoryCopiesClicked(object sender, RoutedEventArgs e)
    {
        var bookmark = sender switch
        {
            MenuItem { CommandParameter: RepositoryBookmark item } => item,
            FrameworkElement { Tag: RepositoryBookmark item } => item,
            _ => null
        };
        if (ViewModel.IsBusy || bookmark is null) return;
        var copies = ViewModel.GetSameNameRepositories(bookmark);
        if (copies.Count > 1) new RepositoryCopiesWindow(ViewModel, copies) { Owner = this }.ShowDialog();
        e.Handled = true;
    }

    private async void RemoveRepositoryRecordClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: RepositoryBookmark bookmark })
            await ViewModel.RemoveRepositoryCopyAsync(bookmark, false);
    }
}
