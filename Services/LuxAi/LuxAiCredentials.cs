using System.Security.Cryptography;
using System.Text;

namespace GitManager.Services.LuxAi;

public sealed record LuxAiSession(string AiToken, string RefreshToken, string EmployeeCode, DateTimeOffset? RenewedAt = null);

public static class LuxAiCredentials
{
    internal static string EndpointKey(LuxAiOptions options) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Endpoint().AbsoluteUri)));
    private static string Target(LuxAiOptions options) => "GitManager/LuxAI/" + EndpointKey(options);

    public static LuxAiSession? Read(LuxAiOptions options) =>
        LuxAiCredentialStore.Read<LuxAiSession>(Target(options), "集团 AI 登录状态");

    public static void Save(LuxAiOptions options, LuxAiSession session)
    {
        if (string.IsNullOrWhiteSpace(session.AiToken) || session.AiToken.Any(char.IsWhiteSpace)
            || session.RefreshToken.Any(char.IsWhiteSpace)) throw new InvalidOperationException("集团 AI 返回的登录凭据无效。");
        LuxAiCredentialStore.Save(Target(options), session.EmployeeCode, session, "集团 AI 登录状态");
    }
}
