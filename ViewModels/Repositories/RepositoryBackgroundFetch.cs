using GitManager.Services;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _backgroundFetchCancellation;
    private Task? _backgroundFetchTask;
    private string? _backgroundFetchKey;
    private bool _fetchAfterOperation, _disposed;

    private async Task StopBackgroundFetchAsync()
    {
        if (_backgroundFetchCancellation is not { } cancellation) return;
        cancellation.Cancel();
        if (_backgroundFetchTask is { } task) await task;
    }

    private void StartBackgroundFetch()
    {
        if (_disposed || !HasRepository || IsRepositoryAutoUploading || _versions?.HasRemote != true) return;
        _backgroundFetchCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _backgroundFetchCancellation = cancellation;
        _backgroundFetchKey = FetchKey;
        NotifyVersionState();
        _backgroundFetchTask = FetchInBackgroundAsync(Root, _versions.RemoteName, FetchKey, cancellation);
    }

    private async Task FetchInBackgroundAsync(string root, string remote, string key, CancellationTokenSource cancellation)
    {
        try
        {
            await Git.FetchVersionsAsync(root, remote, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _fetchedAt[key] = DateTimeOffset.Now;
            _fetchErrors.Remove(key);
            if (_disposed || IsBusy || !HasRepository || FetchKey != key) return;

            StartWorkspaceRefresh(fetchAfter: false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _fetchErrors[key] = AppLog.Clean(ex.Message);
            AppLog.Write("后台更新远程版本失败，保留缓存", ex);
        }
        finally
        {
            if (ReferenceEquals(_backgroundFetchCancellation, cancellation))
            {
                _backgroundFetchCancellation = null;
                _backgroundFetchKey = null;
                _backgroundFetchTask = null;
            }
            cancellation.Dispose();
            if (!_disposed) NotifyVersionState();
        }
    }
}
