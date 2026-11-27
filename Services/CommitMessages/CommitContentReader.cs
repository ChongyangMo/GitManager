using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GitManager.Models;
using GitManager.Services.Remote;

namespace GitManager.Services.CommitMessages;

public sealed record CommitContent(string Text, string Fingerprint, IReadOnlyList<string>? Batches = null)
{
    public IReadOnlyList<string> Parts => Batches ?? [Text];
}

public sealed class CommitContentReader(GitRepositoryService git)
{
    private static readonly JsonSerializerOptions ReadableJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public async Task<CommitContent> ReadAsync(string root, IReadOnlyList<ChangedFile> selected, QwenOptions options, CancellationToken token)
    {
        var source = await new LocalCommitSummaryReader(git).ReadAsync(root, selected, token);
        var head = await git.Runner.RunAsync(root, ["rev-parse", "--verify", "HEAD"], true, token);
        var batches = new CommitContentBatchBuilder(options);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHash(source.Fingerprint);
        Dictionary<string, RemoteWorkingFileInfo>? remoteInfos = null;
        if (RemoteRepositoryPath.TryParse(root, out string alias, out _))
        {
            string gitPath = git.Runner.Remote?.Options.Find(alias)?.EffectiveGitPath ?? "/usr/bin/git";
            var requests = source.Files.Select(file =>
            {
                bool tracked = file.Action != "新增" || file.Added.HasValue;
                bool needText = !file.Binary && (!tracked || head.ExitCode != 0);
                return (file.Path, needText);
            }).ToArray();
            remoteInfos = await git.RemoteFiles.InspectWorkingFilesAsync(root, requests, gitPath, token);
        }
        foreach (var file in source.Files)
        {
            token.ThrowIfCancellationRequested();
            string contentHash = "不存在";
            bool isFile = false, isLink = false;
            string? linkTarget = null;
            RemoteWorkingFileInfo? remoteInfo = null;
            if (remoteInfos is not null)
            {
                remoteInfos.TryGetValue(file.Path, out remoteInfo);
                if (remoteInfo is not null)
                {
                    contentHash = remoteInfo.ContentHash;
                    isFile = remoteInfo.IsFile;
                    isLink = remoteInfo.IsLink;
                    linkTarget = remoteInfo.LinkTarget;
                }
            }
            else
            {
                string path = Path.Combine(root, file.Path);
                isFile = File.Exists(path);
                isLink = (isFile || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
                if (isLink) { linkTarget = new FileInfo(path).LinkTarget; contentHash = "链接:" + linkTarget; }
                else if (Directory.Exists(path))
                    contentHash = (await git.Runner.RunAsync(path, ["rev-parse", "--verify", "HEAD"], true, token)).Output;
                else if (isFile)
                {
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    contentHash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                }
            }
            AppendHash(JsonSerializer.Serialize(new { file.Path, file.OriginalPath, contentHash }));
            string details = JsonSerializer.Serialize(new { file.Path, file.OriginalPath, file.Action }, ReadableJson);
            string patch;
            bool tracked = file.Action != "新增" || file.Added.HasValue;
            if (file.Binary) patch = "二进制文件，省略内容。";
            else if (!tracked || head.ExitCode != 0)
            {
                if (remoteInfos is not null)
                {
                    patch = isLink ? "符号链接目标：" + linkTarget
                        : remoteInfo is { IsBinary: true } ? "二进制文件，省略内容。"
                        : remoteInfo is { InvalidUtf8: true } ? "文件不是可识别的文本，省略内容。"
                        : remoteInfo is { IsFile: true, Text: not null } ? remoteInfo.Text
                        : "文件内容不可读取。";
                }
                else
                {
                    string path = Path.Combine(root, file.Path);
                    patch = isLink ? "符号链接目标：" + linkTarget
                        : isFile ? await ReadNewFileAsync(path, token) : "文件内容不可读取。";
                }
            }
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
            batches.AddFile(details, JsonSerializer.Serialize(new { file.Added, file.Removed, file.Binary }), patch);
        }
        var parts = batches.Build();
        return new(string.Join('\n', parts), Convert.ToHexString(hash.GetHashAndReset()), parts);

        void AppendHash(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes);
        }
    }

    private static async Task<string> ReadNewFileAsync(string path, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            // 先检查文件开头，避免将明显的二进制文件完整读为文本。
            var buffer = new char[4096];
            int count = await reader.ReadBlockAsync(buffer.AsMemory(), token);
            if (buffer.AsSpan(0, count).Contains('\0')) return "二进制文件，省略内容。";
            string text = new string(buffer, 0, count) + await reader.ReadToEndAsync(token);
            return text.Contains('\0') ? "二进制文件，省略内容。" : text;
        }
        catch (DecoderFallbackException) { return "文件不是可识别的文本，省略内容。"; }
    }
}
