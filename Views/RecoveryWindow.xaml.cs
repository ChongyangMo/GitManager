using System.Windows;
using GitManager.Models;

namespace GitManager.Views;

public partial class RecoveryWindow : Window
{
    public RecoveryPoint? Selected => PointsList.SelectedItem as RecoveryPoint;
    public RecoveryWindow(IReadOnlyList<RecoveryPoint> points)
    {
        InitializeComponent();
        GitManager.Services.ThemeService.Attach(this);
        PointsList.ItemsSource = points;
        if (points.Count > 0) PointsList.SelectedIndex = 0;
    }
    private void ApplyClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is not null) DialogResult = true;
    }
}
