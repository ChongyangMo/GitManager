using System.Windows;
using GitManager.Services;
using GitManager.Services.CommitMessages;
using GitManager.ViewModels;

namespace GitManager.Views.CommitMessages;

public sealed class QwenSettingsWindow : FormWindow
{
    private readonly QwenOptions _previous;

    public QwenSettingsWindow(QwenOptions options, ClaudeOptions? claude = null) : base("大模型总结",
        new("千问接口地址", options.BaseUrl), new("千问模型", options.Model),
        new(HasStoredKey(options) ? "千问 API Key（已保存，留空保留）" : "千问 API Key", required: false, secret: true))
    {
        _previous = options;
        claude ??= new();
        ShowNote("总结顺位", "正在检测本机 Claude…");
        Loaded += async (_, _) =>
        {
            try { ShowNote("总结顺位", await SummaryProviderOrder.DescribeAsync(options, claude, default)); }
            catch (Exception ex) { ShowNote("总结顺位", "检测失败：" + AppLog.Clean(ex.Message)); }
        };
        ShowOption("启用大模型总结", options.Enabled);
        ShowAction("用量统计", () => new QwenUsageWindow(options) { Owner = this }.ShowDialog());
        Validate = () =>
        {
            try
            {
                var value = Options;
                _ = value.Endpoint();
                if (value.Enabled && !SummaryProviderOrder.ClaudeInstalled(claude) && string.IsNullOrWhiteSpace(Fields[2].Value) && QwenCredentials.Read(value) is not { Length: > 0 })
                    return "本机没有 Claude，请填写此接口地址对应的千问 API Key。";
                return null;
            }
            catch (Exception ex) { return AppLog.Clean(ex.Message); }
        };
    }

    public QwenOptions Options => new()
    {
        Enabled = OptionValue, BaseUrl = Fields[0].Value.Trim().TrimEnd('/'), Model = Fields[1].Value.Trim(),
        MaxInputCharacters = _previous.MaxInputCharacters, MaxFileCharacters = _previous.MaxFileCharacters,
        MaxOutputTokens = _previous.MaxOutputTokens, TimeoutSeconds = _previous.TimeoutSeconds,
        PublicGitHosts = [.. _previous.PublicGitHosts], Pricing = _previous.Pricing
    };

    private static bool HasStoredKey(QwenOptions options)
    {
        try { return QwenCredentials.Read(options) is { Length: > 0 }; }
        catch (InvalidOperationException) { return false; }
    }

    public static async void Open(Window owner, MainViewModel viewModel)
    {
        try
        {
            var dialog = new QwenSettingsWindow(viewModel.Settings.Value.Qwen, viewModel.Settings.Value.Claude) { Owner = owner };
            if (dialog.ShowDialog() == true) await viewModel.SaveQwenSettingsAsync(dialog.Options, dialog.Fields[2].Value);
            dialog.Fields[2].Value = "";
        }
        catch (Exception ex)
        {
            AppLog.Write("打开大模型总结设置失败", ex);
            MessageBox.Show(owner, AppLog.Clean(ex.Message), "大模型总结", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
