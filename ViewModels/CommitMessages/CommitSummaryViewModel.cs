using System.Windows;
using GitManager.Services;
using GitManager.Services.CommitMessages;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _commitSummaryCancellation;
    private long _commitMessageRevision, _commitSummaryGeneration;
    public bool IsGeneratingCommitMessage => _commitSummaryCancellation is not null;
    public string GenerateCommitMessageLabel => IsGeneratingCommitMessage ? "取消生成" : "自动总结";

    private void CancelCommitSummary()
    {
        _commitSummaryGeneration++;
        _commitSummaryCancellation?.Cancel();
    }

    public async Task GenerateCommitMessageAsync()
    {
        if (_commitSummaryCancellation is { } pending)
        {
            pending.Cancel();
            Status = "已取消生成";
            return;
        }
        if (!CanOperate || _disposed) return;
        var selected = Changes.Where(file => file.Selected).ToArray();
        if (selected.Length == 0)
        {
            Status = "请先勾选需要提交的文件";
            MessageBox.Show(Status, "自动总结", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string root = Root;
        long revision = _commitMessageRevision, generation = ++_commitSummaryGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _commitSummaryCancellation = cancellation;
        Notify(nameof(IsGeneratingCommitMessage)); Notify(nameof(GenerateCommitMessageLabel));
        Status = "正在总结勾选改动";
        try
        {
            var result = await new CommitMessageGenerator(Git, Settings).GenerateAsync(root, selected, cancellation.Token);
            if (_disposed || generation != _commitSummaryGeneration || Repository?.Path != root) return;
            if (_commitMessageRevision != revision)
            {
                Status = "提交说明已手动修改，保留当前内容";
                return;
            }
            if (!CheckedChangePaths().SetEquals(selected.Select(file => file.Path)))
            {
                Status = "勾选范围或文件内容已变化，请重新总结";
                return;
            }
            CommitMessage = result.Message;
            CacheCurrentWorkspace();
            Status = $"已生成提交说明（{result.Source}）";
            AppLog.Write($"已通过{result.Source}总结 {selected.Length} 个勾选文件");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_disposed && generation == _commitSummaryGeneration && Repository?.Path == root) Status = "已取消生成";
        }
        catch (Exception ex)
        {
            if (_disposed || generation != _commitSummaryGeneration || Repository?.Path != root) return;
            Status = AppLog.Clean(ex.Message);
            AppLog.Write("自动总结改动失败", ex);
        }
        finally
        {
            if (ReferenceEquals(_commitSummaryCancellation, cancellation))
            {
                _commitSummaryCancellation = null;
                Notify(nameof(IsGeneratingCommitMessage)); Notify(nameof(GenerateCommitMessageLabel));
            }
        }
    }
}
