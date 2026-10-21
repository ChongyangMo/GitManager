using GitManager.Services;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunGitHubRemoteChecksAsync(string root, string remote)
    {
        var settings = new SettingsStore();
        var git = new GitRepositoryService(Runner, settings.Value.GitHub);
        string before = (await Runner.RunAsync(root, ["rev-parse", "HEAD"])).Output.Trim();
        string url = (await Runner.RunAsync(root, ["remote", "get-url", remote])).Output.Trim();
        Check(Services.GitHub.GitHubRepositoryAddress.Parse(url) is not null, "使用真实 GitHub 仓库地址验证");
        await git.FetchVersionsAsync(root, remote, createMissing: true);
        var remoteRefs = await Runner.RunAsync(root, ["ls-remote", remote]);
        Check(remoteRefs.ExitCode == 0, "远程仓库已存在且可以通过 Git 访问");
        Check((await Runner.RunAsync(root, ["rev-parse", "HEAD"])).Output.Trim() == before, "自动建仓和获取未修改本地提交");
        Check((await Runner.RunAsync(root, ["remote", "get-url", remote])).Output.Trim() == url, "保留原远程仓库地址");
        Console.WriteLine("GitHub 真实远程检查完成：" + url);
    }
}
