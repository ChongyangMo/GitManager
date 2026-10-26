using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GitManager.Views.Motion;

// 页面位置始终固定；仅单次导航做极轻的淡入，连续切换保持稳定显示。
public static class PageTransition
{
    private sealed class State
    {
        public DispatcherOperation? Pending { get; set; }
        public long Generation { get; set; }
        public long LastNavigation { get; set; }
    }

    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(object), typeof(PageTransition), new PropertyMetadata(null, KeyChanged));
    public static object? GetKey(DependencyObject element) => element.GetValue(KeyProperty);
    public static void SetKey(DependencyObject element, object? value) => element.SetValue(KeyProperty, value);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(PageTransition));

    private static void KeyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if (element.GetValue(StateProperty) is not State state)
        {
            state = new State();
            element.SetValue(StateProperty, state);
            element.Unloaded += Unloaded;
        }
        long generation = ++state.Generation;
        long now = Stopwatch.GetTimestamp();
        bool rapid = state.LastNavigation != 0 && Stopwatch.GetElapsedTime(state.LastNavigation, now) < TimeSpan.FromMilliseconds(200);
        state.LastNavigation = now;
        state.Pending?.Abort();
        state.Pending = null;
        if (rapid || args.NewValue is null || !element.IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            Reset(element, state);
            return;
        }
        // 合并同一帧内的多次导航，只为最后一次选择播放动效。
        state.Pending = element.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            state.Pending = null;
            if (generation != state.Generation || !element.IsLoaded || !element.IsVisible) return;
            if (!SystemParameters.ClientAreaAnimation) { Reset(element, state); return; }
            Reset(element, state);
            var fade = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(100))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            fade.Completed += (_, _) =>
            {
                if (generation == state.Generation) Reset(element, state);
            };
            element.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        });
    }

    private static void Reset(FrameworkElement element, State state)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.SetValue(UIElement.OpacityProperty, 1d);
    }

    private static void Unloaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if (element.GetValue(StateProperty) is not State state) return;
        state.Generation++;
        state.Pending?.Abort();
        state.Pending = null;
        Reset(element, state);
    }
}
