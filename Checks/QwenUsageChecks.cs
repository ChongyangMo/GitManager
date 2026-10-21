using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using GitManager.Services;
using GitManager.Services.CommitMessages;
using GitManager.Services.CommitMessages.Usage;
using GitManager.Views.CommitMessages;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunQwenUsageChecks()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Dark", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            try
            {
                var options = new SettingsStore().Value.Qwen;
                string logs = Path.Combine(SettingsStore.DataDirectory, "logs");
                var first = QwenUsageStore.Default.Read(logs);
                Check(first.Entries.Count > 0 && first.UnreadableCount == 0, "已有真实调用日志导入用量记录");
                var second = new QwenUsageStore(QwenUsageStore.Default.DirectoryPath).Read(logs);
                Check(first.Entries.Select(entry => entry.Id).SequenceEqual(second.Entries.Select(entry => entry.Id)), "重复导入和重新读取不增加记录");
                var actual = first.Entries.First();
                var isolated = new QwenUsageStore(Path.Combine(Path.GetTempPath(), "GitManagerUsageChecks", Guid.NewGuid().ToString("N")));
                isolated.Save(actual); isolated.Save(actual);
                Check(isolated.Read().Entries.Count == 1, "同一次调用的重复保存保持唯一");
                Check(!JsonSerializer.Serialize(first.Entries).Contains("ApiKey", StringComparison.OrdinalIgnoreCase), "记录不包含密钥字段");
                var window = new QwenUsageWindow(options) { ShowInTaskbar = false, ShowActivated = false,
                    Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while ((!window.ViewModel.CanRefresh || window.ViewModel.Summary is null) && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
                Check(window.ViewModel.Summary is not null && window.ViewModel.Rows.Count == first.Entries.Count, "窗口显示实际累计调用次数与明细");
                if (first.Entries.Any(entry => entry.Cost.Minimum is null))
                    Check(window.ViewModel.Summary!.Cost.Contains("待确认"), "历史费用缺失时不把未知费用算作零");
                window.ViewModel.Period = "今天";
                Check(window.ViewModel.Rows.Count == first.Entries.Count(entry => entry.Time.ToLocalTime().Date == DateTime.Today), "今天筛选与实际调用时间一致");
                window.ViewModel.Period = "全部";
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "qwen-usage-dark.png"));
                ThemeService.Apply("Light");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "qwen-usage-light.png"));
                window.Close();
                Console.WriteLine($"统计检查通过，真实调用 {first.Entries.Count} 条，未调用模型接口。");
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("千问用量统计检查失败。", failure);
    }
}
