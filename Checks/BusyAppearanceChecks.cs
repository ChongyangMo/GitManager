using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task VerifyBusyAppearanceAsync(Window window, MainViewModel viewModel, string path)
    {
        int idleWhite = CountWhitePixels(window), busyWhite = 0;
        string branch = viewModel.Branch;
        var rows = viewModel.DiffRows;
        var selectedCommit = viewModel.SelectedCommit;
        int resets = 0;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler changed = (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
        viewModel.History.CollectionChanged += changed;
        viewModel.Branches.CollectionChanged += changed;
        await viewModel.ExecuteAsync("正在检查操作中的界面", async () =>
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveScreenshot(window, path);
            busyWhite = CountWhitePixels(window);
            await viewModel.Git.ReadAsync(viewModel.Root);
        });
        Console.WriteLine($"白色像素：空闲 {idleWhite}，操作中 {busyWhite}");
        if (busyWhite > idleWhite + window.ActualWidth * window.ActualHeight / 100)
            throw new InvalidOperationException("操作期间出现了大面积白色背景。");
        await viewModel.RefreshAsync();
        viewModel.History.CollectionChanged -= changed;
        viewModel.Branches.CollectionChanged -= changed;
        if (viewModel.Branch != branch || window.FindName("BranchBox") is not System.Windows.Controls.ComboBox { SelectedItem: string selectedBranch } || selectedBranch != branch) throw new InvalidOperationException("刷新后分支选择发生变化。");
        if (resets > 0 || !ReferenceEquals(rows, viewModel.DiffRows) || !ReferenceEquals(selectedCommit, viewModel.SelectedCommit)) throw new InvalidOperationException($"刷新时重建了未改变的列表或差异：列表重置 {resets}，差异保持 {ReferenceEquals(rows, viewModel.DiffRows)}，提交保持 {ReferenceEquals(selectedCommit, viewModel.SelectedCommit)}，当前页 {viewModel.ActiveTab}。");
        Console.WriteLine("操作期间的深色背景、分支选择和刷新位置保持检查通过。");
    }

    private static int CountWhitePixels(Window window)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        int width = (int)content.ActualWidth, height = (int)content.ActualHeight;
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        byte[] pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 235 && pixels[i + 1] > 235 && pixels[i + 2] > 235 && pixels[i + 3] > 240) count++;
        return count;
    }
}
