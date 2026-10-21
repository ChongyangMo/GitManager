using System.IO;
using GitManager.Models;
using Microsoft.VisualBasic.FileIO;

namespace GitManager.Services.Repositories.Copies;

public sealed class RepositoryLocalDeletion(GitRepositoryService git)
{
    public async Task RecycleAsync(RepositoryBookmark bookmark, IEnumerable<string> otherRepositories)
    {
        string root = await git.OpenAsync(bookmark.Path);
        ValidateTarget(root, otherRepositories);
        var worktrees = await git.Runner.RunAsync(root, ["worktree", "list", "--porcelain", "-z"]);
        if (worktrees.Output.Split('\0').Count(line => line.StartsWith("worktree ", StringComparison.Ordinal)) > 1)
            throw new InvalidOperationException("此仓库还关联其他工作树，请先解除关联后再删除本地文件。");
        await Task.Run(() =>
        {
            ValidateTarget(root, otherRepositories);
            FileSystem.DeleteDirectory(root, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        });
        if (Directory.Exists(root)) throw new IOException("本地目录仍然存在，未移除仓库记录。");
        AppLog.Write("本地仓库已移到回收站：" + root);
    }

    public static void ValidateTarget(string root, IEnumerable<string> otherRepositories)
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
        if (!Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")))
            throw new InvalidOperationException("无法删除链接工作树或缺少独立 Git 元数据的目录。");
    }
}
