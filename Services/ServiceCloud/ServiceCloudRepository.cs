using GitManager.Models;

namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    public async Task<RemoteBranch> ConnectServiceCloudAsync(string root, string baseUrl, string username, string repositoryUrl, string proxyUrl = "")
    {
        var uri = ServiceCloud.ServiceCloudClient.NormalizeBaseUrl(baseUrl);
        ServiceCloud.ServiceCloudClient.ParseProxy(proxyUrl);
        ValidateRemote(repositoryUrl);
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repoUri) || !uri.IsBaseOf(repoUri)
            || !repoUri.AbsolutePath.StartsWith(uri.AbsolutePath + "git/", StringComparison.Ordinal) || !repoUri.AbsolutePath.EndsWith(".git", StringComparison.Ordinal))
            throw new InvalidOperationException("仓库地址不属于当前服务云。");
        string origin = await ReadConfigAsync(root, "remote.origin.url");
        string remote = origin.Length == 0 || origin == repositoryUrl ? "origin" : "servicecloud";
        var remotes = (await Run(root, "remote")).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string previous = await ReadConfigAsync(root, "remote." + remote + ".url");
        if (previous.Length > 0 && previous != repositoryUrl) throw new InvalidOperationException("此本地仓库已连接其他服务云仓库，请先检查远程地址。");
        await Runner.RunAsync(root, remotes.Contains(remote) ? ["remote", "set-url", remote, repositoryUrl] : ["remote", "add", remote, repositoryUrl]);
        string credentialKey = "credential." + uri.AbsoluteUri.TrimEnd('/');
        await Run(root, "config", "--local", credentialKey + ".username", username);
        await Run(root, "config", "--local", credentialKey + ".helper", "manager");
        await Run(root, "config", "--local", credentialKey + ".credentialStore", "wincredman");
        await Run(root, "config", "--local", "http." + uri.AbsoluteUri.TrimEnd('/') + ".proxy", proxyUrl.Trim());
        await Run(root, "config", "--local", "gitmanager.serviceCloudUrl", repositoryUrl);
        await FetchVersionsAsync(root, remote);
        string branch = (await Run(root, "symbolic-ref", "--short", "HEAD")).Output.Trim();
        AppLog.Write("已连接服务云仓库");
        return new(remote, branch);
    }
}
