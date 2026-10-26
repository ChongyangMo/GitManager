using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GitManager.Views.Motion;

// 通用控件只动画自身的视觉层，不改变布局、命中范围或操作时机。
public static class InteractionMotion
{
    public static readonly DependencyProperty IsPressedProperty = DependencyProperty.RegisterAttached(
        "IsPressed", typeof(bool), typeof(InteractionMotion), new PropertyMetadata(false, PressedChanged));
    public static bool GetIsPressed(DependencyObject element) => (bool)element.GetValue(IsPressedProperty);
    public static void SetIsPressed(DependencyObject element, bool value) => element.SetValue(IsPressedProperty, value);

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(InteractionMotion), new PropertyMetadata(true, ActiveChanged));
    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);

    public static readonly DependencyProperty IsIndicatorActiveProperty = DependencyProperty.RegisterAttached(
        "IsIndicatorActive", typeof(bool), typeof(InteractionMotion), new PropertyMetadata(true, IndicatorChanged));
    public static bool GetIsIndicatorActive(DependencyObject element) => (bool)element.GetValue(IsIndicatorActiveProperty);
    public static void SetIsIndicatorActive(DependencyObject element, bool value) => element.SetValue(IsIndicatorActiveProperty, value);

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.RegisterAttached(
        "IsExpanded", typeof(bool), typeof(InteractionMotion), new PropertyMetadata(false, ExpansionChanged));
    public static bool GetIsExpanded(DependencyObject element) => (bool)element.GetValue(IsExpandedProperty);
    public static void SetIsExpanded(DependencyObject element, bool value) => element.SetValue(IsExpandedProperty, value);

    private static void PressedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        var scale = Scale(element);
        double value = (bool)args.NewValue ? 0.975 : 1;
        int duration = (bool)args.NewValue ? 70 : 160;
        Animate(element, scale, ScaleTransform.ScaleXProperty, value, duration);
        Animate(element, scale, ScaleTransform.ScaleYProperty, value, duration);
    }

    private static void ActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement element)
            Animate(element, element, UIElement.OpacityProperty, (bool)args.NewValue ? 1 : 0, 150);
    }

    private static void IndicatorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        bool active = (bool)args.NewValue;
        var scale = Scale(element);
        Animate(element, element, UIElement.OpacityProperty, active ? 1 : 0, 160);
        Animate(element, scale, ScaleTransform.ScaleXProperty, active ? 1 : 0.65, 180);
    }

    private static void ExpansionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if (element.RenderTransform is not RotateTransform rotate)
        {
            rotate = new RotateTransform();
            element.RenderTransform = rotate;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        Animate(element, rotate, RotateTransform.AngleProperty, (bool)args.NewValue ? 180 : 0, 160);
    }

    private static ScaleTransform Scale(FrameworkElement element)
    {
        if (element.RenderTransform is ScaleTransform scale) return scale;
        scale = new ScaleTransform();
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return scale;
    }

    private static void Animate(FrameworkElement owner, DependencyObject target, DependencyProperty property, double value, int milliseconds)
    {
        double current = (double)target.GetValue(property);
        var animation = SystemParameters.ClientAreaAnimation && owner.IsLoaded && owner.IsEnabled
            ? new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            } : null;
        // 使用替换模式，连续点击从当前视觉状态继续，不排队、不留下缩放或透明度残值。
        Begin(target, property, null);
        target.SetCurrentValue(property, value);
        if (animation is not null) Begin(target, property, animation);
    }

    private static void Begin(DependencyObject target, DependencyProperty property, AnimationTimeline? animation)
    {
        if (target is UIElement element) element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        else if (target is Animatable animatable) animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
