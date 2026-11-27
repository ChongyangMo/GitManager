using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Remote;
using GitManager.Services.Repositories;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;
    private Task? _repositoryDiscoveryTask;
    private bool _discoveringRepositories;
    public bool IsDiscoveringRepositories => _discoveringRepositories;

    private async Task DiscoverRepositoriesCoreAsync()
    {
        if (!Settings.Value.RepositoryDiscovery.Enabled) return;
        _lastDiscovery = DateTimeOffset.UtcNow;
        var discovery = new RepositoryDiscovery(Git);
        var roots = RepositoryDiscovery.GetRoots(Settings.Value.RepositoryDiscovery, Repositories.Select(item => item.Path));
        var localTask = discovery.DiscoverAsync(roots, Settings.Value.RepositoryDiscovery, Settings.Value.IgnoredRepositories, _lifetimeCancellation.Token);
        var remoteOptions = Settings.Value.RemoteHosts ?? new RemoteHostsOptions();
        var remoteTask = remoteOptions.Enabled
            ? new RemoteRepositoryDiscovery(Git).DiscoverAsync(remoteOptions, Settings.Value.IgnoredRepositories, _lifetimeCancellation.Token)
            : Task.FromResult(new RemoteDiscoveryResult([], []));
        await Task.WhenAll(localTask, remoteTask);
        var result = await localTask;
        var remoteResult = await remoteTask;
        // 扫描不占用前台操作锁；仅在提交列表变更时避开正在进行的写操作。
        while (IsBusy && !_disposed) await Task.Delay(50, _lifetimeCancellation.Token);
        if (_disposed) return;

        bool hostsChanged = false;
        foreach (var announced in remoteResult.AnnouncedHosts)
        {
            if (remoteOptions.Find(announced.Alias) is null)
            {
                remoteOptions.Hosts.Add(new RemoteHostProfile
                {
                    Alias = announced.Alias,
                    Label = announced.Label,
                    SshHost = announced.Alias,
                    User = announced.User,
                    Port = announced.SshPort,
                    GitPath = announced.GitPath,
                    FallbackAddresses = string.IsNullOrWhiteSpace(announced.Hostname) ? [announced.EndpointAddress] : [announced.EndpointAddress, announced.Hostname]
                });
                hostsChanged = true;
            }
        }
        foreach (var existing in Repositories.Where(item => item.IsRemote)) ApplyRemoteHostLabel(existing);

        var known = Repositories.Select(item => RepositoryDiscovery.Normalize(item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ignored = Settings.Value.IgnoredRepositories.Select(RepositoryDiscovery.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] additions = result.Repositories.Concat(remoteResult.Repositories)
            .Where(path => !ignored.Contains(RepositoryDiscovery.Normalize(path)) && known.Add(RepositoryDiscovery.Normalize(path)))
            .ToArray();
        if (additions.Length == 0)
        {
            if (hostsChanged) Settings.Save();
            return;
        }
        List<string> previous = Settings.Value.Repositories;
        Settings.Value.Repositories = Repositories.Select(item => item.Path).Concat(additions).ToList();
        try { Settings.Save(); }
        catch { Settings.Value.Repositories = previous; throw; }
        foreach (string path in additions)
        {
            var bookmark = new RepositoryBookmark(path);
            ApplyRemoteHostLabel(bookmark);
            Repositories.Add(bookmark);
        }
        int remoteCount = additions.Count(RemoteRepositoryPath.IsRemote);
        int localCount = additions.Length - remoteCount;
        AppLog.Write(remoteCount > 0
            ? $"已自动加入 {localCount} 个本地仓库、{remoteCount} 个远端仓库"
            : $"已自动加入 {localCount} 个本地仓库");
    }

    public Task DiscoverRepositoriesAsync(bool showStatus = true)
    {
        if (_disposed) return Task.CompletedTask;
        if (_repositoryDiscoveryTask is { IsCompleted: false }) return _repositoryDiscoveryTask;
        return _repositoryDiscoveryTask = DiscoverInBackgroundAsync(showStatus);
    }

    private async Task DiscoverInBackgroundAsync(bool showStatus)
    {
        _discoveringRepositories = true;
        Notify(nameof(IsDiscoveringRepositories));
        const string message = "正在识别本地仓库";
        if (showStatus && !IsBusy) Status = message;
        try
        {
            await DiscoverRepositoriesCoreAsync();
            if (_disposed) return;
            await RefreshRepositoryNamesAsync();
            if (_disposed) return;
            _ = RefreshRepositoryPushIndicatorsAsync();
            _ = PreloadRepositoriesAsync();
            if (showStatus && !IsBusy && Status == message) Status = "就绪";
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            AppLog.Write("后台识别本地仓库失败", ex);
            if (showStatus && !_disposed && !IsBusy && Status == message) Status = AppLog.Clean(ex.Message);
        }
        finally { _discoveringRepositories = false; Notify(nameof(IsDiscoveringRepositories)); }
    }

    public Task DiscoverOnActivationAsync() => DateTimeOffset.UtcNow - _lastDiscovery < TimeSpan.FromMinutes(1)
        ? Task.CompletedTask : DiscoverRepositoriesAsync();
}
