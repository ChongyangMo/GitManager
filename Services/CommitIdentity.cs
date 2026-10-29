using GitManager.Models;

namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    private static readonly SemaphoreSlim CommitEmailLock = new(1, 1);

    private static bool IsGitHubEmail(string email) => email.EndsWith("@users.noreply.github.com", StringComparison.OrdinalIgnoreCase)
        || email.EndsWith("@github.com", StringComparison.OrdinalIgnoreCase);

    private async Task<RepositoryCommitIdentity> ReadCommitIdentityAsync(string root)
    {
        string name = await ReadConfigAsync(root, "user.name"), email = await ReadConfigAsync(root, "user.email");
        var destination = await ReadCommitDestinationAsync(root);
        if (destination.GitHub) return new(name, GitHubOptions.CommitEmail.Trim() is { Length: > 0 } configured ? configured : email);
        var local = await Runner.RunAsync(root, ["config", "--local", "--get", "user.email"], true);
        if (local.ExitCode is not (0 or 1)) throw new InvalidOperationException("读取仓库提交邮箱失败：" + local.Error);
        if (local.Output.Trim() is { Length: > 0 } explicitEmail && !IsGitHubEmail(explicitEmail)) return new(name, explicitEmail);
        if (destination.ServiceCloud && await ReadCloudIdentityCacheAsync(root) is { } cached) return cached;
        return new(name, destination.HasRemote && IsGitHubEmail(email) ? "" : email);
    }

    public async Task<bool> UsesGitHubCommitIdentityAsync(string root, string? remote = null) => (await ReadCommitDestinationAsync(root, remote)).GitHub;

    public async Task<RepositoryCommitIdentity> ResolveCommitIdentityAsync(string root, string? remote = null, CancellationToken token = default)
    {
        var destination = await ReadCommitDestinationAsync(root, remote, token);
        string name = await ReadConfigAsync(root, "user.name");
        if (destination.GitHub) return RequireIdentity(new(name, await ResolveGitHubEmailAsync(root, token)));
        var local = await Runner.RunAsync(root, ["config", "--local", "--get", "user.email"], true, token);
        if (local.ExitCode is not (0 or 1)) throw new InvalidOperationException("读取仓库提交邮箱失败：" + local.Error);
        string localEmail = local.Output.Trim();
        if (localEmail.Length > 0 && !IsGitHubEmail(localEmail)) return RequireIdentity(new(name, localEmail));
        if (destination.ServiceCloud) return await ResolveCloudCommitIdentityAsync(root, token);
        string email = await ReadConfigAsync(root, "user.email");
        if (destination.HasRemote && IsGitHubEmail(email))
            throw new InvalidOperationException("非 GitHub 仓库不能使用 GitHub 邮箱，请在设置中填写当前仓库的提交邮箱。");
        return RequireIdentity(new(name, email));
    }

    private static RepositoryCommitIdentity RequireIdentity(RepositoryCommitIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Name) || string.IsNullOrWhiteSpace(identity.Email))
            throw new InvalidOperationException("请在设置中填写当前仓库的提交姓名和邮箱。");
        return identity;
    }

    public async Task<string> ResolveCommitEmailAsync(string? root, CancellationToken token = default) => root is null
        ? await ResolveGitHubEmailAsync(null, token) : (await ResolveCommitIdentityAsync(root, token: token)).Email;

    private async Task<string> ResolveGitHubEmailAsync(string? root, CancellationToken token)
    {
        if (GitHubOptions.CommitEmail.Trim() is { Length: > 0 } configured) return configured;
        await CommitEmailLock.WaitAsync(token);
        try
        {
            if (GitHubOptions.CommitEmail.Trim() is { Length: > 0 } resolved) return resolved;
            using var client = await CreateGitHubClientAsync(root, "https://github.com/",
                "请通过 Git 凭据管理器登录 GitHub，或在设置中填写 GitHub 仓库的提交邮箱。", token);
            GitHubOptions.CommitEmail = await client.GetAccountEmailAsync(token);
            AppLog.Write("已取得 GitHub 账号的提交邮箱");
            return GitHubOptions.CommitEmail;
        }
        finally { CommitEmailLock.Release(); }
    }

    private async Task<GitResult> RunWithCommitIdentityAsync(string root, IEnumerable<string> arguments, bool allowFailure = false, string? remote = null)
    {
        var identity = await ResolveCommitIdentityAsync(root, remote);
        return await Runner.RunAsync(root, new[] { "-c", "user.name=" + identity.Name, "-c", "user.email=" + identity.Email }.Concat(arguments), allowFailure);
    }
}

public sealed record RepositoryCommitIdentity(string Name, string Email);
