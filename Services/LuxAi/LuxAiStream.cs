using System.Text;
using System.IO;
using System.Text.Json;

namespace GitManager.Services.LuxAi;

public static class LuxAiStream
{
    public static async Task<string> ReadAsync(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var text = new StringBuilder();
        string eventName = "";
        var data = new List<string>();
        bool done = false;
        while (!done && await reader.ReadLineAsync(token) is { } line)
        {
            if (line.Length == 0) { Dispatch(); eventName = ""; data.Clear(); }
            else if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].TrimStart();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Add(line[5..].TrimStart(' '));
                if (data.Sum(part => part.Length) > 1000000) throw new InvalidOperationException("集团 AI 单条流事件过长。");
            }
            if (text.Length > 16000) throw new InvalidOperationException("集团 AI 提交说明过长，请重试。");
        }
        if (!done && data.Count > 0) Dispatch();
        string message = text.ToString().Trim();
        if (message.Length == 0 || message.Length > 16000 || message.Contains('\0')) throw new InvalidOperationException("集团 AI 没有返回有效的提交说明。");
        token.ThrowIfCancellationRequested();
        return message;

        void Dispatch()
        {
            if (data.Count == 0) return;
            string raw = string.Join('\n', data);
            if (raw == "[DONE]") { done = true; return; }
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            if (body.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("集团 AI 返回的流事件格式无效。");
            bool hasError = body.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)
                && (error.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(error.GetString()));
            if (eventName.Contains("error", StringComparison.OrdinalIgnoreCase) || hasError
                || body.TryGetProperty("type", out var kind) && kind.GetString() == "error")
                throw new InvalidOperationException("集团 AI 返回错误事件，请重试。");
            if (eventName.Contains("done", StringComparison.OrdinalIgnoreCase)) { done = true; return; }
            if (!eventName.Equals("streaming", StringComparison.OrdinalIgnoreCase)) return;
            if (body.TryGetProperty("type", out var type) && type.GetString() != "text") return;
            if (body.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String) text.Append(delta.GetString());
        }
    }
}
