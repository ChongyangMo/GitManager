using System.Collections.ObjectModel;
using System.IO;
using GitManager.Services;
using GitManager.Services.CommitMessages;
using GitManager.Services.CommitMessages.Usage;

namespace GitManager.ViewModels.CommitMessages.Usage;

public sealed class QwenUsageViewModel(QwenOptions options, QwenUsageStore store, string? logsDirectory) : ObservableObject
{
    private IReadOnlyList<QwenUsageEntry> _entries = [];
    private string _period = "全部", _error = "";
    private bool _loading;
    public string[] Periods { get; } = ["全部", "今天"];
    public string Period { get => _period; set { if (Set(ref _period, value)) Apply(); } }
    public ObservableCollection<QwenUsageRow> Rows { get; } = [];
    public QwenUsageSummary? Summary { get; private set; }
    public bool CanRefresh => !_loading;
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string Price
    {
        get
        {
            try
            {
                if (QwenCostEstimator.Estimate(options, 0, 0, 0).Minimum is null) return "当前模型或地域未配置单价";
                var price = options.Pricing!;
                return $"单价 / 百万 Token：输入 ¥{price.InputPerMillion} · 缓存 ¥{price.CachedInputPerMillion} · 输出 ¥{price.OutputPerMillion}";
            }
            catch (InvalidOperationException) { return "当前模型或地域未配置单价"; }
        }
    }
    public string PriceDetail => $"{options.Model} · {options.Pricing?.Region} · {options.Pricing?.UpdatedOn}\n按配置标价估算，实际以账单为准";

    public async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true; Notify(nameof(CanRefresh));
        try
        {
            var data = await Task.Run(() => store.Read(logsDirectory));
            _entries = data.Entries;
            Error = data.UnreadableCount > 0 ? $"{data.UnreadableCount} 条记录无法读取" : "";
            Apply();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "读取用量统计失败，请重试";
            AppLog.Write("读取千问用量统计失败", ex);
        }
        finally { _loading = false; Notify(nameof(CanRefresh)); }
    }

    private void Apply()
    {
        var filtered = _entries.Where(entry => Period != "今天" || entry.Time.ToLocalTime().Date == DateTime.Today).ToArray();
        Summary = new(filtered); Notify(nameof(Summary));
        Rows.Clear();
        foreach (var entry in filtered) Rows.Add(new(entry));
    }
}
