namespace GitManager.Services.CommitMessages;

// 本机已安装并登录 Claude Code 时优先用它总结，调用失败再依次尝试 Codex、千问。
public sealed class ClaudeOptions
{
    public bool Enabled { get; set; } = true;
    public string Model { get; set; } = "claude-haiku-5-5";
    public int TimeoutSeconds { get; set; } = 90;
}
