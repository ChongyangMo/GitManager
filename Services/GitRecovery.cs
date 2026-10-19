using System.IO;
using GitManager.Models;

namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    public async Task<List<RecoveryPoint>> ReadRecoveryPointsAsync(string root)
    {
        string[] fields = (await Run(root, "stash", "list", "--format=%H%x00%gd%x00%gs%x00")).Output.Split('\0');
        List<RecoveryPoint> points = [];
        for (int i = 0; i + 2 < fields.Length; i += 3)
            points.Add(new(fields[i].TrimStart('\r', '\n'), fields[i + 1], fields[i + 2]));
        return points;
    }

    public async Task<string?> RestoreVersionAsync(string root, string commit, string? path = null)
    {
        await EnsureNoOperationAsync(root);
        if ((await ReadChangesAsync(root)).Any(file => file.IsConflict)) throw new InvalidOperationException("仓库存在冲突，无法创建完整恢复点，请先解决冲突。");
        await Run(root, "cat-file", "-e", commit + "^{commit}");
        await CheckIgnoredCollisionsAsync(root, commit, path);
        string[] paths = path is null ? ["."] : [path];
        string before = (await Run(root, "rev-parse", "HEAD")).Output.Trim();
        var oldStash = await Runner.RunAsync(root, ["rev-parse", "--verify", "refs/stash"], true);
        await Runner.RunAsync(root, new[] { "stash", "push", "--include-untracked", "--message", $"GitManager 恢复前 {DateTime.Now:yyyy-MM-dd HH:mm:ss}" , "--" }.Concat(paths));
        var newStash = await Runner.RunAsync(root, ["rev-parse", "--verify", "refs/stash"], true);
        string? checkpoint = newStash.ExitCode == 0 && newStash.Output != oldStash.Output ? newStash.Output.Trim() : null;
        try
        {
            await RestorePathsAsync(root, commit, paths, path);
            AppLog.Write(path is null ? "已恢复指定版本，历史提交保持不变" : "已恢复指定文件：" + path);
            return checkpoint;
        }
        catch (Exception restoreError)
        {
            try
            {
                await RestorePathsAsync(root, before, paths, path);
                if (checkpoint is not null) await Run(root, "stash", "apply", "--index", checkpoint);
            }
            catch (Exception rollbackError)
            {
                AppLog.Write("恢复失败，自动回退也未完成，请从恢复点取回修改", rollbackError);
                throw new InvalidOperationException("恢复未完成。原修改仍保存在恢复点中，请检查工作区后通过“恢复点”取回。\n" + restoreError.Message);
            }
            throw new InvalidOperationException("恢复未完成，已回退到恢复前状态。\n" + restoreError.Message);
        }
    }

    public async Task ApplyRecoveryPointAsync(string root, string hash)
    {
        await EnsureNoOperationAsync(root);
        if ((await ReadChangesAsync(root)).Count > 0) throw new InvalidOperationException("请先提交或处理当前修改，再取回恢复点。");
        await Run(root, "stash", "apply", "--index", hash);
        AppLog.Write("已取回恢复点，原记录继续保留");
    }

    private async Task RestorePathsAsync(string root, string commit, string[] paths, string? path)
    {
        if (path is not null)
        {
            string target = (await Run(root, "ls-tree", "-z", commit, "--", path)).Output;
            string index = (await Run(root, "ls-files", "-z", "--", path)).Output;
            if (target.Length == 0 && index.Length == 0) return;
        }
        await Runner.RunAsync(root, new[] { "restore", "--source=" + commit, "--staged", "--worktree", "--" }.Concat(paths));
    }

    private async Task CheckIgnoredCollisionsAsync(string root, string commit, string? path)
    {
        string[] targetArgs = path is null ? ["ls-tree", "-r", "--name-only", "-z", commit, "--"] : ["ls-tree", "-r", "--name-only", "-z", commit, "--", path];
        var targets = (await Run(root, targetArgs)).Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0) return;
        var ignored = (await Run(root, "ls-files", "--others", "--ignored", "--exclude-standard", "-z")).Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (string ignoredPath in ignored)
        {
            string probe = ignoredPath;
            while (probe.Length > 0)
            {
                if (targets.Contains(probe)) throw new InvalidOperationException("恢复会覆盖被忽略的文件，请先移走该文件：" + ignoredPath);
                int slash = probe.LastIndexOf('/');
                probe = slash < 0 ? string.Empty : probe[..slash];
            }
            if (targets.Any(target => target.StartsWith(ignoredPath + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("恢复路径与被忽略的文件冲突：" + ignoredPath);
        }
    }
}
