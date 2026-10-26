using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitManager.Services.LuxAi;

public sealed record LuxAiAccount(string Code, string Password, JsonElement? CompanyCode,
    string EmployeeCode = "", DateTimeOffset? SavedAt = null)
{
    public override string ToString() => "集团 AI 自动登录账号（凭据已隐藏）";
}

public static class LuxAiAccountCredentials
{
    private static string Target(LuxAiOptions options) => "GitManager/LuxAIAccount/" + LuxAiCredentials.EndpointKey(options);
    public static LuxAiAccount? Read(LuxAiOptions options) =>
        LuxAiCredentialStore.Read<LuxAiAccount>(Target(options), "集团 AI 自动登录账号");

    public static void Save(LuxAiOptions options, LuxAiAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.Code) || string.IsNullOrEmpty(account.Password))
            throw new InvalidOperationException("请使用账号密码登录集团 AI，以启用断线自动登录。");
        LuxAiCredentialStore.Save(Target(options), account.Code, account with { SavedAt = DateTimeOffset.UtcNow }, "集团 AI 自动登录账号");
    }

    public static void Delete(LuxAiOptions options) => LuxAiCredentialStore.Delete(Target(options));
    internal static string? Key(LuxAiAccount? account) => account is null ? null :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(account))));

    // 只接收本应用内置网页登录的实际请求；厂区值和类型按原请求保存。
    public static LuxAiAccount? ReadLoginRequest(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("code", out var code)
            || code.ValueKind != JsonValueKind.String || !body.TryGetProperty("pwd", out var password)
            || password.ValueKind != JsonValueKind.String) return null;
        string user = code.GetString() ?? "", secret = password.GetString() ?? "";
        if (body.TryGetProperty("isEncrypted", out var encrypted) && encrypted.ValueKind == JsonValueKind.True)
        {
            try { user = Encoding.UTF8.GetString(Convert.FromBase64String(user)); secret = Encoding.UTF8.GetString(Convert.FromBase64String(secret)); }
            catch (FormatException) { return null; }
        }
        if (string.IsNullOrWhiteSpace(user) || secret.Length == 0) return null;
        JsonElement? company = body.TryGetProperty("companyCode", out var value)
            && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.Clone() : null;
        return new(user, secret, company);
    }
}
