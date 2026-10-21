using GitManager.Services.CommitMessages;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunQwenCostChecks()
    {
        var options = new QwenOptions();
        // 使用前面真实调用日志中已记录的用量校验金额，不构造接口响应。
        var actual = QwenCostEstimator.Estimate(options, 2686, 0, 101);
        Check(actual.Minimum == 0.0024215m && actual.Maximum == actual.Minimum && actual.Label == "预估 ¥0.002422",
            "真实历史用量按普通输入及输出价格计算，并显示六位小数");
        var cached = QwenCostEstimator.Estimate(options, 10000, 9000, 300);
        Check(cached.Minimum == 0.00251m && cached.Maximum == cached.Minimum, "缓存输入只按缓存价格计费，不重复算入普通输入");
        var unknownCache = QwenCostEstimator.Estimate(options, 10000, null, 300);
        Check(unknownCache.Minimum == 0.00181m && unknownCache.Maximum == 0.00881m && unknownCache.Label.Contains('～'),
            "缓存用量缺失时显示上下界");
        Check(QwenCostEstimator.Estimate(options, null, 0, 300).Label == "费用待确认", "总输入缺失时不虚构金额");
        Check(QwenCostEstimator.Estimate(options, 100, 101, 50).Label == "费用待确认", "缓存用量超过总输入时不生成错误金额");
        options.Model = "qwen3.7-flash";
        Check(QwenCostEstimator.Estimate(options, 100, 0, 50).Label == "费用待确认", "模型变化后不套用其他模型的价格");
        options.Model = "qwen3.8-flash";
        options.BaseUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";
        Check(QwenCostEstimator.Estimate(options, 100, 0, 50).Label == "费用待确认", "接口地域变化后不套用北京价格");
        Check(QwenCostEstimate.Local.Label == "本次 ¥0", "本地规则和本地缓存本次费用为零");
        Check(new QwenCostEstimate(0.0000001m, 0.0000001m, "").Label == "预估 <¥0.000001", "极小非零费用不会显示为零");
        Console.WriteLine("费用估算检查通过，未调用大模型接口。");
    }
}
