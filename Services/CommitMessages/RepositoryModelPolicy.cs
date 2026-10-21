namespace GitManager.Services.CommitMessages;

public sealed class RepositoryModelPolicy(GitRepositoryService git, SettingsStore settings)
{
    public async Task<string?> ExternalModelBlockReasonAsync(string root, CancellationToken token)
    {
        try
        {
            var remotes = (await git.Runner.RunAsync(root, ["remote"], cancellationToken: token)).Output
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (string remote in remotes)
            {
                if (remote.Equals("servicecloud", StringComparison.OrdinalIgnoreCase)) return "服务云远程";
                foreach (bool push in new[] { false, true })
                {
                    // get-url 会应用 Git 的 insteadOf / pushInsteadOf，检查实际生效的全部地址。
                    string[] arguments = push ? ["remote", "get-url", "--push", "--all", remote] : ["remote", "get-url", "--all", remote];
                    var urls = (await git.Runner.RunAsync(root, arguments, cancellationToken: token)).Output
                        .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (urls.Length == 0) return "远程地址为空";
                    foreach (string url in urls)
                    {
                        string? reason = await InternalGitAddress.ExternalModelBlockReasonAsync(url, settings.Value.ServiceCloud.BaseUrl, token, settings.Value.Qwen.PublicGitHosts);
                        if (reason is not null) return reason;
                    }
                }
            }
            return null;
        }
        catch (InvalidOperationException) { return "无法读取仓库远程配置"; }
    }
}
