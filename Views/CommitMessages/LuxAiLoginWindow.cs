using System.IO;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitManager.Services;
using GitManager.Services.LuxAi;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GitManager.Views.CommitMessages;

public sealed class LuxAiLoginWindow : Window
{
    private readonly LuxAiOptions _options;
    private readonly WebView2 _browser = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly CancellationTokenSource _lifetime = new();
    private LuxAiSession? _observed;
    private bool _completing, _closed;

    public LuxAiLoginWindow(LuxAiOptions options)
    {
        _ = options.Endpoint();
        _options = options;
        Title = "集团 AI 登录";
        Width = 1020; Height = 740; MinWidth = 760; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)FindResource("WorkspaceWindow");
        ThemeService.Attach(this);
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(_browser);
        var footer = new DockPanel { Margin = new Thickness(14, 10, 14, 10) };
        Grid.SetRow(footer, 1); grid.Children.Add(footer);
        var cancel = new Button { Content = "取消", IsCancel = true, Style = (Style)FindResource("QuietButton") };
        DockPanel.SetDock(cancel, Dock.Right); footer.Children.Add(cancel);
        var retry = new Button { Content = "重试", Style = (Style)FindResource("QuietButton"), Margin = new Thickness(0, 0, 8, 0) };
        retry.Click += (_, _) => _browser.CoreWebView2?.Reload();
        DockPanel.SetDock(retry, Dock.Right); footer.Children.Add(retry);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        footer.Children.Add(_status); Content = grid;
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _browser.Dispose(); _lifetime.Dispose(); };
    }

    private async Task InitializeAsync()
    {
        try
        {
            _status.Text = "正在连接集团 AI";
            string directory = Path.Combine(SettingsStore.DataDirectory, "luxai-browser");
            var environment = await CoreWebView2Environment.CreateAsync(null, directory,
                new CoreWebView2EnvironmentOptions("--proxy-server=" + _options.Proxy().GetLeftPart(UriPartial.Authority)));
            if (_closed) return;
            await _browser.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            var core = _browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Profile.PreferredColorScheme = ThemeService.IsDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
            core.NavigationStarting += (_, args) =>
            {
                if (!AllowedNavigation(args.Uri)) { args.Cancel = true; _status.Text = "此登录窗口只打开集团 AI 及企业微信登录页面"; }
            };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (AllowedNavigation(args.Uri)) core.Navigate(args.Uri);
            };
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.NavigationCompleted += (_, args) =>
            {
                if (!_completing) _status.Text = args.IsSuccess ? "在此完成集团 AI 登录，支持企业微信扫码" : "页面连接失败，请检查内网代理后重新打开";
            };
            core.WebResourceResponseReceived += ResponseReceived;
            core.Navigate(LuxAiOptions.LoginPage.AbsoluteUri);
            AppLog.Write("已打开集团 AI 内置登录窗口，使用配置的内网代理");
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _status.Text = "打开集团 AI 登录失败，请确认已安装 Microsoft Edge WebView2 Runtime";
            AppLog.Write("打开集团 AI 登录窗口失败", ex);
        }
    }

    private async void ResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        try
        {
            if (_closed || !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri)
                || uri.Scheme != "https" || uri.Host != "ai.luxvisions-inno.com" || args.Response.StatusCode != 200) return;
            if (uri.Port == 5000 && uri.AbsolutePath.StartsWith("/api/Account/", StringComparison.OrdinalIgnoreCase)
                && (uri.AbsolutePath.Contains("Login", StringComparison.OrdinalIgnoreCase)
                    || uri.AbsolutePath.EndsWith("/RefreshToken", StringComparison.OrdinalIgnoreCase)))
            {
                await using var stream = await args.Response.GetContentAsync();
                if (stream is null) return;
                using var reader = new StreamReader(stream);
                var buffer = new char[65537];
                int count = await reader.ReadBlockAsync(buffer.AsMemory(), _lifetime.Token);
                if (count > 65536) return;
                using var body = JsonDocument.Parse(new string(buffer, 0, count));
                if (LuxAiAuthentication.ReadLogin(body.RootElement) is { } session)
                {
                    _observed = session;
                    await CompleteAsync(session);
                }
            }
            else if (uri.Port == 8000 && uri.AbsolutePath == "/api/v1/chat/conversation"
                && args.Request.Headers.Contains("Authorization")
                && AuthenticationHeaderValue.TryParse(args.Request.Headers.GetHeader("Authorization"), out var header)
                && header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) && header.Parameter is { Length: > 0 } aiToken)
            {
                // 只观察本应用自己的 WebView；不会读取外部 Edge/Chrome 的用户资料。
                await Task.Delay(400, _lifetime.Token);
                var previous = _observed ?? LuxAiCredentials.Read(_options);
                await CompleteAsync(new(aiToken, previous?.RefreshToken ?? "", previous?.EmployeeCode ?? ""));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed) { _completing = false; _status.Text = "登录尚未完成验证，请重试"; AppLog.Write("集团 AI 登录验证失败", ex); }
        }
    }

    private async Task CompleteAsync(LuxAiSession session)
    {
        if (_closed || _completing) return;
        _completing = true;
        _status.Text = "已检测到登录，正在验证接口";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var client = new LuxAiClient(_options);
        await client.ValidateSessionAsync(session, timeout.Token);
        if (_closed) return;
        LuxAiCredentials.Save(_options, session);
        AppLog.Write("集团 AI 登录验证通过，会话已保存至 Windows 凭据管理器");
        DialogResult = true;
    }

    private static bool AllowedNavigation(string address)
    {
        if (address == "about:blank") return true;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https") return false;
        return uri.Host is "ai.luxvisions-inno.com" or "ai.luxshare-ict.com"
            || uri.Host == "weixin.qq.com" || uri.Host.EndsWith(".weixin.qq.com", StringComparison.OrdinalIgnoreCase);
    }
}
