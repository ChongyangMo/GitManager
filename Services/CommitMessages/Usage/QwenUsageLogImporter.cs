using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GitManager.Services.CommitMessages.Usage;

public static partial class QwenUsageLogImporter
{
    public static int Import(QwenUsageStore store, string logsDirectory)
    {
        if (!Directory.Exists(logsDirectory)) return 0;
        int failures = 0;
        foreach (string path in Directory.EnumerateFiles(logsDirectory, "gitmanager-*.log"))
        {
            string date = Path.GetFileNameWithoutExtension(path)["gitmanager-".Length..];
            if (!DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            try
            {
                int lineNumber = 0;
                foreach (string line in File.ReadLines(path))
                {
                    lineNumber++;
                    var match = UsageLine().Match(line);
                    if (!match.Success || !TimeSpan.TryParse(match.Groups["time"].Value, out var time)) continue;
                    var idMatch = RequestId().Match(line);
                    string id = idMatch.Success ? idMatch.Groups[1].Value
                        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(path) + "\n" + lineNumber + "\n" + line)));
                    if (store.Contains(id)) continue;
                    var money = Cost().Match(line);
                    decimal? min = ParseMoney(money.Groups[1].Value), max = ParseMoney(money.Groups[2].Value);
                    store.Save(new QwenUsageEntry
                    {
                        Id = id, Time = new DateTimeOffset(day.Add(time)), Model = match.Groups["model"].Value,
                        Result = "历史记录", InputTokens = ParseCount(match, "input"), CachedTokens = ParseCount(match, "cached"),
                        OutputTokens = ParseCount(match, "output"), Milliseconds = long.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture),
                        Cost = new(min, max ?? min, min.HasValue ? "历史日志中的预估费用，实际以账单为准" : "历史日志未记录费用")
                    }, overwrite: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { failures++; AppLog.Write("导入千问历史用量失败", ex); }
        }
        return failures;
    }

    private static int? ParseCount(Match match, string name) => int.TryParse(match.Groups[name].Value, out int value) ? value : null;
    private static decimal? ParseMoney(string value) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal result) ? result : null;

    [GeneratedRegex(@"^\[(?<time>\d{2}:\d{2}:\d{2})\] 千问(?:调用用量|总结完成)：模型 (?<model>[^，]+)，输入 (?<input>\d+|未返回)，缓存命中 (?<cached>\d+|未返回)，输出 (?<output>\d+|未返回) 个令牌，缓存比例 [^，]+，耗时 (?<ms>\d+) 毫秒")]
    private static partial Regex UsageLine();
    [GeneratedRegex(@"调用编号 ([a-fA-F0-9]{32})(?:\b|$)")]
    private static partial Regex RequestId();
    [GeneratedRegex(@"预估 ¥([0-9]+(?:\.[0-9]+)?)(?:～¥([0-9]+(?:\.[0-9]+)?))?")]
    private static partial Regex Cost();
}
