using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitManager.Models;
using GitManager.Services;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunHistoryNavigationChecks(string repository)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Light", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            var store = new SettingsStore(Path.Combine(CheckDirectories.Create("Checks"), "settings.json"));
            store.Value.RepositoryDiscovery.Enabled = false;
            using var vm = new MainViewModel(store);
            var window = new MainWindow(vm) { ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            try
            {
                await vm.InitializeAsync(repository);
                vm.ActiveTab = MainViewModel.HistoryTab;
                await WaitForIdleAsync(vm, "切换到历史页");
                var commits = vm.History.Take(4).ToArray();
                Check(commits.Length >= 2, "真实仓库有可切换的历史版本");
                vm.SelectedCommit = commits[0];
                var first = vm.ShowCommitAsync();
                Check(vm.IsIdle, "历史读取期间列表保持可操作");
                vm.SelectedCommit = commits[^1];
                var last = vm.ShowCommitAsync();
                await Task.WhenAll(first, last);
                Check(vm.SelectedCommit?.Hash == commits[^1].Hash && vm.Comparison?.AfterLabel == commits[^1].ShortHash, "连续点击时只显示最后选择的提交");
                var list = (ListBox)window.FindName("HistoryList");
                for (int pass = 0; pass < 2; pass++)
                    foreach (var commit in commits)
                    {
                        var timer = Stopwatch.StartNew();
                        list.SelectedItem = commit;
                        Check(vm.HistoryFiles.Count > 0 && vm.Comparison is not null, "切换提交时读取完成前保留上一版本内容，不先清空");
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                        while (vm.IsHistoryLoading && DateTimeOffset.UtcNow < deadline) await Task.Delay(1);
                        timer.Stop();
                        Check(!vm.IsHistoryLoading && vm.SelectedCommit?.Hash == commit.Hash && vm.Comparison?.AfterLabel == commit.ShortHash, "提交、文件和差异属于当前历史版本");
                        if (vm.SelectedHistoryFile is { } file)
                        {
                            var actual = await Runner.RunAsync(repository, ["show", commit.Hash + ":" + file.Path], true, strictUtf8: true);
                            if (vm.Comparison!.After != (actual.ExitCode == 0 ? actual.Output : ""))
                                Console.WriteLine($"内容差异：{file.Path}，预览长度 {vm.Comparison.After.Length}，Git 长度 {actual.Output.Length}，提示 {vm.Comparison.Notice}，预览首字符 {(vm.Comparison.After.Length == 0 ? 0 : (int)vm.Comparison.After[0])}");
                            Check(vm.Comparison!.After == (actual.ExitCode == 0 ? actual.Output : ""), "历史预览与 Git 实际文件内容一致");
                        }
                        Console.WriteLine($"历史切换耗时：第 {pass + 1} 轮 {commit.ShortHash} {timer.Elapsed.TotalMilliseconds:F0} 毫秒");
                    }
                await CheckHistoryContentAsync();
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("历史版本检查失败。", failure);
    }

    private static async Task CheckHistoryContentAsync()
    {
        string root = CheckDirectories.Create("HistoryChecks");
        Directory.CreateDirectory(root);
        await Git.InitializeAsync(root);
        await Git.SaveIdentityAsync(root, "历史校验", "history-checks@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(root, "中文 文件.txt"), "第一行\n初始内容\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(root, "空文件.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(root, "带BOM.txt"), "带签名内容\n", new UTF8Encoding(true));
        await File.WriteAllBytesAsync(Path.Combine(root, "二进制.bin"), [0, 1, 255]);
        await File.WriteAllBytesAsync(Path.Combine(root, "非UTF8.txt"), [255, 254, 253]);
        await File.WriteAllTextAsync(Path.Combine(root, "大文件.txt"), new string('A', 2 * 1024 * 1024 + 1));
        await Git.CommitAsync(root, await Git.ReadChangesAsync(root), "真实历史内容检查");
        var initial = (await Git.ReadAsync(root)).History[0];
        var added = await Git.CompareHistoryAsync(root, initial, new("中文 文件.txt", "新增"));
        Check(added.Before == "" && added.After == "第一行\n初始内容\n", "中文和空格路径的首次提交内容完整");
        Check((await Git.CompareHistoryAsync(root, initial, new("空文件.txt", "新增"))).After == "", "空文件可批量读取");
        Check((await Git.CompareHistoryAsync(root, initial, new("带BOM.txt", "新增"))).After == "带签名内容\n", "带 UTF-8 签名的文本与原预览行为一致");
        Check((await Git.CompareHistoryAsync(root, initial, new("二进制.bin", "新增"))).Notice?.Contains("二进制") == true, "二进制文件保持预览限制");
        Check((await Git.CompareHistoryAsync(root, initial, new("非UTF8.txt", "新增"))).Notice?.Contains("UTF-8") == true, "非 UTF-8 文件保持预览限制");
        Check((await Git.CompareHistoryAsync(root, initial, new("大文件.txt", "新增"))).Notice?.Contains("较大") == true, "大文件在读取内容前限制大小");
        await File.WriteAllTextAsync(Path.Combine(root, "中文 文件.txt"), "第一行\n实际修改\n", new UTF8Encoding(false));
        File.Delete(Path.Combine(root, "空文件.txt"));
        await Git.CommitAsync(root, await Git.ReadChangesAsync(root), "修改和删除内容检查");
        var second = (await Git.ReadAsync(root)).History[0];
        var modified = await Git.CompareHistoryAsync(root, second, new("中文 文件.txt", "修改"));
        Check(modified.Before == added.After && modified.After == "第一行\n实际修改\n" && modified.Patch.Contains("实际修改"), "同一批次准确读取双侧内容并生成差异");
        var deleted = await Git.CompareHistoryAsync(root, second, new("空文件.txt", "删除"));
        Check(deleted.Before == "" && deleted.After == "" && deleted.Notice is null, "删除文件不会误报读取失败");
    }
}
