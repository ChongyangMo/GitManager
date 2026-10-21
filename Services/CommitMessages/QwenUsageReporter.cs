using System.Globalization;
using System.Text.Json;

namespace GitManager.Services.CommitMessages;

public sealed record QwenUsage(int? InputTokens, int? CachedTokens, int? OutputTokens, QwenCostEstimate Cost);

public static class QwenUsageReporter
{
    public static QwenUsage ReadAndLog(QwenOptions options, JsonElement body, long milliseconds, string? requestId = null)
    {
        int? input = null, cached = null, output = null;
        if (body.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = Number(usage, "prompt_tokens"); output = Number(usage, "completion_tokens");
            if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                cached = Number(details, "cached_tokens");
        }
        var cost = QwenCostEstimator.Estimate(options, input, cached, output);
        string ratio = input is > 0 && cached is >= 0 && cached <= input
            ? ((double)cached.Value / input.Value).ToString("P1", CultureInfo.InvariantCulture) : "未返回";
        AppLog.Write($"千问调用用量：模型 {options.Model}，输入 {Count(input)}，缓存命中 {Count(cached)}，输出 {Count(output)} 个令牌，"
            + $"缓存比例 {ratio}，耗时 {milliseconds} 毫秒，{cost.Label}；{cost.Detail}"
            + (requestId is null ? "" : "，调用编号 " + requestId));
        return new(input, cached, output, cost);
    }

    private static int? Number(JsonElement value, string property) => value.TryGetProperty(property, out var number)
        && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out int result) ? result : null;
    private static string Count(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "未返回";
}
