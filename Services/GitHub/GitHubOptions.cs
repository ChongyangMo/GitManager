namespace GitManager.Services.GitHub;

public sealed class GitHubOptions
{
    public bool AutoCreateMissingRepository { get; set; } = true;
    public bool Private { get; set; } = true;
    public string ProxyUrl { get; set; } = "";
    // 所有仓库统一使用的提交邮箱；为空时从 GitHub 登录账号取得关联的 noreply 邮箱。
    public string CommitEmail { get; set; } = "";
}
