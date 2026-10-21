using GitManager.Models;

namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    private async Task<string> ReadCommitEmailAsync(string root)
    {
        var result = await Runner.RunAsync(root, ["config", "--local", "--get", "user.email"], allowFailure: true);
        if (result.ExitCode is not (0 or 1)) throw new InvalidOperationException("读取仓库提交邮箱失败：" + result.Error);
        return result.Output.Trim();
    }

    private async Task<GitResult> RunWithCommitIdentityAsync(string root, IEnumerable<string> arguments, bool allowFailure = false)
    {
        string email = await ReadCommitEmailAsync(root);
        return await Runner.RunAsync(root, new[] { "-c", "user.email=" + email }.Concat(arguments), allowFailure);
    }
}
