using System.ComponentModel;
using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace GitManager.Services;

public sealed class TrayService : IDisposable
{
    private readonly MainWindow _window;
    private readonly Drawing.Icon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly Forms.NotifyIcon _tray;
    private WindowState _restoreState = WindowState.Normal;
    private bool _exiting, _disposed;

    public TrayService(MainWindow window)
    {
        _window = window;
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/GitManager;component/Assets/gitmanager.ico"))
            ?? throw new InvalidOperationException("无法读取托盘图标。");
        using (resource.Stream)
        using (var icon = new Drawing.Icon(resource.Stream, 32, 32))
            _icon = (Drawing.Icon)icon.Clone();

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("显示主窗口", null, (_, _) => Restore());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _startupItem = new Forms.ToolStripMenuItem("开机自启动");
        _startupItem.Click += ToggleStartup;
        _menu.Items.Add(_startupItem);
        _menu.Opening += MenuOpening;
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => Exit());
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "GitManager", ContextMenuStrip = _menu, Visible = true };
        _tray.MouseDoubleClick += TrayDoubleClicked;
        _window.Closing += WindowClosing;
        _window.StateChanged += WindowStateChanged;
    }

    private void MenuOpening(object? sender, CancelEventArgs e)
    {
        try
        {
            _startupItem.Checked = StartupService.IsEnabled;
            _startupItem.Enabled = true;
        }
        catch (Exception ex)
        {
            _startupItem.Checked = false;
            _startupItem.Enabled = false;
            AppLog.Write("读取开机自启动设置失败", ex);
        }
    }

    private void ToggleStartup(object? sender, EventArgs e)
    {
        try
        {
            StartupService.SetEnabled(!StartupService.IsEnabled);
            _startupItem.Checked = StartupService.IsEnabled;
        }
        catch (Exception ex)
        {
            AppLog.Write("修改开机自启动设置失败", ex);
            MessageBox.Show(_window, "无法修改开机自启动设置：" + ex.Message, "设置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        _window.Hide();
        AppLog.Write("主窗口已隐藏到托盘");
    }

    private void WindowStateChanged(object? sender, EventArgs e)
    {
        if (_window.WindowState != WindowState.Minimized) _restoreState = _window.WindowState;
    }

    private void TrayDoubleClicked(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left) Restore();
    }

    public void Restore()
    {
        if (_disposed || _exiting) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = _restoreState;
        _window.Activate();
        AppLog.Write("已从托盘显示主窗口");
    }

    public void Exit()
    {
        if (_disposed || _exiting) return;
        if (_window.ViewModel.IsBusy)
        {
            Restore();
            MessageBox.Show(_window, "正在执行 Git 操作，请等待完成后再退出。", "操作进行中", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        PrepareForShutdown();
        AppLog.Write("已从托盘退出程序");
        Application.Current.Shutdown();
    }

    public void PrepareForShutdown() => _exiting = true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Closing -= WindowClosing;
        _window.StateChanged -= WindowStateChanged;
        _tray.MouseDoubleClick -= TrayDoubleClicked;
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
