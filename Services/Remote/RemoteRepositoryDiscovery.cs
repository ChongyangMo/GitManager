namespace GitManager.Services.Remote;

public sealed record RemoteDiscoveryResult(IReadOnlyList<string> Repositories, IReadOnlyList<DiscoveredRemoteHost> AnnouncedHosts);

public sealed class RemoteRepositoryDiscovery(GitRepositoryService git)
{
    public async Task<RemoteDiscoveryResult> DiscoverAsync(RemoteHostsOptions options, IEnumerable<string> ignored, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || git.Runner.Remote is null) return new([], []);
        var excluded = ignored.Select(RemoteRepositoryPath.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var announced = await RemoteEndpointDiscovery.ProbeAsync(options, forceRefresh: true, cancellationToken).ConfigureAwait(false);
        var announcedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var host in announced)
        {
            announcedAliases.Add(host.Alias);
            var configured = options.Find(host.Alias);
            if (configured is { Enabled: false }) continue;
            foreach (string remoteDir in host.Repositories)
            {
                string path = RemoteRepositoryPath.Format(host.Alias, remoteDir);
                if (!excluded.Contains(path)) found.Add(path);
            }
            AppLog.Write($"局域网已发现远端主机 {host.Label}（{host.EndpointAddress}），识别 {host.Repositories.Count} 个仓库");
        }

        // 未响应 UDP 发现服务的已配置主机，回退到直接通过 SSH 扫描远端目录。
        var fs = new RemoteFileSystem(git.Runner.Remote);
        foreach (var host in options.Hosts.Where(item => item.Enabled && !string.IsNullOrWhiteSpace(item.Alias) && !announcedAliases.Contains(item.Alias)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var scanned = await fs.DiscoverRepositoriesViaSshAsync(host, cancellationToken).ConfigureAwait(false);
                foreach (string path in scanned)
                {
                    if (!excluded.Contains(path)) found.Add(path);
                }
                AppLog.Write($"通过 SSH 扫描远端主机 {host.EffectiveLabel} 完成，识别 {scanned.Count} 个仓库");
            }
            catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or TimeoutException)
            {
                AppLog.Write($"跳过当前不可达的远端主机 {host.EffectiveLabel}：{AppLog.Clean(ex.Message)}");
            }
        }

        return new(found.Order(StringComparer.OrdinalIgnoreCase).ToList(), announced);
    }
}
