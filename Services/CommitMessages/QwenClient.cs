using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GitManager.Services.CommitMessages.Usage;

namespace GitManager.Services.CommitMessages;

public sealed record QwenReply(string Message, int? InputTokens, int? CachedTokens, int? OutputTokens, long Milliseconds, QwenCostEstimate Cost);

public static class QwenClient
{
    public static async Task<QwenReply> GenerateAsync(QwenOptions options, string apiKey, string content, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new { model = options.Model.Trim(), enable_thinking = false,
            max_tokens = options.MaxOutputTokens, temperature = 0.2, stream = false,
            messages = new[] { new { role = "system", content = QwenPrompt.System }, new { role = "user", content } } });
        var timer = Stopwatch.StartNew();
        string requestId = Guid.NewGuid().ToString("N"), result = "失败";
        var startedAt = DateTimeOffset.Now;
        QwenUsage? usage = null;
        bool usageReported = false;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "千问 API Key 无效，请在设置中重新填写。",
                    System.Net.HttpStatusCode.Forbidden => "千问调用被拒绝，请检查模型开通状态和密钥权限。",
                    System.Net.HttpStatusCode.TooManyRequests => "千问请求受限，请检查余额或稍后重试。",
                    _ => $"千问调用失败（HTTP {(int)response.StatusCode}），请检查模型名称、余额和服务状态。"
                });
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var body = document.RootElement;
            usage = QwenUsageReporter.ReadAndLog(options, body, timer.ElapsedMilliseconds, requestId);
            usageReported = true;
            if (!body.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                throw new InvalidOperationException("千问没有返回提交说明，请重试。" + usage.Cost.Label);
            var choice = choices[0];
            if (!choice.TryGetProperty("finish_reason", out var finish) || finish.GetString() != "stop")
                throw new InvalidOperationException("千问回复未完整结束，请重试或调整输出长度。" + usage.Cost.Label);
            string message = choice.GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
            if (message.Length == 0 || message.Contains('\0')) throw new InvalidOperationException("千问返回的提交说明为空或无效。");
            result = "成功";
            return new(message, usage.InputTokens, usage.CachedTokens, usage.OutputTokens, timer.ElapsedMilliseconds, usage.Cost);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("千问总结等待超时，请重试。"); }
        catch (OperationCanceledException) { result = "已取消"; throw; }
        catch (HttpRequestException) { throw new InvalidOperationException("无法连接千问，请检查网络和接口地址。"); }
        catch (JsonException) { throw new InvalidOperationException("千问返回了无法识别的响应，请检查接口地址。"); }
        finally
        {
            if (!usageReported) AppLog.Write("千问请求未取得完整用量，费用待确认，实际以账单为准");
            QwenUsageStore.Default.TrySave(new QwenUsageEntry
            {
                Id = requestId, Time = startedAt, Model = options.Model, Result = result, Milliseconds = timer.ElapsedMilliseconds,
                InputTokens = usage?.InputTokens, CachedTokens = usage?.CachedTokens, OutputTokens = usage?.OutputTokens,
                Cost = usage?.Cost ?? new(null, null, "接口未返回完整用量，实际以账单为准"),
                Pricing = options.Pricing is null ? null : JsonSerializer.Deserialize<QwenTokenPricing>(JsonSerializer.Serialize(options.Pricing))
            });
        }
    }
}
