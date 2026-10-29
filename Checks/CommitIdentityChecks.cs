using GitManager.Services;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunCommitIdentityChecksAsync(string source)
    {
        var settings = new SettingsStore();
        var git = new GitRepositoryService(Runner, settings.Value.GitHub, () => settings.Value.ServiceCloud);
        await CheckActualIdentityAsync(git, source);
    }

    private static async Task RunCommitIdentityHostChecksAsync(string github, string cloud)
    {
        var settings = new SettingsStore();
        var git = new GitRepositoryService(Runner, settings.Value.GitHub, () => settings.Value.ServiceCloud);
        string global = (await Runner.RunAsync(null, ["config", "--global", "--get", "user.email"], true)).Output;
        Check(await git.UsesGitHubCommitIdentityAsync(github), "真实 GitHub 推送地址使用 GitHub 身份");
        Check(!await git.UsesGitHubCommitIdentityAsync(cloud), "真实服务云推送地址使用非 GitHub 身份");
        var githubIdentity = await CheckActualIdentityAsync(git, github);
        var cloudIdentity = await CheckActualIdentityAsync(git, cloud);
        Check(githubIdentity.Email == settings.Value.GitHub.CommitEmail, "GitHub 提交邮箱保持原设置");
        Check(!cloudIdentity.Email.EndsWith("@users.noreply.github.com", StringComparison.OrdinalIgnoreCase), "服务云不使用 GitHub 邮箱");
        Check((await Runner.RunAsync(null, ["config", "--global", "--get", "user.email"], true)).Output == global, "全局 Git 邮箱保持一致");
        Console.WriteLine("按远程服务选择提交身份的检查通过。");
    }

    private static async Task<RepositoryCommitIdentity> CheckActualIdentityAsync(GitRepositoryService git, string root)
    {
        string head = (await Runner.RunAsync(root, ["rev-parse", "HEAD"])).Output;
        string localEmail = (await Runner.RunAsync(root, ["config", "--local", "--get", "user.email"], true)).Output;
        var identity = await git.ResolveCommitIdentityAsync(root);
        foreach (string variable in new[] { "GIT_AUTHOR_IDENT", "GIT_COMMITTER_IDENT" })
        {
            string effective = (await Runner.RunAsync(root, ["-c", "user.name=" + identity.Name, "-c", "user.email=" + identity.Email, "var", variable])).Output;
            Check(effective.Contains("<" + identity.Email + ">", StringComparison.Ordinal), "Git 实际身份使用对应服务的邮箱");
        }
        Check((await Runner.RunAsync(root, ["rev-parse", "HEAD"])).Output == head, "真实仓库提交哈希保持一致");
        Check((await Runner.RunAsync(root, ["config", "--local", "--get", "user.email"], true)).Output == localEmail, "原仓库 Git 邮箱配置保持一致");
        Console.WriteLine("实际提交身份：" + identity.Name + " <" + identity.Email + ">");
        return identity;
    }
}
