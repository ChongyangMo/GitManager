using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GitManager.Services.CommitMessages;

public sealed record ClaudeReply(string Message, QwenCostEstimate Cost);

// 通过本机 Claude Code 命令行总结，使用其登录账号，不需要单独的 API Key。
public static class ClaudeClient
{
    public static string? FindExecutable()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "claude.exe"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"));
        foreach (string path in candidates)
        {
            try { if (File.Exists(path)) return path; }
            catch (Exception ex) when (ex is ArgumentException or IOException) { }
        }
        return null;
    }

    public static async Task<bool> IsLoggedInAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunAsync(executable, ["auth", "status"], null, TimeSpan.FromSeconds(15), cancellationToken);
            using var document = JsonDocument.Parse(result.Output);
            return document.RootElement.TryGetProperty("loggedIn", out var loggedIn) && loggedIn.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { return false; }
    }

    public static async Task<ClaudeReply> GenerateAsync(ClaudeOptions options, string executable, string content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Model) || options.Model.Any(char.IsControl) || options.TimeoutSeconds is < 10 or > 600)
            throw new InvalidOperationException("Claude 配置中的模型或超时设置无效。");
        // 不开放任何工具、不加载项目配置和 MCP，只做一次纯文本总结。
        string[] arguments = ["-p", "--model", options.Model.Trim(), "--output-format", "json", "--tools", "",
            "--system-prompt", QwenPrompt.System, "--no-session-persistence", "--strict-mcp-config", "--setting-sources", "", "--disable-slash-commands"];
        var timer = Stopwatch.StartNew();
        var result = await RunAsync(executable, arguments, content, TimeSpan.FromSeconds(options.TimeoutSeconds), cancellationToken);
        JsonElement body;
        try { using var document = JsonDocument.Parse(result.Output); body = document.RootElement.Clone(); }
        catch (JsonException) { throw new InvalidOperationException("Claude 返回了无法识别的结果" + Detail(result.Error)); }
        string text = body.TryGetProperty("result", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
        if (body.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("Claude 调用失败" + Detail(text.Length > 0 ? text : result.Error));
        if (text.Length == 0 || text.Contains('\0')) throw new InvalidOperationException("Claude 返回的提交说明为空或无效。");
        long input = 0, output = 0;
        if (body.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            foreach (string name in new[] { "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens" })
                if (usage.TryGetProperty(name, out var number) && number.TryGetInt64(out long count)) input += count;
            if (usage.TryGetProperty("output_tokens", out var outputTokens) && outputTokens.TryGetInt64(out long outputCount)) output = outputCount;
        }
        string usd = body.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDecimal().ToString("0.######") : "未返回";
        AppLog.Write($"Claude 调用用量：模型 {options.Model.Trim()}，输入 {input}，输出 {output} 个令牌，耗时 {timer.ElapsedMilliseconds} 毫秒，按 API 标价折合 ${usd}");
        return new(text, new(0m, 0m, "使用本机 Claude 登录账号的额度，不产生千问费用"));
    }

    private static string Detail(string text) => string.IsNullOrWhiteSpace(text) ? "。" : "：" + AppLog.Clean(text.Trim()).Split('\n')[0];

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string executable, string[] arguments, string? standardInput, TimeSpan limit, CancellationToken cancellationToken)
    {
        // 在空的临时目录中运行，避免读取当前仓库里的 CLAUDE.md 等项目说明。
        string directory = Path.Combine(Path.GetTempPath(), "GitManagerClaude");
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false), StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        // 系统环境变量里的 API Key 或中转地址会优先于登录账号，这里只使用 Claude Code 的登录状态。
        foreach (string name in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL" }) start.Environment.Remove(name);
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        process.Start();
        using var cancellation = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        Task input = WriteInputAsync();
        async Task WriteInputAsync()
        {
            try { if (standardInput is not null) await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeout.Token).ConfigureAwait(false); }
            finally { process.StandardInput.Close(); }
        }
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await input.ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            try { await Task.WhenAll(output, error, input).ConfigureAwait(false); } catch (OperationCanceledException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Claude 总结等待超时。");
        }
        catch (IOException) { throw new InvalidOperationException("Claude 进程意外退出。"); }
    }
}
