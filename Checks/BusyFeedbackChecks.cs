using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task VerifyBusyFeedbackAsync(Window window, MainViewModel viewModel)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (viewModel.IsBusy && DateTimeOffset.UtcNow < deadline) await Task.Delay(30);
        var button = (Button)window.FindName("GenerateCommitMessageButton");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Check(viewModel.IsIdle && button.IsEnabled && button.Opacity == 1, "空闲时按钮可用且不变灰");
        bool ran = false, shortEnabled = true, longEnabled = true, shortFeedback = true, longFeedback = false;
        double shortOpacity = 0, longOpacity = 1;
        await viewModel.ExecuteAsync("正在检查延迟忙碌外观", async () =>
        {
            ran = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            (shortEnabled, shortOpacity, shortFeedback) = (button.IsEnabled, button.Opacity, viewModel.IsBusyProgressVisible);
            await Task.Delay(500);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            (longEnabled, longOpacity, longFeedback) = (button.IsEnabled, button.Opacity, viewModel.IsBusyProgressVisible);
        });
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Check(ran, "延迟忙碌外观检查的操作已实际执行");
        Check(!shortEnabled && shortOpacity == 1 && !shortFeedback, "短操作立即禁用输入但不闪灰");
        Check(!longEnabled && longOpacity < 0.5 && longFeedback, "操作超过延迟后显示禁用外观和进度");
        Check(button.IsEnabled && button.Opacity == 1 && !viewModel.IsBusyProgressVisible, "操作结束后立即恢复正常外观");
    }
}
