using GitManager.Models.RepositoryCopies;
using GitManager.Services.CommitMessages;
using GitManager.Services.LuxAi;

namespace GitManager.Services.Repositories.Copies.Analysis;

public sealed record RepositoryCopyAnalysis(string Text, string Source, QwenCostEstimate Cost);

public sealed class RepositoryCopyAnalysisService(GitRepositoryService git, SettingsStore settings)
{
    public async Task<RepositoryCopyAnalysis> GenerateAsync(RepositoryCopyComparison comparison, CancellationToken token)
    {
        if (comparison.Copies.Count < 2) throw new InvalidOperationException("至少需要两份仓库才能分析。");
        var route = await CheckPolicyAsync(comparison, token);
        string[] roots = comparison.Copies.Select(copy => copy.Path).ToArray();
        var routing = new RepositorySummaryRouting(git, settings);
        var evidence = await new RepositoryCopyEvidenceReader(git).ReadAsync(comparison, token);
        string fingerprint = RepositoryCopyAnalysisPrompt.Fingerprint(comparison, evidence);
        int limit = route.Target == SummaryModelTarget.GroupAi ? settings.Value.LuxAi.MaxInputCharacters : settings.Value.Qwen.MaxInputCharacters;
        string content = RepositoryCopyAnalysisPrompt.Build(comparison, evidence, limit);
        await routing.EnsureAsync(roots, route.Target, token);
        var result = await GenerateTextAsync(content, route.Target, roots, token);
        // 耗时调用结束后重新读取：文件、分支、提交或忽略项发生变化时丢弃旧建议。
        var latest = await new RepositoryCopyReader(git).ReadAsync(comparison.Copies.Select(copy => copy.Bookmark).ToArray(), token);
        var latestEvidence = await new RepositoryCopyEvidenceReader(git).ReadAsync(latest, token);
        if (fingerprint != RepositoryCopyAnalysisPrompt.Fingerprint(latest, latestEvidence))
            throw new InvalidOperationException("分析期间仓库内容或提交已变化，请重新分析。");
        await routing.EnsureAsync(roots, route.Target, token);
        token.ThrowIfCancellationRequested();
        AppLog.Write("同名仓库大模型分析完成，来源：" + result.Source);
        return result;
    }

    private async Task<RepositorySummaryRoute> CheckPolicyAsync(RepositoryCopyComparison comparison, CancellationToken token)
    {
        if (!settings.Value.Qwen.Enabled) throw new InvalidOperationException("大模型总结已关闭，请在“设置 → 大模型总结”中开启。");
        foreach (var copy in comparison.Copies)
        {
            if (copy.Error.Length > 0) throw new InvalidOperationException("有仓库读取失败，请先确认目录可用后重新分析。");
        }
        var route = await new RepositorySummaryRouting(git, settings).ResolveAsync(comparison.Copies.Select(copy => copy.Path), token);
        if (route.Target == SummaryModelTarget.LocalRules)
            throw new InvalidOperationException("当前仓库无法调用模型分析：" + route.Reason + "。请在设置中启用集团 AI 后重新分析。");
        return route;
    }

    private async Task<RepositoryCopyAnalysis> GenerateTextAsync(string content, SummaryModelTarget target, string[] roots, CancellationToken token)
    {
        const string prompt = RepositoryCopyAnalysisPrompt.System;
        if (target == SummaryModelTarget.GroupAi)
        {
            // 集团模型失败时不将任何分析数据转发到外部模型。
            using var client = new LuxAiClient(settings.Value.LuxAi);
            string text = await client.GenerateAsync(content, token, prompt);
            return new(text, "集团 AI", new(null, null, "集团接口未返回完整用量或费用，未作估算"));
        }
        var claude = settings.Value.Claude;
        if (claude.Enabled && ClaudeClient.FindExecutable() is { } executable)
        {
            try
            {
                if (await ClaudeClient.IsLoggedInAsync(executable, token))
                {
                    await new RepositorySummaryRouting(git, settings).EnsureAsync(roots, SummaryModelTarget.PublicModels, token);
                    var reply = await ClaudeClient.GenerateAsync(claude, executable, content, token, systemPrompt: prompt);
                    return new(reply.Message, "Claude", reply.Cost);
                }
            }
            catch (Exception ex) when ((ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) && !token.IsCancellationRequested)
            { AppLog.Write("Claude 仓库分析失败，改用千问", ex); }
        }
        var options = settings.Value.Qwen;
        await new RepositorySummaryRouting(git, settings).EnsureAsync(roots, SummaryModelTarget.PublicModels, token);
        string key = QwenCredentials.Read(options) ?? throw new InvalidOperationException("外网仓库没有可用的大模型，请登录 Claude 或在设置中填写千问 API Key。");
        var qwen = await QwenClient.GenerateAsync(options, key, content, token, prompt);
        return new(qwen.Message, "千问", qwen.Cost);
    }
}
