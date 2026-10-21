using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GitManager.Models;

namespace GitManager.Views;

public partial class DiffViewer : UserControl
{
    private const double RowHeight = 21, NumberWidth = 46, TextPadding = 22;
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(nameof(Rows), typeof(List<DiffRow>), typeof(DiffViewer), new PropertyMetadata(null, RowsChanged));
    public static readonly DependencyProperty ComparisonProperty = DependencyProperty.Register(nameof(Comparison), typeof(FileComparison), typeof(DiffViewer));
    public List<DiffRow>? Rows { get => (List<DiffRow>?)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public FileComparison? Comparison { get => (FileComparison?)GetValue(ComparisonProperty); set => SetValue(ComparisonProperty, value); }
    public TranslateTransform BeforeShift { get; } = new();
    public TranslateTransform AfterShift { get; } = new();
    private readonly List<int> _changes = [];
    private int _changeIndex;
    private double _beforeWidth, _afterWidth;
    public DiffViewer() => InitializeComponent();
    private static void RowsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (DiffViewer)sender;
        viewer._changes.Clear();
        bool wasChange = false;
        var rows = e.NewValue as List<DiffRow> ?? [];
        for (int i = 0; i < rows.Count; i++)
        {
            bool isChange = rows[i].Added || rows[i].Removed;
            if (isChange && !wasChange) viewer._changes.Add(i);
            wasChange = isChange;
        }
        viewer._beforeWidth = viewer.MeasureWidest(rows.Select(row => row.Before));
        viewer._afterWidth = viewer.MeasureWidest(rows.Select(row => row.After));
        viewer.UpdateHorizontalRange();
        viewer._changeIndex = 0;
        viewer.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(viewer.ScrollToChange));
    }
    private void ScrollToChange()
    {
        int row = _changes.Count == 0 ? 0 : Math.Max(0, _changes[_changeIndex] - 4);
        FindScrollViewer(DiffList)?.ScrollToVerticalOffset(row * RowHeight);
    }
    private void PreviousChange(object sender, RoutedEventArgs e) { if (_changes.Count > 0) { _changeIndex = (_changeIndex + _changes.Count - 1) % _changes.Count; ScrollToChange(); } }
    private void NextChange(object sender, RoutedEventArgs e) { if (_changes.Count > 0) { _changeIndex = (_changeIndex + 1) % _changes.Count; ScrollToChange(); } }
    private void DiffScrolled(object sender, ScrollChangedEventArgs e) { if (e.ViewportWidthChange != 0) UpdateHorizontalRange(); }

    // 横向范围按全部行的最长文本计算，不随虚拟化的可见行变化而跳动。
    private double MeasureWidest(IEnumerable<string> lines)
    {
        // 先按字符宽度粗选最长的若干行，再精确排版测量，避免逐行测量大文件。
        var candidates = lines.Where(line => line.Length > 0).OrderByDescending(EstimateWidth).Take(24).ToList();
        if (candidates.Count == 0) return 0;
        var typeface = new Typeface(DiffList.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var mode = TextOptions.GetTextFormattingMode(this);
        return candidates.Max(line => new FormattedText(line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, DiffList.FontSize,
            Brushes.Black, null, mode, pixelsPerDip).WidthIncludingTrailingWhitespace) + TextPadding;
    }
    private static int EstimateWidth(string line)
    {
        int width = 0;
        foreach (char c in line) width += c == '\t' ? 8 : c >= 0x1100 ? 2 : 1;
        return width;
    }
    private void UpdateHorizontalRange()
    {
        if (FindScrollViewer(DiffList) is not { } scroll) return;
        double viewport = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : DiffList.ActualWidth;
        // 表头和横向滚动条让出纵向滚动条的宽度，使左右分栏与内容对齐。
        var gutter = new Thickness(0, 0, Math.Max(0, DiffList.ActualWidth - viewport), 0);
        HeaderGrid.Margin = ScrollGrid.Margin = gutter;
        double textWidth = Math.Max(0, (viewport - 1) / 2 - NumberWidth);
        bool before = ConfigureScroll(BeforeScroll, _beforeWidth, textWidth), after = ConfigureScroll(AfterScroll, _afterWidth, textWidth);
        ScrollGrid.Visibility = before || after ? Visibility.Visible : Visibility.Collapsed;
    }
    private static bool ConfigureScroll(ScrollBar bar, double content, double viewport)
    {
        bar.ViewportSize = viewport;
        bar.LargeChange = Math.Max(1, viewport - 40);
        bar.SmallChange = 24;
        bar.Maximum = Math.Max(0, Math.Ceiling(content - viewport));
        bool needed = bar.Maximum > 0;
        bar.Visibility = needed ? Visibility.Visible : Visibility.Hidden;
        return needed;
    }
    private void BeforeScrolled(object sender, RoutedPropertyChangedEventArgs<double> e) => BeforeShift.X = -Math.Round(e.NewValue);
    private void AfterScrolled(object sender, RoutedPropertyChangedEventArgs<double> e) => AfterShift.X = -Math.Round(e.NewValue);
    private bool IsOverBefore(MouseDevice mouse) => mouse.GetPosition(DiffList).X < (FindScrollViewer(DiffList)?.ViewportWidth ?? DiffList.ActualWidth) / 2;
    private void DiffMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift) return;
        var bar = IsOverBefore(e.MouseDevice) ? BeforeScroll : AfterScroll;
        bar.Value -= e.Delta / 120.0 * bar.SmallChange * 2;
        e.Handled = true;
    }
    // 右键所在一侧决定复制原文件还是当前文件；键盘打开菜单时两项都显示。
    private void DiffMenuOpening(object sender, ContextMenuEventArgs e)
    {
        bool keyboard = e.CursorLeft < 0 && e.CursorTop < 0, before = !keyboard && IsOverBefore(Mouse.PrimaryDevice);
        CopyBeforeItem.Visibility = keyboard || before ? Visibility.Visible : Visibility.Collapsed;
        CopyAfterItem.Visibility = keyboard || !before ? Visibility.Visible : Visibility.Collapsed;
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, i)) is { } child) return child;
        return null;
    }
    private void CopyBefore(object sender, RoutedEventArgs e) { if (Comparison is not null) Clipboard.SetText(Comparison.Before); }
    private void CopyAfter(object sender, RoutedEventArgs e) { if (Comparison is not null) Clipboard.SetText(Comparison.After); }
}

// 让行文本按完整宽度排版并裁剪在本栏内，横向滚动时只平移不重新布局。
public sealed class DiffLinePanel : Panel
{
    public DiffLinePanel() => ClipToBounds = true;
    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(0, height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren) child.Arrange(new Rect(0, 0, child.DesiredSize.Width, finalSize.Height));
        return finalSize;
    }
}
