using System.IO;
using System.Text.RegularExpressions;

namespace GitManager.Services.Repositories;

public static class RepositoryTargetIdentity
{
    private static readonly Regex ScpRemote = new(@"^(?:[^@\s]+@)?(?<host>[^:\s/\\]+):(?![\\/])(?<path>.+)$", RegexOptions.Compiled);

    public static string FromRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return "";
        string value = remote.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "ssh" or "git")
            return NetworkTarget(uri.Host, uri.Port, uri.IsDefaultPort, uri.AbsolutePath);
        if (Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "file")
            return LocalTarget(uri.LocalPath);
        if (ScpRemote.Match(value) is { Success: true } match)
            return NetworkTarget(match.Groups["host"].Value, 0, true, match.Groups["path"].Value);
        if (Path.IsPathRooted(value)) return LocalTarget(value);
        return value.TrimEnd('/', '\\').ToLowerInvariant();
    }

    private static string NetworkTarget(string host, int port, bool defaultPort, string path)
    {
        string authority = host.ToLowerInvariant() + (defaultPort || port <= 0 ? "" : ":" + port);
        string targetPath = Uri.UnescapeDataString(path).Trim('/', '\\');
        if (targetPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) targetPath = targetPath[..^4];
        return targetPath.Length == 0 ? authority : authority + "/" + targetPath;
    }

    private static string LocalTarget(string path)
    {
        try { return "file/" + RepositoryDiscovery.Normalize(path).ToLowerInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        { return "file/" + path.TrimEnd('/', '\\').ToLowerInvariant(); }
    }
}
