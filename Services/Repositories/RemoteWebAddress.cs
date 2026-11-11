using System.Text.RegularExpressions;

namespace GitManager.Services.Repositories;

// 把远程 Git 地址换成可在浏览器打开的网页地址：去掉 .git 后缀和账号信息，SSH 地址改为 https；本地路径等无法打开的返回 null。
public static class RemoteWebAddress
{
    public static string? FromRemote(string remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return null;
        string value = remote.Trim();
        string host, path;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh" or "git")
        {
            if (uri.Host.Length == 0) return null;
            bool web = uri.Scheme is "http" or "https";
            host = web && !uri.IsDefaultPort ? uri.Host + ":" + uri.Port : uri.Host;
            path = uri.AbsolutePath;
            if (web) return Build(uri.Scheme, host, path);
        }
        else if (Regex.Match(value, @"^(?:[^@\s/]+@)?([^:\s/\\]{2,}):(?!\\|/)(.+)$") is { Success: true } match)
        {
            host = match.Groups[1].Value;
            path = match.Groups[2].Value;
        }
        else return null;
        return Build("https", host, path);
    }

    private static string? Build(string scheme, string host, string path)
    {
        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return Uri.TryCreate($"{scheme}://{host}/{path}", UriKind.Absolute, out var result) ? result.AbsoluteUri : null;
    }
}
