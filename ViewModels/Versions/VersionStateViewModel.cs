using System.Collections.ObjectModel;
using GitManager.Models;
using GitManager.Services;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private VersionSnapshot? _versions;
    private RemoteBranch? _selectedRemoteBranch;
    private CommitInfo? _selectedRemoteCommit;
    private HistoryFile? _selectedRemoteFile;
    private string? _versionRoot;
    private readonly Dictionary<string, DateTimeOffset> _fetchedAt = [];
    private readonly Dictionary<string, string> _fetchErrors = [];
    public ObservableCollection<RemoteBranch> RemoteBranches { get; } = [];
    public ObservableCollection<CommitInfo> RemoteHistory { get; } = [];
    public ObservableCollection<HistoryFile> RemoteFiles { get; } = [];
    public RemoteBranch? SelectedRemoteBranch { get => _selectedRemoteBranch; set => Set(ref _selectedRemoteBranch, value); }
    public CommitInfo? SelectedRemoteCommit { get => _selectedRemoteCommit; set => Set(ref _selectedRemoteCommit, value); }
    public HistoryFile? SelectedRemoteFile { get => _selectedRemoteFile; set => Set(ref _selectedRemoteFile, value); }
    public string RemoteSource => _versions?.SourceLabel ?? "远程";
    public string RemoteTitle => $"{RemoteSource} {RemoteHistory.Count}";
    public string LocalVersionLabel => _versions?.LocalHead is { } head ? $"本地 {_versions.LocalBranch} · {head.ShortHash}" : "本地尚无提交";
    public string LocalVersionDetail => _versions?.LocalHead?.Subject ?? "";
    public string RemoteVersionHash => _versions?.RemoteHead?.ShortHash ?? (_versions?.HasRemote == true ? "未获取或分支不存在" : "未连接");
    public string RemoteVersionDetail => _versions?.RemoteHead?.Subject ?? "";
    public bool IsMerging => _versions?.IsMerging == true;
    public bool CanResolveSelected => IsMerging && SelectedChange?.IsConflict == true;
    public bool CanMergeRemote => _versions?.Diverged == true && _versions.HasCommonHistory == true && !IsMerging;
    public string CommitButtonLabel => IsMerging ? "完成合并" : "提交到本地";
    private string FetchKey => Root + "\0" + _versions?.RemoteName + "\0" + _versions?.RemoteUrl;
    public string FetchStateText => !HasRepository || _versions?.HasRemote != true ? "尚未设置远程仓库"
        : _backgroundFetchKey == FetchKey ? "正在后台获取远程版本"
        : _fetchErrors.ContainsKey(FetchKey) ? "获取失败，显示远程缓存"
        : _fetchedAt.TryGetValue(FetchKey, out var time) ? $"获取于 {time.LocalDateTime:HH:mm:ss}" : "远程缓存，尚未联网检查";
    public string FetchError => HasRepository && _fetchErrors.TryGetValue(FetchKey, out var error) ? error : "";
    public string SyncStateText => IsMerging ? $"合并中 · {Changes.Count(file => file.IsConflict)} 个冲突"
        : _versions?.HasRemote != true ? "仅本地仓库"
        : _versions.LocalHead is null ? "本地尚无提交"
        : _versions.RemoteHead is null ? "远程分支未获取或尚未创建"
        : _versions.HasCommonHistory == false ? "两端没有共同历史"
        : _versions.Ahead == 0 && _versions.Behind == 0 ? "提交已同步"
        : $"待推送 {_versions.Ahead} · 待拉取 {_versions.Behind}";

    private async Task RefreshVersionsCoreAsync(RepositorySnapshot? snapshot = null)
    {
        bool same = string.Equals(_versionRoot, Root, StringComparison.OrdinalIgnoreCase);
        var state = await Git.ReadVersionsAsync(Root, same && _versions?.LocalBranch == Branch ? SelectedRemoteBranch : null, snapshot);
        string? selectedHash = same ? SelectedRemoteCommit?.Hash : null;
        _versionRoot = Root;
        _versions = state;
        Replace(RemoteBranches, state.Branches);
        SelectedRemoteBranch = state.Target;
        Replace(RemoteHistory, state.RemoteHistory);
        SelectedRemoteCommit = RemoteHistory.FirstOrDefault(item => item.Hash == selectedHash) ?? RemoteHistory.FirstOrDefault();
        await LoadRemoteFilesCoreAsync();
        UpdateVersionChoices(same);
        if (ActiveTab == 3) await LoadVersionFilesCoreAsync();
        NotifyVersionState();
    }

    private void NotifyVersionState()
    {
        foreach (string name in new[] { nameof(RemoteSource), nameof(RemoteTitle), nameof(LocalVersionLabel), nameof(LocalVersionDetail), nameof(RemoteVersionHash), nameof(RemoteVersionDetail), nameof(IsMerging), nameof(CanResolveSelected), nameof(CanMergeRemote), nameof(CommitButtonLabel), nameof(FetchStateText), nameof(FetchError), nameof(SyncStateText) }) Notify(name);
    }

    private void ClearVersions()
    {
        _versions = null; _versionRoot = null;
        RemoteBranches.Clear(); RemoteHistory.Clear(); RemoteFiles.Clear(); VersionChoices.Clear(); VersionFiles.Clear();
        SelectedRemoteBranch = null; SelectedRemoteCommit = null; SelectedRemoteFile = null;
        ComparisonBefore = null; ComparisonAfter = null; SelectedVersionFile = null;
        NotifyVersionState(); Notify(nameof(VersionComparisonState));
    }

    private async Task FetchRemoteCoreAsync(bool tolerateFailure)
    {
        if (_versions?.HasRemote != true) return;
        string key = FetchKey;
        try
        {
            await Git.FetchVersionsAsync(Root, _versions.RemoteName);
            _fetchedAt[key] = DateTimeOffset.Now;
            _fetchErrors.Remove(key);
        }
        catch (Exception ex)
        {
            _fetchErrors[key] = AppLog.Clean(ex.Message);
            AppLog.Write("获取远程版本失败，保留缓存", ex);
            if (!tolerateFailure) throw;
        }
        finally { NotifyVersionState(); }
    }

    private async Task LoadRemoteFilesCoreAsync()
    {
        string? path = SelectedRemoteFile?.Path;
        Replace(RemoteFiles, SelectedRemoteCommit is null ? [] : await Git.ReadHistoryFilesAsync(Root, SelectedRemoteCommit));
        SelectedRemoteFile = RemoteFiles.FirstOrDefault(item => item.Path == path) ?? RemoteFiles.FirstOrDefault();
    }

    public Task SelectRemoteBranchAsync() => ExecuteAsync("正在切换远程分支", async () =>
    {
        await RefreshVersionsCoreAsync();
        await FetchRemoteCoreAsync(tolerateFailure: true);
        await RefreshVersionsCoreAsync();
        await LoadComparisonCoreAsync();
    });

    public Task ShowRemoteCommitAsync() => ExecuteAsync("正在读取远程版本", async () =>
    {
        await LoadRemoteFilesCoreAsync();
        await LoadComparisonCoreAsync();
    });
}
