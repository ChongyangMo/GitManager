using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitManager.Services;
using GitManager.Services.ServiceCloud;
using GitManager.Views.ServiceCloud;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunServiceCloudChecksAsync(string credentialFile, string root, string? repositoryName)
    {
        string configuration = credentialFile == "-" ? "" : await File.ReadAllTextAsync(credentialFile);
        string Read(string name) => Regex.Match(configuration, "NETDISK_E2E_" + name + "=([^\"\\r\\n]*)").Groups[1].Value;
        var settings = new SettingsStore();
        using var cloud = new ServiceCloudClient(settings.Value.ServiceCloud.BaseUrl, settings.Value.ServiceCloud.ProxyUrl);
        string password = Read("PASSWORD");
        string username = Read("USERNAME");
        if (credentialFile == "-")
        {
            using var input = JsonDocument.Parse(await Console.In.ReadToEndAsync());
            username = input.RootElement.GetProperty("username").GetString()!;
            password = input.RootElement.GetProperty("password").GetString()!;
        }
        await cloud.LoginAsync(username, password);
        var repositories = await cloud.ListRepositoriesAsync();
        Check(repositories.Count > 0, $"实际服务云账号已登录，读取 {repositories.Count} 个仓库");
        Check(cloud.RepositoryUrl("AA/仓库 空格").Contains("AA/%E4%BB%93%E5%BA%93%20%E7%A9%BA%E6%A0%BC.git", StringComparison.Ordinal), "目录及中文仓库地址正确编码");
        foreach (string invalid in new[] { "../outside", "/outside", "AA//outside", "AA/../outside", "https://other.invalid/repo" })
        {
            bool rejected = false;
            try { cloud.RepositoryUrl(invalid); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "拒绝无效的仓库路径");
        }
        bool rejectedHttp = false;
        try { ServiceCloudClient.NormalizeBaseUrl("http://example.invalid"); } catch (InvalidOperationException) { rejectedHttp = true; }
        Check(rejectedHttp, "服务云账号仅通过 HTTPS 传输");
        if (repositoryName is null) return;

        var credentials = new ServiceCloudCredentials(Runner);
        await credentials.SaveAsync(cloud.BaseUri.AbsoluteUri, username, password);
        Check(await credentials.ReadPasswordAsync(cloud.BaseUri.AbsoluteUri, username) == password, "Windows 凭据保存与读取成功");
        if (!repositories.Any(repo => repo.Name == repositoryName))
            Check(await cloud.CreateRepositoryAsync(repositoryName) == repositoryName, "已建立实际服务云仓库");
        string url = cloud.RepositoryUrl(repositoryName);
        var target = await Git.ConnectServiceCloudAsync(root, cloud.BaseUri.AbsoluteUri, username, url, settings.Value.ServiceCloud.ProxyUrl);
        await Git.SynchronizeVersionsAsync(root, target, "sync", fetch: false);
        var state = await Git.ReadVersionsAsync(root, target);
        Check(state.LocalHead is not null && state.LocalHead.Hash == state.RemoteHead?.Hash && state.Ahead == 0 && state.Behind == 0, "实际服务云与本地提交完全一致");
        Check(state.SourceLabel == "服务云", "远程来源显示服务云");
        string advertised = (await Runner.RunAsync(root, ["ls-remote", target.RemoteName, "refs/heads/" + target.Name])).Output;
        Check(advertised.StartsWith(state.LocalHead!.Hash + "\t", StringComparison.Ordinal), "服务端实际分支哈希与本地一致");
        string clone = Path.Combine(Path.GetDirectoryName(root)!, "GitManager", "artifacts", "servicecloud-clone-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        await Git.CloneAsync(url, clone, settings.Value.ServiceCloud.ProxyUrl, username);
        await Git.ConnectServiceCloudAsync(clone, cloud.BaseUri.AbsoluteUri, username, url, settings.Value.ServiceCloud.ProxyUrl);
        var cloned = await Git.ReadVersionsAsync(clone);
        Check(cloned.LocalHead?.Hash == state.LocalHead.Hash, "实际服务云仓库可克隆，提交哈希一致");
        await Git.SynchronizeVersionsAsync(clone, null, "pull");
        Check((await Git.ReadVersionsAsync(clone)).LocalHead?.Hash == state.LocalHead.Hash, "实际服务云拉取通过");
        settings.Value.ServiceCloud = new() { BaseUrl = cloud.BaseUri.AbsoluteUri.TrimEnd('/'), Username = username, ProxyUrl = settings.Value.ServiceCloud.ProxyUrl };
        if (!settings.Value.Repositories.Contains(root, StringComparer.OrdinalIgnoreCase)) settings.Value.Repositories.Add(root);
        settings.Value.LastRepository = root;
        settings.Save();
        Console.WriteLine($"服务云同步完成：{url}，提交 {state.LocalHead.ShortHash}");
    }

    private static void RenderServiceCloud(string root, string screenshot)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "Light", "Controls", "Workspace", "VersionTemplates" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/GitManager;component/Themes/{theme}.xaml", UriKind.Relative) });
        var window = new ServiceCloudWindow(new SettingsStore(), root)
        { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
        bool started = false;
        int result = 0;
        window.Loaded += async (_, _) =>
        {
            if (started) return;
            started = true;
            try
            {
                var list = (ListBox)window.FindName("RepositoryList");
                var status = (TextBlock)window.FindName("StatusText");
                var timeout = DateTime.UtcNow.AddSeconds(40);
                while (list.Items.Count == 0 && DateTime.UtcNow < timeout) await Task.Delay(100);
                Check(list.Items.Count > 0 && list.IsEnabled, "服务云窗口通过已保存账号加载真实仓库");
                list.SelectedIndex = 0;
                Check(((Button)window.FindName("ConnectButton")).IsEnabled, "选择实际仓库后可连接同步");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, screenshot);
                ((Button)window.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (!list.IsEnabled && DateTime.UtcNow < timeout) await Task.Delay(100);
                Check(list.Items.Count > 0 && list.IsEnabled, "服务云窗口刷新真实列表成功");
                ThemeService.Apply("Dark");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, Path.ChangeExtension(screenshot, "dark.png"));
                Console.WriteLine("服务云界面检查通过：" + status.Text);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { window.Close(); app.Shutdown(); }
        };
        app.Run(window);
        if (result != 0) throw new InvalidOperationException("服务云界面检查失败。");
    }
}
