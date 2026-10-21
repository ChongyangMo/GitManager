namespace GitManager.Services.CommitMessages;

// 设置窗口展示的总结顺位，与 CommitMessageGenerator 实际的选择顺序一致。
public static class SummaryProviderOrder
{
    public static bool ClaudeInstalled(ClaudeOptions claude) => claude.Enabled && ClaudeClient.FindExecutable() is not null;

    public static async Task<string> DescribeAsync(QwenOptions qwen, ClaudeOptions claude, CancellationToken token, LuxAi.LuxAiOptions? luxAi = null)
    {
        if (!qwen.Enabled) return "已关闭大模型总结，全部使用本地规则。";
        string claudeState;
        if (!claude.Enabled) claudeState = "已在配置中关闭，跳过";
        else if (ClaudeClient.FindExecutable() is not { } executable) claudeState = "本机未安装，跳过";
        else if (await ClaudeClient.IsLoggedInAsync(executable, token)) claudeState = "已登录";
        else claudeState = "未登录，跳过";
        bool hasKey;
        try { hasKey = QwenCredentials.Read(qwen) is { Length: > 0 }; }
        catch (InvalidOperationException) { hasKey = false; }
        string internalState = luxAi is { Enabled: true }
            ? "集团 AI · " + (LuxAi.LuxAiCredentials.Read(luxAi) is not null ? "已连接" : "待登录")
            : "本地规则（集团 AI 未启用）";
        return $"内网：{internalState}\n"
            + $"外网：Claude 优先（{claudeState}），千问兜底（{(hasKey ? "已保存 API Key" : "未填写 API Key")}）";
    }
}
