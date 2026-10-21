using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GitManager.Services;
using GitManager.Services.CommitMessages;
using GitManager.Views.CommitMessages;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunQwenSummaryChecks()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Dark", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            try
            {
                string root = Path.Combine(Path.GetTempPath(), "GitManagerQwenChecks", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                await Git.InitializeAsync(root);
                await Git.SaveIdentityAsync(root, "ChongyangMo", "checks@example.invalid");
                string code = Path.Combine(root, "code.cs");
                await File.WriteAllTextAsync(code, "int value = 1;\n");
                await Git.CommitAsync(root, await Git.ReadChangesAsync(root), "建立真实检查基线");
                await File.WriteAllTextAsync(code, "int value = 2;\n");
                await File.WriteAllTextAsync(Path.Combine(root, "新增.txt"), "新增文件实际内容\n");
                await File.WriteAllBytesAsync(Path.Combine(root, "binary.bin"), [0, 1, 2]);
                var files = await Git.ReadChangesAsync(root);
                string head = (await Runner.RunAsync(root, ["rev-parse", "HEAD"])).Output;
                string index = (await Runner.RunAsync(root, ["diff", "--cached", "--raw", "-z"])).Output;
                var options = new QwenOptions();
                var reader = new CommitContentReader(Git);
                var first = await reader.ReadAsync(root, files, options, default);
                var stats = await new LocalCommitSummaryReader(Git).ReadAsync(root, files);
                Check(first.Text.Contains("-int value = 1;") && first.Text.Contains("+int value = 2;")
                    && first.Text.Contains("新增文件实际内容"), "千问输入包含真实差异和未跟踪文本内容");
                Check(first.Text.Contains("二进制文件，省略内容"), "二进制内容不作为文本发送");
                await File.WriteAllTextAsync(code, "int value = 3;\n");
                var changed = await reader.ReadAsync(root, files, options, default);
                var sameStats = await new LocalCommitSummaryReader(Git).ReadAsync(root, files);
                Check(stats.Fingerprint == sameStats.Fingerprint && first.Fingerprint != changed.Fingerprint,
                    "同路径、同状态和同增删行数的不同内容不会复用缓存");
                Check(CommitSummaryCache.Key(root, options, first) != CommitSummaryCache.Key(root, options, changed), "本地缓存键包含实际内容");
                await File.WriteAllBytesAsync(Path.Combine(root, "binary.bin"), [0, 3, 4]);
                var binaryChanged = await reader.ReadAsync(root, files, options, default);
                Check(changed.Text == binaryChanged.Text && changed.Fingerprint != binaryChanged.Fingerprint, "省略的二进制内容变化仍使缓存失效");
                await File.WriteAllTextAsync(Path.Combine(root, "新增.txt"), new string('字', 3000));
                options.MaxFileCharacters = 256;
                var limited = await reader.ReadAsync(root, files, options, default);
                Check(limited.Text.Contains("截断") && limited.Text.Length < options.MaxInputCharacters, "长文件内容按配置截断并明确标记");
                Check((await Runner.RunAsync(root, ["rev-parse", "HEAD"])).Output == head
                    && (await Runner.RunAsync(root, ["diff", "--cached", "--raw", "-z"])).Output == index, "读取总结输入不修改提交和暂存区");
                var cache = new CommitSummaryCache(Path.Combine(root, "cache"));
                string cacheKey = CommitSummaryCache.Key(root, options, limited);
                string local = LocalCommitSummary.Generate(sameStats.Files);
                cache.Save(cacheKey, local);
                Check(cache.Read(cacheKey) == local, "本地结果缓存可原样读写实际生成的说明");
                var settings = new SettingsStore(Path.Combine(root, "settings.json"));
                settings.Value.Qwen = options;
                settings.Save();
                Check(!File.ReadAllText(settings.FilePath).Contains("ApiKey", StringComparison.OrdinalIgnoreCase), "配置文件不包含密钥字段");
                if (QwenCredentials.Read(options) is null)
                {
                    settings.Value.Claude.Enabled = false;
                    settings.Value.Codex.Enabled = false;
                    bool blocked = false;
                    try { await new CommitMessageGenerator(Git, settings).GenerateAsync(root, files, default); }
                    catch (InvalidOperationException ex) { blocked = ex.Message.Contains("API Key"); }
                    Check(blocked && (await Runner.RunAsync(root, ["rev-parse", "HEAD"])).Output == head,
                        "未填写密钥时明确提示配置，不发起调用或创建提交");
                }
                var window = new QwenSettingsWindow(options, new ClaudeOptions()) { ShowInTaskbar = false, ShowActivated = false,
                    Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                var note = Descendants(window).OfType<TextBlock>().First(block => block.Name == "NoteText");
                var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
                while (note.Text.StartsWith("正在检测", StringComparison.Ordinal) && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(note.Text.Contains("外网：Claude") && note.Text.IndexOf("Codex", StringComparison.Ordinal) > note.Text.IndexOf("Claude", StringComparison.Ordinal)
                    && note.Text.IndexOf("千问", StringComparison.Ordinal) > note.Text.IndexOf("Codex", StringComparison.Ordinal), "设置窗口展示 Claude、Codex、千问顺位");
                Console.WriteLine("总结顺位：" + Environment.NewLine + note.Text);
                Check(Descendants(window).OfType<PasswordBox>().Count(box => box.IsVisible) == 1, "千问设置使用密码输入框");
                Check(window.Options.Model == "qwen3.8-flash" && window.OptionValue, "设置默认启用千问3.8 Flash");
                string screenshot = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "qwen-settings-dark.png");
                SaveScreenshot(window, screenshot);
                window.Close();
                Console.WriteLine("千问本地检查通过，未调用付费接口。真实检查仓库：" + root);
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("千问本地检查失败。", failure);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
