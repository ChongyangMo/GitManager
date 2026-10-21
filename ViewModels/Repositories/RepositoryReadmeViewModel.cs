using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Repositories;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private ReadmeDocument? _readme;
    public ReadmeDocument? Readme { get => _readme; private set { if (Set(ref _readme, value)) Notify(nameof(HasReadme)); } }
    public bool HasReadme => Readme is not null;
    public bool HasRemoteAddresses => RemoteAddresses.Count > 0;

    public IReadOnlyList<RemoteAddress> RemoteAddresses
    {
        get
        {
            if (_versions?.Remotes is { Count: > 0 } remotes) return remotes.Select(remote => remote with { Url = AppLog.Clean(remote.Url) }).ToList();
            return string.IsNullOrWhiteSpace(ActiveRemoteUrl) ? [] : [new(_versions?.RemoteName is { Length: > 0 } name ? name : "origin", AppLog.Clean(ActiveRemoteUrl))];
        }
    }
}
