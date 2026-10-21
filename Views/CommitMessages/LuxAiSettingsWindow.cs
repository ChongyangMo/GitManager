using System.Windows;
using GitManager.Services;
using GitManager.Services.LuxAi;
using GitManager.ViewModels;

namespace GitManager.Views.CommitMessages;

public sealed class LuxAiSettingsWindow : FormWindow
{
    private readonly LuxAiOptions _previous;

    public LuxAiSettingsWindow(LuxAiOptions options) : base("集团 AI", new("接口", options.BaseUrl), new("内网代理", options.ProxyUrl))
    {
        _previous = options;
        ShowOption("内网仓库使用集团 AI", options.Enabled);
        UpdateStatus();
        ShowAction("登录", () =>
        {
            try
            {
                var login = new LuxAiLoginWindow(Options) { Owner = this };
                if (login.ShowDialog() == true) { ShowOption("内网仓库使用集团 AI", true); UpdateStatus(); }
            }
            catch (Exception ex) { ShowNote("连接", AppLog.Clean(ex.Message)); }
        });
        ShowAction("测试", async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                using var client = new LuxAiClient(Options);
                var session = await client.EnsureSessionAsync(timeout.Token);
                await client.ValidateSessionAsync(session, timeout.Token);
                ShowNote("连接", "连接正常");
            }
            catch (Exception ex) { ShowNote("连接", AppLog.Clean(ex.Message)); }
        });
        Validate = () =>
        {
            try
            {
                _ = Options.Endpoint();
                return OptionValue && LuxAiCredentials.Read(Options) is null ? "请先登录集团 AI。" : null;
            }
            catch (Exception ex) { return AppLog.Clean(ex.Message); }
        };
    }

    public LuxAiOptions Options => new()
    {
        Enabled = OptionValue, BaseUrl = Fields[0].Value.Trim().TrimEnd('/'), ProxyUrl = Fields[1].Value.Trim().TrimEnd('/'),
        TimeoutSeconds = _previous.TimeoutSeconds, MaxInputCharacters = _previous.MaxInputCharacters, MaxFileCharacters = _previous.MaxFileCharacters
    };

    private void UpdateStatus()
    {
        try { ShowNote("连接", LuxAiCredentials.Read(Options) is { } session ? "已登录 " + session.EmployeeCode : "未登录"); }
        catch (Exception ex) { ShowNote("连接", AppLog.Clean(ex.Message)); }
    }

    public static async Task OpenAsync(Window owner, MainViewModel viewModel)
    {
        try
        {
            var dialog = new LuxAiSettingsWindow(viewModel.Settings.Value.LuxAi) { Owner = owner };
            if (dialog.ShowDialog() == true) await viewModel.SaveLuxAiSettingsAsync(dialog.Options);
        }
        catch (Exception ex)
        {
            AppLog.Write("打开集团 AI 设置失败", ex);
            MessageBox.Show(owner, AppLog.Clean(ex.Message), "集团 AI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
