using System.IO;
using System.Text;
using GitManager.Models;

namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    private readonly BoundedAsyncCache<List<HistoryFile>> _historyFiles = new(256, 8 * 1024 * 1024,
        files => files.Sum(file => 64L + file.Path.Length * 2L));
    private readonly BoundedAsyncCache<HistoryPreview> _historyPreviews = new(64, 32 * 1024 * 1024,
        value => (value.Comparison.Before.Length + (long)value.Comparison.After.Length + value.Comparison.Patch.Length) * 2
            + value.Rows.Sum(row => 64L + (row.Before.Length + (long)row.After.Length) * 2));

    private static string HistoryKey(string root, string hash, string? path = null)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant() + "\0" + hash + "\0" + path;

    public Task<HistoryPreview> ReadHistoryPreviewAsync(string root, CommitInfo commit, HistoryFile file)
        => _historyPreviews.GetAsync(HistoryKey(root, commit.Hash + "\0" + commit.FirstParent, file.Path), async () =>
        {
            var comparison = await CompareHistoryCoreAsync(root, commit, file);
            return new(comparison, DiffBuilder.Build(comparison));
        });

    private async Task<(string Before, string After)> ReadHistoryTextsAsync(string root, CommitInfo commit, HistoryFile file)
    {
        string[] revisions = commit.FirstParent.Length == 0 ? [commit.Hash] : [commit.FirstParent, commit.Hash];
        // NUL 协议支持中文、空格及换行文件名，先检查不可变对象大小，再批量读取。
        string input = string.Concat(revisions.Select(revision => revision + ":" + file.Path + "\0"));
        string[] metadata = (await Runner.RunAsync(root, ["cat-file", "--batch-check", "-Z"], standardInput: input))
            .Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (metadata.Length != revisions.Length) throw new InvalidDataException("读取历史文件元信息失败。");
        string[] texts = new string[revisions.Length];
        List<(int Index, string Hash, int Size)> blobs = [];
        for (int i = 0; i < metadata.Length; i++)
        {
            if (metadata[i].EndsWith(" missing", StringComparison.Ordinal))
            {
                var exists = await Runner.RunAsync(root, ["ls-tree", "-z", revisions[i], "--", file.Path], true);
                if (exists.ExitCode != 0 || exists.Output.Length != 0) throw new InvalidOperationException("无法读取历史文件：" + file.Path);
                texts[i] = "";
                continue;
            }
            string[] fields = metadata[i].Split(' ');
            if (fields.Length != 3 || !long.TryParse(fields[2], out long size)) throw new InvalidDataException("历史文件元信息无效。");
            if (size > PreviewLimit) throw new PreviewException("文件较大，请使用外部编辑器查看。");
            if (fields[1] != "blob") throw new PreviewException("子模块或目录不显示文本差异。");
            blobs.Add((i, fields[0], checked((int)size)));
        }
        if (blobs.Count > 0)
        {
            string raw = (await Runner.RunAsync(root, ["cat-file", "--batch", "-Z"],
                standardInput: string.Concat(blobs.Select(blob => blob.Hash + "\0")), binaryOutput: true)).Output;
            int offset = 0;
            foreach (var blob in blobs)
            {
                int end = raw.IndexOf('\0', offset);
                if (end < 0 || raw[offset..end] != $"{blob.Hash} blob {blob.Size}" || end + 1L + blob.Size >= raw.Length || raw[end + 1 + blob.Size] != '\0')
                    throw new InvalidDataException("历史文件内容读取不完整。");
                byte[] bytes = Encoding.Latin1.GetBytes(raw.AsSpan(end + 1, blob.Size).ToString());
                if (bytes.Contains((byte)0)) throw new PreviewException("二进制文件不显示文本差异。");
                try
                {
                    string text = new UTF8Encoding(false, true).GetString(bytes);
                    texts[blob.Index] = text.StartsWith('\uFEFF') ? text[1..] : text;
                }
                catch (DecoderFallbackException) { throw new PreviewException("文件不是 UTF-8 文本，请使用外部编辑器查看。"); }
                offset = end + 2 + blob.Size;
            }
        }
        return (texts.Length == 1 ? "" : texts[0], texts[^1]);
    }
}
