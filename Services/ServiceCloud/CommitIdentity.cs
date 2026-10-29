namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    private static readonly SemaphoreSlim CloudIdentityLock = new(1, 1);
    private string CloudIdentityKey() => CloudOptions().BaseUrl.TrimEnd('/') + "|" + CloudOptions().Username.Trim();

    private async Task<RepositoryCommitIdentity?> ReadCloudIdentityCacheAsync(string root)
    {
        if (await ReadConfigAsync(root, "gitmanager.cloudIdentityKey") != CloudIdentityKey()) return null;
        string name = await ReadConfigAsync(root, "gitmanager.cloudIdentityName"), email = await ReadConfigAsync(root, "gitmanager.cloudIdentityEmail");
        return name.Length > 0 && email.Length > 0 && !IsGitHubEmail(email) ? new(name, email) : null;
    }

    private async Task<RepositoryCommitIdentity> ResolveCloudCommitIdentityAsync(string root, CancellationToken token)
    {
        if (await ReadCloudIdentityCacheAsync(root) is { } cached) return cached;
        await CloudIdentityLock.WaitAsync(token);
        try
        {
            if (await ReadCloudIdentityCacheAsync(root) is { } existing) return existing;
            var options = CloudOptions();
            if (string.IsNullOrWhiteSpace(options.BaseUrl) || string.IsNullOrWhiteSpace(options.Username))
                throw new InvalidOperationException("请先连接服务云账号，或在设置中填写当前仓库的公司邮箱。");
            string key = CloudIdentityKey();
            string? password = await new ServiceCloud.ServiceCloudCredentials(Runner).ReadPasswordAsync(options.BaseUrl, options.Username);
            if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("未找到服务云登录凭据，请重新连接服务云账号。");
            using var client = new ServiceCloud.ServiceCloudClient(options.BaseUrl, options.ProxyUrl);
            await client.LoginAsync(options.Username, password, token);
            var identity = RequireIdentity(await client.ReadCommitIdentityAsync(token));
            if (IsGitHubEmail(identity.Email)) throw new InvalidOperationException("服务云账号尚未登记公司邮箱，请先补齐账号邮箱。");
            await Run(root, "config", "--local", "gitmanager.cloudIdentityName", identity.Name);
            await Run(root, "config", "--local", "gitmanager.cloudIdentityEmail", identity.Email);
            await Run(root, "config", "--local", "gitmanager.cloudIdentityKey", key);
            AppLog.Write("已关联服务云工号的提交身份");
            return identity;
        }
        finally { CloudIdentityLock.Release(); }
    }
}
