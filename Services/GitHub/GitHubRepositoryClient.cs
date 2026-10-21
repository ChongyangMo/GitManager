using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GitManager.Services.GitHub;

public sealed class GitHubRepositoryClient : IDisposable
{
    private readonly HttpClient _http;

    public GitHubRepositoryClient(string token, string proxyUrl)
    {
        var proxy = ServiceCloud.ServiceCloudClient.ParseProxy(proxyUrl);
        _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = proxy is not null,
            Proxy = proxy is null ? null : new WebProxy(proxy) })
        { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GitManager/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<bool> EnsureExistsAsync(GitHubRepositoryAddress address, bool isPrivate, CancellationToken token = default)
    {
        using var current = await _http.GetAsync(address.ApiPath, token);
        if (current.IsSuccessStatusCode) return false;
        if (current.StatusCode != HttpStatusCode.NotFound) throw Failure(current);

        using var userResponse = await _http.GetAsync("user", token);
        if (!userResponse.IsSuccessStatusCode) throw Failure(userResponse);
        using var user = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync(token));
        string login = user.RootElement.GetProperty("login").GetString()!;
        string endpoint = "user/repos";
        if (!login.Equals(address.Owner, StringComparison.OrdinalIgnoreCase))
        {
            using var ownerResponse = await _http.GetAsync("users/" + Uri.EscapeDataString(address.Owner), token);
            if (!ownerResponse.IsSuccessStatusCode) throw Failure(ownerResponse);
            using var owner = JsonDocument.Parse(await ownerResponse.Content.ReadAsStringAsync(token));
            if (owner.RootElement.GetProperty("type").GetString() != "Organization")
                throw new InvalidOperationException($"当前 GitHub 登录账号为 {login}，无法在 {address.Owner} 的个人账号下创建仓库，请切换 Git 登录账号。");
            endpoint = "orgs/" + Uri.EscapeDataString(address.Owner) + "/repos";
        }

        using var created = await _http.PostAsJsonAsync(endpoint, new { name = address.Name, @private = isPrivate, auto_init = false }, token);
        if (created.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            // 其他窗口可能刚刚建好同名仓库；重新确认后继续正常获取和推送。
            using var concurrent = await _http.GetAsync(address.ApiPath, token);
            if (concurrent.IsSuccessStatusCode) return false;
        }
        if (created.StatusCode != HttpStatusCode.Created) throw Failure(created);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(token));
        if (!string.Equals(body.RootElement.GetProperty("full_name").GetString(), address.FullName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHub 返回的仓库地址与远程配置不一致，请检查后重试。");
        AppLog.Write($"已自动创建 GitHub {(isPrivate ? "私有" : "公开")}仓库：{address.FullName}");
        return true;
    }

    public async Task<GitHubRepositoryVisibility> GetVisibilityAsync(GitHubRepositoryAddress address, CancellationToken token = default)
    {
        using var response = await _http.GetAsync(address.ApiPath, token);
        if (!response.IsSuccessStatusCode) throw Failure(response);
        return await ReadVisibilityAsync(response, address, token);
    }

    public async Task<GitHubRepositoryVisibility> SetVisibilityAsync(GitHubRepositoryAddress address, bool isPrivate, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, address.ApiPath) { Content = JsonContent.Create(new { visibility = isPrivate ? "private" : "public" }) };
        using var response = await _http.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            throw new InvalidOperationException("GitHub 拒绝修改可见性，分叉仓库或组织策略可能不允许此操作。");
        if (!response.IsSuccessStatusCode) throw Failure(response);
        var result = await ReadVisibilityAsync(response, address, token);
        if (result.IsPrivate != isPrivate) throw new InvalidOperationException("GitHub 未应用新的可见性，请稍后刷新确认。");
        AppLog.Write($"已将 GitHub 仓库 {address.FullName} 改为{result.Label}");
        return result;
    }

    private static async Task<GitHubRepositoryVisibility> ReadVisibilityAsync(HttpResponseMessage response, GitHubRepositoryAddress address, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = body.RootElement;
        if (!string.Equals(root.GetProperty("full_name").GetString(), address.FullName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHub 返回的仓库地址与远程配置不一致，请检查后重试。");
        string visibility = root.TryGetProperty("visibility", out var value) ? value.GetString() ?? "" : root.GetProperty("private").GetBoolean() ? "private" : "public";
        bool admin = root.TryGetProperty("permissions", out var permissions) && permissions.TryGetProperty("admin", out var flag) && flag.GetBoolean();
        bool fork = root.TryGetProperty("fork", out var forked) && forked.GetBoolean();
        return new(visibility, admin && !fork);
    }

    private static InvalidOperationException Failure(HttpResponseMessage response) => new(response.StatusCode switch
    {
        HttpStatusCode.Unauthorized => "GitHub 登录已失效，请通过 Git 凭据管理器重新登录。",
        HttpStatusCode.Forbidden => "当前 GitHub 凭据没有此操作的权限，或请求受到限制，请检查账号授权。",
        HttpStatusCode.NotFound => "GitHub 账号或仓库不可访问，请检查远程地址和登录账号。",
        HttpStatusCode.UnprocessableEntity => "GitHub 未能创建仓库，同名仓库可能已存在但当前账号无权访问，请检查账号授权。",
        _ => $"GitHub 仓库操作失败（{(int)response.StatusCode}），请检查网络和账号授权。"
    });

    public void Dispose() => _http.Dispose();
}

public sealed record GitHubRepositoryVisibility(string Visibility, bool CanChange)
{
    public bool IsPrivate => Visibility != "public";
    public string Label => Visibility switch { "public" => "公开", "internal" => "内部", _ => "私有" };
}
