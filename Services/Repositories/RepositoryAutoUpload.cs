using System.IO;
using GitManager.Models;

namespace GitManager.Services.Repositories;

public static class RepositoryAutoUpload
{
    // 返回已停止修改的全部改动；仍在编辑、存在冲突时返回 null，没有改动时返回空列表。
    public static async Task<IReadOnlyList<ChangedFile>?> ReadSettledChangesAsync(GitRepositoryService git, string root, TimeSpan quiet, CancellationToken token)
    {
        var files = await git.ReadChangesAsync(root, token);
        if (files.Count == 0) return [];
        if (files.Any(file => file.IsConflict)) return null;
        DateTime latest = files.SelectMany(file => file.Paths).Max(path => LastWrite(Path.Combine(root, path)));
        return DateTime.UtcNow - latest >= quiet ? files : null;
    }

    // 已删除的文件没有修改时间，改用所在目录的修改时间，删除文件会更新该时间。
    private static DateTime LastWrite(string path)
    {
        try
        {
            if (File.Exists(path)) return File.GetLastWriteTimeUtc(path);
            if (Directory.Exists(path)) return Directory.GetLastWriteTimeUtc(path);
            for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
                if (Directory.Exists(parent)) return Directory.GetLastWriteTimeUtc(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.UtcNow; }
        return DateTime.MinValue;
    }

    public static async Task<bool> HasRemoteAsync(GitRepositoryService git, string root, CancellationToken token)
    {
        var remotes = await git.Runner.RunAsync(root, ["remote"], true, token);
        return remotes.ExitCode == 0 && remotes.Output.Trim().Length > 0;
    }

    public static string DescribeQuiet(int seconds) => seconds % 60 == 0 ? $"{seconds / 60} 分钟" : $"{seconds} 秒";
}
