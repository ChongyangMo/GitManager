using System.IO;
using GitManager.Models;

namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    public async Task<List<CommitInfo>> ReadCommitsAsync(string root, string revision, int count = 300)
    {
        string text = (await Run(root, "log", "-n", count.ToString(), "--date-order", "--format=%H%x00%h%x00%an%x00%aI%x00%s%x00%P%x00", "--end-of-options", revision, "--")).Output;
        string[] fields = text.Split('\0');
        List<CommitInfo> commits = [];
        for (int i = 0; i + 5 < fields.Length; i += 6)
            commits.Add(new(fields[i].TrimStart('\r', '\n'), fields[i + 1], fields[i + 2], fields[i + 3], fields[i + 4], fields[i + 5]));
        return commits;
    }

    public async Task<VersionSnapshot> ReadVersionsAsync(string root, RemoteBranch? requested = null, RepositorySnapshot? snapshot = null)
    {
        var metadataTask = Run(root, "rev-parse", "--absolute-git-dir");
        var remotesTask = Run(root, "remote");
        var referencesTask = Run(root, "for-each-ref", "--format=%(refname)%00%(symref)%00", "refs/remotes/");
        string localBranch;
        CommitInfo? local;
        bool attached;
        if (snapshot is not null)
        {
            localBranch = snapshot.Branch;
            attached = localBranch != "分离 HEAD";
            local = snapshot.History.FirstOrDefault();
        }
        else
        {
            var branchTask = Runner.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], true);
            var headTask = Runner.RunAsync(root, ["rev-parse", "--verify", "HEAD"], true);
            await Task.WhenAll(branchTask, headTask);
            var branchResult = await branchTask;
            var headResult = await headTask;
            attached = branchResult.ExitCode == 0;
            localBranch = attached ? branchResult.Output.Trim() : "分离 HEAD";
            local = headResult.ExitCode == 0 ? (await ReadCommitsAsync(root, headResult.Output.Trim(), 1)).First() : null;
        }
        await Task.WhenAll(metadataTask, remotesTask, referencesTask);
        string metadata = (await metadataTask).Output.Trim();
        bool merging = File.Exists(Path.Combine(metadata, "MERGE_HEAD"));
        string[] remotes = (await remotesTask).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<RemoteBranch> branches = [];
        string[] references = (await referencesTask).Output.Split('\0');
        for (int i = 0; i + 1 < references.Length; i += 2)
        {
            string reference = references[i].TrimStart('\r', '\n');
            if (references[i + 1].Length > 0) continue;
            string? remote = remotes.OrderByDescending(name => name.Length).FirstOrDefault(name => reference.StartsWith("refs/remotes/" + name + "/", StringComparison.Ordinal));
            if (remote is not null) branches.Add(new(remote, reference[("refs/remotes/" + remote + "/").Length..]));
        }
        string upstreamRemote = attached ? await ReadConfigAsync(root, "branch." + localBranch + ".remote") : "";
        string remoteName = requested is not null && remotes.Contains(requested.RemoteName) ? requested.RemoteName
            : remotes.Contains(upstreamRemote) ? upstreamRemote : remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault() ?? "";
        string remoteUrl = remoteName.Length == 0 ? "" : await ReadConfigAsync(root, "remote." + remoteName + ".url");
        RemoteBranch? target = null;
        if (remoteName.Length > 0)
        {
            string name = requested?.RemoteName == remoteName ? requested.Name : "";
            if (name.Length == 0 && upstreamRemote == remoteName)
            {
                string merge = await ReadConfigAsync(root, "branch." + localBranch + ".merge");
                if (merge.StartsWith("refs/heads/", StringComparison.Ordinal)) name = merge[11..];
            }
            if (name.Length == 0 && attached) name = localBranch;
            if (name.Length == 0)
            {
                var remoteHead = await Runner.RunAsync(root, ["symbolic-ref", "--quiet", "refs/remotes/" + remoteName + "/HEAD"], true);
                if (remoteHead.ExitCode == 0) name = remoteHead.Output.Trim()[("refs/remotes/" + remoteName + "/").Length..];
                else name = branches.FirstOrDefault(item => item.RemoteName == remoteName)?.Name ?? "";
            }
            if (name.Length > 0) { target = new(remoteName, name); if (!branches.Contains(target)) branches.Add(target); }
        }
        List<CommitInfo> remoteHistory = [];
        int? ahead = null, behind = null;
        bool? common = null;
        if (target is not null)
        {
            var resolved = await Runner.RunAsync(root, ["rev-parse", "--verify", "--end-of-options", target.FullRef + "^{commit}"], true);
            if (resolved.ExitCode == 0)
            {
                remoteHistory = await ReadCommitsAsync(root, resolved.Output.Trim());
                if (local is not null)
                {
                    string[] counts = (await Run(root, "rev-list", "--left-right", "--count", local.Hash + "..." + resolved.Output.Trim())).Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    ahead = int.Parse(counts[0]); behind = int.Parse(counts[1]);
                    var ancestor = await Runner.RunAsync(root, ["merge-base", local.Hash, resolved.Output.Trim()], true);
                    if (ancestor.ExitCode is not (0 or 1)) throw new InvalidOperationException("读取版本关系失败：" + ancestor.Error);
                    common = ancestor.ExitCode == 0;
                }
            }
        }
        return new(localBranch, local, remoteName, remoteUrl, target, branches, remoteHistory, ahead, behind, common, merging);
    }

    public async Task FetchVersionsAsync(string root, string remote, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remote)) throw new InvalidOperationException("请先设置远程仓库。");
        await Runner.RunAsync(root, ["fetch", "--prune", remote, "+refs/heads/*:refs/remotes/" + remote + "/*"], cancellationToken: cancellationToken);
        AppLog.Write("已获取远程版本记录");
    }
}
