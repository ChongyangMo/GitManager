using System.IO;
using System.Net;
using System.Net.Sockets;

namespace GitManager.Services.CommitMessages;

public static class InternalGitAddress
{
    public static async Task<string?> LocalOnlyReasonAsync(string address, string serviceCloudUrl, CancellationToken token, IReadOnlyCollection<string>? publicGitHosts = null)
    {
        string? host = Host(address);
        if (host is null) return "本地路径或无法识别的 Git 地址";
        string? cloudHost = Host(serviceCloudUrl);
        if (host.Equals("sdserver.luxvisions-inno.com", StringComparison.OrdinalIgnoreCase)
            || cloudHost is not null && (host.Equals(cloudHost, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + cloudHost, StringComparison.OrdinalIgnoreCase)))
            return "服务云仓库";
        if (IPAddress.TryParse(host, out var ip)) return IsPrivate(ip) ? "内网 Git 地址" : null;
        if (!host.Contains('.') || new[] { ".local", ".lan", ".internal", ".home", ".home.arpa", ".localhost", ".localdomain" }
            .Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            return "内网 Git 主机";
        // 公网托管服务可能被本机代理解析成虚拟内网 IP；仅对明确配置的公开域名放行。
        if (publicGitHosts?.Any(value => host.Equals(value.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase)) == true) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
            return addresses.Length == 0 || addresses.Any(IsPrivate) ? "远程主机解析到内网地址" : null;
        }
        catch (SocketException) { return "无法确认远程主机地址"; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return "远程主机解析超时"; }
    }

    private static string? Host(string address)
    {
        address = address.Trim();
        if (address.Length == 0 || Path.IsPathRooted(address)) return null;
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "ssh" or "git")
            return uri.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (address.Contains("://")) return null;
        // 同时支持 git@host:path 和 git@[IPv6]:path；本地路径不作为外部仓库处理。
        string part = address[(address.LastIndexOf('@') + 1)..];
        if (part.StartsWith('['))
        {
            int end = part.IndexOf(']');
            return end > 0 && part.Length > end + 1 && part[end + 1] == ':' ? part[1..end] : null;
        }
        int colon = part.IndexOf(':');
        return colon > 1 && part[..colon].IndexOfAny(['/', '\\', ':']) < 0
            ? part[..colon].TrimEnd('.').ToLowerInvariant() : null;
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return (bytes[0] & 0xE0) != 0x20;
        return bytes[0] is 0 or 10 or 127 || bytes[0] >= 224
            || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
            || bytes[0] == 169 && bytes[1] == 254
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0)
            || bytes[0] == 198 && bytes[1] is 18 or 19;
    }
}
