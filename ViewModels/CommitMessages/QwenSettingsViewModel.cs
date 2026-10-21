using GitManager.Services;
using GitManager.Services.CommitMessages;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    public Task SaveQwenSettingsAsync(QwenOptions options, string apiKey) => ExecuteAsync("正在保存千问设置", () =>
    {
        _ = options.Endpoint();
        if (!string.IsNullOrWhiteSpace(apiKey)) QwenCredentials.Save(options, apiKey);
        if (options.Enabled && QwenCredentials.Read(options) is not { Length: > 0 })
            throw new InvalidOperationException("请填写此接口地址对应的千问 API Key。");
        CancelCommitSummary();
        var previous = Settings.Value.Qwen;
        Settings.Value.Qwen = options;
        try { Settings.Save(); }
        catch { Settings.Value.Qwen = previous; throw; }
        AppLog.Write("千问设置已保存，密钥保存在 Windows 凭据管理器中");
        Status = options.Enabled ? "已启用千问总结" : "已切换本地规则总结";
        return Task.CompletedTask;
    });
}
