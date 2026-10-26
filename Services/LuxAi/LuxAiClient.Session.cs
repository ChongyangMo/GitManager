using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitManager.Services.LuxAi;

public sealed partial class LuxAiClient
{
    private static string? _rejectedAccountKey, _lastAccountKey;
    private static DateTimeOffset _nextAccountAttempt;

    public async Task<LuxAiSession> EnsureSessionAsync(CancellationToken token, bool renew = false)
    {
        var session = LuxAiCredentials.Read(options);
        return session is null || renew || LuxAiAuthentication.NeedsRenewal(session)
            ? await RefreshAsync(session, token) : session;
    }

    // 用已保存的真实账号重新登录，也供诊断断线恢复使用，不修改或删除现有会话。
    public Task<LuxAiSession> ReloginAsync(CancellationToken token) => RefreshAsync(null, token, signIn: true);

    private async Task<LuxAiSession> RefreshAsync(LuxAiSession? previous, CancellationToken token, bool signIn = false)
    {
        await RefreshGate.WaitAsync(token);
        try
        {
            var current = LuxAiCredentials.Read(options);
            if (!signIn && current is not null && current != previous && !LuxAiAuthentication.NeedsRenewal(current)) return current;
            previous = current ?? previous;
            if (!signIn && previous is { RefreshToken.Length: > 0 })
            {
                try { return await RenewSessionAsync(previous, token); }
                catch (LuxAiLoginRequiredException) { /* 续期凭据失效后尝试账号密码登录。 */ }
            }
            return await SignInSavedAccountAsync(previous, token);
        }
        finally { RefreshGate.Release(); }
    }

    private async Task<LuxAiSession> RenewSessionAsync(LuxAiSession previous, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(LuxAiOptions.AccountEndpoint, "api/Account/RefreshToken"));
        request.Headers.Authorization = new("Bearer", previous.RefreshToken);
        using var response = await _http.SendAsync(request, token);
        using var body = await ReadJsonAsync(response, token);
        var fresh = LuxAiAuthentication.ReadLogin(body.RootElement) ?? throw new LuxAiLoginRequiredException();
        fresh = fresh with { EmployeeCode = fresh.EmployeeCode.Length > 0 ? fresh.EmployeeCode : previous.EmployeeCode };
        fresh = await SaveSessionAsync(fresh, token);
        AppLog.Write("集团 AI 登录状态已自动续期");
        return fresh;
    }

    private async Task<LuxAiSession> SignInSavedAccountAsync(LuxAiSession? previous, CancellationToken token)
    {
        var account = LuxAiAccountCredentials.Read(options) ?? throw new LuxAiLoginRequiredException();
        if (previous is { EmployeeCode.Length: > 0 } && account.EmployeeCode != previous.EmployeeCode)
            throw new LuxAiLoginRequiredException();
        string key = LuxAiAccountCredentials.Key(account)!;
        if (key == _rejectedAccountKey) throw new LuxAiLoginRequiredException();
        if (key == _lastAccountKey && DateTimeOffset.UtcNow < _nextAccountAttempt)
            throw new InvalidOperationException("集团 AI 自动登录暂未完成，将稍后自动重试。");
        _lastAccountKey = key;
        _nextAccountAttempt = DateTimeOffset.UtcNow.AddMinutes(1);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(LuxAiOptions.AccountEndpoint, "api/Account/Login"));
        request.Content = JsonContent.Create(new
        {
            code = Convert.ToBase64String(Encoding.UTF8.GetBytes(account.Code)),
            pwd = Convert.ToBase64String(Encoding.UTF8.GetBytes(account.Password)),
            isFromAdmin = false, isEncrypted = true, companyCode = account.CompanyCode
        }, options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        request.Headers.AcceptLanguage.ParseAdd("zh-CN");
        try
        {
            using var response = await _http.SendAsync(request, token);
            using var body = await ReadJsonAsync(response, token);
            if (LoginDenied(body.RootElement)) throw new LuxAiLoginRequiredException();
            CheckBody(body.RootElement);
            var fresh = LuxAiAuthentication.ReadLogin(body.RootElement) ?? throw new LuxAiLoginRequiredException();
            if (account.EmployeeCode.Length > 0 && fresh.EmployeeCode != account.EmployeeCode)
                throw new LuxAiLoginRequiredException();
            fresh = await SaveSessionAsync(fresh, token);
            _rejectedAccountKey = null;
            _nextAccountAttempt = DateTimeOffset.MinValue;
            AppLog.Write("集团 AI 已使用保存的账号自动恢复登录");
            return fresh;
        }
        catch (LuxAiLoginRequiredException)
        {
            _rejectedAccountKey = key;
            throw;
        }
    }

    private async Task<LuxAiSession> SaveSessionAsync(LuxAiSession fresh, CancellationToken token)
    {
        if (fresh.RefreshToken.Length == 0) throw new InvalidOperationException("集团 AI 未返回完整的登录凭据。");
        fresh = fresh with { RenewedAt = DateTimeOffset.UtcNow };
        // 先保存已轮换的凭据，避免验证遇到临时网络错误时丢失。
        LuxAiCredentials.Save(options, fresh);
        await ValidateSessionAsync(fresh, token);
        return fresh;
    }

    private static bool LoginDenied(JsonElement body)
    {
        if (LuxAiAuthentication.Success(body) || body.ValueKind != JsonValueKind.Object) return false;
        if (LuxAiAuthentication.Code(body) is 401 or 403) return true;
        string message = "";
        foreach (string name in new[] { "Msg", "ErrMsg", "msg" })
            if (body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) message += value.GetString();
        return new[] { "密码", "账号", "工号", "用户", "验证", "验证码" }.Any(message.Contains)
            && new[] { "错误", "不正确", "不存在", "锁定", "失败", "无效", "过期" }.Any(message.Contains);
    }
}
