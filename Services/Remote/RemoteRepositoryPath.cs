using System.IO;
using System.Text.RegularExpressions;

namespace GitManager.Services.Remote;

public static class RemoteRepositoryPath
{
    private const string Prefix = "ssh://";
    private static readonly Regex AliasPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    public static bool IsRemote(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? path, out string alias, out string remotePath)
    {
        alias = "";
        remotePath = "";
        if (!IsRemote(path)) return false;
        string raw = path!.Trim();
        string body = raw[Prefix.Length..];
        int slash = body.IndexOf('/');
        if (slash <= 0) return false;
        string hostPart = body[..slash];
        string pathPart = body[slash..];
        if (!AliasPattern.IsMatch(hostPart)) return false;
        if (pathPart.Any(ch => ch == '\0' || char.IsControl(ch))) return false;
        alias = hostPart;
        remotePath = NormalizePosix(pathPart);
        return remotePath.StartsWith('/');
    }

    public static (string Alias, string RemotePath) Parse(string path)
    {
        if (!TryParse(path, out string alias, out string remotePath))
            throw new ArgumentException("远端仓库路径格式无效：" + path, nameof(path));
        return (alias, remotePath);
    }

    public static string Format(string alias, string remotePath)
    {
        string trimmedAlias = alias.Trim();
        if (!AliasPattern.IsMatch(trimmedAlias))
            throw new ArgumentException("远端主机别名无效：" + alias, nameof(alias));
        string normalized = NormalizePosix(remotePath);
        if (!normalized.StartsWith('/'))
            throw new ArgumentException("远端仓库路径必须是绝对路径：" + remotePath, nameof(remotePath));
        return Prefix + trimmedAlias + normalized;
    }

    public static string Normalize(string path)
    {
        if (TryParse(path, out string alias, out string remotePath))
            return Format(alias, remotePath);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static string NormalizePosix(string path)
    {
        string value = path.Trim().Replace('\\', '/');
        while (value.Contains("//", StringComparison.Ordinal))
            value = value.Replace("//", "/", StringComparison.Ordinal);
        if (value.Length > 1) value = value.TrimEnd('/');
        return value;
    }

    public static string Combine(string root, string relativePath)
    {
        if (TryParse(root, out string alias, out string remotePath))
        {
            string rel = relativePath.Replace('\\', '/').TrimStart('/');
            return Format(alias, remotePath == "/" ? "/" + rel : remotePath + "/" + rel);
        }
        return Path.Combine(root, relativePath);
    }

    public static string FileName(string path)
    {
        if (TryParse(path, out _, out string remotePath))
        {
            int index = remotePath.LastIndexOf('/');
            string name = index >= 0 ? remotePath[(index + 1)..] : remotePath;
            return name.Length > 0 ? name : remotePath;
        }
        string trimmed = path.TrimEnd('\\', '/');
        return Path.GetFileName(trimmed) is { Length: > 0 } localName ? localName : path;
    }

    public static string DisplayLocation(string path, Func<string, string?>? labelResolver = null)
    {
        if (!TryParse(path, out string alias, out string remotePath)) return path;
        string label = labelResolver?.Invoke(alias) is { Length: > 0 } resolved ? resolved : alias;
        return $"{label} · {remotePath}";
    }
}
