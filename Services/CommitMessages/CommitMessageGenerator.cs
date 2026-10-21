using System.Globalization;
using System.IO;
using GitManager.Models;

namespace GitManager.Services.CommitMessages;

public sealed record GeneratedCommitMessage(string Message, string Source, string Fingerprint);

public sealed class CommitMessageGenerator(GitRepositoryService git, SettingsStore settings)
{
    public async Task<GeneratedCommitMessage> GenerateAsync(string root, IReadOnlyList<ChangedFile> selected, CancellationToken token)
    {
        var options = settings.Value.Qwen;
        if (!options.Enabled)
        {
            var localReader = new LocalCommitSummaryReader(git);
            var local = await localReader.ReadAsync(root, selected, token);
            var latest = await localReader.ReadAsync(root, selected, token);
            if (local.Fingerprint != latest.Fingerprint) throw new InvalidOperationException("改动已变化，请重新总结。");
            return new(LocalCommitSummary.Generate(local.Files), "本地规则", local.Fingerprint);
        }
        _ = options.Endpoint();
        string key = QwenCredentials.Read(options) ?? throw new InvalidOperationException("请先在“设置 → 千问总结”中填写 API Key。");
        var reader = new CommitContentReader(git);
        var content = await reader.ReadAsync(root, selected, options, token);
        var cache = new CommitSummaryCache(Path.Combine(Path.GetDirectoryName(settings.FilePath)!, "commit-summary-cache"));
        string cacheKey = CommitSummaryCache.Key(root, options, content);
        string? message = cache.Read(cacheKey);
        bool cached = message is not null;
        if (!cached)
        {
            var reply = await QwenClient.GenerateAsync(options, key, content.Text, token);
            message = reply.Message;
            string ratio = reply.InputTokens is > 0 && reply.CachedTokens is { } hits
                ? ((double)hits / reply.InputTokens.Value).ToString("P1", CultureInfo.InvariantCulture) : "未返回";
            AppLog.Write($"千问总结完成：模型 {options.Model}，输入 {Count(reply.InputTokens)}，缓存命中 {Count(reply.CachedTokens)}，"
                + $"输出 {Count(reply.OutputTokens)} 个令牌，缓存比例 {ratio}，耗时 {reply.Milliseconds} 毫秒");
        }
        var current = await reader.ReadAsync(root, selected, options, token);
        if (content.Fingerprint != current.Fingerprint) throw new InvalidOperationException("总结期间文件内容已变化，请重新总结或提交。");
        token.ThrowIfCancellationRequested();
        if (!cached) cache.Save(cacheKey, message!);
        else AppLog.Write("已复用本地千问总结，无需再次调用接口");
        return new(message!, cached ? "本地缓存" : "千问", content.Fingerprint);
    }

    private static string Count(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "未返回";
}
