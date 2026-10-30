namespace GitManager.Services;

public sealed class GitExecutionOptions
{
    public int ReadTimeoutSeconds { get; set; } = 180;
    public int WriteTimeoutSeconds { get; set; } = 7200;

    private static readonly HashSet<string> LongOperations = new(StringComparer.Ordinal)
    {
        "add", "commit", "clone", "fetch", "push", "pull", "merge", "checkout", "restore",
        "switch", "reset", "stash", "rebase", "cherry-pick", "revert", "lfs"
    };

    public TimeSpan TimeoutFor(IReadOnlyList<string> arguments)
    {
        string command = "";
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument is "-c" or "-C" or "--git-dir" or "--work-tree" or "--namespace") { index++; continue; }
            if (argument.StartsWith('-')) continue;
            command = argument;
            break;
        }
        // LFS 会在暂存、提交及工作区更新时运行，不能只延长网络命令的等待时间。
        int seconds = LongOperations.Contains(command) ? WriteTimeoutSeconds : ReadTimeoutSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 86400));
    }
}
