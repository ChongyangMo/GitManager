using System.Windows;
using GitManager.Views.ServiceCloud;

namespace GitManager;

public partial class MainWindow
{
    private async void ServiceCloudClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        var dialog = new ServiceCloudWindow(ViewModel.Settings, ViewModel.Repository?.Path) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Connection is { } connection)
            await ViewModel.ConnectServiceCloudAsync(connection);
    }
}
