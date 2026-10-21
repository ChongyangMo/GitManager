using System.IO;
using GitManager.Models;
using Microsoft.VisualBasic.FileIO;

namespace GitManager.Services.Repositories.Copies;

public sealed class RepositoryLocalDeletion(GitRepositoryService git)
{
    // 返回给用户看的结果说明；链接工作树会同时清理 Git 中的工作树登记、已合并的分支和因此变空的上层目录。
    // confirmRelease 用于询问是否结束占用目录的其他程序；为空时只自动解除安全的占用。
    public async Task<string> RecycleAsync(RepositoryBookmark bookmark, IEnumerable<string> otherRepositories,
        Func<IReadOnlyList<DirectoryLockHolder>, bool>? confirmRelease = null)
    {
        string root = await git.OpenAsync(bookmark.Path);
        var dirs = (await git.Runner.RunAsync(root, ["rev-parse", "--path-format=absolute", "--git-common-dir", "--absolute-git-dir"])).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string common = RepositoryDiscovery.Normalize(dirs[0]), gitDir = RepositoryDiscovery.Normalize(dirs[1]);
        bool linked = File.Exists(Path.Combine(root, ".git"))
            && gitDir.StartsWith(common + Path.DirectorySeparatorChar + "worktrees" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        ValidateTarget(root, otherRepositories, linked);
        string? branch = null;
        if (linked)
        {
            var head = await git.Runner.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], true);
            if (head.ExitCode == 0) branch = head.Output.Trim();
        }
        else
        {
            // 手动删掉的工作树目录会留下失效登记，先清理，避免误判为仍关联其他工作树。
            await git.Runner.RunAsync(root, ["worktree", "prune"], true);
            var worktrees = await git.Runner.RunAsync(root, ["worktree", "list", "--porcelain", "-z"]);
            if (worktrees.Output.Split('\0').Count(line => line.StartsWith("worktree ", StringComparison.Ordinal)) > 1)
                throw new InvalidOperationException("此仓库还关联其他工作树，请先删除那些工作树后再删除本地文件。");
        }
        var locks = await Task.Run(() => DirectoryLocks.Inspect(root));
        if (locks.Holders.Count > 0)
        {
            AppLog.Write("删除前发现目录占用：" + root + "\n" + locks.Describe());
            locks = await DirectoryLocks.ReleaseAsync(root, locks, confirmRelease);
            if (locks.Holders.Count > 0) throw new IOException("目录仍被以下程序占用，未删除：\n" + locks.Describe());
        }
        try
        {
            await Task.Run(() =>
            {
                ValidateTarget(root, otherRepositories, linked);
                FileSystem.DeleteDirectory(root, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // 检查与删除之间可能有程序新打开了目录；失败时重新调查，告诉用户具体是谁。
            var current = await Task.Run(() => DirectoryLocks.Inspect(root));
            AppLog.Write("删除本地目录失败：" + root + "\n" + current.Describe(), ex);
            throw new IOException("删除未完成，目录占用情况：\n" + current.Describe(), ex);
        }
        string? leftover = await RemoveLeftoverAsync(root, confirmRelease);
        AppLog.Write("本地仓库已移到回收站：" + root);
        var notes = new List<string> { linked ? "已将工作树目录移到回收站" : "已将本地目录移到回收站" };
        if (leftover is not null) notes.Add(leftover);
        if (!linked) return string.Join("，", notes);
        await git.Runner.RunAsync(common, ["worktree", "prune"], true);
        if (branch is not null)
        {
            // 只删除已合并的分支（-d）；仍有未合并提交时保留，避免丢失工作。
            var deleted = await git.Runner.RunAsync(common, ["branch", "-d", "--", branch], true);
            notes.Add(deleted.ExitCode == 0 ? $"已删除已合并的分支 {branch}" : $"分支 {branch} 有未合并的提交，已保留");
            AppLog.Write(deleted.ExitCode == 0 ? "已删除工作树分支：" + branch : "工作树分支未合并，已保留：" + branch + "；" + deleted.Error.Trim());
        }
        // 主工作区位于公共 Git 目录的上一级；只清理其下因删除而变空的目录（如 .claude\worktrees）。
        string owner = Path.GetFileName(common).Equals(".git", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(common)! : common;
        for (string? parent = Path.GetDirectoryName(root); parent is not null
             && parent.StartsWith(owner + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
        {
            if (!Directory.Exists(parent) || Directory.EnumerateFileSystemEntries(parent).Any()) break;
            try { Directory.Delete(parent); AppLog.Write("已删除空目录：" + parent); }
            catch (IOException) { break; }
        }
        return string.Join("，", notes);
    }

    // 回收站有时只移走内容而留下被短暂占用的空目录：空目录直接删除；仍被占用时返回说明，仍有文件时报错，不静默残留。
    private static async Task<string?> RemoveLeftoverAsync(string root, Func<IReadOnlyList<DirectoryLockHolder>, bool>? confirmRelease)
    {
        for (int attempt = 0; Directory.Exists(root); attempt++)
        {
            string[] remaining = Directory.EnumerateFileSystemEntries(root, "*", System.IO.SearchOption.AllDirectories).Take(4).ToArray();
            if (remaining.Length == 0)
            {
                try { Directory.Delete(root); continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (attempt == 5)
            {
                var locks = await Task.Run(() => DirectoryLocks.Inspect(root));
                if (locks.Holders.Count > 0) await DirectoryLocks.ReleaseAsync(root, locks, confirmRelease);
            }
            if (attempt >= 10)
            {
                var locks = await Task.Run(() => DirectoryLocks.Inspect(root));
                if (remaining.Length > 0)
                    throw new IOException("部分文件未能删除：\n" + string.Join("\n", remaining.Select(path => Path.GetRelativePath(root, path))) + "\n占用情况：\n" + locks.Describe());
                AppLog.Write("空目录仍被占用：" + root + "\n" + locks.Describe());
                return "内容已移到回收站，但空目录仍被占用：" + locks.Describe();
            }
            await Task.Delay(200);
        }
        return null;
    }

    public static void ValidateTarget(string root, IEnumerable<string> otherRepositories, bool linkedWorktree = false)
    {
        root = RepositoryDiscovery.Normalize(root);
        bool Contains(string parent, string child) => string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (Directory.GetParent(root) is null || Contains(root, AppContext.BaseDirectory))
            throw new InvalidOperationException("无法删除磁盘根目录或正在运行的程序目录。");
        foreach (var special in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Windows })
            if (Contains(root, Environment.GetFolderPath(special))) throw new InvalidOperationException("无法删除系统或个人文件目录。");
        if (otherRepositories.Select(RepositoryDiscovery.Normalize).Any(path => Contains(root, path)))
            throw new InvalidOperationException("目录内还包含其他仓库，请先处理嵌套仓库。");
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("此路径经过目录链接，请从实际目录处理本地文件。");
        if (linkedWorktree ? !File.Exists(Path.Combine(root, ".git")) : !Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")))
            throw new InvalidOperationException("无法删除缺少独立 Git 元数据的目录（例如子模块）。");
    }
}
