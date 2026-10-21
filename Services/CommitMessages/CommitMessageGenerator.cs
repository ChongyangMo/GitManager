using System.IO;
using GitManager.Models;

namespace GitManager.Services.CommitMessages;

public sealed record GeneratedCommitMessage(string Message, string Source, string Fingerprint, QwenCostEstimate Cost);

public sealed class CommitMessageGenerator(GitRepositoryService git, SettingsStore settings)
{
    public async Task<GeneratedCommitMessage> GenerateAsync(string root, IReadOnlyList<ChangedFile> selected, CancellationToken token)
    {
        var options = settings.Value.Qwen;
        var policy = new RepositoryModelPolicy(git, settings);
        string? localReason = options.Enabled ? await policy.LocalOnlyReasonAsync(root, token) : null;
        if (!options.Enabled || localReason is not null)
        {
            if (localReason is not null) AppLog.Write("此仓库使用本地规则总结，已禁用大模型：" + localReason);
            return await GenerateLocalAsync(root, selected, token);
        }
        _ = options.Endpoint();
        string key = QwenCredentials.Read(options) ?? throw new InvalidOperationException("请先在“设置 → 千问总结”中填写 API Key。");
        var reader = new CommitContentReader(git);
        var content = await reader.ReadAsync(root, selected, options, token);
        var cache = new CommitSummaryCache(Path.Combine(Path.GetDirectoryName(settings.FilePath)!, "commit-summary-cache"));
        string cacheKey = CommitSummaryCache.Key(root, options, content);
        string? message = cache.Read(cacheKey);
        bool cached = message is not null;
        QwenCostEstimate cost = QwenCostEstimate.Local;
        if (!cached)
        {
            if (await policy.LocalOnlyReasonAsync(root, token) is { } changedReason)
            {
                AppLog.Write("仓库远程配置已变化，使用本地规则总结：" + changedReason);
                return await GenerateLocalAsync(root, selected, token);
            }
            var reply = await QwenClient.GenerateAsync(options, key, content.Text, token);
            message = reply.Message;
            cost = reply.Cost;
        }
        var current = await reader.ReadAsync(root, selected, options, token);
        if (content.Fingerprint != current.Fingerprint) throw new InvalidOperationException("总结期间文件内容已变化，请重新总结或提交。");
        token.ThrowIfCancellationRequested();
        if (!cached) cache.Save(cacheKey, message!);
        else AppLog.Write("已复用本地千问总结，无需再次调用接口，本次 ¥0");
        return new(message!, cached ? "本地缓存" : "千问", content.Fingerprint, cost);
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
