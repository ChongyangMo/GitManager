using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitManager.Models;
using GitManager.Services.LuxAi;

namespace GitManager.Services.CommitMessages;

public sealed partial class CommitMessageGenerator
{
    private async Task<GeneratedCommitMessage> GenerateLuxAiAsync(string root, IReadOnlyList<ChangedFile> selected, CancellationToken token)
    {
        var options = settings.Value.LuxAi;
        _ = options.Endpoint();
        var session = LuxAiCredentials.Read(options) ?? throw new LuxAiLoginRequiredException();
        // 集团内接口单独处理：不经过 Claude、千问分支，失败也不把内容转给其他提供方。
        var limits = new QwenOptions { MaxInputCharacters = options.MaxInputCharacters, MaxFileCharacters = options.MaxFileCharacters };
        var reader = new CommitContentReader(git);
        var content = await reader.ReadAsync(root, selected, limits, token);
        string identity = session.EmployeeCode.Length > 0 ? session.EmployeeCode
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.AiToken)));
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Root = Path.GetFullPath(root), Provider = "luxai", Endpoint = options.Endpoint().AbsoluteUri,
            Employee = identity, Prompt = QwenPrompt.Version, QwenPrompt.System, content.Fingerprint, content.Text
        }))));
        var cache = new CommitSummaryCache(Path.Combine(Path.GetDirectoryName(settings.FilePath)!, "commit-summary-cache"));
        string? message = cache.Read(key);
        bool cached = message is not null;
        var routing = new RepositorySummaryRouting(git, settings);
        await routing.EnsureAsync([root], SummaryModelTarget.GroupAi, token);
        if (!cached)
        {
            using var client = new LuxAiClient(options);
            message = await client.GenerateAsync(content.Text, token);
        }
        var current = await reader.ReadAsync(root, selected, limits, token);
        if (content.Fingerprint != current.Fingerprint) throw new InvalidOperationException("总结期间文件内容已变化，请重新总结或提交。");
        await routing.EnsureAsync([root], SummaryModelTarget.GroupAi, token);
        token.ThrowIfCancellationRequested();
        if (!cached) cache.Save(key, message!);
        else AppLog.Write("已复用集团 AI 总结缓存，本次未调用接口");
        return new(message!, cached ? "集团 AI 本地缓存" : "集团 AI", content.Fingerprint,
            cached ? QwenCostEstimate.Local : new(null, null, "集团接口未返回模型型号、完整用量或费用，未作估算"));
    }
}
