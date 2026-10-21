using System.IO;
using System.Windows;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using GitManager.Services;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunPushIndicatorChecks(string screenshot, string[] repositories)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Dark", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), "GitManagerChecks", Guid.NewGuid().ToString("N"), "settings.json"));
            settings.Value.RepositoryDiscovery.Enabled = false;
            settings.Value.Repositories = repositories.ToList();
            using var model = new MainViewModel(settings);
            var window = new MainWindow(model) { ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            try
            {
                await model.InitializeAsync(repositories[0]);
                await model.RefreshRepositoryPushIndicatorsAsync();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                foreach (var bookmark in model.Repositories)
                {
                    var actual = await Git.ReadVersionsAsync(bookmark.Path);
                    int? count = actual.HasRemote ? actual.Ahead : null;
                    Check(bookmark.PendingPushCount == count && bookmark.HasPendingPush == count is > 0,
                        bookmark.Name + " 导航提醒与真实 Git 待推送数量一致");
                    var dot = FindPushDots(window).Single(item => ReferenceEquals(item.DataContext, bookmark));
                    Check(dot.Visibility == (count is > 0 ? Visibility.Visible : Visibility.Collapsed), bookmark.Name + " 导航红点显示正确");
                    Console.WriteLine($"仓库待推送检查：{bookmark.Name}，数量 {count?.ToString() ?? "未知"}，红点 {bookmark.HasPendingPush}");
                }
                Check(model.Repositories.Any(item => item != model.Repository && item.HasPendingPush), "未选中的真实仓库也能显示待推送提醒");
                SaveScreenshot(window, screenshot);
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("仓库待推送提醒检查失败。", failure);
    }

    private static IEnumerable<Ellipse> FindPushDots(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Ellipse { Name: "PendingPushDot" } dot) yield return dot;
            foreach (var descendant in FindPushDots(child)) yield return descendant;
        }
    }
}
