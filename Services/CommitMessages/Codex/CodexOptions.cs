namespace GitManager.Services.CommitMessages;

public sealed class CodexOptions
{
    public bool Enabled { get; set; } = true;
    public string ExecutablePath { get; set; } = "";
    public string Model { get; set; } = "gpt-6-luna";
    public string ReasoningEffort { get; set; } = "low";
    public int TimeoutSeconds { get; set; } = 90;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model) || Model.Any(char.IsControl)
            || ReasoningEffort is not ("low" or "medium" or "high" or "xhigh" or "max")
            || TimeoutSeconds is < 10 or > 600)
            throw new InvalidOperationException("Codex 配置中的模型、推理强度或超时设置无效。");
    }
}
