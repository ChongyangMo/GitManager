using System.IO;
using GitManager.Services;
using GitManager.Services.CommitMessages;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunModelPolicyChecksAsync(string[] actualRepositories)
    {
        string root = Path.Combine(Path.GetTempPath(), "GitManagerModelPolicyChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await Git.InitializeAsync(root);
        var settings = new SettingsStore(Path.Combine(root, "settings.json"));
        settings.Value.ServiceCloud.BaseUrl = "https://sdserver.luxvisions-inno.com";
        var policy = new RepositoryModelPolicy(Git, settings);
        await Runner.RunAsync(root, ["remote", "add", "origin", "https://sdserver.luxvisions-inno.com/git/team/project.git"]);
        Check(await policy.LocalOnlyReasonAsync(root, default) is not null, "服务云作为 origin 时禁用模型");
        await Runner.RunAsync(root, ["remote", "set-url", "origin", "https://github.com/ChongyangMo/GitManager.git"]);
        Check(await policy.LocalOnlyReasonAsync(root, default) is null, "正常公网仓库继续允许模型");
        await Runner.RunAsync(root, ["remote", "add", "servicecloud", "https://github.com/ChongyangMo/GitManager.git"]);
        Check(await policy.LocalOnlyReasonAsync(root, default) is not null, "存在 servicecloud 远程时整个仓库使用本地规则");
        await Runner.RunAsync(root, ["remote", "remove", "servicecloud"]);
        await Runner.RunAsync(root, ["remote", "set-url", "--push", "origin", "ssh://git@10.18.32.67/team/project.git"]);
        Check(await policy.LocalOnlyReasonAsync(root, default) is not null, "公网拉取但内网推送的仓库禁用模型");
        await Runner.RunAsync(root, ["config", "--unset-all", "remote.origin.pushurl"]);
        await Runner.RunAsync(root, ["config", "url.ssh://git@10.18.32.67/.insteadOf", "https://github.com/"]);
        Check(await policy.LocalOnlyReasonAsync(root, default) is not null, "Git 地址重写到内网时禁用模型");
        await Runner.RunAsync(root, ["config", "--unset-all", "url.ssh://git@10.18.32.67/.insteadOf"]);
        foreach (string url in new[] { "git@192.168.50.2:team/project.git", "192.168.50.2:team/project.git", "ssh://git@[fd00::1]/project.git",
            "https://[::ffff:10.18.32.67]/project.git", @"C:\repositories\project.git", @"\\server\git\project.git", "git@gitserver:team/project.git" })
            Check(await InternalGitAddress.LocalOnlyReasonAsync(url, settings.Value.ServiceCloud.BaseUrl, default) is not null, "内网、局域网主机和本地路径禁用模型");
        Check(await InternalGitAddress.LocalOnlyReasonAsync("github.com:ChongyangMo/GitManager.git", "", default, settings.Value.Qwen.PublicGitHosts) is null,
            "不带账号的公网 SSH 地址正常识别");
        settings.Value.ServiceCloud.BaseUrl = "https://github.com";
        Check(await policy.LocalOnlyReasonAsync(root, default) is not null, "自定义服务云域名优先于公网域名判断");
        settings.Value.ServiceCloud.BaseUrl = "https://sdserver.luxvisions-inno.com";
        await Runner.RunAsync(root, ["remote", "set-url", "origin", "https://sdserver.luxvisions-inno.com/git/team/project.git"]);
        await File.WriteAllTextAsync(Path.Combine(root, "实际修改.txt"), "服务云仓库的实际未跟踪内容\n");
        var files = await Git.ReadChangesAsync(root);
        // 即使全局启用且模型配置不可用，内网仓库仍应在读取密钥之前直接生成本地说明。
        settings.Value.Qwen.Enabled = true;
        settings.Value.Qwen.BaseUrl = "";
        var generated = await new CommitMessageGenerator(Git, settings).GenerateAsync(root, files, default);
        var source = await new LocalCommitSummaryReader(Git).ReadAsync(root, files);
        Check(generated.Source == "本地规则" && generated.Message == LocalCommitSummary.Generate(source.Files),
            "服务云真实总结走本地规则，不依赖接口配置、密钥或模型缓存");
        foreach (string repository in actualRepositories)
        {
            string? reason = await policy.LocalOnlyReasonAsync(repository, default);
            Console.WriteLine($"实际仓库 {Path.GetFileName(repository)}：{(reason is null ? "允许按全局设置使用模型" : "仅本地规则，" + reason)}");
        }
        Console.WriteLine("仓库模型策略检查通过，未调用大模型接口。");
    }
}
