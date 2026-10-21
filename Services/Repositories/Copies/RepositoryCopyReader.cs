using System.IO;
using System.Security.Cryptography;
using GitManager.Models;
using GitManager.Models.RepositoryCopies;

namespace GitManager.Services.Repositories.Copies;

public sealed class RepositoryCopyReader(GitRepositoryService git)
{
    public async Task<RepositoryCopyComparison> ReadAsync(IReadOnlyList<RepositoryBookmark> bookmarks, CancellationToken token = default)
    {
        var copies = await Task.WhenAll(bookmarks.Select((bookmark, index) => ReadCopyAsync(bookmark, index + 1, token)));
        List<RepositoryCopyDifference> differences = [], lineEndings = [];
        if (copies.All(copy => copy.Error.Length == 0))
        {
            foreach (string path in copies.SelectMany(copy => copy.Files.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var values = copies.Select(copy => copy.Files.GetValueOrDefault(path)).ToArray();
                if (values.Select(value => value?.Hash).Distinct(StringComparer.Ordinal).Count() == 1) continue;
                // 同一提交在不同 core.autocrlf 下检出会得到 CRLF/LF 两种文件，内容一致，单独列出不计入差异。
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
        return new(copies, differences, lineEndings);
    }

    private async Task<RepositoryCopySnapshot> ReadCopyAsync(RepositoryBookmark bookmark, int number, CancellationToken token)
    {
        try
        {
            string root = await git.OpenAsync(bookmark.Path, token);
            string remote = await git.ReadConfigAsync(root, "remote.origin.url");
            if (remote.Length == 0) remote = await git.ReadConfigAsync(root, "gitmanager.serviceCloudUrl");
            var branch = await git.Runner.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], true, token);
            var commit = await git.Runner.RunAsync(root, ["log", "-1", "--format=%H%n%s"], true, token);
            var head = await git.Runner.RunAsync(root, ["rev-parse", "--verify", "HEAD"], true, token);
            if (commit.ExitCode != 0 && head.ExitCode == 0) throw new IOException("读取最近提交失败。" + commit.Error);
            var changes = await git.ReadChangesAsync(root, token);
            string files = (await git.Runner.RunAsync(root, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], cancellationToken: token)).Output;
            var fingerprints = await Task.Run(() => ReadFiles(root, files, token), token);
            return new(bookmark, number, remote.Length == 0 ? "未配置" : AppLog.Clean(remote),
                branch.ExitCode == 0 ? branch.Output.Trim() : "分离 HEAD",
                commit.ExitCode == 0 ? commit.Output.Trim() : "暂无提交",
                changes.Count == 0 ? "无未提交改动" : string.Join("\n", changes.Select(file => $"{file.State} · {file.Path}")),
                fingerprints, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            AppLog.Write("读取同名仓库差异失败：" + bookmark.Path, ex);
            return new(bookmark, number, "未读取", "未读取", "未读取", "未读取", new Dictionary<string, RepositoryFileFingerprint>(), AppLog.Clean(ex.Message));
        }
    }

    private static Dictionary<string, RepositoryFileFingerprint> ReadFiles(string root, string output, CancellationToken token)
    {
        var files = new Dictionary<string, RepositoryFileFingerprint>(StringComparer.Ordinal);
        foreach (string path in output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
            if (!fullPath.StartsWith(root.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("仓库文件路径超出根目录。");
            // 子模块是独立仓库，不把目录当作普通文件读取。
            if (Directory.Exists(fullPath)) { files[path] = RepositoryFileFingerprint.Special(RepositoryFileFingerprint.Submodule); continue; }
            if (!File.Exists(fullPath)) { files[path] = RepositoryFileFingerprint.Special("工作区已删除"); continue; }
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            files[path] = Fingerprint(stream);
        }
        return files;
    }

    // 一次读取同时计算原始哈希和 CRLF→LF 后的内容哈希；与 Git 一致，前 8000 字节含 NUL 视为二进制，不做换行处理。
    public static RepositoryFileFingerprint Fingerprint(Stream stream)
    {
        const byte CarriageReturn = 13, LineFeed = 10;
        using var raw = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920], normalized = new byte[buffer.Length + 1];
        bool? binary = null;
        bool pendingCr = false;
        long crlf = 0, lf = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            raw.AppendData(buffer, 0, read);
            binary ??= buffer.AsSpan(0, Math.Min(read, 8000)).Contains((byte)0);
            if (binary.Value) continue;
            int length = 0;
            for (int i = 0; i < read; i++)
            {
                byte value = buffer[i];
                if (pendingCr)
                {
                    pendingCr = false;
                    if (value == LineFeed) { crlf++; normalized[length++] = value; continue; }
                    normalized[length++] = CarriageReturn;
                }
                if (value == CarriageReturn) { pendingCr = true; continue; }
                if (value == LineFeed) lf++;
                normalized[length++] = value;
            }
            content.AppendData(normalized, 0, length);
        }
        if (pendingCr) content.AppendData([CarriageReturn]);
        string hash = Convert.ToHexString(raw.GetHashAndReset());
        if (binary == true) return new(hash, hash, "");
        string lineEnding = (crlf, lf) switch { (0, 0) => "无换行", (_, 0) => "CRLF", (0, _) => "LF", _ => "CRLF/LF 混合" };
        return new(hash, Convert.ToHexString(content.GetHashAndReset()), lineEnding);
    }
}
