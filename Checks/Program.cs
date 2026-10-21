using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GitManager.Models;
using GitManager.Services;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static readonly GitRunner Runner = new(() => @"C:\Program Files\Git\cmd\git.exe");
    private static readonly GitRepositoryService Git = new(Runner);
    private static int _count;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--ensure-github-remote", var githubRoot, var githubRemote]) { RunGitHubRemoteChecksAsync(githubRoot, githubRemote).GetAwaiter().GetResult(); return 0; }
            if (args.Length > 2 && args[0] == "--codex-live") { RunCodexLiveAsync(args[1], args[2..]).GetAwaiter().GetResult(); return 0; }
            if (args.Length >= 3 && args[0] == "--push-indicators") { RunPushIndicatorChecks(args[1], args[2..]); return 0; }
            if (args is ["--layout", var layoutRepository, var layoutScreenshot]) { RunLayoutChecks(layoutRepository, layoutScreenshot); return 0; }
            if (args is ["--luxai-ui", var screenshot]) { RenderLuxAiSettings(screenshot); return 0; }
            if (args is ["--luxai-session"]) { RunLuxAiSessionAsync().GetAwaiter().GetResult(); return 0; }
            if (args.Length >= 3 && args[0] == "--luxai-live") { RunLuxAiLiveAsync(args[1], args[2..]).GetAwaiter().GetResult(); return 0; }
            if (args.Length == 1 && args[0] == "--repository-paths") { RunRepositoryPathChecksAsync().GetAwaiter().GetResult(); return 0; }
            if (args.Length == 1 && args[0] == "--repository-copies") { RunRepositoryCopiesChecks(); return 0; }
            if (args.Length >= 3 && args[0] == "--copy-analysis-live") { RunCopyAnalysisLive(args[1..]); return 0; }
            if (args.Length == 1 && args[0] == "--line-endings") { RunLineEndingChecksAsync().GetAwaiter().GetResult(); return 0; }
            if (args.Length == 1 && args[0] == "--local-deletion") { RunLocalDeletionChecksAsync().GetAwaiter().GetResult(); return 0; }
            if (args.Length == 2 && args[0] == "--locks") { Console.WriteLine(GitManager.Services.Repositories.Copies.DirectoryLocks.Inspect(args[1]).Describe()); return 0; }
            if (args.Length == 1 && args[0] == "--qwen-usage") { RunQwenUsageChecks(); return 0; }
            if (args.Length == 1 && args[0] == "--qwen-cost") { RunQwenCostChecks(); return 0; }
            if (args.Length == 1 && args[0] == "--claude") { RunClaudeChecksAsync().GetAwaiter().GetResult(); return 0; }
            if (args.Length > 0 && args[0] == "--model-policy") { RunModelPolicyChecksAsync(args[1..]).GetAwaiter().GetResult(); return 0; }
            if (args is ["--model-routing-ui", var routingScreenshot]) { RenderModelRoutingSettings(routingScreenshot); return 0; }
            if (args.Length == 1 && args[0] == "--qwen") { RunQwenSummaryChecks(); return 0; }
            if (args.Length > 2 && args[0] == "--qwen-live") { RunCommitSummaryChecks(args[1], args[2..], qwenLive: true); return 0; }
            if (args.Length == 2 && args[0] == "--identity") { RunCommitIdentityChecksAsync(args[1]).GetAwaiter().GetResult(); return 0; }
            if (args.Length is 3 or 4 && args[0] == "--servicecloud") { RunServiceCloudChecksAsync(args[1], args[2], args.Length == 4 ? args[3] : null).GetAwaiter().GetResult(); return 0; }
            if (args.Length == 3 && args[0] == "--servicecloud-ui") { RenderServiceCloud(args[1], args[2]); return 0; }
            if (args.Length > 2 && args[0] == "--summary") { RunCommitSummaryChecks(args[1], args[2..]); return 0; }
            if (args.Length > 2 && args[0] == "--summary-cancel") { RunCommitSummaryChecks(args[1], args[2..], cancelOnly: true); return 0; }
            if (args.Length > 2 && args[0] == "--auto-commit") { RunCommitSummaryChecks(args[1], args[2..], autoCommit: true); return 0; }
            if (args.Length == 2 && args[0] == "--commit-details") { RunCommitDetailsChecks(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--history") { RunHistoryNavigationChecks(args[1]); return 0; }
            if (args.Length > 1 && args[0] == "--switching") { RunSwitchingChecks(args[1..]); return 0; }
            if (args.Length == 1 && args[0] == "--discovery") { RunDiscoveryChecksAsync().GetAwaiter().GetResult(); return 0; }
            if (args.Length == 1 && args[0] == "--discover-local") { DiscoverLocalAsync().GetAwaiter().GetResult(); return 0; }
            if (args.Length == 1 && args[0] == "--tray") { RunTrayChecks(); return 0; }
            if (args.Length == 1 && args[0] == "--remote-groups") { RunRemoteGroupChecks(); return 0; }
            if (args.Length == 3 && args[0] == "--ui") { RenderPreview(args[1], args[2]); return 0; }
            if (args.Length == 1 && args[0] == "--versions") { RunVersionChecksAsync().GetAwaiter().GetResult(); return 0; }
            string testRoot = RunChecksAsync().GetAwaiter().GetResult();
            Console.WriteLine($"检查通过，共 {_count} 项。真实临时仓库：{testRoot}");
            if (args.Length == 2) RenderPreview(args[0], args[1]);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("检查失败：" + ex); return 1; }
    }

    private static async Task<string> RunChecksAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "GitManagerChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await Git.InitializeAsync(root);
        await Git.SaveIdentityAsync(root, "GitManager 校验", "gitmanager-checks@example.invalid");
        Check(!(await Git.ReadAsync(root)).HasHead, "空仓库可读取");
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "cache/\n");
        await File.WriteAllTextAsync(Path.Combine(root, "中文 文件.txt"), "第一行\n原始内容\n最后一行\n");
        await File.WriteAllTextAsync(Path.Combine(root, "其他.txt"), "其他原始内容\n");
        await Git.CommitAsync(root, await Git.ReadChangesAsync(root), "建立检查版本");
        var initial = (await Git.ReadAsync(root)).History[0];
        Check((await Git.ReadHistoryFilesAsync(root, initial)).Count == 3, "首次提交文件列表可读取");

        await File.WriteAllTextAsync(Path.Combine(root, "中文 文件.txt"), "第一行\n更新后的内容\n最后一行\n新增一行\n");
        await File.WriteAllTextAsync(Path.Combine(root, "其他.txt"), "不应被一起提交\n");
        await Runner.RunAsync(root, ["add", "--", "其他.txt"]);
        var changed = (await Git.ReadChangesAsync(root)).Single(file => file.Path == "中文 文件.txt");
        var comparison = await Git.CompareWorkingAsync(root, changed, true);
        var rows = DiffBuilder.Build(comparison);
        Check(rows.Any(row => row.Removed && row.Before == "原始内容") && rows.Any(row => row.Added && row.After == "更新后的内容"), "中文文件的并排差异正确");
        Check(rows.Where(row => row.BeforeNumber.Length > 0).Select(row => row.Before).SequenceEqual(comparison.Before.TrimEnd('\n').Split('\n')), "差异保留全部原始行");
        Check(rows.Where(row => row.AfterNumber.Length > 0).Select(row => row.After).SequenceEqual(comparison.After.TrimEnd('\n').Split('\n')), "差异保留全部修改行");
        await Git.CommitAsync(root, [changed], "只提交勾选的文件");
        var state = await Git.ReadAsync(root);
        Check(state.Changes.Count == 1 && state.Changes[0].Path == "其他.txt" && state.Changes[0].IndexState == 'M', "提交不会夹带其他已暂存文件");
        string second = state.History[0].Hash;
        await Runner.RunAsync(root, ["restore", "--source=HEAD", "--staged", "--worktree", "--", "其他.txt"]);
        await File.WriteAllTextAsync(Path.Combine(root, "中文 文件.txt"), "恢复前尚未提交的修改\n");
        await File.WriteAllTextAsync(Path.Combine(root, "未跟踪.txt"), "需要保留的未跟踪文件\n");
        await Runner.RunAsync(root, ["add", "--", "中文 文件.txt"]);
        string? checkpoint = await Git.RestoreVersionAsync(root, initial.Hash);
        Check(checkpoint is not null && File.ReadAllText(Path.Combine(root, "中文 文件.txt")).Contains("原始内容"), "整版恢复前建立恢复点");
        Check((await Runner.RunAsync(root, ["rev-parse", "HEAD"])).Output.Trim() == second, "整版恢复不改写提交历史");
        Check((await Git.ReadRecoveryPointsAsync(root)).Any(point => point.Hash == checkpoint), "恢复点可查询");
        await Runner.RunAsync(root, ["restore", "--source=HEAD", "--staged", "--worktree", "--", "."]);
        await Git.ApplyRecoveryPointAsync(root, checkpoint!);
        Check(File.ReadAllText(Path.Combine(root, "中文 文件.txt")).Contains("尚未提交") && File.Exists(Path.Combine(root, "未跟踪.txt")), "恢复点可取回已暂存与未跟踪文件");
        Check((await Git.ReadChangesAsync(root)).Single(file => file.Path == "中文 文件.txt").IndexState == 'M', "恢复点保留原暂存状态");
        await Runner.RunAsync(root, ["restore", "--source=HEAD", "--staged", "--worktree", "--", "中文 文件.txt"]);
        File.Delete(Path.Combine(root, "未跟踪.txt"));

        await File.WriteAllTextAsync(Path.Combine(root, "中文 文件.txt"), "文件恢复前内容\n");
        await File.WriteAllTextAsync(Path.Combine(root, "其他.txt"), "其他文件保持不变\n");
        await Runner.RunAsync(root, ["add", "--", "其他.txt"]);
        await Git.RestoreVersionAsync(root, initial.Hash, "中文 文件.txt");
        Check(File.ReadAllText(Path.Combine(root, "其他.txt")) == "其他文件保持不变\n", "恢复单个文件不覆盖其他工作区文件");
        Check((await Git.ReadChangesAsync(root)).Single(file => file.Path == "其他.txt").IndexState == 'M', "恢复单个文件保留其他文件暂存状态");
        await Runner.RunAsync(root, ["restore", "--source=HEAD", "--staged", "--worktree", "--", "."]);
        await Runner.RunAsync(root, ["mv", "--", "中文 文件.txt", "更名 文件.txt"]);
        var rename = (await Git.ReadChangesAsync(root)).Single();
        Check(rename.Path == "更名 文件.txt" && rename.OriginalPath == "中文 文件.txt", "重命名状态解析正确");
        var renameDiff = await Git.CompareWorkingAsync(root, rename, true);
        Check(renameDiff.Before.Replace("\r\n", "\n") == renameDiff.After.Replace("\r\n", "\n") && DiffBuilder.Build(renameDiff).All(row => !row.Removed && !row.Added), "重命名文件可读取原内容");
        await Git.CommitAsync(root, [rename], "重命名检查");
        var renameCommit = (await Git.ReadAsync(root)).History[0];
        Check((await Git.ReadHistoryFilesAsync(root, renameCommit)).Count == 2, "历史重命名以删除和新增显示");

        Directory.CreateDirectory(Path.Combine(root, "cache"));
        await File.WriteAllTextAsync(Path.Combine(root, "cache", "保留.txt"), "原本受版本管理的文件\n");
        await Runner.RunAsync(root, ["add", "-f", "--", "cache/保留.txt"]);
        await Runner.RunAsync(root, ["commit", "-m", "记录曾被跟踪的文件"]);
        string withIgnored = (await Runner.RunAsync(root, ["rev-parse", "HEAD"])).Output.Trim();
        await Runner.RunAsync(root, ["rm", "--", "cache/保留.txt"]);
        await Runner.RunAsync(root, ["commit", "-m", "停止跟踪文件"]);
        string absentCommit = (await Runner.RunAsync(root, ["rev-parse", "HEAD"])).Output.Trim();
        await Git.RestoreVersionAsync(root, absentCommit, "cache/保留.txt");
        Check(!File.Exists(Path.Combine(root, "cache", "保留.txt")), "已删除文件恢复到不存在的版本可正常处理");
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        await File.WriteAllTextAsync(Path.Combine(root, "cache", "保留.txt"), "不能覆盖的忽略文件\n");
        bool blocked = false;
        try { await Git.RestoreVersionAsync(root, withIgnored); } catch (InvalidOperationException ex) { blocked = ex.Message.Contains("忽略"); }
        Check(blocked && File.ReadAllText(Path.Combine(root, "cache", "保留.txt")).Contains("不能覆盖"), "恢复不会覆盖被忽略的文件");

        string remote = Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "-remote.git");
        await Runner.RunAsync(null, ["init", "--bare", "--initial-branch=main", remote]);
        await Git.SaveRemoteAsync(root, remote);
        await Git.SynchronizeAsync(root, "push");
        string remoteHead = (await Runner.RunAsync(root, ["ls-remote", "origin", "refs/heads/main"])).Output.Split('\t')[0];
        Check(remoteHead == (await Runner.RunAsync(root, ["rev-parse", "HEAD"])).Output.Trim(), "本地真实远程推送成功");
        await Git.SynchronizeAsync(root, "fetch");
        await Git.SynchronizeAsync(root, "pull");
        await Git.SwitchBranchAsync(root, "checks/branch", create: true);
        await Git.SwitchBranchAsync(root, "main");
        Check((await Git.ReadAsync(root)).Branch == "main", "分支新建和切换成功");
        bool parentBlocked = false;
        try { await Git.OpenAsync(Path.Combine(root, "cache")); } catch (InvalidOperationException) { parentBlocked = true; }
        Check(parentBlocked, "子目录不会误识别为独立仓库");
        await CheckDiffEdgesAsync(root);
        return root;
    }

    private static async Task CheckDiffEdgesAsync(string root)
    {
        string filePath = Path.Combine(root, "更名 文件.txt");
        string[] changes = ["在开头新增\n第一行\n更新后的内容\n最后一行\n新增一行\n", "第一行\n新增一行\n", "", "单行无换行", "第一行\n更新后的内容\n最后一行\n新增一行\n"];
        foreach (string value in changes)
        {
            await File.WriteAllTextAsync(filePath, value, new UTF8Encoding(false));
            var file = new ChangedFile("更名 文件.txt", null, ' ', 'M');
            var comparison = await Git.CompareWorkingAsync(root, file, true);
            var rows = DiffBuilder.Build(comparison);
            static IEnumerable<string> Lines(string text) => text.Length == 0 ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            Check(rows.Where(row => row.BeforeNumber.Length > 0).Select(row => row.Before).SequenceEqual(Lines(comparison.Before)) && rows.Where(row => row.AfterNumber.Length > 0).Select(row => row.After).SequenceEqual(Lines(comparison.After)), "差异行对齐边界检查");
        }
        await Runner.RunAsync(root, ["restore", "--source=HEAD", "--worktree", "--", "更名 文件.txt"]);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        _count++;
        Console.WriteLine("通过：" + label);
    }

    // 设置 ActiveTab 会经 MainWindow.TabChanged 启动一次无人等待的读取，占用 IsBusy；断言或发起新操作前先等它结束。
    private static async Task WaitForIdleAsync(MainViewModel viewModel, string label, int seconds = 15)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while ((viewModel.IsBusy || viewModel.IsHistoryLoading) && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        if (viewModel.IsBusy || viewModel.IsHistoryLoading) throw new TimeoutException($"{label}：等待 {seconds} 秒后操作仍未结束。");
    }

    private static void RenderPreview(string repository, string screenshotPath)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Light", "Controls", "Workspace", "VersionTemplates" }) app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "GitManagerChecks", Guid.NewGuid().ToString("N"), "settings.json"));
        var viewModel = new MainViewModel(store);
        var window = new GitManager.MainWindow(viewModel) { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
        int errors = 0;
        bool started = false;
        app.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine("界面检查失败：" + e.Exception); errors++; e.Handled = true; app.Shutdown(1); };
        window.Loaded += async (_, _) =>
        {
            if (started) return;
            started = true;
            try
            {
                await viewModel.InitializeAsync(repository);
                if (viewModel.Repository is null) throw new InvalidOperationException("界面未加载真实仓库。");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, screenshotPath);
                viewModel.ActiveTab = MainViewModel.HistoryTab;
                await WaitForIdleAsync(viewModel, "切换到历史页");
                await viewModel.ShowCommitAsync();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (viewModel.HistoryFiles.Count == 0 || viewModel.Comparison is null) throw new InvalidOperationException("历史界面未加载真实提交。");
                SaveScreenshot(window, Path.ChangeExtension(screenshotPath, "history.png"));
                await viewModel.SetThemeAsync("Dark");
                if (!ThemeService.IsDark || new SettingsStore(store.FilePath).Value.Theme != "Dark") throw new InvalidOperationException("黑夜模式或配置持久化未生效。");
                await viewModel.SetThemeAsync("Light");
                if (ThemeService.IsDark) throw new InvalidOperationException("切换白天模式未生效。");
                await viewModel.SetThemeAsync("Dark");
                if (!ThemeService.IsDark) throw new InvalidOperationException("切换黑夜模式未生效。");
                viewModel.ActiveTab = MainViewModel.ReadmeTab;
                await WaitForIdleAsync(viewModel, "切换到说明页");
                await viewModel.ShowActiveTabAsync();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (viewModel.Readme is null || viewModel.Comparison is not null) throw new InvalidOperationException("说明页未读取 README。");
                var remoteNames = (await Runner.RunAsync(repository, ["remote"])).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!viewModel.RemoteAddresses.Select(remote => remote.Name).ToHashSet().SetEquals(remoteNames)) throw new InvalidOperationException("说明页未列出全部远程地址。");
                SaveScreenshot(window, Path.ChangeExtension(screenshotPath, "readme.png"));
                viewModel.ActiveTab = MainViewModel.ChangesTab;
                await WaitForIdleAsync(viewModel, "切换到改动页");
                await viewModel.ShowActiveTabAsync();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, Path.ChangeExtension(screenshotPath, "dark.png"));
                await VerifyBusyAppearanceAsync(window, viewModel, Path.ChangeExtension(screenshotPath, "busy.png"));
                await VerifyBusyFeedbackAsync(window, viewModel);
                await VerifyVersionViewsAsync(window, viewModel, screenshotPath);
                Console.WriteLine("黑夜模式、说明页和配置保存检查通过。");
            }
            catch (Exception ex) { errors++; Console.Error.WriteLine("界面检查失败：" + ex); }
            finally { app.Shutdown(); }
        };
        app.Run(window);
        if (errors > 0) throw new InvalidOperationException("界面检查未通过。");
    }

    private static void SaveScreenshot(Window window, string screenshotPath)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            context.DrawRectangle(window.Background, null, bounds);
            context.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds }, null, bounds);
        }
        image.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshotPath))!);
        using (var stream = File.Create(screenshotPath)) encoder.Save(stream);
        Console.WriteLine("界面预览已保存：" + screenshotPath);
    }
}
