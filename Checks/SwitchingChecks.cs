using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using GitManager.Services;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunSwitchingChecks(string[] repositories)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Light", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), "GitManagerChecks", Guid.NewGuid().ToString("N"), "settings.json"));
            settings.Value.RepositoryDiscovery.Enabled = false;
            settings.Value.Repositories = repositories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            using var viewModel = new MainViewModel(settings);
            var window = new MainWindow(viewModel) { ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            try
            {
                await viewModel.InitializeAsync(repositories[0]);
                await viewModel.PreloadRepositoriesAsync();
                var heads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in repositories.Distinct(StringComparer.OrdinalIgnoreCase))
                    heads[path] = (await Runner.RunAsync(path, ["rev-parse", "--verify", "HEAD"])).Output.Trim();
                var drafts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in repositories.Skip(1).Concat(repositories))
                {
                    var timer = Stopwatch.StartNew();
                    var bookmark = viewModel.Repositories.FirstOrDefault(item => item.Path == path);
                    if (bookmark is null) { bookmark = new(path); viewModel.Repositories.Add(bookmark); }
                    var list = (System.Windows.Controls.ListBox)window.FindName("RepositoryList");
                    list.SelectedItem = bookmark;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                    timer.Stop();
                    Check(viewModel.CanOperate && !viewModel.IsWorkspaceLoading && string.Equals(viewModel.Repository?.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase), "预读后的仓库立即显示并可操作");
                    string head = heads[path];
                    Check(viewModel.History.FirstOrDefault()?.Hash == head && viewModel.LocalVersionLabel.Contains(head[..7]), "本地历史及版本栏属于当前真实仓库");
                    if (drafts.TryGetValue(path, out var draft)) Check(viewModel.CommitMessage == draft, "仓库切换保留各自提交草稿");
                    else { drafts[path] = "检查草稿 " + Path.GetFileName(path); viewModel.CommitMessage = drafts[path]; }
                    Console.WriteLine($"仓库切换耗时：{Path.GetFileName(path)} {timer.Elapsed.TotalMilliseconds:F0} 毫秒；{viewModel.FetchStateText}");
                }
                var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
                while ((viewModel.IsWorkspaceRefreshing || viewModel.FetchStateText == "正在后台获取远程版本" || viewModel.IsBusy) && DateTimeOffset.UtcNow < deadline) await Task.Delay(30);
                Check(viewModel.IsIdle && viewModel.FetchStateText.StartsWith("获取于 ", StringComparison.Ordinal), "后台真实远程获取完成并更新状态");
                string finalHead = (await Runner.RunAsync(repositories[^1], ["rev-parse", "HEAD"])).Output.Trim();
                Check(viewModel.History[0].Hash == finalHead && viewModel.LocalVersionLabel.Contains(finalHead[..7]), "后台完成后仍显示最后选择仓库的真实版本");
                await viewModel.ShowChangeAsync();
                Check(viewModel.IsIdle, "后台完成后可读取文件差异");

                string empty = Path.Combine(Path.GetTempPath(), "GitManagerChecks", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(empty);
                await Git.InitializeAsync(empty);
                viewModel.Repositories.Add(new(empty));
                await viewModel.OpenRepositoryAsync(empty);
                Check(viewModel.IsWorkspaceLoading && !viewModel.CanOperate && viewModel.History.Count == 0, "未预读仓库明确加载中，不显示上个仓库数据");
                await viewModel.OpenRepositoryAsync(repositories[0]);
                Check(viewModel.CanOperate && viewModel.History[0].Hash == heads[repositories[0]], "首次加载期间仍可立即切换到已预读仓库");
                await viewModel.OpenRepositoryAsync(empty);
                while (viewModel.IsWorkspaceRefreshing) await Task.Delay(15);
                Check(viewModel.IsIdle && viewModel.History.Count == 0 && viewModel.RemoteHistory.Count == 0 && viewModel.FetchStateText == "尚未设置远程仓库", "切换空仓库后清除上个仓库的历史及远程状态");
                await File.WriteAllTextAsync(Path.Combine(empty, "真实改动.txt"), "第一次修改\n");
                await viewModel.OpenRepositoryAsync(empty);
                while (viewModel.IsWorkspaceRefreshing) await Task.Delay(15);
                Check(viewModel.Changes.Any(item => item.Path == "真实改动.txt") && viewModel.Comparison?.After.Contains("第一次修改") == true, "后台校验可发现缓存之后新增的真实文件");
                await viewModel.OpenRepositoryAsync(repositories[0]);
                await File.WriteAllTextAsync(Path.Combine(empty, "真实改动.txt"), "第二次修改\n");
                await viewModel.OpenRepositoryAsync(empty);
                Check(viewModel.Comparison?.After.Contains("第一次修改") == true, "切换立即显示上次真实读取的内容");
                while (viewModel.IsWorkspaceRefreshing) await Task.Delay(15);
                Check(viewModel.Comparison?.After.Contains("第二次修改") == true, "后台校验会更新当前文件差异");
                await Git.SaveRemoteAsync(empty, Path.Combine(empty, "不存在的远程.git"));
                await viewModel.OpenRepositoryAsync(empty);
                deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while ((viewModel.IsWorkspaceRefreshing || viewModel.FetchStateText == "正在后台获取远程版本" || viewModel.IsBusy) && DateTimeOffset.UtcNow < deadline) await Task.Delay(30);
                Check(viewModel.IsIdle && viewModel.FetchStateText == "获取失败，显示远程缓存" && viewModel.FetchError.Length > 0, "真实远程路径不可用时保留可操作状态并显示错误");
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("仓库切换检查失败。", failure);
    }
}
