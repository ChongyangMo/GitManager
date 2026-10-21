using GitManager.Services;
using GitManager.Services.Repositories;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    public const int ReadmeTab = 4;
    private ReadmeDocument? _readme;
    public ReadmeDocument? Readme { get => _readme; private set { if (Set(ref _readme, value)) Notify(nameof(HasReadme)); } }
    public bool HasReadme => Readme is not null;
    public string RemoteDisplay => string.IsNullOrWhiteSpace(ActiveRemoteUrl) ? "未设置" : AppLog.Clean(ActiveRemoteUrl);
}
