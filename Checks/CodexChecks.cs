using System.IO;
using GitManager.Services;
using GitManager.Services.CommitMessages;
using GitManager.Services.CommitMessages.Usage;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunCodexLiveAsync(string repository, string[] paths)
    {
        var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), "GitManagerCodexChecks", Guid.NewGuid().ToString("N"), "settings.json"));
        settings.Value.Claude.Enabled = false;
        settings.Value.Codex = new();
        settings.Value.Qwen.Enabled = true;
        var options = settings.Value.Codex;
        string executable = CodexClient.FindExecutable(options) ?? throw new InvalidOperationException("本机未检测到 Codex。");
        Check(await CodexClient.IsLoggedInAsync(executable, default), "Codex 复用本机 ChatGPT 登录");
        var files = (await Git.ReadChangesAsync(repository)).Where(file => paths.Contains(file.Path, StringComparer.OrdinalIgnoreCase)).ToArray();
        Check(files.Length == paths.Length, "使用指定文件的真实 Git 改动");
        int before = QwenUsageStore.Default.Read().Entries.Count;
        var generator = new CommitMessageGenerator(Git, settings);
        var result = await generator.GenerateAsync(repository, files, default);
        Check(result.Source == "Codex" && !string.IsNullOrWhiteSpace(result.Message), "Claude 关闭后由 Codex 接手真实总结");
        var recorded = QwenUsageStore.Default.Read().Entries;
        Check(recorded.Count == before + 1 && recorded[0].Provider == QwenUsageEntry.CodexProvider
            && recorded[0].Model == options.Model && recorded[0].Result == "成功"
            && recorded[0].InputTokens > 0 && recorded[0].OutputTokens > 0, "记录 Codex 实际模型、调用结果及令牌用量");
        Check(result.Cost.Minimum is null, "账号额度未返回金额时保持费用待确认");
        var cached = await generator.GenerateAsync(repository, files, default);
        Check(cached.Source == "本地缓存" && cached.Message == result.Message
            && QwenUsageStore.Default.Read().Entries.Count == recorded.Count, "相同改动复用 Codex 缓存，不重复消耗额度");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool stopped = false;
        try { await generator.GenerateAsync(repository, files, cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped && QwenUsageStore.Default.Read().Entries.Count == recorded.Count, "取消后停止调用，不触发千问回退");
        settings.Save();
        Check(new SettingsStore(settings.FilePath).Value.Codex.Model == options.Model, "Codex 设置可通过配置文件保存和恢复");
        Console.WriteLine("Codex 实际模型：" + options.Model + " / " + options.ReasoningEffort);
        Console.WriteLine(result.Message);
    }
}
