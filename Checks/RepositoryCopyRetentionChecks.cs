using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Repositories;
using GitManager.Services.Repositories.Copies;
using GitManager.ViewModels;
using GitManager.Views.Repositories;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunCopyRetentionChecks(string[] roots)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Dark", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            try
            {
                var store = new SettingsStore(Path.Combine(CheckDirectories.Create("copy-retention"), "settings.json"));
                store.Value.Repositories = roots.Select(RepositoryDiscovery.Normalize).ToList();
                store.Value.RetainedRepositoryPairs = [];
                store.Value.Qwen.Enabled = false;
                store.Save();
                using var model = new MainViewModel(store);
                foreach (var bookmark in model.Repositories)
                    bookmark.UpdateDisplayName(await RepositoryDisplayName.ReadAsync(model.Git, bookmark.Path));
                var copies = model.Repositories.ToArray();
                Check(copies.Length == 2 && copies.All(copy => copy.HasNameConflict), "真实同名仓库初始显示提醒");
                var window = new RepositoryCopiesWindow(model, copies) { ShowInTaskbar = false, ShowActivated = false,
                    Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
                while (window.Comparison is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
                Check(window.Comparison is { Copies.Count: 2 } comparison && comparison.Copies.All(copy => copy.Error.Length == 0),
                    "真实仓库比较成功，保留按钮不依赖模型分析");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                string output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "copy-retention");
                Directory.CreateDirectory(output);
                SaveScreenshot(window, Path.Combine(output, "retain-dark.png"));
                ThemeService.Apply("Light");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, Path.Combine(output, "retain-light.png"));
                var button = (Button)window.FindName("RetainCopiesButton");
                Check(button.IsEnabled && Equals(button.Content, "保留两份，不再提醒"), "按钮可用且沿用两份仓库文案");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!window.IsVisible && copies.All(copy => !copy.HasNameConflict) && model.Repositories.Count == 2,
                    "实际点击保留后关闭窗口、取消提醒并保留两条记录");
                var reloadedStore = new SettingsStore(store.FilePath);
                Check(reloadedStore.Value.RetainedRepositoryPairs.Count == 1
                    && reloadedStore.Value.IgnoredRepositories.Count == 0 && reloadedStore.Value.Repositories.Count == 2,
                    "配置持久保存仓库组合，不移除记录或加入扫描排除");
                using var reloaded = new MainViewModel(reloadedStore);
                foreach (var bookmark in reloaded.Repositories) bookmark.UpdateDisplayName(copies[0].Name);
                Check(reloaded.Repositories.All(copy => !copy.HasNameConflict), "重新加载配置后不再提醒");
                reloaded.RetainRepositoryCopies(reloaded.Repositories.Reverse().ToArray());
                Check(reloadedStore.Value.RetainedRepositoryPairs.Count == 1, "顺序颠倒不会重复保存组合");
                Check(RepositoryCopyRetention.Contains(reloadedStore.Value.RetainedRepositoryPairs,
                    roots[1].ToUpperInvariant() + Path.DirectorySeparatorChar, roots[0].ToLowerInvariant()), "路径大小写和结尾分隔符不影响记忆");
                var third = new RepositoryBookmark(Directory.GetCurrentDirectory());
                third.UpdateDisplayName(copies[0].Name);
                reloaded.Repositories.Add(third);
                Check(reloaded.Repositories.All(copy => copy.HasNameConflict), "第三个同名目录出现时仍然提醒");
                reloaded.Repositories.Remove(third);
                Check(reloaded.Repositories.All(copy => !copy.HasNameConflict), "第三个目录移除后仍记得原来的保留选择");
                Check(reloaded.HasRetainedRepositoryCopies(reloaded.Repositories[0]), "已保留组合提供恢复提醒入口");
                reloaded.RestoreRepositoryCopyReminders(reloaded.Repositories[0]);
                Check(reloaded.Repositories.All(copy => copy.HasNameConflict)
                    && new SettingsStore(store.FilePath).Value.RetainedRepositoryPairs.Count == 0, "恢复提醒同时更新界面和配置");
                File.Delete(store.FilePath);
                Directory.CreateDirectory(store.FilePath);
                bool saveFailed = false;
                try { reloaded.RetainRepositoryCopies(reloaded.Repositories.ToArray()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { saveFailed = true; }
                Check(saveFailed && reloadedStore.Value.RetainedRepositoryPairs.Count == 0
                    && reloaded.Repositories.All(copy => copy.HasNameConflict), "保存失败时保留原提醒状态");
                Console.WriteLine($"同名仓库保留选择检查通过，共 {_count} 项。");
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw failure;
    }
}
