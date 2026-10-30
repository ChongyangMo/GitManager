using System.Diagnostics;
using System.IO;
using GitManager.Models;
using GitManager.Models.RepositoryCopies;

namespace GitManager.Services.Repositories.Copies;

public sealed record RepositoryCopyReadProgress(int Number, string State, RepositoryCopySnapshot? Snapshot = null);

public sealed class RepositoryCopyReader(GitRepositoryService git)
{
    private sealed record CopyFiles(RepositoryCopySnapshot Snapshot, string[] Paths);

    public async Task<RepositoryCopyComparison> ReadAsync(IReadOnlyList<RepositoryBookmark> bookmarks, CancellationToken token = default,
        IProgress<RepositoryCopyReadProgress>? progress = null)
    {
        var watch = Stopwatch.StartNew();
        var entries = await Task.WhenAll(bookmarks.Select((bookmark, index) => ReadCopyAsync(bookmark, index + 1, token, progress))).ConfigureAwait(false);
        // 先合并路径清单。只有至少两份副本都存在的路径才需要读取内容，单份文件可直接判断为缺失差异。
        var shared = entries.SelectMany(entry => entry.Paths).GroupBy(path => path, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var copies = await Task.WhenAll(entries.Select(entry => Task.Run(() =>
        {
            if (entry.Snapshot.Error.Length > 0) return entry.Snapshot;
            try
            {
                return entry.Snapshot with { Files = ReadFiles(entry, shared, token, progress) };
            }
            catch (Exception ex) when (IsReadError(ex)) { return Failed(entry.Snapshot.Bookmark, entry.Snapshot.Number, ex); }
        }, token))).ConfigureAwait(false);
        List<RepositoryCopyDifference> differences = [], lineEndings = [];
        if (copies.All(copy => copy.Error.Length == 0))
        {
            foreach (string path in copies.SelectMany(copy => copy.Files.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var values = copies.Select(copy => copy.Files.GetValueOrDefault(path)).ToArray();
                if (values.Select(value => value?.Hash).Distinct(StringComparer.Ordinal).Count() == 1) continue;
                if (values.All(value => value is not null) && values.Select(value => value!.Content).Distinct(StringComparer.Ordinal).Count() == 1)
                {
                    lineEndings.Add(new(path, string.Join("；", values.Select((value, index) => $"{index + 1}：{value!.LineEnding}"))));
                    continue;
                }
                var versions = values.Where(value => value is not null).Select(value => value!.Content).Distinct(StringComparer.Ordinal).ToList();
                differences.Add(new(path, string.Join("；", values.Select((value, index) =>
                    $"{index + 1}：" + (value is null ? "缺失" : versions.Count == 1 ? "存在" : $"内容 {versions.IndexOf(value.Content) + 1}")))));
            }
        }
        AppLog.Write($"同名仓库比较完成，共 {copies.Length} 份，{shared.Count} 个共有路径，耗时 {watch.ElapsedMilliseconds} 毫秒");
        return new(copies, differences, lineEndings);
    }

    private async Task<CopyFiles> ReadCopyAsync(RepositoryBookmark bookmark, int number, CancellationToken token,
        IProgress<RepositoryCopyReadProgress>? progress)
    {
        progress?.Report(new(number, "正在读取仓库信息"));
        try
        {
            string root = await git.OpenAsync(bookmark.Path, token).ConfigureAwait(false);
            string remote = await ReadConfigAsync(root, "remote.origin.url", token).ConfigureAwait(false);
            if (remote.Length == 0) remote = await ReadConfigAsync(root, "gitmanager.serviceCloudUrl", token).ConfigureAwait(false);
            var branch = await git.Runner.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], true, token).ConfigureAwait(false);
            var commit = await git.Runner.RunAsync(root, ["log", "-1", "--format=%H%n%s"], true, token).ConfigureAwait(false);
            var head = await git.Runner.RunAsync(root, ["rev-parse", "--verify", "HEAD"], true, token).ConfigureAwait(false);
            if (commit.ExitCode != 0 && head.ExitCode == 0) throw new IOException("读取最近提交失败。" + commit.Error);
            var changes = await git.ReadChangesAsync(root, token).ConfigureAwait(false);
            progress?.Report(new(number, "正在读取文件清单"));
            string files = (await git.Runner.RunAsync(root, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], cancellationToken: token).ConfigureAwait(false)).Output;
            var snapshot = new RepositoryCopySnapshot(bookmark, number, remote.Length == 0 ? "未配置" : AppLog.Clean(remote),
                branch.ExitCode == 0 ? branch.Output.Trim() : "分离 HEAD",
                commit.ExitCode == 0 ? commit.Output.Trim() : "暂无提交",
                changes.Count == 0 ? "无未提交改动" : string.Join("\n", changes.Select(file => $"{file.State} · {file.Path}")),
                new Dictionary<string, RepositoryFileFingerprint>(), "");
            progress?.Report(new(number, "仓库信息已读取", snapshot));
            return new(snapshot, files.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray());
        }
        catch (Exception ex) when (IsReadError(ex)) { return new(Failed(bookmark, number, ex), []); }
    }

    private async Task<string> ReadConfigAsync(string root, string name, CancellationToken token)
    {
        var result = await git.Runner.RunAsync(root, ["config", "--get", name], true, token).ConfigureAwait(false);
        if (result.ExitCode is not (0 or 1)) throw new IOException("读取 Git 配置失败：" + result.Error);
        return result.Output.Trim();
    }

    private static Dictionary<string, RepositoryFileFingerprint> ReadFiles(CopyFiles entry, HashSet<string> shared, CancellationToken token,
        IProgress<RepositoryCopyReadProgress>? progress)
    {
        string root = entry.Snapshot.Path;
        var files = new Dictionary<string, RepositoryFileFingerprint>(StringComparer.Ordinal);
        var watch = Stopwatch.StartNew();
        foreach (string path in entry.Paths)
        {
            token.ThrowIfCancellationRequested();
            if (files.Count == 0 || watch.ElapsedMilliseconds >= 200)
            {
                progress?.Report(new(entry.Snapshot.Number, $"正在比较文件 {files.Count}/{entry.Paths.Length}"));
                watch.Restart();
            }
            string fullPath = Path.GetFullPath(Path.Combine(root, path));
            if (!fullPath.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("仓库文件路径超出根目录。");
            var info = new FileInfo(fullPath);
            // 属性、大小和修改时间复用同一次文件信息查询，避免给数万个文件重复查询磁盘。
            FileAttributes attributes = info.Attributes;
            if (attributes == (FileAttributes)(-1)) { files[path] = RepositoryFileFingerprint.Special("工作区已删除"); continue; }
            if ((attributes & FileAttributes.Directory) != 0) { files[path] = RepositoryFileFingerprint.Special(RepositoryFileFingerprint.Submodule); continue; }
            if (!shared.Contains(path))
            {
                files[path] = RepositoryFileFingerprint.Unique(info.Length, info.LastWriteTimeUtc.Ticks);
                continue;
            }
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            files[path] = Fingerprint(stream, token);
        }
        progress?.Report(new(entry.Snapshot.Number, $"文件比较完成 {files.Count}/{entry.Paths.Length}"));
        return files;
    }

    private static bool IsReadError(Exception ex)
        => ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException;

    private static RepositoryCopySnapshot Failed(RepositoryBookmark bookmark, int number, Exception ex)
    {
        AppLog.Write("读取同名仓库差异失败：" + bookmark.Path, ex);
        return new(bookmark, number, "未读取", "未读取", "未读取", "未读取", new Dictionary<string, RepositoryFileFingerprint>(), AppLog.Clean(ex.Message));
    }

    public static RepositoryFileFingerprint Fingerprint(Stream stream, CancellationToken token = default)
        => RepositoryFileFingerprintReader.Read(stream, token);
}
