using System.IO;
using System.Windows;
using GitManager.Services;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunTrayChecks()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Light", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        var settings = new SettingsStore(Path.Combine(CheckDirectories.Create("Checks"), "settings.json"));
        var viewModel = new MainViewModel(settings);
        var window = new MainWindow(viewModel)
        {
            ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000
        };
        app.MainWindow = window;
        using var tray = new TrayService(window);
        Exception? failure = null;
        bool closed = false, exited = false, started = false;
        window.Closed += (_, _) => closed = true;
        app.Exit += (_, _) => { tray.Dispose(); exited = true; };
        window.Loaded += async (_, _) =>
        {
            if (started) return;
            started = true;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                window.Close();
                Check(!window.IsVisible && !closed, "关闭窗口仅隐藏，窗口仍可恢复");
                tray.Restore();
                Check(window.IsVisible, "托盘可恢复隐藏的窗口");
                window.WindowState = WindowState.Minimized;
                tray.Restore();
                Check(window.WindowState == WindowState.Normal, "托盘可恢复最小化窗口");
                Task operation = viewModel.ExecuteAsync("正在检查托盘生命周期", () => completion.Task);
                window.Close();
                Check(!window.IsVisible && viewModel.IsBusy && !closed, "操作过程中关闭仅隐藏，操作继续执行");
                tray.Restore();
                completion.SetResult();
                await operation;
                Check(window.IsVisible && !viewModel.IsBusy, "恢复窗口后操作正常结束");
                tray.Exit();
            }
            catch (Exception ex) { failure = ex; tray.PrepareForShutdown(); app.Shutdown(1); }
            finally { completion.TrySetResult(); }
        };
        app.Run(window);
        if (failure is not null) throw new InvalidOperationException("托盘检查失败。", failure);
        Check(closed && exited, "显式退出关闭窗口并释放托盘资源");
    }
}
