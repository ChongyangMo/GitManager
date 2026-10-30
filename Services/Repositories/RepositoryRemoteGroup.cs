using System.Text.RegularExpressions;

namespace GitManager.Services.Repositories;

// 导航栏按仓库的远程 Git 服务分组：服务云、GitHub、其他主机，没有远程的归入“仅本地”。
public static class RepositoryRemoteGroup
{
    public const string ServiceCloud = "服务云", GitHub = "GitHub", LocalPath = "本地路径", LocalOnly = "仅本地";

    public static int Order(string group) => group switch { ServiceCloud => 0, GitHub => 1, LocalOnly => 4, "" => 5, LocalPath => 3, _ => 2 };

    public static string FromRemote(string remote, string serviceCloudUrl, string serviceCloudBaseUrl, string serviceCloudGitBaseUrl = "")
    {
        if (string.IsNullOrWhiteSpace(remote)) return LocalOnly;
        string? host = Host(remote);
        if (host is null) return LocalPath;
        if (remote.Trim() == serviceCloudUrl.Trim() || host.Equals(Host(serviceCloudUrl), StringComparison.OrdinalIgnoreCase)
            || host.Equals(Host(serviceCloudBaseUrl), StringComparison.OrdinalIgnoreCase)
            || host.Equals(Host(serviceCloudGitBaseUrl), StringComparison.OrdinalIgnoreCase)) return ServiceCloud;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? GitHub : host.ToLowerInvariant();
    }

    public static string? Host(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        string value = address.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh" or "git")
            return uri.Host.Length > 0 ? uri.Host : null;
        return Regex.Match(value, @"^(?:[^@\s/]+@)?([^:\s/\\]{2,}):(?!\\|/)") is { Success: true } match ? match.Groups[1].Value : null;
    }

    // 返回用于显示名称的地址（origin，缺省时为服务云地址）及所属分组；没有 origin 时按第一个远程分组。
    public static async Task<(string Address, string Group)> ReadAsync(GitRepositoryService git, string root, string serviceCloudBaseUrl, string serviceCloudGitBaseUrl = "")
    {
        string origin = await git.ReadConfigAsync(root, "remote.origin.url");
        string cloud = await git.ReadConfigAsync(root, "gitmanager.serviceCloudUrl");
        string address = origin.Length > 0 ? origin : cloud;
        string grouped = address;
        if (grouped.Length == 0)
        {
            var urls = await git.Runner.RunAsync(root, ["config", "--get-regexp", @"^remote\..*\.url$"], true);
            if (urls.ExitCode is not (0 or 1)) throw new InvalidOperationException("读取远程地址失败：" + urls.Error);
            string first = urls.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            grouped = first.IndexOf(' ') is int space and > 0 ? first[(space + 1)..].Trim() : "";
        }
        return (address, FromRemote(grouped, cloud, serviceCloudBaseUrl, serviceCloudGitBaseUrl));
    }
}
