using System.IO;
using GitManager.Models;
using GitManager.Services.Repositories.Copies;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunLocalDeletionChecksAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "GitManagerChecks", Guid.NewGuid().ToString("N"));
        string main = Path.Combine(root, "main"), worktrees = Path.Combine(main, ".claude", "worktrees");
        string merged = Path.Combine(worktrees, "merged"), unmerged = Path.Combine(worktrees, "unmerged"), stale = Path.Combine(root, "stale");
        Directory.CreateDirectory(main);
        await Git.InitializeAsync(main);
        await Git.SaveIdentityAsync(main, "GitManager 校验", "gitmanager-checks@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(main, ".gitignore"), ".claude/\n");
        await File.WriteAllTextAsync(Path.Combine(main, "说明.txt"), "内容\n");
        await CommitAllAsync(main, "初始提交");
        await Runner.RunAsync(main, ["worktree", "add", "-b", "wt-merged", merged]);
        await Runner.RunAsync(main, ["worktree", "add", "-b", "wt-unmerged", unmerged]);
        await File.WriteAllTextAsync(Path.Combine(unmerged, "未合并.txt"), "未合并\n");
        await CommitAllAsync(unmerged, "未合并的提交");
        var deletion = new RepositoryLocalDeletion(Git);
        async Task<string[]> Branches() => (await Runner.RunAsync(main, ["branch", "--format=%(refname:short)"])).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        async Task<int> WorktreeCount() => (await Runner.RunAsync(main, ["worktree", "list", "--porcelain"])).Output.Split('\n').Count(line => line.StartsWith("worktree ", StringComparison.Ordinal));

        string first = await deletion.RecycleAsync(new RepositoryBookmark(merged), [main]);
        Check(!Directory.Exists(merged) && Directory.Exists(unmerged) && await WorktreeCount() == 2, "删除链接工作树后目录不残留，Git 工作树登记同步清理");
        Check(!(await Branches()).Contains("wt-merged") && first.Contains("已删除已合并的分支 wt-merged"), "已合并的工作树分支随之删除");

        string second = await deletion.RecycleAsync(new RepositoryBookmark(unmerged), [main]);
        Check(!Directory.Exists(unmerged) && await WorktreeCount() == 1 && (await Branches()).Contains("wt-unmerged")
            && second.Contains("wt-unmerged 有未合并的提交，已保留"), "有未合并提交的分支保留并说明");
        Check(!Directory.Exists(worktrees) && !Directory.Exists(Path.Combine(main, ".claude")) && Directory.Exists(main), "因删除而变空的上层目录一并清理，主仓库保留");

        await Runner.RunAsync(main, ["worktree", "add", "-b", "wt-stale", stale]);
        Directory.Delete(stale, recursive: true);
        string third = await deletion.RecycleAsync(new RepositoryBookmark(main), []);
        Check(!Directory.Exists(main) && third == "已将本地目录移到回收站", "手动删掉的工作树留下的失效登记不再阻止删除主仓库");
        Console.WriteLine($"本地删除检查通过，共 {_count} 项。临时目录：{root}");
    }
}
