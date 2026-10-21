namespace GitManager.Services.CommitMessages;

public sealed record PublicModelReply(string Message, string Source, QwenCostEstimate Cost);

// 提交总结和仓库分析共用外网顺序，每次对外调用前重新检查所有仓库的路由。
public sealed class PublicModelSummary(GitRepositoryService git, SettingsStore settings)
{
    public async Task<PublicModelReply> GenerateAsync(string[] roots, string content, CancellationToken token, string? prompt = null)
    {
        var routing = new RepositorySummaryRouting(git, settings);
        var claude = settings.Value.Claude;
        if (claude.Enabled && ClaudeClient.FindExecutable() is { } claudeExecutable
            && await ClaudeClient.IsLoggedInAsync(claudeExecutable, token))
        {
            await routing.EnsureAsync(roots, SummaryModelTarget.PublicModels, token);
            try
            {
                var reply = await ClaudeClient.GenerateAsync(claude, claudeExecutable, content, token, systemPrompt: prompt);
                return new(reply.Message, "Claude", reply.Cost);
            }
            catch (Exception ex) when (CanFallback(ex, token)) { AppLog.Write("Claude 总结失败，尝试 Codex：" + AppLog.Clean(ex.Message)); }
        }
        var codex = settings.Value.Codex;
        if (codex.Enabled && CodexClient.FindExecutable(codex) is { } codexExecutable
            && await CodexClient.IsLoggedInAsync(codexExecutable, token))
        {
            await routing.EnsureAsync(roots, SummaryModelTarget.PublicModels, token);
            try
            {
                var reply = await CodexClient.GenerateAsync(codex, codexExecutable, content, token, systemPrompt: prompt);
                return new(reply.Message, "Codex", reply.Cost);
            }
            catch (Exception ex) when (CanFallback(ex, token)) { AppLog.Write("Codex 总结失败，改用千问：" + AppLog.Clean(ex.Message)); }
        }
        await routing.EnsureAsync(roots, SummaryModelTarget.PublicModels, token);
        token.ThrowIfCancellationRequested();
        var options = settings.Value.Qwen;
        string key = QwenCredentials.Read(options) ?? throw new InvalidOperationException("外网没有可用的大模型，请登录 Claude、Codex，或在“设置 → 大模型总结”中填写千问 API Key。");
        var qwen = await QwenClient.GenerateAsync(options, key, content, token, prompt);
        return new(qwen.Message, "千问", qwen.Cost);
    }

    private static bool CanFallback(Exception ex, CancellationToken token) => !token.IsCancellationRequested
        && ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or UnauthorizedAccessException;
}
