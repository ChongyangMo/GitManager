using System.Windows.Threading;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    // 操作或首次加载超过该时长才显示忙碌外观，短操作不闪烁。
    private static readonly TimeSpan BusyFeedbackDelay = TimeSpan.FromMilliseconds(300);
    private DispatcherTimer? _busyFeedbackTimer;
    private bool _busyFeedbackVisible;
    private bool IsLocked => _busy || _workspaceLoading;
    public bool IsBusyFeedbackDeferred => IsLocked && !_busyFeedbackVisible;
    public bool IsBusyProgressVisible => _busy && _busyFeedbackVisible;
    public bool IsWorkspaceLoadingHintVisible => _workspaceLoading && _busyFeedbackVisible;

    private void UpdateBusyFeedback()
    {
        if (IsLocked && !_busyFeedbackVisible && !_disposed)
        {
            _busyFeedbackTimer ??= CreateBusyFeedbackTimer();
            if (!_busyFeedbackTimer.IsEnabled) _busyFeedbackTimer.Start();
        }
        else if (!IsLocked)
        {
            _busyFeedbackTimer?.Stop();
            _busyFeedbackVisible = false;
        }
        NotifyBusyFeedback();
    }

    private DispatcherTimer CreateBusyFeedbackTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = BusyFeedbackDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!IsLocked || _disposed) return;
            _busyFeedbackVisible = true;
            NotifyBusyFeedback();
        };
        return timer;
    }

    private void NotifyBusyFeedback()
    {
        Notify(nameof(IsBusyFeedbackDeferred)); Notify(nameof(IsBusyProgressVisible)); Notify(nameof(IsWorkspaceLoadingHintVisible));
    }
}
