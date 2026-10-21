using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GitManager.Models;

namespace GitManager.Services.CommitMessages;

public sealed record CommitContent(string Text, string Fingerprint);

public sealed class CommitContentReader(GitRepositoryService git)
{
    private static readonly JsonSerializerOptions ReadableJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public async Task<CommitContent> ReadAsync(string root, IReadOnlyList<ChangedFile> selected, QwenOptions options, CancellationToken token)
    {
        var source = await new LocalCommitSummaryReader(git).ReadAsync(root, selected, token);
        var head = await git.Runner.RunAsync(root, ["rev-parse", "--verify", "HEAD"], true, token);
        var text = new StringBuilder("请总结以下真实 Git 改动。\n");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHash(source.Fingerprint);
        int omitted = 0;
        foreach (var file in source.Files)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.Combine(root, file.Path);
            string contentHash = "不存在";
            bool isFile = File.Exists(path), isLink = (isFile || Directory.Exists(path))
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            if (isLink) contentHash = "链接:" + new FileInfo(path).LinkTarget;
            else if (Directory.Exists(path))
                contentHash = (await git.Runner.RunAsync(path, ["rev-parse", "--verify", "HEAD"], true, token)).Output;
            else if (isFile)
            {
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                contentHash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
            }
            AppendHash(JsonSerializer.Serialize(new { file.Path, file.OriginalPath, contentHash }));
            string details = JsonSerializer.Serialize(new { file.Path, file.OriginalPath, file.Action }, ReadableJson);
            string patch;
            bool tracked = file.Action != "新增" || file.Added.HasValue;
            if (file.Binary) patch = "二进制文件，省略内容。";
            else if (text.Length + details.Length > options.MaxInputCharacters - 150 ||
                (file.Added ?? 0) + (long)(file.Removed ?? 0) > 6000)
                patch = "文件差异较长，省略内容。";
            else if (!tracked || head.ExitCode != 0)
                patch = isLink ? "符号链接目标：" + new FileInfo(path).LinkTarget
                    : isFile ? await ReadNewFileAsync(path, options.MaxFileCharacters, token) : "文件内容不可读取。";
            else
            {
                var paths = file.OriginalPath is null ? new[] { file.Path } : new[] { file.OriginalPath, file.Path };
                patch = (await git.Runner.RunAsync(root,
                    new[] { "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--unified=3", head.Output.Trim(), "--" }.Concat(paths),
                    cancellationToken: token)).Output;
                // 子模块提交号等不一定体现在行数中，也必须参与内容指纹。
                AppendHash(patch);
                // 对象编号每次都会变化；移除该差异头，保留真正的代码变化与模式信息。
                patch = string.Join('\n', patch.Replace("\r\n", "\n").Split('\n')
                    .Where(line => !line.StartsWith("index ", StringComparison.Ordinal)));
            }
            if (patch.Length > options.MaxFileCharacters) patch = patch[..options.MaxFileCharacters] + "\n[文件内容已截断]";
            string block = "\n文件：" + details + "\n差异或新增内容：\n" + patch
                + "\n统计：" + JsonSerializer.Serialize(new { file.Added, file.Removed, file.Binary }) + "\n";
            int available = options.MaxInputCharacters - text.Length - 100;
            if (available <= details.Length + 20) { omitted++; continue; }
            if (block.Length > available) block = block[..Math.Max(0, available - 20)] + "\n[内容已截断]\n";
            text.Append(block);
        }
        if (omitted > 0) text.Append($"\n另有 {omitted} 个文件因输入长度限制未展示，不推断其具体改动。\n");
        return new(text.ToString(), Convert.ToHexString(hash.GetHashAndReset()));

        void AppendHash(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes);
        }
    }

    private static async Task<string> ReadNewFileAsync(string path, int limit, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var buffer = new char[limit + 1];
            int count = await reader.ReadBlockAsync(buffer.AsMemory(), token);
            if (buffer.AsSpan(0, count).Contains('\0')) return "二进制文件，省略内容。";
            return new string(buffer, 0, Math.Min(limit, count)) + (count > limit ? "\n[新增文件内容已截断]" : "");
        }
        catch (DecoderFallbackException) { return "文件不是可识别的文本，省略内容。"; }
    }
}
