using System.Windows.Controls;
using GitManager.ViewModels;
using GitManager.Models;

namespace GitManager.Views.Versions;

public partial class VersionComparisonView : UserControl
{
    public VersionComparisonView() => InitializeComponent();
    private async void VersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel { IsBusy: false, HasRepository: true } vm || sender is not ComboBox { SelectedItem: VersionChoice choice }) return;
        if (sender == BeforeSelector) vm.ComparisonBefore = choice;
        else vm.ComparisonAfter = choice;
        await vm.CompareSelectedVersionsAsync();
    }
    private async void FileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel { IsBusy: false, HasRepository: true } vm || sender is not ListBox list) return;
        vm.SelectedVersionFile = list.SelectedItem as HistoryFile;
        await vm.ShowChangeAsync();
    }
}
