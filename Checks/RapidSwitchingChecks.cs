using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitManager.Services;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunRapidSwitchingChecks(string[] repositories)
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
                Check(repositories.Length > 8, "使用超过原缓存上限的真实仓库验证");
                var list = (ListBox)window.FindName("RepositoryList");
                list.SelectedItem = model.Repositories.First(item => item.Path == repositories[0]);
                window.UpdateLayout();
                Check(model.IsWorkspaceLoading && ((FrameworkElement)window.FindName("WorkspaceTabs")).IsVisible,
                    "首次读取立即显示工作区，不隐藏为黑屏");
                SaveScreenshot(window, Path.Combine("artifacts", "rapid-switching-cold.png"));

                // 不等待读取，直接连续点击，覆盖切走后才完成读取的情况。
                foreach (string path in repositories.Skip(1))
                    list.SelectedItem = model.Repositories.First(item => item.Path == path);
                await WaitWorkspaceReadsAsync(model);
                Check(model.Repository?.Path == repositories[^1] && !model.IsWorkspaceLoading, "快速点击后完成最后一个仓库的加载");
                Check(model.Readme is null || Path.GetDirectoryName(model.Readme.Path) == model.Root, "过期说明读取不会覆盖当前仓库");
                SaveScreenshot(window, Path.Combine("artifacts", "rapid-switching.png"));

                var heads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var drafts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in repositories)
                    heads[path] = (await Runner.RunAsync(path, ["rev-parse", "HEAD"])).Output.Trim();
                for (int pass = 0; pass < 2; pass++)
                    foreach (string path in repositories)
                    {
                        var timer = Stopwatch.StartNew();
                        list.SelectedItem = model.Repositories.First(item => item.Path == path);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        Check(model.CanOperate && !model.IsWorkspaceLoading && model.Repository?.Path == path,
                            "快速切走的仓库再次点击立即可用：" + Path.GetFileName(path));
                        Check(model.History.FirstOrDefault()?.Hash == heads[path], "缓存历史对应真实仓库");
                        if (drafts.TryGetValue(path, out string? draft)) Check(model.CommitMessage == draft, "连续切换保留提交草稿");
                        else model.CommitMessage = drafts[path] = "检查草稿 " + Path.GetFileName(path);
                        Console.WriteLine($"快速切换：{Path.GetFileName(path)}，耗时 {timer.ElapsedMilliseconds} 毫秒");
                    }
                await WaitWorkspaceReadsAsync(model);
                Check(model.Repository?.Path == repositories[^1] && model.History[0].Hash == heads[repositories[^1]], "全部后台读取结束后仍是最后选择的仓库");
                await model.PreloadRepositoriesAsync();
                string executable = settings.Value.GitExecutablePath;
                settings.Value.GitExecutablePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "git.exe");
                list.SelectedItem = model.Repositories.First(item => item.Path == repositories[0]);
                await WaitWorkspaceReadsAsync(model);
                Check(model.CanOperate && !model.IsWorkspaceLoading && model.History[0].Hash == heads[repositories[0]]
                    && model.Status.Contains("未找到 Git"), "后台读取真实失败时保留缓存页面并显示错误");
                settings.Value.GitExecutablePath = executable;
                Console.WriteLine($"快速切换检查通过，共 {_count} 项，真实仓库 {repositories.Length} 个。");
            }
            catch (Exception ex) { failure = ex; }
            finally { model.Dispose(); app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("快速切换检查失败。", failure);
    }

    private static async Task WaitWorkspaceReadsAsync(MainViewModel model)
    {
        // 等待所有实际在途读取，确认被切走的读取也已完成，避免用固定延迟掩盖竞态。
        var reads = (IDictionary)typeof(MainViewModel).GetField("_workspaceReads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
        while ((reads.Count > 0 || model.IsWorkspaceRefreshing || model.IsBusy) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        Check(reads.Count == 0 && !model.IsWorkspaceRefreshing && !model.IsBusy, "实际仓库读取在限定时间内完成");
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
