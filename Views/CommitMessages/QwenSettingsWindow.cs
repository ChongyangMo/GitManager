using System.Windows;
using GitManager.Services;
using GitManager.Services.CommitMessages;
using GitManager.ViewModels;

namespace GitManager.Views.CommitMessages;

public sealed class QwenSettingsWindow : FormWindow
{
    private readonly QwenOptions _previous;

    public QwenSettingsWindow(QwenOptions options) : base("千问总结",
        new("接口地址", options.BaseUrl), new("模型", options.Model),
        new(HasStoredKey(options) ? "API Key（已保存，留空保留）" : "API Key", required: false, secret: true))
    {
        _previous = options;
        ShowOption("启用千问总结", options.Enabled);
        Validate = () =>
        {
            try
            {
                var value = Options;
                _ = value.Endpoint();
                if (value.Enabled && string.IsNullOrWhiteSpace(Fields[2].Value) && QwenCredentials.Read(value) is not { Length: > 0 })
                    return "请填写此接口地址对应的 API Key。";
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
            var dialog = new QwenSettingsWindow(viewModel.Settings.Value.Qwen) { Owner = owner };
            if (dialog.ShowDialog() == true) await viewModel.SaveQwenSettingsAsync(dialog.Options, dialog.Fields[2].Value);
            dialog.Fields[2].Value = "";
        }
        catch (Exception ex)
        {
            AppLog.Write("打开千问设置失败", ex);
            MessageBox.Show(owner, AppLog.Clean(ex.Message), "千问总结", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
