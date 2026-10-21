using System.IO;
using System.Windows;
using GitManager.Services;
using GitManager.Services.CommitMessages;
using GitManager.Services.CommitMessages.Usage;
using GitManager.ViewModels.CommitMessages.Usage;

namespace GitManager.Views.CommitMessages;

public partial class QwenUsageWindow : Window
{
    public QwenUsageViewModel ViewModel { get; }

    public QwenUsageWindow(QwenOptions options) : this(options, QwenUsageStore.Default, Path.Combine(SettingsStore.DataDirectory, "logs")) { }
    public QwenUsageWindow(QwenOptions options, QwenUsageStore store, string? logsDirectory)
    {
        ViewModel = new(options, store, logsDirectory);
        InitializeComponent(); DataContext = ViewModel;
        ThemeService.Attach(this);
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;
        Loaded += async (_, _) => await ViewModel.ReloadAsync();
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await ViewModel.ReloadAsync();
}
