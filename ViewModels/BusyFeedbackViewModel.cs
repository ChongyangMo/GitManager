using System.Windows.Threading;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    // 写操作超过该时长才显示忙碌外观，后台读取不触发全局灰显。
    private static readonly TimeSpan BusyFeedbackDelay = TimeSpan.FromMilliseconds(300);
    private DispatcherTimer? _busyFeedbackTimer;
    private bool _busyFeedbackVisible;
    private bool IsLocked => _busy;
    public bool IsBusyFeedbackDeferred => IsLocked && !_busyFeedbackVisible;
    public bool IsBusyProgressVisible => _busy && _busyFeedbackVisible;

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
        Notify(nameof(IsBusyFeedbackDeferred)); Notify(nameof(IsBusyProgressVisible));
    }
}
