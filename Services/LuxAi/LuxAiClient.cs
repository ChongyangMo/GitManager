using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GitManager.Services.CommitMessages;

namespace GitManager.Services.LuxAi;

public sealed class LuxAiClient(LuxAiOptions options) : IDisposable
{
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private readonly HttpClient _http = options.CreateHttpClient();

    public async Task ValidateSessionAsync(LuxAiSession session, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.Endpoint(), "api/v1/chat/conversation"));
        request.Headers.Authorization = new("Bearer", session.AiToken);
        using var response = await _http.SendAsync(request, token);
        using var body = await ReadJsonAsync(response, token);
        CheckBody(body.RootElement);
    }

    public async Task<LuxAiSession> EnsureSessionAsync(CancellationToken token, bool renew = false)
    {
        var session = LuxAiCredentials.Read(options) ?? throw new LuxAiLoginRequiredException();
        return renew || LuxAiAuthentication.Expiring(session.AiToken) ? await RefreshAsync(session, token) : session;
    }

    private async Task<LuxAiSession> RefreshAsync(LuxAiSession previous, CancellationToken token)
    {
        await RefreshGate.WaitAsync(token);
        try
        {
            var current = LuxAiCredentials.Read(options);
            if (current is not null && current.AiToken != previous.AiToken) return current;
            if (previous.RefreshToken.Length == 0) throw new LuxAiLoginRequiredException();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(LuxAiOptions.AccountEndpoint, "api/Account/RefreshToken"));
            request.Headers.Authorization = new("Bearer", previous.RefreshToken);
            using var response = await _http.SendAsync(request, token);
            using var body = await ReadJsonAsync(response, token);
            var fresh = LuxAiAuthentication.ReadLogin(body.RootElement) ?? throw new LuxAiLoginRequiredException();
            if (fresh.RefreshToken.Length == 0) throw new LuxAiLoginRequiredException();
            fresh = fresh with { EmployeeCode = fresh.EmployeeCode.Length > 0 ? fresh.EmployeeCode : previous.EmployeeCode };
            await ValidateSessionAsync(fresh, token);
            LuxAiCredentials.Save(options, fresh);
            AppLog.Write("集团 AI 登录状态已自动续期");
            return fresh;
        }
        finally { RefreshGate.Release(); }
    }

    public async Task<string> GenerateAsync(string content, CancellationToken cancellationToken, string? systemPrompt = null)
    {
        _ = options.Endpoint();
        string prompt = (systemPrompt ?? QwenPrompt.System) + "\n\n" + content;
        if (prompt.Length > 16000) throw new InvalidOperationException("集团 AI 总结输入超过 16000 字符，请减少勾选文件或调整长度配置。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var timer = Stopwatch.StartNew();
        try
        {
            var session = await EnsureSessionAsync(timeout.Token);
            using var result = await SendAuthenticatedAsync(HttpMethod.Post, "api/v1/chat/session", new { conversation_id = (string?)null }, session, timeout.Token);
            using var json = await ReadJsonAsync(result.Response, timeout.Token);
            CheckBody(json.RootElement);
            string id = json.RootElement.GetProperty("data").GetProperty("id").GetString()
                ?? throw new InvalidOperationException("集团 AI 未返回会话 ID。");
            using var chat = await SendAuthenticatedAsync(HttpMethod.Post, "api/v1/ollama/chat/" + Uri.EscapeDataString(id),
                new { content = prompt, thinking_enabled = false, search_enabled = false }, result.Session, timeout.Token);
            string? mediaType = chat.Response.Content.Headers.ContentType?.MediaType;
            if (mediaType != "text/event-stream")
            {
                using var error = await ReadJsonAsync(chat.Response, timeout.Token);
                CheckBody(error.RootElement);
                throw new InvalidOperationException("集团 AI 没有返回流式回答。");
            }
            await using var stream = await chat.Response.Content.ReadAsStreamAsync(timeout.Token);
            string message = await LuxAiStream.ReadAsync(stream, timeout.Token);
            AppLog.Write($"集团 AI 总结完成，耗时 {timer.ElapsedMilliseconds} 毫秒；平台未披露模型名称和费用");
            return message;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("集团 AI 总结等待超时，请重试。"); }
        catch (HttpRequestException) { throw new InvalidOperationException("无法通过内网代理连接集团 AI，请检查 WindowsStream 内网连接。"); }
        catch (JsonException) { throw new InvalidOperationException("集团 AI 返回格式无法识别，请检查平台接口。"); }
    }

    private async Task<AuthenticatedResponse> SendAuthenticatedAsync(HttpMethod method, string path, object data, LuxAiSession session, CancellationToken token)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(method, new Uri(options.Endpoint(), path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AiToken);
            request.Headers.AcceptLanguage.ParseAdd("zh-CN");
            request.Content = JsonContent.Create(data);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            bool unauthorized = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            if (!unauthorized && response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                try
                {
                    await response.Content.LoadIntoBufferAsync(1000000, token);
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    unauthorized = LuxAiAuthentication.Code(body.RootElement) is 401 or 403;
                }
                catch { response.Dispose(); throw; }
            }
            if (!unauthorized)
            {
                if (!response.IsSuccessStatusCode)
                {
                    int status = (int)response.StatusCode;
                    response.Dispose();
                    throw new InvalidOperationException($"集团 AI 返回 HTTP {status}。");
                }
                return new(response, session);
            }
            response.Dispose();
            if (attempt == 1) throw new LuxAiLoginRequiredException();
            session = await RefreshAsync(session, token);
        }
        throw new LuxAiLoginRequiredException();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LuxAiLoginRequiredException();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"集团 AI 返回 HTTP {(int)response.StatusCode}。");
        await response.Content.LoadIntoBufferAsync(1000000, token);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }

    private static void CheckBody(JsonElement body)
    {
        if (LuxAiAuthentication.Code(body) is 401 or 403) throw new LuxAiLoginRequiredException();
        if (!LuxAiAuthentication.Success(body)) throw new InvalidOperationException($"集团 AI 返回业务码 {LuxAiAuthentication.Code(body)}。");
    }

    public void Dispose() => _http.Dispose();
    private sealed record AuthenticatedResponse(HttpResponseMessage Response, LuxAiSession Session) : IDisposable
    { public void Dispose() => Response.Dispose(); }
}
