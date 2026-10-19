using System.Windows;
using System.Windows.Controls;
using GitManager.ViewModels;
using GitManager.Models;

namespace GitManager.Views.Versions;

public partial class RemoteHistoryView : UserControl
{
    public RemoteHistoryView() => InitializeComponent();
    private async void CommitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel { IsBusy: false, HasRepository: true } vm || sender is not ListBox list) return;
        vm.SelectedRemoteCommit = list.SelectedItem as CommitInfo;
        await vm.ShowRemoteCommitAsync();
    }
    private async void FileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel { IsBusy: false, HasRepository: true } vm || sender is not ListBox list) return;
        vm.SelectedRemoteFile = list.SelectedItem as HistoryFile;
        await vm.ShowChangeAsync();
    }
    private async void CompareClicked(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) await vm.CompareHeadsAsync(selectedRemoteCommit: true); }
    private async void RestoreClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { SelectedRemoteCommit: { } commit } vm) return;
        if (MessageBox.Show(Window.GetWindow(this), $"将工作区恢复到远程提交 {commit.ShortHash}。\n当前修改会保存到恢复点，已有历史继续保留。", "恢复远程版本", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await vm.RestoreRemoteVersionAsync();
    }
}
