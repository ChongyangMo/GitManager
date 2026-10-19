using System.IO;
using System.Windows;
using System.Windows.Threading;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task VerifyVersionViewsAsync(Window window, MainViewModel viewModel, string path)
    {
        if (viewModel.RemoteHistory.Count > 0)
        {
            viewModel.ActiveTab = 2;
            while (viewModel.IsBusy) await Task.Delay(20);
            await viewModel.ShowRemoteCommitAsync();
            if (viewModel.RemoteFiles.Count == 0 || viewModel.Comparison is null) throw new InvalidOperationException("远程版本界面未显示真实提交文件。");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveScreenshot(window, Path.ChangeExtension(path, "remote.png"));
            await viewModel.CompareHeadsAsync();
            if (viewModel.ComparisonBefore?.Commit.Hash != viewModel.RemoteHistory[0].Hash || viewModel.ComparisonAfter?.Commit.Hash != viewModel.History[0].Hash) throw new InvalidOperationException("本地与远程对比未选择实际分支版本。");
        }
        if (viewModel.History.Count > 1)
        {
            viewModel.ActiveTab = 3;
            while (viewModel.IsBusy) await Task.Delay(20);
            viewModel.ComparisonBefore = viewModel.VersionChoices.First(item => item.Source.StartsWith("本地 ", StringComparison.Ordinal) && item.Commit.Hash == viewModel.History[^1].Hash);
            while (viewModel.IsBusy) await Task.Delay(20);
            viewModel.ComparisonAfter = viewModel.VersionChoices.First(item => item.Source.StartsWith("本地 ", StringComparison.Ordinal) && item.Commit.Hash == viewModel.History[0].Hash);
            while (viewModel.IsBusy) await Task.Delay(20);
            await viewModel.CompareSelectedVersionsAsync();
            if (viewModel.VersionFiles.Count == 0 || viewModel.Comparison is null) throw new InvalidOperationException("任意版本对比未加载实际文件差异。");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveScreenshot(window, Path.ChangeExtension(path, "compare.png"));
        }
        Console.WriteLine($"版本界面检查通过：{viewModel.LocalVersionLabel}；{viewModel.RemoteSource} {viewModel.RemoteVersionHash}；{viewModel.FetchStateText}");
    }
}
