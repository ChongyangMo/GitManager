using System.Windows;
using GitManager.Services;
using GitManager.Services.LuxAi;
using GitManager.Services.Repositories.Copies.Analysis;

namespace GitManager.Views.Repositories;

public partial class RepositoryCopiesWindow
{
    private readonly CancellationTokenSource _windowCancellation = new();
    private CancellationTokenSource? _analysisCancellation;
    private bool _closed;
    public string? AnalysisText { get; private set; }
    public bool IsAnalyzing => _analysisCancellation is not null;

    private async void AnalyzeClicked(object sender, RoutedEventArgs e)
    {
        if ((_analysisCancellation ?? _readCancellation) is { } pending)
        {
            pending.Cancel();
            AnalyzeButton.IsEnabled = false;
            if (_readCancellation is not null) StateText.Text = "正在取消比较";
            else AnalysisState.Text = "正在取消分析";
            return;
        }
        if (!_loading && !_owner.IsBusy) await ReloadAsync();
    }

    private void ClearAnalysis()
    {
        AnalysisText = null;
        AnalysisDocument.Document = null;
        AnalysisDocument.Visibility = Visibility.Collapsed;
        AnalysisState.Text = "正在读取仓库";
    }

    private async Task AnalyzeAsync()
    {
        AnalysisPanel.Visibility = _bookmarks.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        if (Comparison is null || _bookmarks.Count < 2) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        _analysisCancellation = cancellation;
        _loading = true;
        CopiesList.IsEnabled = false;
        AnalyzeButton.Content = "取消分析";
        AnalysisState.Text = "正在分析提交关系与清理建议";
        try
        {
            var result = await new RepositoryCopyAnalysisService(_owner.Git, _owner.Settings).GenerateAsync(Comparison, cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested) return;
            AnalysisText = result.Text;
            var document = MarkdownDocument.Build(result.Text);
            document.PagePadding = new Thickness(0);
            AnalysisDocument.Document = document;
            AnalysisDocument.Visibility = Visibility.Visible;
            AnalysisState.Text = result.Source + " · " + result.Cost.Label;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closed) AnalysisState.Text = "已取消分析";
        }
        catch (LuxAiLoginRequiredException ex)
        {
            if (!_closed) AnalysisState.Text = ex.Message + " 请在设置中连接后重新分析。";
        }
        catch (Exception ex)
        {
            if (!_closed) AnalysisState.Text = "分析未完成：" + AppLog.Clean(ex.Message);
            AppLog.Write("同名仓库大模型分析失败", ex);
        }
        finally
        {
            _analysisCancellation = null;
            _loading = false;
            CopiesList.IsEnabled = true;
            AnalyzeButton.Content = "重新分析";
            AnalyzeButton.IsEnabled = true;
        }
    }
}
