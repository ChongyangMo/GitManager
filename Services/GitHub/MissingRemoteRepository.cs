namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    private async Task<bool> TryCreateMissingGitHubRepositoryAsync(string root, string remote, string failure, CancellationToken token)
    {
        if (!GitHubOptions.AutoCreateMissingRepository || !failure.Contains("repository not found", StringComparison.OrdinalIgnoreCase)) return false;
        string[] fetchUrls = (await Runner.RunAsync(root, ["remote", "get-url", "--all", remote], cancellationToken: token)).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var address = fetchUrls.Length == 1 ? GitHub.GitHubRepositoryAddress.Parse(fetchUrls[0]) : null;
        if (address is null) return false;
        string[] pushUrls = (await Runner.RunAsync(root, ["remote", "get-url", "--push", "--all", remote], cancellationToken: token)).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pushUrls.Length != 1 || !string.Equals(GitHub.GitHubRepositoryAddress.Parse(pushUrls[0])?.FullName, address.FullName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("获取和推送地址不一致，无法自动创建远程仓库，请先检查远程设置。");
        await EnsureNoOperationAsync(root);
        var head = await Runner.RunAsync(root, ["rev-parse", "--verify", "HEAD"], true, token);
        var branch = await Runner.RunAsync(root, ["symbolic-ref", "--quiet", "HEAD"], true, token);
        if (head.ExitCode != 0 || branch.ExitCode != 0)
            throw new InvalidOperationException("请先在本地分支提交代码，再自动创建并推送远程仓库。");

        using var client = await CreateGitHubClientAsync(root, address, "自动创建 GitHub 仓库需要登录授权，请先通过 Git 凭据管理器登录具有创建仓库权限的账号。", token);
        await client.EnsureExistsAsync(address, GitHubOptions.Private, token);
        return true;
    }

    private Task<GitHub.GitHubRepositoryClient> CreateGitHubClientAsync(string? root, GitHub.GitHubRepositoryAddress address, string missingCredential, CancellationToken token)
        => CreateGitHubClientAsync(root, address.HttpsUrl, missingCredential, token);

    private async Task<GitHub.GitHubRepositoryClient> CreateGitHubClientAsync(string? root, string httpsUrl, string missingCredential, CancellationToken token)
    {
        // 复用 Git 凭据管理器，令牌只在内存中使用，不写入配置或日志。
        var credential = await Runner.RunAsync(root, ["-c", "credential.interactive=false", "credential", "fill"], true, token,
            standardInput: "url=" + httpsUrl + "\n\n");
        string? secret = credential.Output.Split('\n').FirstOrDefault(line => line.StartsWith("password=", StringComparison.Ordinal))?[9..].TrimEnd('\r');
        if (credential.ExitCode != 0 || string.IsNullOrEmpty(secret))
            throw new InvalidOperationException(missingCredential);
        string proxy = GitHubOptions.ProxyUrl;
        if (string.IsNullOrWhiteSpace(proxy))
        {
            var configured = await Runner.RunAsync(root, ["config", "--get-urlmatch", "http.proxy", "https://api.github.com/"], true, token);
            if (configured.ExitCode == 1) configured = await Runner.RunAsync(root, ["config", "--get-urlmatch", "http.proxy", httpsUrl], true, token);
            if (configured.ExitCode is not (0 or 1)) throw new InvalidOperationException("读取 Git 代理配置失败，请检查仓库设置。");
            proxy = configured.Output.Trim();
        }
        return new GitHub.GitHubRepositoryClient(secret, proxy);
    }
}
