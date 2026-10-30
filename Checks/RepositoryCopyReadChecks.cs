using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitManager.Models;
using GitManager.Models.RepositoryCopies;
using GitManager.Services;
using GitManager.Services.Repositories.Copies;
using GitManager.ViewModels;
using GitManager.Views.Repositories;

namespace GitManager.Checks;

internal static partial class Program
{
    // 只读指定的实际仓库，不联网、不创建副本，也不调用模型。
    private static async Task RunCopyReadChecksAsync(string[] roots)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var watch = Stopwatch.StartNew();
        var comparison = await new RepositoryCopyReader(Git).ReadAsync(
            roots.Select(root => new RepositoryBookmark(root)).ToArray(), cancellation.Token);
        foreach (var copy in comparison.Copies)
        {
            Check(copy.Error.Length == 0, $"第 {copy.Number} 份实际仓库读取成功：{copy.Error}");
            Console.WriteLine($"第 {copy.Number} 份：{copy.Files.Count} 个文件");
        }
        Console.WriteLine($"实际仓库比较耗时 {watch.ElapsedMilliseconds} 毫秒；{comparison.FileStatus}");
    }

    private static void RunCopyReadWindowChecks(string[] roots)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Dark", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            RepositoryCopiesWindow? window = null;
            try
            {
                string directory = CheckDirectories.Create("copy-read-window");
                Directory.CreateDirectory(directory);
                var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
                settings.Value.Qwen.Enabled = false;
                settings.Value.Repositories = roots.ToList();
                settings.Value.RepositoryDiscovery.Enabled = false;
                using var model = new MainViewModel(settings);
                window = new RepositoryCopiesWindow(model, model.Repositories.ToArray())
                {
                    ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                var button = (Button)window.FindName("AnalyzeButton");
                var copies = (ItemsControl)window.FindName("CopiesList");
                var state = (TextBlock)window.FindName("StateText");
                window.Show();
                await Task.Delay(100);
                Check((string)button.Content == "取消比较" && button.IsEnabled, "实际仓库读取期间可取消比较");
                var cancelWatch = Stopwatch.StartNew();
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while ((string)button.Content != "重新分析" && cancelWatch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
                Check(window.Comparison is null && state.Text == "已取消比较" && copies.Items.Count == 0,
                    $"取消实际仓库读取成功，耗时 {cancelWatch.ElapsedMilliseconds} 毫秒");

                bool previewShown = false;
                long previewMs = 0;
                var watch = Stopwatch.StartNew();
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while ((window.Comparison is null || !button.IsEnabled) && watch.Elapsed < TimeSpan.FromSeconds(60))
                {
                    await Task.Delay(25);
                    if (!previewShown && window.Comparison is null && copies.Items.Count > 0)
                    {
                        previewShown = true;
                        previewMs = watch.ElapsedMilliseconds;
                    }
                    if (state.Text.StartsWith("比较失败：", StringComparison.Ordinal)) break;
                }
                Check(previewShown, $"文件比较结束前显示实际仓库信息，耗时 {previewMs} 毫秒");
                Check(window.Comparison is { } result && result.Copies.All(copy => copy.Error.Length == 0), "实际窗口比较成功");
                var comparison = window.Comparison!;
                Check(comparison.Copies.SelectMany(copy => copy.Files.Values).Any(file => file.Hash == RepositoryFileFingerprint.UniqueFile),
                    "单份存在的实际文件直接判断为缺失差异，不读取其内容");
                Console.WriteLine($"实际窗口比较耗时 {watch.ElapsedMilliseconds} 毫秒；{comparison.FileStatus}");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                string output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "repository-copies");
                Directory.CreateDirectory(output);
                SaveScreenshot(window, Path.Combine(output, "performance.png"));
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("仓库比较窗口检查失败。", failure);
    }
}
