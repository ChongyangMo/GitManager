using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitManager.ViewModels;

namespace GitManager.Views.Versions;

public partial class ConflictActions : UserControl
{
    public ConflictActions() => InitializeComponent();
    private async void ResolveClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not Button { Tag: string side }) return;
        if (side != "edited" && MessageBox.Show(Window.GetWindow(this), "将用所选一侧的整个文件替换当前冲突内容。", "处理冲突", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await vm.ResolveSelectedConflictAsync(side);
    }
    private async void EditClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { SelectedChange: { } file } vm) return;
        await vm.ExecuteAsync("正在打开冲突文件", () =>
        {
            string path = Path.Combine(vm.Root, file.Path);
            if (!File.Exists(path)) throw new FileNotFoundException("文件不存在，可通过“采用本地”或“采用远程”处理删除冲突。");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")) { UseShellExecute = false };
            start.ArgumentList.Add(path);
            Process.Start(start);
            return Task.CompletedTask;
        });
    }
    private async void AbortClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && MessageBox.Show(Window.GetWindow(this), "取消本次合并并返回合并前，当前冲突处理中的编辑将被放弃。", "取消合并", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK) await vm.CancelMergeAsync();
    }
}
