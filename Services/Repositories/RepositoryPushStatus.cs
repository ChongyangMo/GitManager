namespace GitManager.Services.Repositories;

// 只比较本地提交与已获取的远程引用，不联网，不读取工作区文件及整段提交说明。
public static class RepositoryPushStatus
{
    public static async Task<int?> ReadAsync(GitRepositoryService git, string root)
    {
        var branchTask = git.Runner.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], true);
        var configTask = git.Runner.RunAsync(root, ["config", "--get-regexp", @"^(branch\..*\.(remote|merge)|remote\..*\.url)$"], true);
        await Task.WhenAll(branchTask, configTask);
        var branch = await branchTask;
        var config = await configTask;
        if (branch.ExitCode == 1) return null;
        if (branch.ExitCode != 0 || config.ExitCode is not (0 or 1))
            throw new InvalidOperationException("读取仓库推送状态失败：" + branch.Error + config.Error);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in config.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf(' ');
            if (separator > 0) values[line[..separator]] = line[(separator + 1)..].TrimEnd('\r');
        }
        string name = branch.Output.Trim();
        string upstream = values.GetValueOrDefault("branch." + name + ".remote", "");
        string[] remotes = values.Keys.Where(key => key.StartsWith("remote.", StringComparison.Ordinal) && key.EndsWith(".url", StringComparison.Ordinal))
            .Select(key => key[7..^4]).Order(StringComparer.Ordinal).ToArray();
        string? remote = remotes.Contains(upstream) ? upstream : remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault();
        if (remote is null || string.IsNullOrWhiteSpace(values["remote." + remote + ".url"])) return null;
        string merge = upstream == remote ? values.GetValueOrDefault("branch." + name + ".merge", "") : "";
        string target = "refs/remotes/" + remote + "/" + (merge.StartsWith("refs/heads/", StringComparison.Ordinal) ? merge[11..] : name);
        var headTask = git.Runner.RunAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], true);
        var remoteTask = git.Runner.RunAsync(root, ["rev-parse", "--verify", "--end-of-options", target + "^{commit}"], true);
        await Task.WhenAll(headTask, remoteTask);
        var head = await headTask;
        var remoteHead = await remoteTask;
        // 引用尚未获取、分支尚未创建或本地无提交时，保持未知，不编造待推送数量。
        if (head.ExitCode != 0 || remoteHead.ExitCode != 0) return null;
        var count = await git.Runner.RunAsync(root, ["rev-list", "--count", head.Output.Trim(), "--not", remoteHead.Output.Trim()]);
        return int.Parse(count.Output.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
