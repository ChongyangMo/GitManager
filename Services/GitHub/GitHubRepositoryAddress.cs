using System.Text.RegularExpressions;

namespace GitManager.Services.GitHub;

public sealed record GitHubRepositoryAddress(string Owner, string Name)
{
    public string FullName => Owner + "/" + Name;
    public string HttpsUrl => "https://github.com/" + FullName + ".git";
    public string ApiPath => "repos/" + Uri.EscapeDataString(Owner) + "/" + Uri.EscapeDataString(Name);

    public static GitHubRepositoryAddress? Parse(string address)
    {
        string path;
        if (address.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) path = address[15..];
        else if (Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.Scheme is "https" or "ssh" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && (uri.Scheme == "ssh" ? uri.UserInfo is "" or "git" : uri.UserInfo.Length == 0)
            && uri.IsDefaultPort) path = uri.AbsolutePath.TrimStart('/');
        else return null;
        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        string[] parts = path.Split('/');
        if (parts.Length != 2 || parts.Any(part => part is "." or ".." || !Regex.IsMatch(part, @"\A[A-Za-z0-9_.-]+\z"))) return null;
        return new(parts[0], parts[1]);
    }
}
