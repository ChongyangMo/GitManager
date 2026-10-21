using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace GitManager.Services.ServiceCloud;

public sealed class ServiceCloudClient : IDisposable
{
    private readonly HttpClient _http;
    public Uri BaseUri { get; }
    public string Username { get; private set; } = "";

    public ServiceCloudClient(string baseUrl, string proxyUrl = "")
    {
        BaseUri = NormalizeBaseUrl(baseUrl);
        var proxy = ParseProxy(proxyUrl);
        _http = new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false,
            UseProxy = proxy is not null, Proxy = proxy is null ? null : new WebProxy(proxy) })
        { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(30) };
    }

    public static Uri? ParseProxy(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "socks5")
            || uri.UserInfo.Length > 0 || uri.Host.Length == 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new InvalidOperationException("代理地址应为完整的 HTTP、HTTPS 或 SOCKS5 地址，且不包含账号密码。");
        return uri;
    }

    public static Uri NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("请填写完整的 HTTPS 服务云地址，不要包含账号密码或查询参数。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    public async Task LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) throw new InvalidOperationException("请填写服务云账号和密码。");
        using var response = await _http.PostAsJsonAsync("api/login", new { username = username.Trim(), password }, cancellationToken);
        using var body = await ReadResponseAsync(response, cancellationToken);
        if (!body.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
            || !body.RootElement.TryGetProperty("user", out var user) || string.IsNullOrWhiteSpace(user.GetString()))
            throw new InvalidOperationException("服务云登录未成功，请检查账号和密码。");
        Username = user.GetString()!;
        AppLog.Write("服务云登录成功");
    }

    public async Task<IReadOnlyList<CloudRepository>> ListRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/git/repos", cancellationToken);
        using var body = await ReadResponseAsync(response, cancellationToken);
        if (!body.RootElement.TryGetProperty("repos", out var repos)) throw new InvalidOperationException("服务云没有返回仓库列表。");
        if (repos.ValueKind == JsonValueKind.Null) return [];
        return repos.EnumerateArray().Select(repo => new CloudRepository(repo.GetProperty("name").GetString()!,
            repo.TryGetProperty("is_private", out var isPrivate) && isPrivate.ValueKind == JsonValueKind.True))
            .OrderBy(repo => repo.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string> CreateRepositoryAsync(string name, CancellationToken cancellationToken = default)
    {
        name = NormalizeRepositoryName(name);
        using var response = await _http.PostAsJsonAsync("api/git/repos/create", new { name }, cancellationToken);
        using var body = await ReadResponseAsync(response, cancellationToken);
        if (!body.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("服务云未确认仓库创建成功。");
        return NormalizeRepositoryName(body.RootElement.GetProperty("name").GetString()!);
    }

    public string RepositoryUrl(string name) => new Uri(BaseUri, "git/" + string.Join('/',
        NormalizeRepositoryName(name).Split('/').Select(Uri.EscapeDataString)) + ".git").AbsoluteUri;

    public static string NormalizeRepositoryName(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || name.StartsWith('-') || name.Contains('\\') || name.Any(char.IsControl)
            || name.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.IndexOfAny([':', '?', '#']) >= 0))
            throw new InvalidOperationException("请填写有效的仓库名称，目录使用斜杠分隔。");
        return name;
    }

    private static async Task<JsonDocument> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("服务云账号密码错误或登录已过期，请重新登录。");
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException("当前服务云账号没有此操作的权限。");
        JsonDocument body;
        try { body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)); }
        catch (JsonException) { throw new InvalidOperationException($"服务云返回了无法识别的响应（{(int)response.StatusCode}），请检查服务地址。"); }
        if (response.IsSuccessStatusCode) return body;
        string error = body.RootElement.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : $"请求失败（{(int)response.StatusCode}）";
        body.Dispose();
        throw new InvalidOperationException("服务云操作失败：" + error[..Math.Min(error.Length, 300)]);
    }

    public void Dispose() => _http.Dispose();
}
