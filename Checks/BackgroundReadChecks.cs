using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitManager.Services;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunBackgroundReadChecks(string[] repositories)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Dark", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            var settings = new SettingsStore(Path.Combine(CheckDirectories.Create("Checks"), "settings.json"));
            settings.Value.Repositories = repositories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            settings.Value.AutoUpload.DisabledRepositories = settings.Value.Repositories.ToList();
            settings.Value.GitHub.CommitEmail = ChecksCommitEmail;
            settings.Value.RepositoryDiscovery.Enabled = true;
            using var model = new MainViewModel(settings);
            model.Repositories.CollectionChanged += (_, _) => settings.Value.AutoUpload.DisabledRepositories = model.Repositories.Select(item => item.Path).ToList();
            var window = new MainWindow(model) { ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            try
            {
                var list = (ListBox)window.FindName("RepositoryList");
                var tabs = (TabControl)window.FindName("WorkspaceTabs");
                var initialize = model.InitializeAsync(repositories[0]);
                window.UpdateLayout();
                Check(model.IsIdle && model.IsWorkspaceLoading && list.IsEnabled && tabs.IsEnabled,
                    "启动读取仓库期间导航和标签页保持可用");
                model.ActiveTab = MainViewModel.ChangesTab;
                model.CommitMessage = "检查前台草稿";
                await initialize;
                Check(model.ActiveTab == MainViewModel.ChangesTab && model.CommitMessage == "检查前台草稿",
                    "首次后台读取不覆盖用户已选择的标签页和输入");
                await model.PreloadRepositoriesAsync();
                await model.DiscoverRepositoriesAsync();

                var scan = model.DiscoverRepositoriesAsync();
                Check(model.IsDiscoveringRepositories && ReferenceEquals(scan, model.DiscoverRepositoriesAsync()),
                    "实际目录扫描独立运行，重复识别合并为同一任务");
                var refresh = model.RefreshAsync();
                Check(ReferenceEquals(refresh, model.RefreshAsync()), "连续刷新合并，避免重复排队");
                await Task.Delay(450);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                Check(model.IsIdle && model.CanOperate && list.IsEnabled && tabs.IsEnabled && !model.IsBusyProgressVisible,
                    "后台扫描和状态读取超过灰显延迟后，前台仍可操作");
                var input = (TextBox)window.FindName("CommitInput");
                Check(input.IsEnabled && input.Opacity == 1, "识别仓库期间提交说明仍可编辑且不变灰");
                model.CommitMessage = "扫描期间继续输入";
                model.ActiveTab = MainViewModel.HistoryTab;
                var history = model.ShowCommitAsync();
                Check(model.IsIdle, "扫描同时读取历史不占用前台锁");
                await history;
                model.ActiveTab = MainViewModel.VersionTab;
                var comparison = model.CompareSelectedVersionsAsync();
                Check(model.IsIdle, "版本差异读取不占用前台锁");
                await comparison;
                var fetch = model.RunVersionSyncAsync("fetch");
                Check(model.IsIdle, "远程状态获取不占用前台锁");
                model.ActiveTab = MainViewModel.ReadmeTab;
                var readme = model.ShowActiveTabAsync();
                Check(model.IsIdle, "说明读取不占用前台锁");
                list.SetCurrentValue(ListBox.SelectedItemProperty, model.Repositories.First(item => item.Path == repositories[1]));
                Check(model.Repository?.Path == repositories[1] && model.CanOperate, "扫描及读取期间可立即切换到另一仓库");
                await Task.WhenAll(scan, refresh, fetch, readme);
                await WaitWorkspaceReadsAsync(model);
                Check(model.Repository?.Path == repositories[1], "旧扫描、刷新和预览完成后不覆盖前台仓库选择");
                await model.OpenRepositoryAsync(repositories[0]);
                Check(model.CommitMessage == "扫描期间继续输入", "后台状态更新保留用户继续输入的草稿");
                await WaitWorkspaceReadsAsync(model);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                SaveScreenshot(window, Path.Combine("artifacts", "background-read.png"));
                Console.WriteLine($"后台读取检查通过，共 {_count} 项。");
            }
            catch (Exception ex) { failure = ex; }
            finally { model.Dispose(); app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("后台读取检查失败。", failure);
    }
}
