using System.IO;
using System.Security.Cryptography;
using GitManager.Models;
using GitManager.Models.RepositoryCopies;

namespace GitManager.Services.Repositories.Copies;

public sealed class RepositoryCopyReader(GitRepositoryService git)
{
    public async Task<RepositoryCopyComparison> ReadAsync(IReadOnlyList<RepositoryBookmark> bookmarks)
    {
        var copies = await Task.WhenAll(bookmarks.Select((bookmark, index) => ReadCopyAsync(bookmark, index + 1)));
        var differences = new List<RepositoryCopyDifference>();
        if (copies.All(copy => copy.Error.Length == 0))
        {
            foreach (string path in copies.SelectMany(copy => copy.Files.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                string?[] values = copies.Select(copy => copy.Files.GetValueOrDefault(path)).ToArray();
                if (values.Distinct(StringComparer.Ordinal).Count() == 1) continue;
                var versions = values.Where(value => value is not null).Distinct(StringComparer.Ordinal).ToList();
                differences.Add(new(path, string.Join("；", values.Select((value, index) =>
                    $"{index + 1}：" + (value is null ? "缺失" : versions.Count == 1 ? "存在" : $"内容 {versions.IndexOf(value) + 1}")))));
            }
        }
        return new(copies, differences);
    }

    private async Task<RepositoryCopySnapshot> ReadCopyAsync(RepositoryBookmark bookmark, int number)
    {
        try
        {
            string root = await git.OpenAsync(bookmark.Path);
            string remote = await git.ReadConfigAsync(root, "remote.origin.url");
            if (remote.Length == 0) remote = await git.ReadConfigAsync(root, "gitmanager.serviceCloudUrl");
            var branch = await git.Runner.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], true);
            var commit = await git.Runner.RunAsync(root, ["log", "-1", "--format=%H%n%s"], true);
            var head = await git.Runner.RunAsync(root, ["rev-parse", "--verify", "HEAD"], true);
            if (commit.ExitCode != 0 && head.ExitCode == 0) throw new IOException("读取最近提交失败。" + commit.Error);
            var changes = await git.ReadChangesAsync(root);
            string files = (await git.Runner.RunAsync(root, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"])).Output;
            var fingerprints = await Task.Run(() => ReadFiles(root, files));
            return new(bookmark, number, remote.Length == 0 ? "未配置" : AppLog.Clean(remote),
                branch.ExitCode == 0 ? branch.Output.Trim() : "分离 HEAD",
                commit.ExitCode == 0 ? commit.Output.Trim() : "暂无提交",
                changes.Count == 0 ? "无未提交改动" : string.Join("\n", changes.Select(file => $"{file.State} · {file.Path}")),
                fingerprints, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            AppLog.Write("读取同名仓库差异失败：" + bookmark.Path, ex);
            return new(bookmark, number, "未读取", "未读取", "未读取", "未读取", new Dictionary<string, string>(), AppLog.Clean(ex.Message));
        }
    }

    private static Dictionary<string, string> ReadFiles(string root, string output)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
        {
            string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
            if (!fullPath.StartsWith(root.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("仓库文件路径超出根目录。");
            // 子模块是独立仓库，不把目录当作普通文件读取。
            if (Directory.Exists(fullPath)) { files[path] = "子模块未比较"; continue; }
            if (!File.Exists(fullPath)) { files[path] = "工作区已删除"; continue; }
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            files[path] = Convert.ToHexString(SHA256.HashData(stream));
        }
        return files;
    }
}
