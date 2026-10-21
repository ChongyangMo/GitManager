using System.Globalization;
using GitManager.Services.CommitMessages;
using GitManager.Services.CommitMessages.Usage;

namespace GitManager.ViewModels.CommitMessages.Usage;

public sealed class QwenUsageSummary(IReadOnlyList<QwenUsageEntry> entries)
{
    public string Calls => entries.Count.ToString("N0");
    public string Providers => entries.Count == 0 ? "" : string.Join(" · ", new[] { QwenUsageEntry.ClaudeProvider, QwenUsageEntry.CodexProvider, QwenUsageEntry.QwenProvider }
        .Select(provider => $"{provider} {entries.Count(entry => entry.ProviderLabel == provider):N0}"));
    public string Input => Tokens(entry => entry.InputTokens);
    public string Output => Tokens(entry => entry.OutputTokens);
    // 缓存命中只影响千问计费，缓存统计只计千问调用。
    private IReadOnlyList<QwenUsageEntry> QwenEntries => entries.Where(entry => entry.ProviderLabel == QwenUsageEntry.QwenProvider).ToArray();
    public string Cached => Tokens(entry => entry.CachedTokens is >= 0 && entry.CachedTokens <= entry.InputTokens ? entry.CachedTokens : null, QwenEntries);
    public string CacheRatio
    {
        get
        {
            var entries = QwenEntries;
            if (entries.Count == 0 || entries.Any(entry => entry.InputTokens is null or < 0 || entry.CachedTokens is null or < 0 || entry.CachedTokens > entry.InputTokens))
                return "缓存比例待确认";
            long total = entries.Sum(entry => (long)entry.InputTokens!.Value);
            return total > 0 ? ((double)entries.Sum(entry => (long)entry.CachedTokens!.Value) / total).ToString("P1", CultureInfo.InvariantCulture) : "";
        }
    }
    public string Cost
    {
        get
        {
            var known = entries.Where(entry => entry.Cost.Minimum is >= 0m && entry.Cost.Maximum >= entry.Cost.Minimum).ToArray();
            int unknown = entries.Count - known.Length;
            if (known.Length == 0 && unknown > 0) return $"{unknown} 笔待确认";
            var sum = new QwenCostEstimate(known.Sum(entry => entry.Cost.Minimum!.Value), known.Sum(entry => entry.Cost.Maximum!.Value), "");
            string label = sum.Label.Replace("本次 ", "").Replace("预估 ", "");
            return label + (unknown > 0 ? $" + {unknown} 笔待确认" : "");
        }
    }

    private string Tokens(Func<QwenUsageEntry, int?> get, IReadOnlyList<QwenUsageEntry>? source = null)
    {
        var values = (source ?? entries).Select(get).ToArray();
        int unknown = values.Count(value => value is null or < 0);
        if (unknown == values.Length && unknown > 0) return "待确认";
        return values.Where(value => value is >= 0).Sum(value => (long)value!.Value).ToString("N0") + (unknown > 0 ? " + 待确认" : "");
    }
}

public sealed class QwenUsageRow(QwenUsageEntry entry)
{
    public string Time => entry.Time.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string Provider => entry.ProviderLabel;
    public string Model => entry.Model;
    public string Result => entry.Result;
    public string Input => Count(entry.InputTokens);
    public string Output => Count(entry.OutputTokens);
    public string Cached => entry.ProviderLabel != QwenUsageEntry.QwenProvider && entry.CachedTokens is null ? "—"
        : entry.CachedTokens <= entry.InputTokens ? Count(entry.CachedTokens) : "未返回";
    public string Cost => entry.Cost.Label.Replace("预估 ", "").Replace("本次 ", "");
    public string Duration => (entry.Milliseconds / 1000d).ToString("0.00", CultureInfo.InvariantCulture) + " 秒";
    public string Detail => entry.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "\n" + entry.Cost.Detail
        + (entry.Pricing is { } price ? $"\n计价配置：{price.Model} · {price.Region} · {price.UpdatedOn}"
            + $"\n每百万 Token：输入 ¥{price.InputPerMillion} · 缓存 ¥{price.CachedInputPerMillion} · 输出 ¥{price.OutputPerMillion}" : "");
    private static string Count(int? count) => count is >= 0 ? count.Value.ToString("N0") : "未返回";
}
