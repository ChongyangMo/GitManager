using System.IO;
using GitManager.Models;

namespace GitManager.Services.CommitMessages;

public sealed record GeneratedCommitMessage(string Message, string Source, string Fingerprint, QwenCostEstimate Cost);

public sealed partial class CommitMessageGenerator(GitRepositoryService git, SettingsStore settings)
{
    public async Task<GeneratedCommitMessage> GenerateAsync(string root, IReadOnlyList<ChangedFile> selected, CancellationToken token)
    {
        var options = settings.Value.Qwen;
        var routing = new RepositorySummaryRouting(git, settings);
        var route = await routing.ResolveAsync([root], token);
        if (route.Target == SummaryModelTarget.GroupAi)
            return await GenerateLuxAiAsync(root, selected, token);
        if (route.Target == SummaryModelTarget.LocalRules)
        {
            AppLog.Write("此仓库使用本地规则总结：" + route.Reason);
            return await GenerateLocalAsync(root, selected, token);
        }
        var reader = new CommitContentReader(git);
        var content = await reader.ReadAsync(root, selected, options, token);
        var cache = new CommitSummaryCache(Path.Combine(Path.GetDirectoryName(settings.FilePath)!, "commit-summary-cache"));
        var claude = settings.Value.Claude;
        string claudeKey = CommitSummaryCache.ClaudeKey(root, claude, content);
        string codexKey = CommitSummaryCache.CodexKey(root, settings.Value.Codex, content);
        string? qwenKey = null;
        try { qwenKey = CommitSummaryCache.Key(root, options, content); }
        catch (InvalidOperationException) { }
        string? message = cache.Read(claudeKey) ?? cache.Read(codexKey) ?? (qwenKey is null ? null : cache.Read(qwenKey));
        bool cached = message is not null;
        string source = "本地缓存", cacheKey = claudeKey;
        QwenCostEstimate cost = QwenCostEstimate.Local;
        if (!cached)
        {
            var provider = new PublicModelSummary(git, settings);
            var reply = await CommitBatchSummary.GenerateAsync(content, options.MaxInputCharacters,
                (text, prompt, cancellation) => provider.GenerateAsync([root], text, cancellation, prompt), token);
            (message, cost, source) = (reply.Message, reply.Cost, reply.Source);
            cacheKey = source switch { "Claude" => claudeKey, "Codex" => codexKey, _ => qwenKey! };
        }
        var current = await reader.ReadAsync(root, selected, options, token);
        if (content.Fingerprint != current.Fingerprint) throw new InvalidOperationException("总结期间文件内容已变化，请重新总结或提交。");
        await routing.EnsureAsync([root], SummaryModelTarget.PublicModels, token);
        token.ThrowIfCancellationRequested();
        if (!cached) cache.Save(cacheKey, message!);
        else AppLog.Write("已复用本地总结缓存，无需再次调用接口，本次 ¥0");
        return new(message!, source, content.Fingerprint, cost);
    }

    private async Task<GeneratedCommitMessage> GenerateLocalAsync(string root, IReadOnlyList<ChangedFile> selected, CancellationToken token)
    {
        var reader = new LocalCommitSummaryReader(git);
        var source = await reader.ReadAsync(root, selected, token);
        var latest = await reader.ReadAsync(root, selected, token);
        if (source.Fingerprint != latest.Fingerprint) throw new InvalidOperationException("改动已变化，请重新总结。");
        return new(LocalCommitSummary.Generate(source.Files), "本地规则", source.Fingerprint, QwenCostEstimate.Local);
    }
}
