using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitManager.Services;
using GitManager.Services.CommitMessages;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunCommitSummaryChecks(string repository, string[] paths, bool cancelOnly = false)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Light", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "GitManagerChecks", Guid.NewGuid().ToString("N"), "settings.json"));
            store.Value.RepositoryDiscovery.Enabled = false;
            using var vm = new MainViewModel(store);
            var window = new MainWindow(vm) { ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            try
            {
                await vm.InitializeAsync(repository);
                foreach (var file in vm.Changes) file.Selected = paths.Contains(file.Path, StringComparer.Ordinal);
                var selected = vm.Changes.Where(file => file.Selected).ToArray();
                Check(selected.Length == paths.Length, "总结范围来自指定的真实改动");
                var source = await new LocalCommitSummaryReader(vm.Git).ReadAsync(repository, selected);
                Check(source.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal).SetEquals(paths), "本地规则只接收勾选文件");
                if (cancelOnly)
                {
                    vm.CommitMessage = "需要保留的现有草稿";
                    var cancelled = vm.GenerateCommitMessageAsync();
                    await vm.GenerateCommitMessageAsync();
                    await cancelled;
                    Check(!vm.IsGeneratingCommitMessage && vm.CommitMessage == "需要保留的现有草稿", "取消生成保留现有草稿");
                    return;
                }
                string head = (await Runner.RunAsync(repository, ["rev-parse", "HEAD"])).Output;
                string staged = (await Runner.RunAsync(repository, ["diff", "--cached", "--raw", "-z"])).Output;
                var timer = Stopwatch.StartNew();
                ((Button)window.FindName("GenerateCommitMessageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(vm.IsGeneratingCommitMessage && vm.IsIdle, "本地生成期间界面保持可操作");
                var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (vm.IsGeneratingCommitMessage && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
                Check(!vm.IsGeneratingCommitMessage && vm.CommitMessage == LocalCommitSummary.Generate(source.Files), "按钮已填入本地规则生成的真实说明");
                Check((await Runner.RunAsync(repository, ["rev-parse", "HEAD"])).Output == head
                    && (await Runner.RunAsync(repository, ["diff", "--cached", "--raw", "-z"])).Output == staged, "自动总结没有提交或改变暂存区");
                Console.WriteLine($"本地总结耗时：{timer.Elapsed.TotalMilliseconds:F0} 毫秒\n{vm.CommitMessage}");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, Path.Combine(repository, "artifacts", "commit-summary-local.png"));
                string draft = vm.CommitMessage;
                var pending = vm.GenerateCommitMessageAsync();
                await vm.GenerateCommitMessageAsync();
                await pending;
                Check(!vm.IsGeneratingCommitMessage && vm.CommitMessage == draft, "取消生成会保留已有提交说明");
                await CheckLocalSummaryFactsAsync();
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("本地总结检查失败。", failure);
    }

    private static async Task CheckLocalSummaryFactsAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "GitManagerLocalSummaryChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await Git.InitializeAsync(root);
        await Git.SaveIdentityAsync(root, "本地规则校验", "local-summary@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(root, "中文 文件.cs"), "旧内容\n");
        var reader = new LocalCommitSummaryReader(Git);
        var initial = await reader.ReadAsync(root, await Git.ReadChangesAsync(root));
        Check(initial.Files.Single().Action == "新增" && initial.Files.Single().Added is null, "初次提交不填造增删行数");
        await File.WriteAllTextAsync(Path.Combine(root, "删除.md"), "文档\n");
        await File.WriteAllTextAsync(Path.Combine(root, "旧名.txt"), "保持原内容\n");
        await File.WriteAllBytesAsync(Path.Combine(root, "binary.bin"), [0, 1, 2]);
        await Git.CommitAsync(root, await Git.ReadChangesAsync(root), "本地规则检查基线");
        await File.WriteAllTextAsync(Path.Combine(root, "中文 文件.cs"), "新内容\n新增一行\n");
        File.Delete(Path.Combine(root, "删除.md"));
        await Runner.RunAsync(root, ["mv", "--", "旧名.txt", "新名.txt"]);
        await File.WriteAllBytesAsync(Path.Combine(root, "binary.bin"), [0, 4, 5]);
        await File.WriteAllTextAsync(Path.Combine(root, "未跟踪.txt"), "实际新增内容\n");
        var actual = await reader.ReadAsync(root, await Git.ReadChangesAsync(root));
        var modified = actual.Files.Single(file => file.Path == "中文 文件.cs");
        Check(modified.Added == 2 && modified.Removed == 1, "中文和空格路径准确统计真实增删行数");
        Check(actual.Files.Single(file => file.Path == "新名.txt") is { Action: "重命名", OriginalPath: "旧名.txt", Added: null }, "重命名保留原路径且不误报整文件增删");
        Check(actual.Files.Single(file => file.Path == "删除.md") is { Action: "删除", Added: 0, Removed: 1 }, "删除文件统计准确");
        Check(actual.Files.Single(file => file.Path == "binary.bin") is { Binary: true, Added: null, Removed: null }, "二进制文件不填造文本行数");
        Check(actual.Files.Single(file => file.Path == "未跟踪.txt") is { Action: "新增", Added: null }, "未跟踪文件不填造行数");
    }
}
