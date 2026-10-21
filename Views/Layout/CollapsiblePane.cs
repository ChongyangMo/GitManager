using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GitManager.Services;
using GitManager.Services.Layout;

namespace GitManager.Views.Layout;

// 各区域共用同一折叠控件，内容保留在原有视图中，切换时不重新加载数据。
public sealed class CollapsiblePane : ContentControl
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(CollapsiblePane), new PropertyMetadata(true, ExpansionChanged));
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(CollapsiblePane), new PropertyMetadata(Orientation.Vertical));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(CollapsiblePane), new PropertyMetadata(""));
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string SettingsKey { get; set; } = "";
    private SettingsStore? _settings;
    private Grid? _grid;
    private GridLength _expandedLength;
    private double _minimum;
    private bool _ready;
    private Button? _toggle;

    public CollapsiblePane()
    {
        SetResourceReference(StyleProperty, typeof(CollapsiblePane));
        Loaded += PaneLoaded;
        Unloaded += PaneUnloaded;
    }

    public override void OnApplyTemplate()
    {
        if (_toggle is not null) _toggle.Click -= ToggleClicked;
        base.OnApplyTemplate();
        _toggle = GetTemplateChild("PART_Toggle") as Button;
        if (_toggle is not null) _toggle.Click += ToggleClicked;
    }

    private void ToggleClicked(object sender, RoutedEventArgs e) => IsExpanded = !IsExpanded;

    private void PaneLoaded(object sender, RoutedEventArgs e)
    {
        if (Parent is not Grid grid || Window.GetWindow(this) is not MainWindow window) return;
        _grid = grid;
        _settings = window.ViewModel.Settings;
        if (!_ready)
        {
            _minimum = Orientation == Orientation.Horizontal ? Column.MinWidth : Row.MinHeight;
            _expandedLength = Length;
            if (_settings.Value.WorkspaceLayout.TryGetValue(SettingsKey, out var state))
            {
                if (double.IsFinite(state.Size) && state.Size > 0)
                    _expandedLength = new GridLength(state.Size, state.IsStar ? GridUnitType.Star : GridUnitType.Pixel);
                SetCurrentValue(IsExpandedProperty, state.IsExpanded);
            }
            _ready = true;
        }
        ApplySize();
        grid.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(SplitterDragged));
    }

    private void PaneUnloaded(object sender, RoutedEventArgs e)
    {
        _grid?.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(SplitterDragged));
    }

    private ColumnDefinition Column => _grid!.ColumnDefinitions[Grid.GetColumn(this)];
    private RowDefinition Row => _grid!.RowDefinitions[Grid.GetRow(this)];
    private GridLength Length
    {
        get => Orientation == Orientation.Horizontal ? Column.Width : Row.Height;
        set { if (Orientation == Orientation.Horizontal) Column.Width = value; else Row.Height = value; }
    }

    private static void ExpansionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var pane = (CollapsiblePane)sender;
        if (!pane._ready) return;
        pane.ApplySize();
        pane.Save();
    }

    private void ApplySize()
    {
        var panes = _grid!.Children.OfType<CollapsiblePane>()
            .Where(p => p._ready && p.Orientation == Orientation && ReferenceEquals(p._grid, _grid)).ToArray();
        var expanded = panes.Where(p => p.IsExpanded).ToArray();
        foreach (var pane in panes)
        {
            if (Orientation == Orientation.Horizontal) pane.Column.MinWidth = pane.IsExpanded ? pane._minimum : 28;
            else pane.Row.MinHeight = pane.IsExpanded ? pane._minimum : 28;
            // 仅剩一个展开区域时自动填满空间，原尺寸单独保存，重新展开其他区域时恢复。
            pane.Length = !pane.IsExpanded ? new GridLength(28)
                : panes.Length > 1 && expanded.Length == 1 ? new GridLength(1, GridUnitType.Star)
                : pane._expandedLength;
        }
        // 折叠后禁止相邻分隔条改变收起区域的尺寸，展开后恢复拖动。
        foreach (var splitter in _grid!.Children.OfType<GridSplitter>())
        {
            int index = Orientation == Orientation.Horizontal ? Grid.GetColumn(splitter) : Grid.GetRow(splitter);
            var neighbors = _grid.Children.OfType<CollapsiblePane>().Where(p => p.Orientation == Orientation &&
                Math.Abs((Orientation == Orientation.Horizontal ? Grid.GetColumn(p) : Grid.GetRow(p)) - index) == 1);
            splitter.IsEnabled = neighbors.All(p => p.IsExpanded);
        }
    }

    private void SplitterDragged(object sender, DragCompletedEventArgs e)
    {
        if (e.OriginalSource is not GridSplitter splitter || !ReferenceEquals(splitter.Parent, _grid) || !IsExpanded) return;
        _expandedLength = Length;
        Save();
    }

    private void Save()
    {
        if (_settings is null || string.IsNullOrWhiteSpace(SettingsKey)) return;
        _settings.Value.WorkspaceLayout[SettingsKey] = new PaneLayoutState
        {
            IsExpanded = IsExpanded, Size = _expandedLength.Value, IsStar = _expandedLength.IsStar
        };
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppLog.Write("保存区域展开折叠状态失败", ex); }
    }
}
