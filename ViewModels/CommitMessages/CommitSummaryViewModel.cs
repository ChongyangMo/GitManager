using System.Windows;
using GitManager.Services;
using GitManager.Services.CommitMessages;
using GitManager.Services.LuxAi;

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
        if (!CanWriteRepository || _disposed) return;
        var selected = Changes.Where(file => file.Selected).ToArray();
        if (selected.Length == 0)
        {
            if (Changes.Count == 0)
            {
                Status = "没有未提交改动";
                MessageBox.Show(Status, "自动总结", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            foreach (var file in Changes) file.Selected = true;
            selected = [.. Changes];
        }
        string root = Root;
        long revision = _commitMessageRevision, generation = ++_commitSummaryGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _commitSummaryCancellation = cancellation;
        SetSummaryCost(null);
        Notify(nameof(IsGeneratingCommitMessage)); Notify(nameof(GenerateCommitMessageLabel));
        Status = "正在总结勾选改动";
        try
        {
            var result = await new CommitMessageGenerator(Git, Settings).GenerateAsync(root, selected, cancellation.Token);
            if (_disposed || generation != _commitSummaryGeneration || Repository?.Path != root) return;
            SetSummaryCost(result.Cost);
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
        catch (LuxAiLoginRequiredException ex)
        {
            if (_disposed || generation != _commitSummaryGeneration || Repository?.Path != root) return;
            Status = ex.Message;
            try
            {
                var login = new Views.CommitMessages.LuxAiLoginWindow(Settings.Value.LuxAi) { Owner = Application.Current.MainWindow };
                if (login.ShowDialog() == true) Status = "集团 AI 已登录，请再次点击自动总结";
            }
            catch (Exception loginError) { Status = AppLog.Clean(loginError.Message); AppLog.Write("集团 AI 重新登录失败", loginError); }
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
