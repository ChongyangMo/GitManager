using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitManager.Services;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunLargeRepositorySwitchChecks(string largeRoot, string smallRoot)
    {
        largeRoot = Path.GetFullPath(largeRoot);
        smallRoot = Path.GetFullPath(smallRoot);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Dark", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            var settings = new SettingsStore(Path.Combine(CheckDirectories.Create("LargeRepositorySwitch"), "settings.json"));
            settings.Value.RepositoryDiscovery.Enabled = false;
            settings.Value.Repositories = [smallRoot, largeRoot];
            settings.Value.AutoUpload.DisabledRepositories = [smallRoot, largeRoot];
            using var model = new MainViewModel(settings);
            var window = new MainWindow(model) { ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            try
            {
                await model.InitializeAsync(smallRoot);
                await model.PreloadRepositoriesAsync();
                await WaitWorkspaceReadsAsync(model);
                var list = (ListBox)window.FindName("RepositoryList");
                long slowest = 0;
                for (int pass = 0; pass < 2; pass++)
                {
                    foreach (string root in new[] { largeRoot, smallRoot })
                    {
                        var timer = Stopwatch.StartNew();
                        list.SelectedItem = model.Repositories.First(item => item.Path == root);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        timer.Stop();
                        slowest = Math.Max(slowest, timer.ElapsedMilliseconds);
                        Console.WriteLine($"真实仓库切换：{Path.GetFileName(root)}，耗时 {timer.ElapsedMilliseconds} 毫秒，本地文件 {model.HistoryFiles.Count} 个，远程文件 {model.RemoteFiles.Count} 个");
                        Check(model.Repository?.Path == root && model.CanOperate && model.ActiveTab == MainViewModel.ReadmeTab,
                            "真实仓库切换后立即显示对应说明页并可操作");
                        if (root == largeRoot) Check(model.HistoryFiles.Count > 10000 && model.RemoteFiles.Count > 10000,
                            "使用真实的大提交文件列表验证切换");
                        Check(timer.ElapsedMilliseconds < 1000, "大文件列表的仓库切换和界面呈现不超过一秒");
                    }
                }
                await WaitWorkspaceReadsAsync(model);
                Check(model.Repository?.Path == smallRoot, "过期后台读取不会覆盖最后选择的仓库");
                Console.WriteLine($"大仓库切换检查通过，最慢 {slowest} 毫秒。");
            }
            catch (Exception ex) { failure = ex; }
            finally { model.Dispose(); app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("大仓库切换检查失败。", failure);
    }
}
