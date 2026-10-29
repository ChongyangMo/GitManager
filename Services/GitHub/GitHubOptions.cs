namespace GitManager.Services.GitHub;

public sealed class GitHubOptions
{
    public bool AutoCreateMissingRepository { get; set; } = true;
    public bool Private { get; set; } = true;
    public string ProxyUrl { get; set; } = "";
    // 仅用于 GitHub 仓库；为空时从 GitHub 登录账号取得关联邮箱。
    public string CommitEmail { get; set; } = "";
}
