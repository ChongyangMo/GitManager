using System.IO;
using System.Text;
using GitManager.Models;

namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    private const int PreviewLimit = 2 * 1024 * 1024;
    public async Task<FileComparison> CompareWorkingAsync(string root, ChangedFile file, bool hasHead)
    {
        try
        {
            string before = hasHead ? await ReadBlobAsync(root, "HEAD", file.OriginalPath ?? file.Path) : string.Empty;
            string diskPath = Path.Combine(root, file.Path);
            string after = File.Exists(diskPath) ? await ReadDiskTextAsync(diskPath) : string.Empty;
            string patch = await CompareTextsAsync(before, after);
            return new(before, after, patch, "上次提交", "工作区");
        }
        catch (PreviewException ex) { return new("", "", "", "上次提交", "工作区", ex.Message); }
    }

    public async Task<FileComparison> CompareHistoryAsync(string root, CommitInfo commit, HistoryFile file)
    {
        try
        {
            string before = commit.FirstParent.Length == 0 ? string.Empty : await ReadBlobAsync(root, commit.FirstParent, file.Path);
            string after = await ReadBlobAsync(root, commit.Hash, file.Path);
            string patch = await CompareTextsAsync(before, after);
            return new(before, after, patch, "上一版本", commit.ShortHash);
        }
        catch (PreviewException ex) { return new("", "", "", "上一版本", commit.ShortHash, ex.Message); }
    }

    private async Task<string> ReadBlobAsync(string root, string revision, string path)
    {
        string objectName = revision + ":" + path;
        var size = await Runner.RunAsync(root, ["cat-file", "-s", objectName], true);
        if (size.ExitCode != 0)
        {
            var exists = await Runner.RunAsync(root, ["ls-tree", "-z", revision, "--", path], true);
            if (exists.ExitCode == 0 && exists.Output.Length == 0) return string.Empty;
            throw new InvalidOperationException("无法读取历史文件：" + path + "\n" + size.Error);
        }
        if (!long.TryParse(size.Output.Trim(), out long bytes) || bytes > PreviewLimit) throw new PreviewException("文件较大，请使用外部编辑器查看。");
        if ((await Run(root, "cat-file", "-t", objectName)).Output.Trim() != "blob") throw new PreviewException("子模块或目录不显示文本差异。");
        string content;
        try { content = (await Runner.RunAsync(root, ["show", objectName], strictUtf8: true)).Output; }
        catch (DecoderFallbackException) { throw new PreviewException("文件不是 UTF-8 文本，请使用外部编辑器查看。"); }
        if (content.Contains('\0')) throw new PreviewException("二进制文件不显示文本差异。");
        return content;
    }

    private static async Task<string> ReadDiskTextAsync(string path)
    {
        if (new FileInfo(path).Length > PreviewLimit) throw new PreviewException("文件较大，请使用外部编辑器查看。");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        if (bytes.Contains((byte)0)) throw new PreviewException("二进制文件不显示文本差异。");
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new PreviewException("文件不是 UTF-8 文本，请使用外部编辑器查看。"); }
    }

    private sealed class PreviewException(string message) : Exception(message);

    private async Task<string> CompareTextsAsync(string before, string after)
    {
        before = before.Replace("\r\n", "\n");
        after = after.Replace("\r\n", "\n");
        if (before == after || before.Length == 0 || after.Length == 0) return string.Empty;
        string directory = Path.Combine(SettingsStore.DataDirectory, "comparisons", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string left = Path.Combine(directory, "before.txt"), right = Path.Combine(directory, "after.txt");
            await File.WriteAllTextAsync(left, before, new UTF8Encoding(false));
            await File.WriteAllTextAsync(right, after, new UTF8Encoding(false));
            var result = await Runner.RunAsync(directory, ["diff", "--no-index", "--no-ext-diff", "--no-textconv", "--unified=3", "--", "before.txt", "after.txt"], true);
            if (result.ExitCode is not (0 or 1)) throw new InvalidOperationException("生成代码差异失败：" + result.Error);
            return result.Output;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
