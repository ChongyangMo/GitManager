using System.Windows;
using System.Windows.Controls;
using GitManager.ViewModels;
using GitManager.Models;

namespace GitManager.Views.Versions;

public partial class VersionStatusBar : UserControl
{
    public VersionStatusBar() => InitializeComponent();
    private async void RemoteBranchChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel { IsBusy: false, HasRepository: true } vm && sender is ComboBox { SelectedItem: RemoteBranch branch })
        {
            vm.SelectedRemoteBranch = branch;
            await vm.SelectRemoteBranchAsync();
        }
    }
    private async void CompareClicked(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) await vm.CompareHeadsAsync(); }
    private async void SyncClicked(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) await vm.RunVersionSyncAsync("sync"); }
    private async void MergeClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && MessageBox.Show(Window.GetWindow(this), "将远程版本合并到当前本地分支。若出现冲突，可在“改动”中处理或取消合并。", "合并远程", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await vm.RunVersionSyncAsync("merge");
    }
}
