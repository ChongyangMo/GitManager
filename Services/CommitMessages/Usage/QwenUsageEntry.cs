namespace GitManager.Services.CommitMessages.Usage;

public sealed class QwenUsageEntry
{
    public string Id { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    // 总结来源：Claude、Codex 或千问；旧记录没有此字段，均为千问调用。
    public string Provider { get; set; } = QwenProvider;
    public string Model { get; set; } = "";
    public string Result { get; set; } = "";
    public int? InputTokens { get; set; }
    public int? CachedTokens { get; set; }
    public int? OutputTokens { get; set; }
    public long Milliseconds { get; set; }
    public QwenCostEstimate Cost { get; set; } = new(null, null, "费用待确认");
    public QwenTokenPricing? Pricing { get; set; }
    public const string QwenProvider = "千问", ClaudeProvider = "Claude", CodexProvider = "Codex";
    [System.Text.Json.Serialization.JsonIgnore] public string ProviderLabel => string.IsNullOrWhiteSpace(Provider) ? QwenProvider : Provider;
}

public sealed record QwenUsageReadResult(IReadOnlyList<QwenUsageEntry> Entries, int UnreadableCount);
