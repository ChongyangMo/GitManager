using System.Globalization;

namespace GitManager.Services.CommitMessages;

public sealed class QwenTokenPricing
{
    public string Model { get; set; } = "qwen3.8-flash";
    public string Region { get; set; } = "cn-beijing";
    public decimal InputPerMillion { get; set; } = 0.8m;
    public decimal CachedInputPerMillion { get; set; } = 0.1m;
    public decimal OutputPerMillion { get; set; } = 2.7m;
    public string UpdatedOn { get; set; } = "2026-10-21";
}

public sealed record QwenCostEstimate(decimal? Minimum, decimal? Maximum, string Detail)
{
    public static QwenCostEstimate Local => new(0m, 0m, "未调用大模型接口");
    public string Label => Minimum is not { } min || Maximum is not { } max ? "费用待确认"
        : min == 0m && max == 0m ? "本次 ¥0"
        : min == max ? "预估 " + Money(min) : "预估 " + Money(min) + "～" + Money(max);

    private static string Money(decimal amount) => amount is > 0m and < 0.000001m ? "<¥0.000001"
        : "¥" + amount.ToString("0.######", CultureInfo.InvariantCulture);
}

public static class QwenCostEstimator
{
    public static QwenCostEstimate Estimate(QwenOptions options, int? input, int? cached, int? output)
    {
        var price = options.Pricing;
        if (price is null || !string.Equals(price.Model, options.Model.Trim(), StringComparison.OrdinalIgnoreCase))
            return Unknown("未配置此模型的价格");
        string host = options.Endpoint().Host;
        bool matchesRegion = host.EndsWith("." + price.Region + ".maas.aliyuncs.com", StringComparison.OrdinalIgnoreCase)
            || price.Region == "cn-beijing" && host == "dashscope.aliyuncs.com"
            || price.Region == "ap-southeast-1" && host == "dashscope-intl.aliyuncs.com";
        if (!matchesRegion) return Unknown("接口地域与计价地域不一致");
        if (price.InputPerMillion is < 0m or > 1000000m || price.CachedInputPerMillion is < 0m or > 1000000m
            || price.OutputPerMillion is < 0m or > 1000000m) return Unknown("计价配置无效");
        if (input is null || output is null) return Unknown("接口未返回完整用量");
        if (input < 0 || output < 0 || cached < 0 || cached > input) return Unknown("接口返回的用量无效");
        decimal outputCost = output.Value * price.OutputPerMillion / 1000000m;
        if (cached is { } hit)
        {
            decimal cost = ((input.Value - hit) * price.InputPerMillion + hit * price.CachedInputPerMillion) / 1000000m + outputCost;
            return new(cost, cost, $"按 {price.UpdatedOn} 配置标价估算，实际以账单为准");
        }
        decimal uncachedCost = input.Value * price.InputPerMillion / 1000000m + outputCost;
        decimal cachedCost = input.Value * price.CachedInputPerMillion / 1000000m + outputCost;
        return new(Math.Min(uncachedCost, cachedCost), Math.Max(uncachedCost, cachedCost), "缓存用量未返回，显示费用范围，实际以账单为准");
    }

    private static QwenCostEstimate Unknown(string reason) => new(null, null, reason);
}
