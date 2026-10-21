using GitManager.Services;
using GitManager.Services.GitHub;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    // 按 GitHub 仓库全名缓存可见性，同一远程的多个本地副本共用结果。
    private readonly Dictionary<string, GitHubRepositoryVisibility> _gitHubVisibility = new(StringComparer.OrdinalIgnoreCase);
    private string? _gitHubVisibilityKey;
    private string _gitHubVisibilityError = "";
    private bool _gitHubVisibilityLoading;
    private int _gitHubVisibilityGeneration;

    private GitHubRepositoryAddress? GitHubAddress => HasRepository ? GitHubRepositoryAddress.Parse(ActiveRemoteUrl) : null;
    public bool IsGitHubRemote => GitHubAddress is not null;
    public string GitHubRepositoryName => GitHubAddress?.FullName ?? "";
    public GitHubRepositoryVisibility? GitHubVisibility => _gitHubVisibilityKey is { } key && _gitHubVisibility.TryGetValue(key, out var value) ? value : null;
    public string GitHubVisibilityText => _gitHubVisibilityLoading ? "正在读取…"
        : GitHubVisibility is { } visibility ? visibility.CanChange ? visibility.Label : visibility.Label + " · 当前账号无权修改"
        : _gitHubVisibilityError.Length > 0 ? "读取失败：" + _gitHubVisibilityError : "";
    public string GitHubVisibilityAction => GitHubVisibility is { } visibility ? visibility.IsPrivate ? "改为公开" : "改为私有" : "重新读取";
    public bool CanChangeGitHubVisibility => !_gitHubVisibilityLoading && (GitHubVisibility?.CanChange ?? _gitHubVisibilityError.Length > 0);

    private void UpdateGitHubVisibility(bool reload = false)
    {
        var address = GitHubAddress;
        if (!reload && address?.FullName == _gitHubVisibilityKey) return;
        _gitHubVisibilityKey = address?.FullName;
        _gitHubVisibilityError = "";
        _gitHubVisibilityLoading = false;
        int generation = ++_gitHubVisibilityGeneration;
        if (address is not null && (reload || !_gitHubVisibility.ContainsKey(address.FullName))) _ = LoadGitHubVisibilityAsync(Root, address, generation);
        NotifyGitHubVisibility();
    }

    public void ReloadGitHubVisibility() => UpdateGitHubVisibility(reload: true);

    private async Task LoadGitHubVisibilityAsync(string root, GitHubRepositoryAddress address, int generation)
    {
        _gitHubVisibilityLoading = true;
        NotifyGitHubVisibility();
        try { _gitHubVisibility[address.FullName] = await Git.ReadGitHubVisibilityAsync(root, address, _lifetimeCancellation.Token); }
        catch (Exception ex)
        {
            if (_disposed) return;
            if (generation == _gitHubVisibilityGeneration) _gitHubVisibilityError = AppLog.Clean(ex.Message);
            AppLog.Write("读取 GitHub 可见性失败", ex);
        }
        if (generation != _gitHubVisibilityGeneration) return;
        _gitHubVisibilityLoading = false;
        NotifyGitHubVisibility();
    }

    public Task ChangeGitHubVisibilityAsync() => ExecuteAsync("正在修改 GitHub 可见性", async () =>
    {
        if (GitHubAddress is not { } address || GitHubVisibility is not { CanChange: true } current) return;
        _gitHubVisibility[address.FullName] = await Git.SetGitHubVisibilityAsync(Root, address, !current.IsPrivate, _lifetimeCancellation.Token);
        NotifyGitHubVisibility();
        Status = $"{address.FullName} 已改为{GitHubVisibility?.Label}";
    });

    private void NotifyGitHubVisibility()
    {
        foreach (string name in new[] { nameof(IsGitHubRemote), nameof(GitHubRepositoryName), nameof(GitHubVisibility), nameof(GitHubVisibilityText), nameof(GitHubVisibilityAction), nameof(CanChangeGitHubVisibility) }) Notify(name);
    }
}
