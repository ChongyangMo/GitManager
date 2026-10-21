using System.IO;
using System.Windows.Threading;
using GitManager.Services;
using GitManager.Services.Repositories;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private DispatcherTimer? _repositoryPushTimer;
    private Task? _repositoryPushScan;
    private readonly Dictionary<string, long> _repositoryPushRevisions = new(StringComparer.OrdinalIgnoreCase);

    private void StartRepositoryPushMonitoring()
    {
        if (_disposed) return;
        if (_repositoryPushTimer is null)
        {
            _repositoryPushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
            _repositoryPushTimer.Tick += (_, _) => { if (!IsBusy) _ = RefreshRepositoryPushIndicatorsAsync(); };
            _repositoryPushTimer.Start();
        }
        _ = RefreshRepositoryPushIndicatorsAsync();
    }

    public Task RefreshRepositoryPushIndicatorsAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_repositoryPushScan is { IsCompleted: false }) return _repositoryPushScan;
        return _repositoryPushScan = ReadRepositoryPushIndicatorsAsync();
    }

    private void UpdateRepositoryPushIndicator(string root, int? count)
    {
        _repositoryPushRevisions[root] = _repositoryPushRevisions.GetValueOrDefault(root) + 1;
        Repositories.FirstOrDefault(item => string.Equals(item.Path, root, StringComparison.OrdinalIgnoreCase))?.UpdatePendingPushCount(count);
    }

    private async Task ReadRepositoryPushIndicatorsAsync()
    {
        foreach (var bookmark in Repositories.ToArray())
        {
            if (_disposed) return;
            long revision = _repositoryPushRevisions.GetValueOrDefault(bookmark.Path);
            try
            {
                int? count = await RepositoryPushStatus.ReadAsync(Git, bookmark.Path);
                if (!_disposed && _repositoryPushRevisions.GetValueOrDefault(bookmark.Path) == revision && Repositories.Contains(bookmark))
                    bookmark.UpdatePendingPushCount(count);
            }
            catch (OperationCanceledException) when (_disposed) { return; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
            {
                if (_disposed) return;
                if (_repositoryPushRevisions.GetValueOrDefault(bookmark.Path) == revision) bookmark.UpdatePendingPushCount(null);
                AppLog.Write("读取仓库待推送提醒失败：" + bookmark.Name, ex);
            }
        }
    }
}
