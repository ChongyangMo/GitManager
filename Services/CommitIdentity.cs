using GitManager.Models;

namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    private static readonly SemaphoreSlim CommitEmailLock = new(1, 1);

    // 界面显示用：已设置统一邮箱时显示统一邮箱，否则显示仓库本地配置。
    private async Task<string> ReadCommitEmailAsync(string root)
    {
        if (GitHubOptions.CommitEmail.Trim() is { Length: > 0 } configured) return configured;
        var result = await Runner.RunAsync(root, ["config", "--local", "--get", "user.email"], allowFailure: true);
        if (result.ExitCode is not (0 or 1)) throw new InvalidOperationException("读取仓库提交邮箱失败：" + result.Error);
        return result.Output.Trim();
    }

    // 提交邮箱不再留空：优先使用设置中的统一邮箱，未设置时从 GitHub 登录账号取得并记住。
    public async Task<string> ResolveCommitEmailAsync(string? root, CancellationToken token = default)
    {
        if (GitHubOptions.CommitEmail.Trim() is { Length: > 0 } configured) return configured;
        await CommitEmailLock.WaitAsync(token);
        try
        {
            if (GitHubOptions.CommitEmail.Trim() is { Length: > 0 } resolved) return resolved;
            using var client = await CreateGitHubClientAsync(root, "https://github.com/",
                "提交邮箱需要与 GitHub 账号关联，请先通过 Git 凭据管理器登录 GitHub，或在设置中填写提交邮箱。", token);
            GitHubOptions.CommitEmail = await client.GetAccountEmailAsync(token);
            AppLog.Write("已从 GitHub 账号取得提交邮箱：" + GitHubOptions.CommitEmail);
            return GitHubOptions.CommitEmail;
        }
        finally { CommitEmailLock.Release(); }
    }

    private async Task<GitResult> RunWithCommitIdentityAsync(string root, IEnumerable<string> arguments, bool allowFailure = false)
    {
        string email = await ResolveCommitEmailAsync(root);
        return await Runner.RunAsync(root, new[] { "-c", "user.email=" + email }.Concat(arguments), allowFailure);
    }
}
