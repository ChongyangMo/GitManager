using System.Windows;
using GitManager.Services;
using GitManager.ViewModels;

namespace GitManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write("界面操作失败", args.Exception);
            MessageBox.Show(args.Exception.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try
        {
            var settings = new SettingsStore();
            ThemeService.Apply(settings.Value.Theme);
            var viewModel = new MainViewModel(settings);
            var window = new MainWindow(viewModel);
            MainWindow = window;
            string? repository = e.Args.Length == 2 && e.Args[0] == "--repository" ? e.Args[1] : null;
            window.Loaded += async (_, _) => await viewModel.InitializeAsync(repository);
            window.Show();
            AppLog.Write("Git 管理器已启动");
        }
        catch (Exception ex)
        {
            AppLog.Write("程序启动失败", ex);
            MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
