using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using GitManager.Services;
using GitManager.Services.CommitMessages;
using GitManager.Services.LuxAi;
using System.Windows;
using System.Windows.Threading;
using GitManager.Views.CommitMessages;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task InspectLuxAiSessionAsync()
    {
        var options = new SettingsStore().Value.LuxAi;
        var session = LuxAiCredentials.Read(options) ?? throw new LuxAiLoginRequiredException();
        foreach (var (name, value) in new[] { ("AI", session.AiToken), ("续期", session.RefreshToken) })
        {
            var parts = value.Split('.');
            if (parts.Length != 3) { Console.WriteLine(name + "凭据没有 JWT 到期信息"); continue; }
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            foreach (string claim in new[] { "iat", "nbf", "exp" })
                if (json.RootElement.TryGetProperty(claim, out var time) && time.TryGetInt64(out long seconds))
                    Console.WriteLine($"{name} {claim}：{DateTimeOffset.FromUnixTimeSeconds(seconds):O}");
        }
        Console.WriteLine("当前需要续期：" + LuxAiAuthentication.NeedsRenewal(session));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var client = new LuxAiClient(options);
        await client.ValidateSessionAsync(session, timeout.Token);
        Console.WriteLine("已保存的 AI 会话验证通过");
    }

    private static async Task RunLuxAiMaintenanceAsync()
    {
        var options = new SettingsStore().Value.LuxAi;
        var before = LuxAiCredentials.Read(options) ?? throw new LuxAiLoginRequiredException();
        bool due = LuxAiAuthentication.NeedsRenewal(before);
        var maintenance = new LuxAiSessionMaintenance(() => options);
        try
        {
            // 使用本机真实凭据和真实接口，检查后台处理及取消，不创建测试登录数据。
            await Task.Delay(TimeSpan.FromSeconds(3));
            Check(!maintenance.Completion.IsCompleted, "后台维护完成本轮检查后继续等待，不因凭据失效退出");
        }
        finally { maintenance.Dispose(); }
        await maintenance.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(maintenance.Completion.IsCompletedSuccessfully, "后台维护可及时取消并正常停止");
        var after = LuxAiCredentials.Read(options);
        Check(after is not null, "后台维护保留登录凭据");
        if (after != before)
            Check(after!.RenewedAt > before.RenewedAt || before.RenewedAt is null && after!.RenewedAt is not null,
                "真实后台续期更新保存时间");
        Console.WriteLine("本轮开始时需要续期：" + due);
        Console.WriteLine("本轮会话已更新：" + (after != before));
    }

    private static void RenderLuxAiSettings(string screenshot)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Dark", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            try
            {
                var settings = new SettingsStore();
                ThemeService.Apply(settings.Value.Theme);
                var window = new LuxAiSettingsWindow(settings.Value.LuxAi) { ShowInTaskbar = false, ShowActivated = false,
                    Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(window.Options.Enabled && LuxAiCredentials.Read(window.Options) is not null, "设置窗口读取真实启用状态和已保存的登录会话");
                SaveScreenshot(window, screenshot);
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw failure;
    }

    private static async Task RunLuxAiSessionAsync()
    {
        var settings = new SettingsStore();
        var options = settings.Value.LuxAi;
        var before = LuxAiCredentials.Read(options) ?? throw new LuxAiLoginRequiredException();
        Console.WriteLine("集团 AI 已启用：" + options.Enabled);
        Console.WriteLine("配置文件：" + settings.FilePath);
        Check(before.RefreshToken.Length > 0, "真实登录提供了独立续期凭据");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = new LuxAiClient(options);
        var renewed = await client.EnsureSessionAsync(timeout.Token, renew: true);
        await client.ValidateSessionAsync(renewed, timeout.Token);
        Check(LuxAiCredentials.Read(options)?.AiToken == renewed.AiToken, "续期后的会话已保存且通过实际接口验证");
        string report = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "GitManager集团AI登录验证.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            Time = DateTimeOffset.Now, Enabled = options.Enabled, LoginVerified = true, RenewalVerified = true,
            EmployeeCode = renewed.EmployeeCode, CredentialStorage = "Windows 凭据管理器", PasswordStored = false
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private static async Task RunLuxAiLiveAsync(string root, string[] paths)
    {
        var current = new SettingsStore();
        var options = current.Value.LuxAi;
        _ = options.Endpoint();
        if (LuxAiCredentials.Read(options) is null) throw new LuxAiLoginRequiredException();
        var selected = (await Git.ReadChangesAsync(root)).Where(file => paths.Contains(file.Path, StringComparer.Ordinal)).ToArray();
        if (selected.Length != paths.Length) throw new InvalidOperationException("请指定实际存在未提交改动的文件。");
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "work", "luxai-live-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        settings.Value.Qwen.Enabled = true;
        settings.Value.LuxAi = options;
        settings.Value.LuxAi.Enabled = true;
        string head = (await Runner.RunAsync(root, ["rev-parse", "HEAD"])).Output;
        string index = (await Runner.RunAsync(root, ["diff", "--cached", "--raw", "-z"])).Output;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds + 10));
        var result = await new CommitMessageGenerator(Git, settings).GenerateAsync(root, selected, timeout.Token);
        Check(result.Source == "集团 AI" && result.Message.Length > 0, "集团 AI 经真实总结流程返回结果");
        Check(head == (await Runner.RunAsync(root, ["rev-parse", "HEAD"])).Output
            && index == (await Runner.RunAsync(root, ["diff", "--cached", "--raw", "-z"])).Output, "总结没有创建提交或改变暂存区");
        string report = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "GitManager集团AI实测.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            Time = DateTimeOffset.Now, Repository = root, Files = selected.Select(file => file.Path), result.Source,
            result.Message, result.Fingerprint, Cost = result.Cost.Detail, Model = (string?)null, HeadUnchanged = true, IndexUnchanged = true
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine(result.Message);
        Console.WriteLine("真实调用结果已保存：" + report);
    }
}
