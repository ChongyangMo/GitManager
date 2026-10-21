using System.Text;
using System.Text.Json;

namespace GitManager.Services.LuxAi;

public sealed class LuxAiLoginRequiredException() : InvalidOperationException("请在“设置 → 大模型总结 → 集团 AI”中登录一次，后续自动复用登录状态。");

public static class LuxAiAuthentication
{
    public static bool Success(JsonElement body) => body.ValueKind == JsonValueKind.Object
        && (!body.TryGetProperty("code", out var code)
            ? !body.TryGetProperty("Code", out code) || code.TryGetInt32(out var capital) && capital is 0 or 200 or 10001
            : code.TryGetInt32(out var lower) && lower is 0 or 200 or 10001);

    public static int? Code(JsonElement body) => body.ValueKind == JsonValueKind.Object
        && (body.TryGetProperty("code", out var value) || body.TryGetProperty("Code", out value))
        && value.TryGetInt32(out int code) ? code : null;

    public static LuxAiSession? ReadLogin(JsonElement body)
    {
        if (!Success(body) || !body.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        string ai = "", refresh = Text(data, "NewToken");
        if (refresh.Length == 0) refresh = Text(data, "Token");
        if (data.TryGetProperty("AIToken", out var token))
        {
            if (token.ValueKind == JsonValueKind.String) ai = token.GetString() ?? "";
            else if (token.ValueKind == JsonValueKind.Object && token.TryGetProperty("Data", out var nested)) ai = Text(nested, "token");
        }
        string employee = data.TryGetProperty("User", out var user) ? Text(user, "EmpCode") : "";
        return ai.Length > 0 ? new(ai, refresh, employee) : null;
    }

    public static bool Expiring(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return false;
            string value = parts[1].Replace('-', '+').Replace('_', '/');
            value = value.PadRight((value.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(value));
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out long seconds)
                && DateTimeOffset.FromUnixTimeSeconds(seconds) <= DateTimeOffset.UtcNow.AddMinutes(3);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException) { return false; }
    }

    private static string Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
