using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GitManager.Views.Motion;

// 仓库页面进入动效：直接显示新内容，仅在绑定更新后的首帧做轻微过渡。
public static class PageTransition
{
    private sealed class State
    {
        public TranslateTransform Offset { get; } = new();
        public DispatcherOperation? Pending { get; set; }
        public long Generation { get; set; }
        public bool Running { get; set; }
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
            element.RenderTransform = state.Offset;
            element.Unloaded += Unloaded;
        }
        long generation = ++state.Generation;
        state.Pending?.Abort();
        state.Pending = null;
        if (args.NewValue is null || !element.IsLoaded || !SystemParameters.ClientAreaAnimation)
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
            bool continuing = state.Running;
            double opacity = continuing ? element.Opacity : 0.84;
            double offset = continuing ? state.Offset.Y : 6;
            Reset(element, state);
            state.Running = true;
            var fade = Animation(opacity, 1);
            fade.Completed += (_, _) =>
            {
                if (generation == state.Generation) Reset(element, state);
            };
            element.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
            state.Offset.BeginAnimation(TranslateTransform.YProperty, Animation(offset, 0), HandoffBehavior.SnapshotAndReplace);
        });
    }

    private static DoubleAnimation Animation(double from, double to) => new(from, to, TimeSpan.FromMilliseconds(200))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop
    };

    private static void Reset(FrameworkElement element, State state)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        state.Offset.BeginAnimation(TranslateTransform.YProperty, null);
        element.SetValue(UIElement.OpacityProperty, 1d);
        state.Offset.Y = 0;
        state.Running = false;
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
