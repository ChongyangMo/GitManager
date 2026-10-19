using System.ComponentModel;
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
        Closing += WindowClosing;
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
        RepositoryList.SelectedItem = ViewModel.Repository;
    }
    private async void RemoveRepositoryClicked(object sender, RoutedEventArgs e)
    {
        if (RepositoryList.SelectedItem is RepositoryBookmark repository) await ViewModel.RemoveRepositoryAsync(repository);
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();
    private async void CommitClicked(object sender, RoutedEventArgs e) => await ViewModel.CommitAsync();
    private void SelectAllClicked(object sender, RoutedEventArgs e) { foreach (var file in ViewModel.Changes) file.Selected = true; }
    private void SelectNoneClicked(object sender, RoutedEventArgs e) { foreach (var file in ViewModel.Changes) file.Selected = false; }
    private async void ChangeSelected(object sender, SelectionChangedEventArgs e) { if (!ViewModel.IsBusy) await ViewModel.ShowChangeAsync(); }
    private async void CommitSelected(object sender, SelectionChangedEventArgs e) { if (!ViewModel.IsBusy) await ViewModel.ShowCommitAsync(); }
    private async void HistoryFileSelected(object sender, SelectionChangedEventArgs e) { if (!ViewModel.IsBusy) await ViewModel.ShowChangeAsync(); }
    private async void TabChanged(object sender, SelectionChangedEventArgs e) { if (e.Source == WorkspaceTabs && !ViewModel.IsBusy) await ViewModel.ShowChangeAsync(); }
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
        var dialog = new FormWindow("远程仓库", new FormField("origin 地址", ViewModel.Remote)) { Owner = this };
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
        if (ViewModel.HasRepository) { fields.Add(new("本仓库提交姓名", ViewModel.UserName)); fields.Add(new("本仓库提交邮箱", ViewModel.UserEmail)); }
        var dialog = new FormWindow("设置", fields.ToArray()) { Owner = this };
        dialog.ShowTheme(ViewModel.Settings.Value.Theme);
        if (dialog.ShowDialog() == true) await ViewModel.SaveSettingsAsync(fields[0].Value.Trim(), dialog.Theme, fields.Count > 1 ? fields[1].Value : "", fields.Count > 2 ? fields[2].Value : "");
    }
    private void LogsClicked(object sender, RoutedEventArgs e)
    {
        string directory = Path.Combine(SettingsStore.DataDirectory, "logs");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
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
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!ViewModel.IsBusy) return;
        e.Cancel = true;
        MessageBox.Show(this, "正在执行 Git 操作，请等待完成后再退出。", "操作进行中", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
