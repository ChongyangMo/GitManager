using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GitManager.Services.CommitMessages.Usage;

namespace GitManager.Services.CommitMessages;

public sealed record CodexReply(string Message, QwenCostEstimate Cost);

public static class CodexClient
{
    public static string? FindExecutable(CodexOptions options) => CodexProcess.FindExecutable(options);

    public static async Task<bool> IsLoggedInAsync(string executable, CancellationToken token)
    {
        try
        {
            var reply = await CodexProcess.RunAsync(executable, ["login", "status", "-c", "forced_login_method=\"chatgpt\""],
                Path.GetTempPath(), null, 15, token);
            return reply.ExitCode == 0 && (reply.Output + reply.Error).Contains("Logged in using ChatGPT", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { return false; }
    }

    public static async Task<CodexReply> GenerateAsync(CodexOptions options, string executable, string content, CancellationToken token,
        QwenUsageStore? usageStore = null, string? systemPrompt = null)
    {
        options.Validate();
        string id = Guid.NewGuid().ToString("N"), result = "失败";
        string directory = Path.Combine(Path.GetTempPath(), "GitManagerCodex", id);
        var startedAt = DateTimeOffset.Now;
        var timer = Stopwatch.StartNew();
        int? input = null, cached = null, output = null;
        var cost = new QwenCostEstimate(null, null, "使用本机 Codex 登录账号额度；接口未返回实际费用，不作金额估算");
        try
        {
            Directory.CreateDirectory(directory);
            string instructions = Path.Combine(directory, "summary.md");
            await File.WriteAllTextAsync(instructions, systemPrompt ?? QwenPrompt.System, token);
            // 使用独立工作目录和本机登录，仅做文本总结，不加载用户配置、项目说明、记忆、插件或执行工具。
            List<string> arguments = ["exec", "--ignore-user-config", "--ignore-rules", "--ephemeral", "--skip-git-repo-check",
                "--sandbox", "read-only", "--json", "--color", "never", "--model", options.Model.Trim(),
                "-c", "forced_login_method=\"chatgpt\"", "-c", "approval_policy=\"never\"",
                "-c", "model_reasoning_effort=" + JsonSerializer.Serialize(options.ReasoningEffort),
                "-c", "model_instructions_file=" + JsonSerializer.Serialize(instructions),
                "-c", "project_doc_max_bytes=0", "-c", "web_search=\"disabled\"", "-c", "mcp_servers={}"];
            foreach (string feature in new[] { "shell_tool", "unified_exec", "multi_agent", "multi_agent_v2", "plugins", "apps",
                "remote_plugin", "memories", "browser_use", "computer_use", "image_generation", "view_image", "goals",
                "sleep_tool", "workspace_dependencies", "code_mode_host", "hooks" })
                arguments.AddRange(["--disable", feature]);
            arguments.Add("-");
            var reply = await CodexProcess.RunAsync(executable, arguments, directory, content, options.TimeoutSeconds, token);
            string message = "";
            bool completed = false;
            foreach (string line in reply.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = JsonDocument.Parse(line);
                var body = document.RootElement;
                string? type = body.TryGetProperty("type", out var kind) ? kind.GetString() : null;
                if (type == "turn.completed")
                {
                    completed = true;
                    if (body.TryGetProperty("usage", out var usage))
                    {
                        input = Count(usage, "input_tokens"); cached = Count(usage, "cached_input_tokens"); output = Count(usage, "output_tokens");
                    }
                }
                if (type == "item.completed" && body.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var itemType) && itemType.GetString() == "agent_message")
                    message = item.GetProperty("text").GetString()?.Trim() ?? "";
            }
            if (reply.ExitCode != 0 || !completed)
            {
                AppLog.Write("Codex 调用失败，退出码：" + reply.ExitCode + "，错误：" + AppLog.Clean(reply.Error).Trim());
                throw new InvalidOperationException("Codex 调用失败，请检查登录状态、模型权限或账号额度。");
            }
            if (message.Length == 0 || message.Contains('\0')) throw new InvalidOperationException("Codex 返回的总结为空或无效。");
            result = "成功";
            AppLog.Write($"Codex 调用用量：模型 {options.Model.Trim()}，输入 {Display(input)}，缓存 {Display(cached)}，输出 {Display(output)} 个令牌，耗时 {timer.ElapsedMilliseconds} 毫秒，调用编号 {id}");
            return new(message, cost);
        }
        catch (TimeoutException) { result = "超时"; throw; }
        catch (OperationCanceledException) { result = "已取消"; throw; }
        catch (JsonException) { throw new InvalidOperationException("Codex 返回了无法识别的结果。"); }
        catch (IOException) { throw new InvalidOperationException("Codex 总结文件读写失败。"); }
        finally
        {
            (usageStore ?? QwenUsageStore.Default).TrySave(new QwenUsageEntry
            {
                Id = id, Time = startedAt, Provider = QwenUsageEntry.CodexProvider, Model = options.Model.Trim(), Result = result,
                Milliseconds = timer.ElapsedMilliseconds, InputTokens = input, CachedTokens = cached, OutputTokens = output, Cost = cost
            });
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Write("清理 Codex 总结临时目录失败", ex); }
        }
    }

    private static int? Count(JsonElement body, string name) => body.TryGetProperty(name, out var number)
        && number.TryGetInt32(out int count) && count >= 0 ? count : null;
    private static string Display(int? count) => count?.ToString() ?? "未返回";
}
