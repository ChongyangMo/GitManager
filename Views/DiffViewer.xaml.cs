using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitManager.Models;

namespace GitManager.Views;

public partial class DiffViewer : UserControl
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(nameof(Rows), typeof(List<DiffRow>), typeof(DiffViewer), new PropertyMetadata(null, RowsChanged));
    public static readonly DependencyProperty ComparisonProperty = DependencyProperty.Register(nameof(Comparison), typeof(FileComparison), typeof(DiffViewer));
    public List<DiffRow>? Rows { get => (List<DiffRow>?)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public FileComparison? Comparison { get => (FileComparison?)GetValue(ComparisonProperty); set => SetValue(ComparisonProperty, value); }
    private bool _syncing;
    private readonly List<int> _changes = [];
    private int _changeIndex;
    public DiffViewer() => InitializeComponent();
    private static void RowsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (DiffViewer)sender;
        viewer._changes.Clear();
        bool wasChange = false;
        if (e.NewValue is List<DiffRow> rows)
            for (int i = 0; i < rows.Count; i++)
            {
                bool isChange = rows[i].Added || rows[i].Removed;
                if (isChange && !wasChange) viewer._changes.Add(i);
                wasChange = isChange;
            }
        viewer._changeIndex = 0;
        viewer.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(viewer.ScrollToChange));
    }
    private void ScrollToChange()
    {
        int row = _changes.Count == 0 ? 0 : Math.Max(0, _changes[_changeIndex] - 4);
        FindScrollViewer(BeforeList)?.ScrollToVerticalOffset(row * 21);
        FindScrollViewer(AfterList)?.ScrollToVerticalOffset(row * 21);
    }
    private void PreviousChange(object sender, RoutedEventArgs e) { if (_changes.Count > 0) { _changeIndex = (_changeIndex + _changes.Count - 1) % _changes.Count; ScrollToChange(); } }
    private void NextChange(object sender, RoutedEventArgs e) { if (_changes.Count > 0) { _changeIndex = (_changeIndex + 1) % _changes.Count; ScrollToChange(); } }
    private void DiffScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_syncing || e.VerticalChange == 0 || sender is not ListBox source || e.OriginalSource is not ScrollViewer viewer) return;
        _syncing = true;
        try { FindScrollViewer(source == BeforeList ? AfterList : BeforeList)?.ScrollToVerticalOffset(viewer.VerticalOffset); }
        finally { _syncing = false; }
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
