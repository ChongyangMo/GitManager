using System.IO;
using System.Text;
using GitManager.Services;
using GitManager.Services.CommitMessages;
using GitManager.Services.LuxAi;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunSummaryBatchChecksAsync(string repository, bool live)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        string sourceHead = (await Runner.RunAsync(repository, ["rev-parse", "HEAD"], cancellationToken: token)).Output.Trim();
        string parent = (await Runner.RunAsync(repository, ["rev-parse", "HEAD^"], cancellationToken: token)).Output.Trim();
        var paths = (await Runner.RunAsync(repository, ["diff", "--name-only", "-z", parent, sourceHead], cancellationToken: token))
            .Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length == 0) throw new InvalidOperationException("真实提交没有文件改动。");
        // 仅重放该真实提交涉及的文件，不修改原仓库，不生成虚构差异。
        string root = Path.Combine(Path.GetTempPath(), "GitManagerActualBatchChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await Git.InitializeAsync(root);
        await Git.SaveIdentityAsync(root, "ChongyangMo", ChecksCommitEmail);
        await RestoreFilesAsync(parent);
        await Runner.RunAsync(root, ["add", "--all"], cancellationToken: token);
        await Runner.RunAsync(root, ["commit", "--allow-empty", "-m", "保存真实提交的父版本用于完整输入检查"], cancellationToken: token);
        await RestoreFilesAsync(sourceHead);
        var files = await Git.ReadChangesAsync(root, token);
        Check(files.Count == paths.Length, "检查目录完整重放真实提交的全部文件改动");
        var settings = new SettingsStore();
        var options = settings.Value.LuxAi;
        var limits = new QwenOptions { MaxInputCharacters = options.MaxInputCharacters, MaxFileCharacters = options.MaxFileCharacters };
        var reader = new CommitContentReader(Git);
        var content = await reader.ReadAsync(root, files, limits, token);
        Check(content.Parts.Count > 1 && content.Parts.All(part => part.Length <= options.MaxInputCharacters),
            "超过集团输入限制的真实改动分成多批，每批均在上限内");
        Check(paths.All(path => content.Text.Contains(path, StringComparison.Ordinal)), "全部实际改动文件均包含在输入中，没有省略尾部文件");
        Check(!content.Text.Contains("内容已截断") && !content.Text.Contains("因输入长度限制未展示"), "输入不再截断或省略文本差异");
        string patch = (await Runner.RunAsync(root, ["diff", "HEAD", "--no-ext-diff", "--no-textconv"], cancellationToken: token)).Output;
        var pieces = SummaryTextBatches.Split(patch, 257).ToArray();
        Check(string.Concat(pieces) == patch && pieces.All(piece => piece.Length <= 257), "真实差异按整行和长行分片后可无损重组");
        if (paths.Contains("backend/internal/http/api_token_test.go", StringComparer.Ordinal))
            Check(content.Text.Contains("func TestUsernameFromSessionUsesTokenUserOnlyFromContext")
                && content.Text.Contains("令牌用户=%q 识别=%t"), "用户指出的测试函数及原先截断后的函数尾部完整包含在输入中");
        Console.WriteLine($"真实提交 {sourceHead[..8]}：{files.Count} 个文件，{content.Text.Length} 字符，{content.Parts.Count} 批");
        string indexBefore = (await Runner.RunAsync(root, ["diff", "--cached", "--raw", "-z"], cancellationToken: token)).Output;
        if (live)
        {
            var routing = new RepositorySummaryRouting(Git, settings);
            await routing.EnsureAsync([repository], SummaryModelTarget.GroupAi, token);
            using var client = new LuxAiClient(options);
            int calls = 0;
            var result = await CommitBatchSummary.GenerateAsync(content, options.MaxInputCharacters, async (text, prompt, cancellation) =>
            {
                await routing.EnsureAsync([repository], SummaryModelTarget.GroupAi, cancellation);
                calls++;
                return new(await client.GenerateAsync(text, cancellation, prompt), "集团 AI", new(null, null, "实际费用待确认"));
            }, token);
            Check(calls >= content.Parts.Count + 1 && result.Source == "集团 AI", "所有原始批次及最终合并均通过集团真实接口完成");
            Check(!result.Message.Contains("[内容已截断]") && !result.Message.Contains("因输入长度限制未展示"), "最终说明不包含原来的截断提示");
            Check(result.Cost.Minimum is null && result.Cost.Maximum is null, "集团分批调用费用未知时仍保持待确认");
            Console.WriteLine(result.Message);
            Check((await reader.ReadAsync(root, files, limits, token)).Fingerprint == content.Fingerprint,
                "模型总结结束后实际文件内容指纹保持一致");
        }
        Check((await Runner.RunAsync(root, ["diff", "--cached", "--raw", "-z"], cancellationToken: token)).Output == indexBefore
            && (await Runner.RunAsync(repository, ["rev-parse", "HEAD"], cancellationToken: token)).Output.Trim() == sourceHead,
            "总结没有改变原仓库提交或检查目录暂存区");

        async Task RestoreFilesAsync(string revision)
        {
            foreach (string path in paths)
            {
                string target = Path.GetFullPath(Path.Combine(root, path));
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("真实提交路径超出检查目录。");
                var result = await Runner.RunAsync(repository, ["show", revision + ":" + path], allowFailure: true,
                    cancellationToken: token, binaryOutput: true);
                if (result.ExitCode == 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllBytesAsync(target, Encoding.Latin1.GetBytes(result.Output), token);
                }
                else if (File.Exists(target)) File.Delete(target);
            }
        }
    }
}
