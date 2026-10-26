using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GitManager.Services;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunInteractionMotionChecks(string[] repositories)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Dark", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), "GitManagerChecks", Guid.NewGuid().ToString("N"), "settings.json"));
            settings.Value.RepositoryDiscovery.Enabled = false;
            settings.Value.Repositories = repositories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            settings.Value.AutoUpload.DisabledRepositories = settings.Value.Repositories.ToList();
            settings.Value.GitHub.CommitEmail = ChecksCommitEmail;
            using var model = new MainViewModel(settings);
            var window = new MainWindow(model) { ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            try
            {
                await model.InitializeAsync(repositories[0]);
                await model.PreloadRepositoriesAsync();
                await WaitWorkspaceReadsAsync(model);
                await Task.Delay(240);
                var list = (ListBox)window.FindName("RepositoryList");
                var tabs = (TabControl)window.FindName("WorkspaceTabs");
                var page = (FrameworkElement)window.FindName("RepositoryPage");
                bool sawPageTransition = false;
                bool pageStayedStill = true;
                VerifySelectionVisuals(list, tabs);
                for (int pass = 0; pass < 2; pass++)
                {
                    foreach (var bookmark in model.Repositories.ToArray())
                    {
                        list.SetCurrentValue(ListBox.SelectedItemProperty, bookmark);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        sawPageTransition |= page.HasAnimatedProperties;
                        pageStayedStill &= page.RenderTransform.Value.IsIdentity && page.Opacity >= 0.959;
                        await Task.Delay(25);
                    }
                    foreach (TabItem tab in tabs.Items)
                    {
                        tabs.SetCurrentValue(TabControl.SelectedItemProperty, tab);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        await Task.Delay(25);
                    }
                    await Task.Delay(240);
                    VerifySelectionVisuals(list, tabs);
                    Check(ReferenceEquals(list.SelectedItem, model.Repository) && tabs.SelectedIndex == model.ActiveTab,
                        "快速切换后高亮、实际仓库及标签选择保持一致");
                    Check(page.Opacity == 1 && page.RenderTransform.Value.IsIdentity && !page.HasAnimatedProperties,
                        "页面过渡结束后完整显示，连续切换不残留透明度或位移");
                }
                Check(!SystemParameters.ClientAreaAnimation || sawPageTransition, "仓库切换实际播放页面过渡，遵循系统动画设置");
                Check(pageStayedStill, "整个切换过程页面位置固定，不移动或大幅闪烁");
                model.ActiveTab = MainViewModel.ReadmeTab;
                await model.ShowActiveTabAsync();
                await WaitWorkspaceReadsAsync(model);
                await Task.Delay(240);
                VerifySelectionVisuals(list, tabs);
                SaveScreenshot(window, Path.Combine("artifacts", "motion-selection.png"));
                await VerifyCheckmarkMotionAsync();
                Console.WriteLine($"动效状态检查通过，共 {_count} 项。");
            }
            catch (Exception ex) { failure = ex; }
            finally { model.Dispose(); app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("动效状态检查失败。", failure);
    }

    private static void VerifySelectionVisuals(ListBox list, TabControl tabs)
    {
        list.UpdateLayout();
        int selectedRows = 0, highlightedRows = 0;
        foreach (var item in list.Items)
        {
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem row) continue;
            row.ApplyTemplate();
            var layer = (FrameworkElement)row.Template.FindName("Selection", row);
            if (row.IsSelected) selectedRows++;
            if (layer.IsVisible && layer.Opacity > 0.01) highlightedRows++;
            Check(Math.Abs(layer.Opacity - (row.IsSelected ? 1 : 0)) < 0.01, "仓库高亮与真实选中状态一致");
        }
        Check(selectedRows == 1 && highlightedRows == 1, "始终只有一个仓库高亮");
        int indicators = 0;
        foreach (TabItem tab in tabs.Items)
        {
            tab.ApplyTemplate();
            var indicator = (FrameworkElement)tab.Template.FindName("Indicator", tab);
            if (indicator.IsVisible && indicator.Opacity > 0.01) indicators++;
            Check(Math.Abs(indicator.Opacity - (tab.IsSelected ? 1 : 0)) < 0.01, "标签下划线与真实选中状态一致");
        }
        Check(indicators == 1, "始终只有当前标签显示下划线");
    }

    private static async Task VerifyCheckmarkMotionAsync()
    {
        var check = new CheckBox { IsChecked = true };
        var radio = new RadioButton { IsChecked = false };
        var panel = new StackPanel(); panel.Children.Add(check); panel.Children.Add(radio);
        var window = new Window { Content = panel, Width = 200, Height = 100, ShowInTaskbar = false, ShowActivated = false,
            Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show(); window.UpdateLayout();
        var mark = (FrameworkElement)check.Template.FindName("Mark", check);
        var dot = (FrameworkElement)radio.Template.FindName("Dot", radio);
        Check(mark.Opacity == 1 && dot.Opacity == 0, "勾选标记的首次显示状态正确");
        check.IsChecked = false; radio.IsChecked = true;
        await Task.Delay(240);
        Check(mark.Opacity == 0 && dot.Opacity == 1, "动画结束后勾选状态不会恢复默认值");
        check.IsChecked = true; await Task.Delay(25); check.IsChecked = false;
        radio.IsChecked = false;
        await Task.Delay(240);
        Check(mark.Opacity == 0 && dot.Opacity == 0, "快速反复勾选后没有残留标记");
        window.Close();
    }
}
