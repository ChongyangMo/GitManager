using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private static void RunRepositoryCopiesChecks()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Dark", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            try
            {
                string output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "repository-copies");
                Directory.CreateDirectory(output);
                var actual = new SettingsStore();
                string settingsPath = Path.Combine(output, "settings-check.json");
                File.WriteAllText(settingsPath, JsonSerializer.Serialize(actual.Value));
                using var model = new MainViewModel(new SettingsStore(settingsPath));
                foreach (var bookmark in model.Repositories)
                    bookmark.UpdateDisplayName(await RepositoryDisplayName.ReadAsync(model.Git, bookmark.Path));
                var sameName = model.Repositories.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).First(group => group.Count() > 1).ToArray();
                Check(sameName.All(item => item.HasNameConflict), "真实同名仓库全部标记提示，不合并列表");
                Check(model.Repositories.Count == actual.Value.Repositories.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "独立本地仓库均保留");
                var window = new RepositoryCopiesWindow(model, sameName) { ShowInTaskbar = false, ShowActivated = false,
                    Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
                while (window.Comparison is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
                var comparison = window.Comparison ?? throw new InvalidOperationException("比较窗口未完成加载。");
                Check(comparison.Copies.Count == sameName.Length && comparison.Copies.All(copy => copy.Error.Length == 0), "真实仓库的分支、提交、改动及文件指纹读取成功");
                Check(comparison.Copies.Select(copy => copy.Path).Distinct().Count() == sameName.Length, "对比显示每份仓库的独立目录");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, Path.Combine(output, "copies-dark.png"));
                ThemeService.Apply("Light");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, Path.Combine(output, "copies-light.png"));
                window.Close();
                ThemeService.Apply("Dark");
                var main = new MainWindow(model) { ShowInTaskbar = false, ShowActivated = false,
                    Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
                main.Show();
                await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(main, Path.Combine(output, "sidebar-dark.png"));
                var repositoryList = (ListBox)main.FindName("RepositoryList");
                var container = (ListBoxItem)repositoryList.ItemContainerGenerator.ContainerFromItem(sameName[1]);
                typeof(MainWindow).GetMethod("RepositoryContextMenuOpening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(main, [container, null]);
                container.ContextMenu!.PlacementTarget = container;
                container.ContextMenu.IsOpen = true;
                await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(((MenuItem)container.ContextMenu.Items[0]).CommandParameter == sameName[1], "右键菜单针对点击项，不误用当前选中仓库");
                container.ContextMenu.IsOpen = false;

                var invalid = await new RepositoryCopyReader(Git).ReadAsync([new RepositoryBookmark(Path.Combine(output, "missing")), sameName[0]]);
                Check(invalid.Copies[0].Error.Length > 0 && invalid.FileStatus == "文件比较未完成" && !invalid.Copies[0].CanDeleteLocal,
                    "目录读取失败明确显示未知，不伪报内容相同");
                bool guarded = false;
                try { RepositoryLocalDeletion.ValidateTarget(sameName[0].Path, [Path.Combine(sameName[0].Path, "nested")]); }
                catch (InvalidOperationException) { guarded = true; }
                Check(guarded, "删除保护阻止连带删除其他仓库");

                // 用真实仓库的副本制造确定的改动：修改一个跟踪文件、新增一个文件，不依赖用户列表里恰好有内容不同的仓库。
                string clone = Path.Combine(output, "recycle-check");
                await Runner.RunAsync(null, ["clone", "--local", "--no-hardlinks", "--", sameName[0].Path, clone]);
                string tracked = (await Runner.RunAsync(clone, ["ls-files", "-z"])).Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .First(path => File.Exists(Path.Combine(clone, path)));
                await File.AppendAllTextAsync(Path.Combine(clone, tracked), "\n同名仓库校验改动\n");
                await File.WriteAllTextAsync(Path.Combine(clone, "同名仓库校验新增.txt"), "新增\n");
                var different = await new RepositoryCopyReader(Git).ReadAsync([sameName[0], new RepositoryBookmark(clone)]);
                Check(different.Copies.All(copy => copy.Error.Length == 0)
                    && different.Differences.Any(file => file.Path == "同名仓库校验新增.txt" && file.Difference == "1：缺失；2：存在")
                    && different.Differences.Any(file => file.Path == tracked && file.Difference == "1：内容 1；2：内容 2"), "不同真实仓库能定位缺失文件和内容不同的文件");
                Console.WriteLine($"真实副本对比：{different.FileStatus}");
                var deletion = new RepositoryLocalDeletion(Git);
                await deletion.RecycleAsync(new RepositoryBookmark(clone), [sameName[0].Path]);
                Check(!Directory.Exists(clone) && Directory.Exists(sameName[0].Path), "实际副本进入回收站，原仓库完整保留");

                var isolatedSettings = new SettingsStore(Path.Combine(output, "removal-check.json"));
                isolatedSettings.Value.Repositories = [sameName[0].Path];
                isolatedSettings.Save();
                using var removal = new MainViewModel(isolatedSettings);
                Check(await removal.RemoveRepositoryCopyAsync(removal.Repositories[0], false), "仅移除记录执行成功");
                var saved = new SettingsStore(isolatedSettings.FilePath).Value;
                Check(saved.Repositories.Count == 0 && saved.IgnoredRepositories.Contains(sameName[0].Path)
                    && Directory.Exists(sameName[0].Path), "仅移除不动文件，持久排除防止扫描重新添加");
                main.Close();
                Console.WriteLine($"同名仓库检查通过，共 {_count} 项；真实副本 {comparison.Copies.Count} 份，Git 文件差异 {comparison.Differences.Count} 项。");
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("同名仓库检查失败。", failure);
    }
}
