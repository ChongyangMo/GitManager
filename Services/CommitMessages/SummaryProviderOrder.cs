namespace GitManager.Services.CommitMessages;

// 设置窗口展示的总结顺位，与 CommitMessageGenerator 实际的选择顺序一致。
public static class SummaryProviderOrder
{
    public static bool ClaudeInstalled(ClaudeOptions claude) => claude.Enabled && ClaudeClient.FindExecutable() is not null;

    public static async Task<string> DescribeAsync(QwenOptions qwen, ClaudeOptions claude, CancellationToken token)
    {
        if (!qwen.Enabled) return "已关闭大模型总结，全部使用本地规则。";
        bool claudeReady = false;
        string claudeState;
        if (!claude.Enabled) claudeState = "已在配置中关闭，跳过";
        else if (ClaudeClient.FindExecutable() is not { } executable) claudeState = "本机未安装，跳过";
        else if (await ClaudeClient.IsLoggedInAsync(executable, token)) { claudeState = "已登录"; claudeReady = true; }
        else claudeState = "未登录，跳过";
        bool hasKey;
        try { hasKey = QwenCredentials.Read(qwen) is { Length: > 0 }; }
        catch (InvalidOperationException) { hasKey = false; }
        string current = claudeReady ? "Claude" : hasKey ? "千问" : "无可用模型，请登录 Claude 或填写千问 API Key";
        return $"1. Claude · {claude.Model} · {claudeState}\n"
            + $"2. 千问 · {qwen.Model} · {(hasKey ? "已保存 API Key" : "未填写 API Key")}\n"
            + $"当前使用：{current}\n前一位调用失败时自动改用下一位；服务云和内网仓库始终使用本地规则。";
    }
}
