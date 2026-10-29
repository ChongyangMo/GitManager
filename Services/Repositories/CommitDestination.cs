namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    private sealed record CommitDestination(bool HasRemote, bool GitHub, bool ServiceCloud);

    private async Task<CommitDestination> ReadCommitDestinationAsync(string root, string? requested = null, CancellationToken token = default)
    {
        string[] remotes = (await Runner.RunAsync(root, ["remote"], cancellationToken: token)).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (remotes.Length == 0) return new(false, false, false);
        string branch = (await Runner.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], true, token)).Output.Trim();
        string upstream = branch.Length > 0 ? await ReadConfigAsync(root, "branch." + branch + ".remote") : "";
        string remote = requested is not null && remotes.Contains(requested) ? requested
            : remotes.Contains(upstream) ? upstream : remotes.Contains("origin") ? "origin" : remotes[0];
        string[] urls = (await Runner.RunAsync(root, ["remote", "get-url", "--push", "--all", remote], cancellationToken: token)).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        static bool GitHub(string url) => Repositories.RepositoryRemoteGroup.Host(url) is { } host
            && (host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || host.Equals("ssh.github.com", StringComparison.OrdinalIgnoreCase));
        var cloud = CloudOptions();
        bool Cloud(string url) => Repositories.RepositoryRemoteGroup.Host(url) is { } host
            && (host.Equals(Repositories.RepositoryRemoteGroup.Host(cloud.BaseUrl), StringComparison.OrdinalIgnoreCase)
                || host.Equals(Repositories.RepositoryRemoteGroup.Host(cloud.GitBaseUrl), StringComparison.OrdinalIgnoreCase));
        return new(true, urls.Length > 0 && urls.All(GitHub), urls.Any(Cloud) && urls.Where(url => !GitHub(url)).All(Cloud));
    }
}
