using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GitManager.Services;
using GitManager.ViewModels;
using GitManager.Views.Layout;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunLayoutChecks(string repository, string screenshot)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Dark", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        var store = new SettingsStore(Path.Combine(CheckDirectories.Create("Checks"), "settings.json"));
        store.Value.RepositoryDiscovery.Enabled = false;
        var model = new MainViewModel(store);
        var window = new MainWindow(model) { ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
        Exception? failure = null;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await model.InitializeAsync(repository);
                model.ActiveTab = MainViewModel.HistoryTab;
                await WaitForIdleAsync(model, "加载本地提交记录");
                await model.ShowCommitAsync();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var panes = FindPanes(window).ToArray();
                foreach (string key in new[] { "Repositories", "Workspace", "Preview", "LocalHistory", "LocalFiles" })
                {
                    var pane = panes.Single(p => p.SettingsKey == key);
                    var parent = (Grid)pane.Parent;
                    bool horizontal = pane.Orientation == Orientation.Horizontal;
                    double before = horizontal ? pane.ActualWidth : pane.ActualHeight;
                    var sibling = parent.Children.OfType<CollapsiblePane>().SingleOrDefault(p => p != pane && p.Orientation == pane.Orientation);
                    double siblingBefore = sibling is null ? 0 : horizontal ? sibling.ActualWidth : sibling.ActualHeight;
                    var toggle = (Button)pane.Template.FindName("PART_Toggle", pane);
                    Check(toggle is not null && before > 28, key + " 展开区与按钮可见");
                    toggle!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    Check(!pane.IsExpanded && Math.Abs((horizontal ? pane.ActualWidth : pane.ActualHeight) - 28) < 1, key + " 折叠为可展开入口");
                    if (sibling is not null)
                        Check(Math.Abs((horizontal ? sibling.ActualWidth : sibling.ActualHeight) - (siblingBefore + before - 28)) < 1,
                            key + " 折叠后相邻区域自动填满释放空间");
                    Check(!new SettingsStore(store.FilePath).Value.WorkspaceLayout[key].IsExpanded, key + " 状态已写入配置文件");
                    toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    Check(pane.IsExpanded && Math.Abs((horizontal ? pane.ActualWidth : pane.ActualHeight) - before) < 1, key + " 展开恢复原尺寸");
                }
                model.ActiveTab = MainViewModel.RemoteTab;
                await WaitForIdleAsync(model, "加载远程提交记录");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var remote = FindPanes(window).Where(p => p.SettingsKey is "RemoteHistory" or "RemoteFiles").ToArray();
                Check(remote.Length == 2, "远程记录与文件区域均支持折叠");
                foreach (var pane in remote) { pane.IsExpanded = false; window.UpdateLayout(); Check(Math.Abs(pane.ActualHeight - 28) < 1, pane.SettingsKey + " 独立折叠"); }
                foreach (var pane in remote) pane.IsExpanded = true;
                model.ActiveTab = MainViewModel.HistoryTab;
                await WaitForIdleAsync(model, "返回本地提交记录");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, screenshot);
                var preview = FindPanes(window).Single(p => p.SettingsKey == "Preview");
                preview.IsExpanded = false;
                window.UpdateLayout();
                SaveScreenshot(window, Path.ChangeExtension(screenshot, "fill.png"));
                var filledStore = new SettingsStore(store.FilePath);
                var filledModel = new MainViewModel(filledStore);
                var filledWindow = new MainWindow(filledModel) { ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
                filledWindow.Show();
                filledWindow.UpdateLayout();
                var filledPanes = FindPanes(filledWindow).ToArray();
                var filledWorkspace = filledPanes.Single(p => p.SettingsKey == "Workspace");
                var filledPreview = filledPanes.Single(p => p.SettingsKey == "Preview");
                var filledGrid = (Grid)filledWorkspace.Parent;
                Check(!filledPreview.IsExpanded && Math.Abs(filledWorkspace.ActualWidth + filledPreview.ActualWidth + 5 - filledGrid.ActualWidth) < 1,
                    "新窗口恢复折叠状态后自动填满空间");
                filledPreview.IsExpanded = true;
                filledWindow.UpdateLayout();
                Check(Math.Abs(filledWorkspace.ActualWidth - 360) < 1, "重启后重新展开恢复记录区域原宽度");
                filledWindow.Close();
                preview.IsExpanded = true;
                // 关闭窗口后重新创建，从磁盘读取全部折叠状态，验证重启恢复而非仅内存保留。
                foreach (var pane in FindPanes(window)) pane.IsExpanded = false;
                SaveScreenshot(window, Path.ChangeExtension(screenshot, "collapsed.png"));
                var restoredModel = new MainViewModel(new SettingsStore(store.FilePath));
                var restored = new MainWindow(restoredModel) { ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
                restored.Show();
                restored.UpdateLayout();
                foreach (var pane in FindPanes(restored).Where(p => p.SettingsKey is "Repositories" or "Workspace" or "Preview"))
                {
                    Check(!pane.IsExpanded && Math.Abs(pane.ActualWidth - 28) < 1, pane.SettingsKey + " 新窗口恢复折叠状态");
                    pane.IsExpanded = true;
                }
                restored.Close();
                Check(model.HistoryFiles.Count > 0 && model.Comparison is not null, "折叠切换保留真实提交文件和差异");
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        };
        app.Run(window);
        if (failure is not null) throw new InvalidOperationException("区域折叠检查失败。", failure);
    }

    private static IEnumerable<CollapsiblePane> FindPanes(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is CollapsiblePane pane) yield return pane;
            foreach (var descendant in FindPanes(child)) yield return descendant;
        }
    }
}
