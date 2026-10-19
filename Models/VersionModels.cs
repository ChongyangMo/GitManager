namespace GitManager.Models;

public sealed record RemoteBranch(string RemoteName, string Name)
{
    public string Display => RemoteName + "/" + Name;
    public string FullRef => "refs/remotes/" + Display;
}

public sealed record VersionChoice(string Source, CommitInfo Commit)
{
    public string Display => $"{Source} · {Commit.ShortHash} · {Commit.Subject}";
    public string Label => Source + " " + Commit.ShortHash;
}

public sealed record VersionSnapshot(
    string LocalBranch, CommitInfo? LocalHead, string RemoteName, string RemoteUrl,
    RemoteBranch? Target, List<RemoteBranch> Branches, List<CommitInfo> RemoteHistory,
    int? Ahead, int? Behind, bool? HasCommonHistory, bool IsMerging)
{
    public CommitInfo? RemoteHead => RemoteHistory.FirstOrDefault();
    public bool HasRemote => RemoteName.Length > 0 && RemoteUrl.Length > 0;
    public bool Diverged => Ahead is > 0 && Behind is > 0;
    public string SourceLabel => Uri.TryCreate(RemoteUrl, UriKind.Absolute, out var uri) && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(RemoteUrl, @"^[^@\s]+@github\.com:", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "GitHub" : "远程";
}

public sealed record SyncOutcome(string Message, bool HasConflicts = false);
