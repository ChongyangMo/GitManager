using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitManager.Models;
using GitManager.Services;
using GitManager.ViewModels;
using GitManager.Views;
using Microsoft.Win32;

namespace GitManager;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        FitToWorkArea();
        ThemeService.Attach(this);
        Closed += (_, _) => viewModel.Dispose();
    }
    // 小屏幕上默认尺寸可能超出可用区域，按工作区比例收缩。
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Max(Math.Min(Width, area.Width * 0.85), Math.Min(MinWidth, area.Width));
        Height = Math.Max(Math.Min(Height, area.Height * 0.88), Math.Min(MinHeight, area.Height));
    }
    private string? ChooseFolder(string title)
    {
        var picker = new OpenFolderDialog { Title = title };
        return picker.ShowDialog(this) == true ? picker.FolderName : null;
    }
    private void AddMenuClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button) { menu.PlacementTarget = button; menu.IsOpen = true; }
    }
    private async void AddRepositoryClicked(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder("添加本地仓库") is { } path) await ViewModel.OpenRepositoryAsync(path);
    }
    private async void CreateRepositoryClicked(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder("选择要建立仓库的文件夹") is { } path) await ViewModel.CreateRepositoryAsync(path);
    }
    private async void CloneRepositoryClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new FormWindow("克隆仓库", new("仓库地址"), new("本地空目录", picker: "folder")) { Owner = this };
        if (dialog.ShowDialog() == true) await ViewModel.CloneRepositoryAsync(dialog.Fields[0].Value.Trim(), dialog.Fields[1].Value.Trim());
    }
    private async void RepositoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.IsBusy || RepositoryList.SelectedItem is not RepositoryBookmark repository || repository == ViewModel.Repository) return;
        await ViewModel.OpenRepositoryAsync(repository.Path);
    }
    private async void RemoveRepositoryClicked(object sender, RoutedEventArgs e)
    {
        if (RepositoryList.SelectedItem is RepositoryBookmark repository) await ViewModel.RemoveRepositoryAsync(repository);
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();
    private async void CommitClicked(object sender, RoutedEventArgs e) => await ViewModel.CommitAsync();
    private async void GenerateSummaryClicked(object sender, RoutedEventArgs e) => await ViewModel.GenerateCommitMessageAsync();
    private void SelectAllClicked(object sender, RoutedEventArgs e) { foreach (var file in ViewModel.Changes) file.Selected = true; }
    private void SelectNoneClicked(object sender, RoutedEventArgs e) { foreach (var file in ViewModel.Changes) file.Selected = false; }
    private async void ChangeSelected(object sender, SelectionChangedEventArgs e) { if (!ViewModel.IsBusy) await ViewModel.ShowChangeAsync(); }
    private async void CommitSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.IsBusy || ViewModel.IsUpdatingHistorySelection || sender is not ListBox list) return;
        ViewModel.SelectedCommit = list.SelectedItem as CommitInfo;
        await ViewModel.ShowCommitAsync();
    }
    private async void HistoryFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.IsBusy || ViewModel.IsUpdatingHistorySelection || sender is not ListBox list) return;
        ViewModel.SelectedHistoryFile = list.SelectedItem as HistoryFile;
        await ViewModel.ShowChangeAsync();
    }
    private async void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != WorkspaceTabs || ViewModel.IsBusy) return;
        ViewModel.ActiveTab = WorkspaceTabs.SelectedIndex;
        await ViewModel.ShowActiveTabAsync();
    }
    private async void BranchChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ViewModel.IsBusy || !ViewModel.HasRepository || BranchBox.SelectedItem is not string branch) return;
        await ViewModel.SwitchBranchAsync(branch);
    }
    private async void NewBranchClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new FormWindow("新建分支", new FormField("分支名称")) { Owner = this };
        if (dialog.ShowDialog() == true) await ViewModel.SwitchBranchAsync(dialog.Fields[0].Value.Trim(), create: true);
    }
    private async void FetchClicked(object sender, RoutedEventArgs e) => await ViewModel.SynchronizeAsync("fetch");
    private async void PullClicked(object sender, RoutedEventArgs e) => await ViewModel.SynchronizeAsync("pull");
    private async void PushClicked(object sender, RoutedEventArgs e) => await ViewModel.SynchronizeAsync("push");
    private async void RemoteClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new FormWindow("远程仓库", new FormField((ViewModel.SelectedRemoteBranch?.RemoteName ?? "origin") + " 地址", ViewModel.ActiveRemoteUrl)) { Owner = this };
        if (dialog.ShowDialog() == true) await ViewModel.SaveRemoteAsync(dialog.Fields[0].Value);
    }
    private async void RestoreVersionClicked(object sender, RoutedEventArgs e) => await RestoreAsync(fileOnly: false);
    private async void RestoreFileClicked(object sender, RoutedEventArgs e) => await RestoreAsync(fileOnly: true);
    private async Task RestoreAsync(bool fileOnly)
    {
        if (ViewModel.SelectedCommit is not { } commit || fileOnly && ViewModel.SelectedHistoryFile is null) return;
        string target = fileOnly ? "此文件" : "整个工作区";
        if (MessageBox.Show(this, $"将{target}恢复到 {commit.ShortHash}。\n当前相关修改会先保存到“恢复点”，已有提交历史继续保留。", "恢复代码", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await ViewModel.RestoreAsync(fileOnly);
    }
    private async void RecoveryClicked(object sender, RoutedEventArgs e)
    {
        RecoveryPoint? selected = null;
        await ViewModel.ExecuteAsync("正在读取恢复点", async () =>
        {
            var dialog = new RecoveryWindow(await ViewModel.Git.ReadRecoveryPointsAsync(ViewModel.Root)) { Owner = this };
            if (dialog.ShowDialog() == true) selected = dialog.Selected;
        });
        if (selected is not null) await ViewModel.ApplyRecoveryPointAsync(selected);
    }
    private async void SettingsClicked(object sender, RoutedEventArgs e)
    {
        List<FormField> fields = [new("Git 程序", ViewModel.Settings.Value.GitExecutablePath, "file")];
        if (ViewModel.HasRepository) { fields.Add(new("本仓库提交姓名", ViewModel.UserName)); fields.Add(new("本仓库提交邮箱", ViewModel.UserEmail, required: false)); }
        var dialog = new FormWindow("设置", fields.ToArray()) { Owner = this };
        dialog.ShowTheme(ViewModel.Settings.Value.Theme);
        dialog.ShowAction("打开日志", OpenLogs);
        dialog.ShowAction("大模型总结", () => GitManager.Views.CommitMessages.QwenSettingsWindow.Open(dialog, ViewModel));
        if (dialog.ShowDialog() == true) await ViewModel.SaveSettingsAsync(fields[0].Value.Trim(), dialog.Theme, fields.Count > 1 ? fields[1].Value : "", fields.Count > 2 ? fields[2].Value : "");
    }
    private static void OpenLogs()
    {
        string directory = Path.Combine(SettingsStore.DataDirectory, "logs");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
    }
    private void OpenRepositoryFolderClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Repository is { } repository && Directory.Exists(repository.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", repository.Path) { UseShellExecute = true });
    }
    private void OpenReadmeClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Readme is { } readme && File.Exists(readme.Path))
            Process.Start(new ProcessStartInfo(readme.Path) { UseShellExecute = true });
    }
    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        if (e.Key == Key.F5) { e.Handled = true; await ViewModel.RefreshAsync(); }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && ViewModel.HasRepository) { e.Handled = true; await ViewModel.CommitAsync(); }
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; AddRepositoryClicked(sender, e); }
    }
    private async void RepositoryDropped(object sender, DragEventArgs e)
    {
        if (!ViewModel.IsBusy && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            foreach (string path in paths.Where(Directory.Exists)) await ViewModel.OpenRepositoryAsync(path);
    }
}
