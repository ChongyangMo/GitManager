using System.Net;
using System.Net.Http;

namespace GitManager.Services.LuxAi;

public sealed class LuxAiOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://ai.luxvisions-inno.com:8000";
    public string ProxyUrl { get; set; } = "http://127.0.0.1:1080";
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxInputCharacters { get; set; } = 12000;
    public int MaxFileCharacters { get; set; } = 4000;

    public Uri Endpoint()
    {
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.Equals("ai.luxvisions-inno.com", StringComparison.OrdinalIgnoreCase) || uri.Port != 8000
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new InvalidOperationException("请填写集团 AI 的 HTTPS 8000 接口地址。");
        if (TimeoutSeconds is < 10 or > 300 || MaxInputCharacters is < 1000 or > 14500
            || MaxFileCharacters is < 256 or > 10000)
            throw new InvalidOperationException("集团 AI 的输入长度或超时配置无效。");
        _ = Proxy();
        return uri;
    }

    public Uri Proxy()
    {
        if (!Uri.TryCreate(ProxyUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "http"
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new InvalidOperationException("请填写有效的 HTTP 内网代理地址。");
        return uri;
    }

    public HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        UseProxy = true, Proxy = new WebProxy(Proxy()), AllowAutoRedirect = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static Uri LoginPage => new("https://ai.luxvisions-inno.com/#/pc/guide");
    public static Uri AccountEndpoint => new("https://ai.luxvisions-inno.com:5000/");
}
