namespace GitManager.Services.LuxAi;

// 独立于总结操作续期，窗口隐藏到托盘时也继续维护登录状态。
public sealed class LuxAiSessionMaintenance : IDisposable
{
    private readonly Func<LuxAiOptions> _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private LuxAiSession? _rejectedSession;
    private string? _rejectedAccountKey;
    private string? _lastError;
    private bool _disposed;

    public LuxAiSessionMaintenance(Func<LuxAiOptions> options)
    {
        _options = options;
        _worker = Task.Run(RunAsync);
    }

    public Task Completion => _worker;

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do { await MaintainAsync(); }
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task MaintainAsync()
    {
        LuxAiSession? session = null;
        string? accountKey = null;
        try
        {
            var options = _options();
            if (!options.Enabled) return;
            session = LuxAiCredentials.Read(options);
            accountKey = LuxAiAccountCredentials.Key(LuxAiAccountCredentials.Read(options));
            if (session is null && accountKey is null
                || session == _rejectedSession && accountKey == _rejectedAccountKey
                || session is not null && !LuxAiAuthentication.NeedsRenewal(session)) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var client = new LuxAiClient(options);
            await client.EnsureSessionAsync(timeout.Token);
            _rejectedSession = null;
            _rejectedAccountKey = null;
            _lastError = null;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (LuxAiLoginRequiredException)
        {
            _rejectedSession = session;
            _rejectedAccountKey = accountKey;
            AppLog.Write("集团 AI 自动恢复登录未通过，请使用账号密码重新登录；更新后将自动恢复后台维护");
        }
        catch (Exception ex)
        {
            if (_lastError != ex.Message)
                AppLog.Write("集团 AI 后台续期暂未完成，将自动重试", ex);
            _lastError = ex.Message;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        // 等待后台请求结束后释放取消源，不阻塞界面退出。
        _ = _worker.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
