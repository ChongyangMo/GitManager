namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    private const string VisibilityCredentialMissing = "读取 GitHub 可见性需要登录授权，请先通过 Git 凭据管理器登录 GitHub 账号。";

    public async Task<GitHub.GitHubRepositoryVisibility> ReadGitHubVisibilityAsync(string root, GitHub.GitHubRepositoryAddress address, CancellationToken token = default)
    {
        using var client = await CreateGitHubClientAsync(root, address, VisibilityCredentialMissing, token);
        return await client.GetVisibilityAsync(address, token);
    }

    public async Task<GitHub.GitHubRepositoryVisibility> SetGitHubVisibilityAsync(string root, GitHub.GitHubRepositoryAddress address, bool isPrivate, CancellationToken token = default)
    {
        using var client = await CreateGitHubClientAsync(root, address, VisibilityCredentialMissing, token);
        return await client.SetVisibilityAsync(address, isPrivate, token);
    }
}
