using GitManager.Services;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    public Task SetThemeAsync(string theme) => ExecuteAsync("正在切换主题", () =>
    {
        if (theme is not ("Dark" or "Light" or "System")) throw new InvalidOperationException("不支持的主题。");
        string previous = Settings.Value.Theme;
        Settings.Value.Theme = theme;
        try { Settings.Save(); ThemeService.Apply(theme); }
        catch
        {
            Settings.Value.Theme = previous;
            Settings.Save();
            ThemeService.Apply(previous);
            throw;
        }
        string name = theme switch { "Dark" => "黑夜模式", "Light" => "白天模式", _ => "跟随系统主题" };
        Status = "已切换为" + name;
        AppLog.Write(Status);
        return Task.CompletedTask;
    });
}
