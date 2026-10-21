using System.IO;
using GitManager.Services;
using GitManager.Services.CommitMessages;
using System.Windows;
using System.Windows.Threading;
using GitManager.Views.CommitMessages;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RenderModelRoutingSettings(string screenshot)
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
                string note = await SummaryProviderOrder.DescribeAsync(settings.Value.Qwen, settings.Value.Claude, default, settings.Value.LuxAi);
                Check(note.Contains("内网：集团 AI") && note.Contains("外网：Claude 优先"), "真实设置同时显示内网和外网的模型顺位");
                Console.WriteLine(note);
                var window = new QwenSettingsWindow(settings.Value.Qwen, settings.Value.Claude, luxAi: () => settings.Value.LuxAi)
                {
                    ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Show();
                await Task.Delay(1500);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, screenshot);
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw failure;
    }

    private static async Task RunModelPolicyChecksAsync(string[] actualRepositories)
    {
        string root = Path.Combine(Path.GetTempPath(), "GitManagerModelPolicyChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await Git.InitializeAsync(root);
        var settings = new SettingsStore(Path.Combine(root, "settings.json"));
        settings.Value.ServiceCloud.BaseUrl = "https://sdserver.luxvisions-inno.com";
        settings.Value.Qwen.Enabled = true;
        settings.Value.LuxAi.Enabled = true;
        var policy = new RepositoryModelPolicy(Git, settings);
        var routing = new RepositorySummaryRouting(Git, settings);
        await Runner.RunAsync(root, ["remote", "add", "origin", "https://sdserver.luxvisions-inno.com/git/team/project.git"]);
        Check(await policy.ExternalModelBlockReasonAsync(root, default) is not null, "服务云作为 origin 时不调用公网模型");
        Check((await routing.ResolveAsync([root], default)).Target == SummaryModelTarget.GroupAi, "服务云仓库走集团 AI");
        await Runner.RunAsync(root, ["remote", "set-url", "origin", "https://github.com/ChongyangMo/GitManager.git"]);
        Check(await policy.ExternalModelBlockReasonAsync(root, default) is null, "正常公网仓库继续允许模型");
        Check((await routing.ResolveAsync([root], default)).Target == SummaryModelTarget.PublicModels, "启用集团 AI 后公网仓库仍走 Claude 和千问");
        await Runner.RunAsync(root, ["remote", "add", "servicecloud", "https://github.com/ChongyangMo/GitManager.git"]);
        Check(await policy.ExternalModelBlockReasonAsync(root, default) is not null, "存在 servicecloud 远程时整个仓库不调用公网模型");
        await Runner.RunAsync(root, ["remote", "remove", "servicecloud"]);
        await Runner.RunAsync(root, ["remote", "set-url", "--push", "origin", "ssh://git@10.18.32.67/team/project.git"]);
        Check(await policy.ExternalModelBlockReasonAsync(root, default) is not null, "公网拉取但内网推送的仓库不调用公网模型");
        Check((await routing.ResolveAsync([root], default)).Target == SummaryModelTarget.GroupAi, "混合内外网远程使用集团 AI");
        bool changedRouteBlocked = false;
        try { await routing.EnsureAsync([root], SummaryModelTarget.PublicModels, default); }
        catch (InvalidOperationException) { changedRouteBlocked = true; }
        Check(changedRouteBlocked, "公网调用前发现远程变成内网时阻止继续发送");
        await Runner.RunAsync(root, ["config", "--unset-all", "remote.origin.pushurl"]);
        await Runner.RunAsync(root, ["config", "url.ssh://git@10.18.32.67/.insteadOf", "https://github.com/"]);
        Check(await policy.ExternalModelBlockReasonAsync(root, default) is not null, "Git 地址重写到内网时不调用公网模型");
        await Runner.RunAsync(root, ["config", "--unset-all", "url.ssh://git@10.18.32.67/.insteadOf"]);
        foreach (string url in new[] { "git@192.168.50.2:team/project.git", "192.168.50.2:team/project.git", "ssh://git@[fd00::1]/project.git",
            "https://[::ffff:10.18.32.67]/project.git", @"C:\repositories\project.git", @"\\server\git\project.git", "git@gitserver:team/project.git" })
            Check(await InternalGitAddress.ExternalModelBlockReasonAsync(url, settings.Value.ServiceCloud.BaseUrl, default) is not null, "内网、局域网主机和本地路径不调用公网模型");
        Check(await InternalGitAddress.ExternalModelBlockReasonAsync("github.com:ChongyangMo/GitManager.git", "", default, settings.Value.Qwen.PublicGitHosts) is null,
            "不带账号的公网 SSH 地址正常识别");
        settings.Value.ServiceCloud.BaseUrl = "https://github.com";
        Check(await policy.ExternalModelBlockReasonAsync(root, default) is not null, "自定义服务云域名优先于公网域名判断");
        settings.Value.ServiceCloud.BaseUrl = "https://sdserver.luxvisions-inno.com";
        await Runner.RunAsync(root, ["remote", "set-url", "origin", "https://sdserver.luxvisions-inno.com/git/team/project.git"]);
        await File.WriteAllTextAsync(Path.Combine(root, "实际修改.txt"), "服务云仓库的实际未跟踪内容\n");
        var files = await Git.ReadChangesAsync(root);
        // 即使全局启用且模型配置不可用，内网仓库仍应在读取密钥之前直接生成本地说明。
        settings.Value.Qwen.Enabled = true;
        settings.Value.LuxAi.Enabled = false;
        settings.Value.Qwen.BaseUrl = "";
        var generated = await new CommitMessageGenerator(Git, settings).GenerateAsync(root, files, default);
        var source = await new LocalCommitSummaryReader(Git).ReadAsync(root, files);
        Check(generated.Source == "本地规则" && generated.Message == LocalCommitSummary.Generate(source.Files),
            "服务云真实总结走本地规则，不依赖接口配置、密钥或模型缓存");
        settings.Value.LuxAi.Enabled = true;
        string groupBase = settings.Value.LuxAi.BaseUrl;
        settings.Value.LuxAi.BaseUrl = "";
        bool groupFailureStopped = false;
        try { await new CommitMessageGenerator(Git, settings).GenerateAsync(root, files, default); }
        catch (InvalidOperationException ex) { groupFailureStopped = ex.Message.Contains("集团 AI"); }
        Check(groupFailureStopped, "集团 AI 配置异常时停止，不转发内网改动给公网模型");
        settings.Value.LuxAi.BaseUrl = groupBase;
        settings.Value.Qwen.Enabled = false;
        Check((await routing.ResolveAsync([root], default)).Target == SummaryModelTarget.LocalRules, "全局关闭模型时内网仓库使用本地规则");
        settings.Value.Qwen.Enabled = true;
        foreach (string repository in actualRepositories)
        {
            var actualRoute = await routing.ResolveAsync([repository], default);
            Console.WriteLine($"实际仓库 {Path.GetFileName(repository)}：{actualRoute.Target}，{actualRoute.Reason}");
        }
        Console.WriteLine("仓库模型策略检查通过，未调用大模型接口。");
    }
}
