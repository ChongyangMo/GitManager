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
        string? qwenKey = null;
        try { qwenKey = CommitSummaryCache.Key(root, options, content); }
        catch (InvalidOperationException) { }
        string? message = cache.Read(claudeKey) ?? (qwenKey is null ? null : cache.Read(qwenKey));
        bool cached = message is not null;
        string source = "本地缓存", cacheKey = claudeKey;
        QwenCostEstimate cost = QwenCostEstimate.Local;
        if (!cached)
        {
            await routing.EnsureAsync([root], SummaryModelTarget.PublicModels, token);
            if (await TryClaudeAsync(root, claude, content.Text, token) is { } reply)
                (message, cost, source) = (reply.Message, reply.Cost, "Claude");
            else
            {
                await routing.EnsureAsync([root], SummaryModelTarget.PublicModels, token);
                _ = options.Endpoint();
                string key = QwenCredentials.Read(options) ?? throw new InvalidOperationException("本机 Claude 不可用，请先在“设置 → 大模型总结”中填写 API Key。");
                var qwen = await QwenClient.GenerateAsync(options, key, content.Text, token);
                (message, cost, source, cacheKey) = (qwen.Message, qwen.Cost, "千问", qwenKey!);
            }
        }
        var current = await reader.ReadAsync(root, selected, options, token);
        if (content.Fingerprint != current.Fingerprint) throw new InvalidOperationException("总结期间文件内容已变化，请重新总结或提交。");
        await routing.EnsureAsync([root], SummaryModelTarget.PublicModels, token);
        token.ThrowIfCancellationRequested();
        if (!cached) cache.Save(cacheKey, message!);
        else AppLog.Write("已复用本地总结缓存，无需再次调用接口，本次 ¥0");
        return new(message!, source, content.Fingerprint, cost);
    }

    // 本机没有 Claude、未登录或调用失败时返回 null，由千问接手。
    private async Task<ClaudeReply?> TryClaudeAsync(string root, ClaudeOptions options, string content, CancellationToken token)
    {
        if (!options.Enabled) return null;
        if (ClaudeClient.FindExecutable() is not { } executable) { AppLog.Write("本机未安装 Claude Code，使用千问总结"); return null; }
        if (!await ClaudeClient.IsLoggedInAsync(executable, token)) { AppLog.Write("本机 Claude Code 未登录，使用千问总结"); return null; }
        try
        {
            await new RepositorySummaryRouting(git, settings).EnsureAsync([root], SummaryModelTarget.PublicModels, token);
            return await ClaudeClient.GenerateAsync(options, executable, content, token);
        }
        catch (Exception ex) when ((ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) && !token.IsCancellationRequested)
        {
            AppLog.Write("Claude 总结失败，改用千问：" + AppLog.Clean(ex.Message));
            return null;
        }
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
