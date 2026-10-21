using System.Windows;

namespace GitManager.Views;

// 为真时，禁用的控件暂不显示变灰外观；输入仍按 IsEnabled 立即禁用，避免短操作整屏闪灰。
public static class BusyFeedback
{
    public static readonly DependencyProperty DeferDisabledLookProperty = DependencyProperty.RegisterAttached(
        "DeferDisabledLook", typeof(bool), typeof(BusyFeedback), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetDeferDisabledLook(DependencyObject element) => (bool)element.GetValue(DeferDisabledLookProperty);
    public static void SetDeferDisabledLook(DependencyObject element, bool value) => element.SetValue(DeferDisabledLookProperty, value);
}
