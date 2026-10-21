using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Threading;
using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Repositories.Copies;
using GitManager.Services.Repositories;
using GitManager.Services.Repositories.Copies.Analysis;
using GitManager.ViewModels;
using GitManager.Views.Repositories;

namespace GitManager.Checks;

internal static partial class Program
{
    // 直接使用指定的真实仓库和现有模型登录，验证完整窗口路径，不制造或删除仓库。
    private static void RunCopyAnalysisLive(string[] roots)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Dark", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            try
            {
                string directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "copy-analysis");
                Directory.CreateDirectory(directory);
                var settings = new SettingsStore();
                var heads = new List<string>();
                var indexes = new List<string>();
                foreach (string root in roots)
                {
                    heads.Add((await Runner.RunAsync(root, ["rev-parse", "HEAD"])).Output);
                    indexes.Add((await Runner.RunAsync(root, ["diff", "--cached", "--raw", "-z"])).Output);
                }
                var bookmarks = roots.Select(root => new RepositoryBookmark(root)).ToArray();
                foreach (var bookmark in bookmarks)
                    bookmark.UpdateDisplayName(await RepositoryDisplayName.ReadAsync(Git, bookmark.Path));
                var comparison = await new RepositoryCopyReader(Git).ReadAsync(bookmarks);
                var evidence = await new RepositoryCopyEvidenceReader(Git).ReadAsync(comparison, default);
                Check(evidence.Repositories.All(repo => repo.Error.Length == 0), "真实仓库的忽略项、分支、工作树及暂存记录读取成功");
                foreach (var relation in evidence.Relations)
                    Console.WriteLine($"第 {relation.Left} 份与第 {relation.Right} 份：{relation.State}，各自额外提交 {relation.LeftOnly}/{relation.RightOnly}");
                var route = await new Services.CommitMessages.RepositorySummaryRouting(Git, settings).ResolveAsync(roots, default);
                Console.WriteLine("实际仓库模型路由：" + route.Target + "，" + route.Reason);
                int limit = route.Target == Services.CommitMessages.SummaryModelTarget.GroupAi ? settings.Value.LuxAi.MaxInputCharacters : settings.Value.Qwen.MaxInputCharacters;
                Console.WriteLine("实际模型输入字符数：" + RepositoryCopyAnalysisPrompt.Build(comparison, evidence, limit).Length);
                using var model = new MainViewModel(settings);
                var window = new RepositoryCopiesWindow(model, bookmarks)
                {
                    ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Show();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(300);
                while (window.AnalysisText is null && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(100);
                    if (window.Comparison is not null && !window.IsAnalyzing) break;
                }
                string state = ((System.Windows.Controls.TextBlock)window.FindName("AnalysisState")).Text;
                Check(!string.IsNullOrWhiteSpace(window.AnalysisText), "真实大模型分析显示成功：" + state);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, Path.Combine(directory, "analysis-dark.png"));
                ThemeService.Apply("Light");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, Path.Combine(directory, "analysis-light.png"));
                for (int i = 0; i < roots.Length; i++)
                {
                    Check(heads[i] == (await Runner.RunAsync(roots[i], ["rev-parse", "HEAD"])).Output
                        && indexes[i] == (await Runner.RunAsync(roots[i], ["diff", "--cached", "--raw", "-z"])).Output,
                        $"第 {i + 1} 份仓库的提交和暂存区保持原样");
                }
                await File.WriteAllTextAsync(Path.Combine(directory, "analysis.json"), JsonSerializer.Serialize(new
                {
                    Time = DateTimeOffset.Now, Evidence = evidence, ModelResult = window.AnalysisText, Source = state,
                    HeadAndIndexUnchanged = true
                }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                Console.WriteLine(window.AnalysisText);
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw failure;
    }
}
