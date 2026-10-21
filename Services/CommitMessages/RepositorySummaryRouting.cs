namespace GitManager.Services.CommitMessages;

public enum SummaryModelTarget { LocalRules, GroupAi, PublicModels }
public sealed record RepositorySummaryRoute(SummaryModelTarget Target, string Reason);

// 提交总结和仓库分析共用路由；多份仓库中只要含非公网内容，就不能交给公网模型。
public sealed class RepositorySummaryRouting(GitRepositoryService git, SettingsStore settings)
{
    public async Task<RepositorySummaryRoute> ResolveAsync(IEnumerable<string> roots, CancellationToken token)
    {
        if (!settings.Value.Qwen.Enabled) return new(SummaryModelTarget.LocalRules, "已关闭大模型总结");
        var policy = new RepositoryModelPolicy(git, settings);
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await policy.ExternalModelBlockReasonAsync(root, token) is not { } reason) continue;
            return settings.Value.LuxAi.Enabled
                ? new(SummaryModelTarget.GroupAi, reason)
                : new(SummaryModelTarget.LocalRules, reason + "，未启用集团 AI");
        }
        return new(SummaryModelTarget.PublicModels, "公网仓库，Claude → Codex → 千问");
    }

    public async Task EnsureAsync(IEnumerable<string> roots, SummaryModelTarget expected, CancellationToken token)
    {
        var current = await ResolveAsync(roots, token);
        if (current.Target != expected)
            throw new InvalidOperationException("仓库远程或模型设置已变化，请重新总结或分析。");
    }
}
