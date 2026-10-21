using System.IO;
using System.Text.RegularExpressions;

namespace GitManager.Services.Repositories;

public static class RepositoryDisplayName
{
    public static string? FromRemote(string remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return null;
        string path = remote.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh" or "git" or "file")
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        else if (Regex.Match(path, @"^[^@\s]+@[^:\s]+:(.+)$") is { Success: true } match)
            path = match.Groups[1].Value;
        string name = Path.GetFileName(path.TrimEnd('/', '\\'));
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) ? null : name;
    }

    public static async Task<string?> ReadAsync(GitRepositoryService git, string root)
    {
        string remote = await git.ReadConfigAsync(root, "remote.origin.url");
        if (remote.Length == 0) remote = await git.ReadConfigAsync(root, "gitmanager.serviceCloudUrl");
        return FromRemote(remote);
    }
}
