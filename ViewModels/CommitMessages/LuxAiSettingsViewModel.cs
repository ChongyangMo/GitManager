using GitManager.Services;
using GitManager.Services.LuxAi;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    public Task SaveLuxAiSettingsAsync(LuxAiOptions options) => ExecuteAsync("正在保存集团 AI 设置", () =>
    {
        _ = options.Endpoint();
        if (options.Enabled && LuxAiCredentials.Read(options) is null && LuxAiAccountCredentials.Read(options) is null)
            throw new LuxAiLoginRequiredException();
        CancelCommitSummary();
        var previous = Settings.Value.LuxAi;
        Settings.Value.LuxAi = options;
        try { Settings.Save(); }
        catch { Settings.Value.LuxAi = previous; throw; }
        Status = options.Enabled ? "已启用内网仓库集团 AI 总结" : "已关闭内网仓库集团 AI 总结";
        AppLog.Write(Status);
        return Task.CompletedTask;
    });
}
