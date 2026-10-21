using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitManager.Services;
using GitManager.Services.ServiceCloud;
using Microsoft.Win32;

namespace GitManager.Views.ServiceCloud;

public partial class ServiceCloudWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly ServiceCloudCredentials _credentials;
    private readonly string? _root;
    private readonly CancellationTokenSource _lifetime = new();
    private ServiceCloudClient? _client;
    private bool _busy, _closed;
    public CloudConnection? Connection { get; private set; }

    public ServiceCloudWindow(SettingsStore settings, string? root)
    {
        InitializeComponent();
        ThemeService.Attach(this);
        _settings = settings;
        _root = root;
        _credentials = new(new GitRunner(() => settings.Value.GitExecutablePath, _lifetime.Token));
        ServerBox.Text = settings.Value.ServiceCloud.BaseUrl;
        UsernameBox.Text = settings.Value.ServiceCloud.Username;
        ProxyBox.Text = settings.Value.ServiceCloud.ProxyUrl;
        NewNameBox.Text = root is null ? "" : Path.GetFileName(root);
        Loaded += async (_, _) =>
        {
            if (UsernameBox.Text.Length == 0 || ServerBox.Text.Length == 0) return;
            await RunAsync(async () =>
            {
                string? password = await _credentials.ReadPasswordAsync(ServerBox.Text, UsernameBox.Text);
                if (password is not null) await LoginAsync(password);
                else StatusText.Text = "请登录服务云";
            });
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _client?.Dispose(); };
    }

    private async void LoginClicked(object sender, RoutedEventArgs e)
    {
        if (_client is not null)
        {
            _client.Dispose(); _client = null; RepositoryList.ItemsSource = null;
            StatusText.Text = ""; UpdateControls(); return;
        }
        await RunAsync(async () => await LoginAsync(PasswordBox.Password));
    }

    private async Task LoginAsync(string password)
    {
        var client = new ServiceCloudClient(ServerBox.Text, ProxyBox.Text);
        try
        {
            await client.LoginAsync(UsernameBox.Text, password, _lifetime.Token);
            var repositories = await client.ListRepositoriesAsync(_lifetime.Token);
            string loginName = UsernameBox.Text.Trim();
            await _credentials.SaveAsync(client.BaseUri.AbsoluteUri, loginName, password);
            _settings.Value.ServiceCloud = new() { BaseUrl = client.BaseUri.AbsoluteUri.TrimEnd('/'), Username = loginName, ProxyUrl = ProxyBox.Text.Trim() };
            _settings.Save();
            _client = client;
            ServerBox.Text = _settings.Value.ServiceCloud.BaseUrl;
            UsernameBox.Text = loginName;
            PasswordBox.Clear();
            RepositoryList.ItemsSource = repositories;
            StatusText.Text = $"已连接，{repositories.Count} 个仓库";
        }
        catch { client.Dispose(); throw; }
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_client is null) return;
        string? name = (RepositoryList.SelectedItem as CloudRepository)?.Name;
        var repositories = await _client.ListRepositoriesAsync(_lifetime.Token);
        RepositoryList.ItemsSource = repositories;
        RepositoryList.SelectedItem = repositories.FirstOrDefault(repo => repo.Name == name);
        StatusText.Text = $"{repositories.Count} 个仓库";
    });

    private void RepositoryChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) UpdateControls(); }
    private void ConnectClicked(object sender, RoutedEventArgs e)
    {
        if (RepositoryList.SelectedItem is CloudRepository repo) Complete(repo.Name, null);
    }
    private void CloneClicked(object sender, RoutedEventArgs e)
    {
        if (RepositoryList.SelectedItem is not CloudRepository repo) return;
        var picker = new OpenFolderDialog { Title = "选择本地空目录" };
        if (picker.ShowDialog(this) == true) Complete(repo.Name, picker.FolderName);
    }
    private async void CreateClicked(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_client is null) return;
        string name = await _client.CreateRepositoryAsync(NewNameBox.Text, _lifetime.Token);
        Complete(name, null);
    });

    private void Complete(string name, string? cloneDirectory)
    {
        if (_client is null) return;
        Connection = new(_client.BaseUri.AbsoluteUri, _settings.Value.ServiceCloud.Username, _client.RepositoryUrl(name), cloneDirectory, _settings.Value.ServiceCloud.ProxyUrl);
        DialogResult = true;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; UpdateControls(); StatusText.Text = "正在连接服务云";
        try { await action(); }
        catch (Exception ex) when (_closed || ex is OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = AppLog.Clean(ex.Message); AppLog.Write("服务云操作失败", ex); }
        finally { _busy = false; if (!_closed) UpdateControls(); }
    }

    private void UpdateControls()
    {
        bool logged = _client is not null, selected = RepositoryList.SelectedItem is CloudRepository;
        LoginFields.IsEnabled = !_busy && !logged;
        LoginButton.IsEnabled = !_busy; LoginButton.Content = logged ? "重新登录" : "登录";
        RefreshButton.IsEnabled = RepositoryList.IsEnabled = !_busy && logged;
        CreatePanel.IsEnabled = !_busy && logged && _root is not null;
        ConnectButton.IsEnabled = !_busy && logged && selected && _root is not null;
        CloneButton.IsEnabled = !_busy && logged && selected;
    }
}
