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
    }

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
