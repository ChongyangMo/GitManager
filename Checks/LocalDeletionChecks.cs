using System.IO;
using GitManager.Models;
using GitManager.Services.Repositories.Copies;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunLocalDeletionChecksAsync()
    {
        string root = CheckDirectories.Create("Checks");
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

        // 真实进程占用：一个终端把当前目录放在仓库里，另一个进程独占打开其中的文件。
        string locked = Path.Combine(root, "locked");
        Directory.CreateDirectory(locked);
        await Git.InitializeAsync(locked);
        await File.WriteAllTextAsync(Path.Combine(locked, "占用.txt"), "内容\n");
        using var terminal = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1 >nul")
            { WorkingDirectory = locked, CreateNoWindow = true, UseShellExecute = false })!;
        using var reader = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
            $"-NoProfile -Command \"$f=[IO.File]::Open('{Path.Combine(locked, "占用.txt")}','Open','Read','None'); Start-Sleep 120\"")
            { WorkingDirectory = root, CreateNoWindow = true, UseShellExecute = false })!;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        DirectoryLockReport report;
        do { await Task.Delay(300); report = DirectoryLocks.Inspect(locked); }
        while (!report.Holders.Any(holder => holder.ProcessId == reader.Id) && DateTimeOffset.UtcNow < deadline);
        Check(report.Holders.Any(holder => holder.ProcessId == terminal.Id && holder.Reason.Contains("当前工作目录"))
            && report.Holders.Any(holder => holder.ProcessId == reader.Id && holder.Reason.Contains("打开了其中的文件")), "调查出当前目录占用和文件占用的具体进程");

        IReadOnlyList<DirectoryLockHolder>? asked = null;
        string refused = "";
        try { await deletion.RecycleAsync(new RepositoryBookmark(locked), [], holders => { asked = holders; return false; }); }
        catch (IOException ex) { refused = ex.Message; }
        Check(Directory.Exists(locked) && !terminal.HasExited && !reader.HasExited && asked?.Any(holder => holder.ProcessId == terminal.Id) == true
            && refused.Contains($"PID {terminal.Id}") && refused.Contains($"PID {reader.Id}"), "未同意结束程序时不删除，并说明占用者");

        await deletion.RecycleAsync(new RepositoryBookmark(locked), [], _ => true);
        Check(!Directory.Exists(locked) && terminal.WaitForExit(5000) && reader.WaitForExit(5000), "同意后结束占用程序并完整删除目录");
        Console.WriteLine($"本地删除检查通过，共 {_count} 项。临时目录：{root}");
    }
}
