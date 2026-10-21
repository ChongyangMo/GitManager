using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using GitManager.Models;
using GitManager.Models.RepositoryCopies;
using GitManager.Services;
using GitManager.Services.Repositories.Copies;
using GitManager.ViewModels;

namespace GitManager.Views.Repositories;

public partial class RepositoryCopiesWindow : Window
{
    private readonly MainViewModel _owner;
    private readonly List<RepositoryBookmark> _bookmarks;
    private bool _loading;
    public RepositoryCopyComparison? Comparison { get; private set; }

    public RepositoryCopiesWindow(MainViewModel owner, IReadOnlyList<RepositoryBookmark> bookmarks)
    {
        _owner = owner;
        _bookmarks = bookmarks.ToList();
        InitializeComponent();
        ThemeService.Attach(this);
        Title = bookmarks.FirstOrDefault()?.Name + " · 同名仓库";
        Loaded += async (_, _) => await ReloadAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _windowCancellation.Cancel();
            _analysisCancellation?.Cancel();
        };
    }

    private async Task ReloadAsync()
    {
        _loading = true;
        ClearAnalysis();
        AnalyzeButton.IsEnabled = false;
        CopiesList.IsEnabled = false;
        StateText.Text = "正在比较仓库";
        try
        {
            Comparison = await new RepositoryCopyReader(_owner.Git).ReadAsync(_bookmarks, _windowCancellation.Token);
            if (_closed) return;
            CopiesList.ItemsSource = Comparison.Copies;
            DifferencesList.ItemsSource = Comparison.Differences;
            LineEndingList.ItemsSource = Comparison.LineEndingDifferences;
            LineEndingList.Visibility = Visibility.Collapsed;
            LineEndingToggle.Visibility = Comparison.LineEndingDifferences.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateLineEndingToggle();
            FileState.Text = Comparison.FileStatus;
            StateText.Text = "";
        }
        catch (OperationCanceledException) when (_closed) { return; }
        catch (Exception ex)
        {
            Comparison = null;
            StateText.Text = "比较失败：" + AppLog.Clean(ex.Message);
            AppLog.Write("比较同名仓库失败", ex);
        }
        finally { _loading = false; CopiesList.IsEnabled = true; AnalyzeButton.IsEnabled = true; }
        if (!_closed && Comparison is not null) await AnalyzeAsync();
    }

    private void LineEndingToggleClicked(object sender, RoutedEventArgs e)
    {
        LineEndingList.Visibility = LineEndingList.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        UpdateLineEndingToggle();
    }

    private void UpdateLineEndingToggle() => LineEndingToggle.Content = (LineEndingList.Visibility == Visibility.Visible ? "隐藏" : "查看")
        + $" {Comparison?.LineEndingDifferences.Count ?? 0} 个仅换行符不同的文件";

    private void OpenFolderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RepositoryCopySnapshot copy }) return;
        try { Process.Start(new ProcessStartInfo(copy.Path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, AppLog.Clean(ex.Message), "打开目录失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void RemoveClicked(object sender, RoutedEventArgs e) => await RemoveAsync(sender, false);
    private async void DeleteClicked(object sender, RoutedEventArgs e) => await RemoveAsync(sender, true);

    private async Task RemoveAsync(object sender, bool deleteLocal)
    {
        if (_loading || _owner.IsBusy || sender is not FrameworkElement { DataContext: RepositoryCopySnapshot copy }) return;
        string message = deleteLocal
            ? $"将整个目录（含 .git、未提交文件和忽略文件）移到回收站，并移除记录？链接工作树会同时清理工作树登记和已合并的分支。\n\n{copy.Path}\n\n{copy.Changes}"
            : $"仅移除这条仓库记录？\n\n{copy.Path}\n\n本地文件保留。";
        if (MessageBox.Show(this, message, deleteLocal ? "删除本地仓库" : "移除仓库记录",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ClearAnalysis();
        AnalyzeButton.IsEnabled = false;
        _loading = true;
        CopiesList.IsEnabled = false;
        try
        {
            if (await _owner.RemoveRepositoryCopyAsync(copy.Bookmark, deleteLocal, ConfirmRelease)) _bookmarks.Remove(copy.Bookmark);
            if (_bookmarks.Count == 0) { Close(); return; }
        }
        finally { _loading = false; CopiesList.IsEnabled = true; AnalyzeButton.IsEnabled = true; }
        await ReloadAsync();
    }

    // 编译服务、资源管理器窗口等安全占用已自动解除；这里只询问需要结束的其他程序。
    private bool ConfirmRelease(IReadOnlyList<DirectoryLockHolder> holders) => Dispatcher.Invoke(() => MessageBox.Show(this,
        "以下程序正在占用该目录：\n\n" + string.Join("\n", holders.Select(holder => "· " + holder))
        + "\n\n结束这些程序后继续删除？会先请求正常关闭，未响应再强制结束，未保存的内容可能丢失。",
        "目录被占用", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
}
