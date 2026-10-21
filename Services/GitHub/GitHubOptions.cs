namespace GitManager.Services.GitHub;

public sealed class GitHubOptions
{
    public bool AutoCreateMissingRepository { get; set; } = true;
    public bool Private { get; set; } = true;
    public string ProxyUrl { get; set; } = "";
}
