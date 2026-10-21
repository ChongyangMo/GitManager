using GitManager.Services.CommitMessages;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private QwenCostEstimate? _summaryCost;
    public string SummaryCostLabel => _summaryCost?.Label ?? "";
    public string SummaryCostDetail => _summaryCost?.Detail ?? "";

    private void SetSummaryCost(QwenCostEstimate? cost)
    {
        _summaryCost = cost;
        Notify(nameof(SummaryCostLabel)); Notify(nameof(SummaryCostDetail));
    }
}
