namespace GitManager.Services.CommitMessages.Usage;

public sealed class QwenUsageEntry
{
    public string Id { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public string Model { get; set; } = "";
    public string Result { get; set; } = "";
    public int? InputTokens { get; set; }
    public int? CachedTokens { get; set; }
    public int? OutputTokens { get; set; }
    public long Milliseconds { get; set; }
    public QwenCostEstimate Cost { get; set; } = new(null, null, "费用待确认");
    public QwenTokenPricing? Pricing { get; set; }
}

public sealed record QwenUsageReadResult(IReadOnlyList<QwenUsageEntry> Entries, int UnreadableCount);
