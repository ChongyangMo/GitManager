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
        var compare = new MenuItem { Header = "比较同名仓库", CommandParameter = bookmark, IsEnabled = bookmark.HasNameConflict };
        compare.Click += CompareRepositoryCopiesClicked;
        var remove = new MenuItem { Header = "移除记录", CommandParameter = bookmark };
        remove.Click += RemoveRepositoryRecordClicked;
        menu.Items.Add(compare);
        menu.Items.Add(remove);
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
