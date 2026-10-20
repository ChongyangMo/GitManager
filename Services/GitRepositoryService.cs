using System.IO;
using GitManager.Models;

namespace GitManager.Services;

public sealed partial class GitRepositoryService(GitRunner runner)
{
    public GitRunner Runner { get; } = runner;
    private Task<GitResult> Run(string root, params string[] args) => Runner.RunAsync(root, args);

    public async Task<string> OpenAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("仓库目录不存在。" + directory);
        var result = await Runner.RunAsync(directory, ["rev-parse", "--show-toplevel"], allowFailure: true, cancellationToken: cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException("这个目录尚未建立 Git 仓库，请使用“新建仓库”。");
        string root = Path.GetFullPath(result.Output.Trim());
        if (!string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择仓库根目录，当前目录属于上级仓库：" + root);
        return root;
    }

    public async Task<string> InitializeAsync(string directory)
    {
        if (File.Exists(Path.Combine(directory, ".git")) || Directory.Exists(Path.Combine(directory, ".git")))
            throw new InvalidOperationException("所选目录已经是仓库，请直接添加。");
        await Run(directory, "init", "--initial-branch=main", ".");
        AppLog.Write("已建立本地仓库：" + directory);
        return await OpenAsync(directory);
    }

    public async Task<string> CloneAsync(string url, string destination)
    {
        ValidateRemote(url);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new InvalidOperationException("克隆目标必须是空目录。");
        await Runner.RunAsync(null, ["clone", "--", url, destination]);
        return await OpenAsync(destination);
    }

    public async Task<RepositorySnapshot> ReadAsync(string root)
    {
        await OpenAsync(root);
        var branchTask = Runner.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], true);
        var headTask = Runner.RunAsync(root, ["rev-parse", "--verify", "HEAD"], true);
        var branchesTask = Run(root, "for-each-ref", "--format=%(refname:short)", "refs/heads/");
        var changesTask = ReadChangesAsync(root);
        var remoteTask = ReadConfigAsync(root, "remote.origin.url");
        var nameTask = ReadConfigAsync(root, "user.name");
        var emailTask = ReadConfigAsync(root, "user.email");
        await Task.WhenAll(branchTask, headTask, branchesTask, changesTask, remoteTask, nameTask, emailTask);
        var branch = await branchTask;
        var head = await headTask;
        string current = branch.ExitCode == 0 ? branch.Output.Trim() : "分离 HEAD";
        var branches = (await branchesTask).Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!branches.Contains(current)) branches.Insert(0, current);
        var changes = await changesTask;
        List<CommitInfo> commits = [];
        if (head.ExitCode == 0)
        {
            commits = await ReadCommitsAsync(root, head.Output.Trim());
        }
        return new(current, branches, changes, commits, await remoteTask, await nameTask, await emailTask, head.ExitCode == 0);
    }

    public async Task<List<ChangedFile>> ReadChangesAsync(string root)
    {
        string output = (await Run(root, "status", "--porcelain=v1", "-z", "--untracked-files=all")).Output;
        string[] entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        List<ChangedFile> result = [];
        for (int i = 0; i < entries.Length; i++)
        {
            string entry = entries[i];
            if (entry.Length < 4) continue;
            string? oldPath = (entry[0] is 'R' or 'C' || entry[1] is 'R' or 'C') && i + 1 < entries.Length ? entries[++i] : null;
            result.Add(new(entry[3..], oldPath, entry[0], entry[1]));
        }
        return result;
    }

    public async Task<string> ReadConfigAsync(string root, string name)
    {
        var result = await Runner.RunAsync(root, ["config", "--get", name], true);
        if (result.ExitCode is not (0 or 1)) throw new InvalidOperationException("读取 Git 配置失败：" + result.Error);
        return result.Output.Trim();
    }

    public async Task<List<HistoryFile>> ReadHistoryFilesAsync(string root, CommitInfo commit)
    {
        string[] args = commit.FirstParent.Length == 0
            ? ["diff-tree", "--root", "--no-commit-id", "--name-status", "--no-renames", "-r", "-z", commit.Hash, "--"]
            : ["diff", "--name-status", "--no-renames", "-z", commit.FirstParent, commit.Hash, "--"];
        string[] fields = (await Run(root, args)).Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        List<HistoryFile> files = [];
        for (int i = 0; i + 1 < fields.Length; i += 2)
            files.Add(new(fields[i + 1], fields[i] switch { "A" => "新增", "D" => "删除", _ => "修改" }));
        return files;
    }
}
