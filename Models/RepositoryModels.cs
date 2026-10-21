using GitManager.ViewModels;

namespace GitManager.Models;

public sealed class RepositoryBookmark(string path) : ObservableObject
{
    private string? _displayName;
    private bool _hasNameConflict;
    private string _conflictHint = "";
    private int? _pendingPushCount;
    public int? PendingPushCount => _pendingPushCount;
    public bool HasPendingPush => _pendingPushCount is > 0;
    public string PendingPushHint => HasPendingPush ? $"有 {_pendingPushCount} 个本地提交待推送" : "";
    public void UpdatePendingPushCount(int? count)
    {
        if (!Set(ref _pendingPushCount, count, nameof(PendingPushCount))) return;
        Notify(nameof(HasPendingPush));
        Notify(nameof(PendingPushHint));
    }
    private string _path = Services.Repositories.RepositoryPaths.Actual(path);
    public string Path => _path;
    public bool RefreshPath()
    {
        string actual = Services.Repositories.RepositoryPaths.Actual(Path);
        if (!Set(ref _path, actual, nameof(Path))) return false;
        Notify(nameof(Name));
        return true;
    }
    public string Name => _displayName ?? (System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Path);
    public bool HasNameConflict { get => _hasNameConflict; private set => Set(ref _hasNameConflict, value); }
    public string ConflictHint { get => _conflictHint; private set => Set(ref _conflictHint, value); }
    public void UpdateNameConflict(IReadOnlyList<RepositoryBookmark> copies)
    {
        HasNameConflict = copies.Count > 1;
        ConflictHint = HasNameConflict ? "同名仓库，点击比较\n" + string.Join("\n", copies.Select(item => item.Path)) : Path;
    }
    public void UpdateDisplayName(string? name)
    {
        if (Set(ref _displayName, string.IsNullOrWhiteSpace(name) ? null : name)) Notify(nameof(Name));
    }
    public string RemoteGroup { get => _remoteGroup; private set => Set(ref _remoteGroup, value); }
    public void UpdateRemoteGroup(string group) => RemoteGroup = group;
    private string _remoteGroup = "";
}

public sealed class ChangedFile(string path, string? originalPath, char indexState, char worktreeState) : ObservableObject
{
    private bool _selected;
    public string Path { get; } = path;
    public string? OriginalPath { get; } = originalPath;
    public char IndexState { get; } = indexState;
    public char WorktreeState { get; } = worktreeState;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public bool IsConflict => IndexState == 'U' || WorktreeState == 'U' || (IndexState, WorktreeState) is ('A', 'A') or ('D', 'D');
    public string State => IsConflict ? "冲突" : IndexState == '?' ? "新增" : IndexState == 'R' || WorktreeState == 'R' ? "重命名" : IndexState == 'D' || WorktreeState == 'D' ? "删除" : IndexState == 'A' ? "新增" : "修改";
    public IEnumerable<string> Paths => OriginalPath is null ? [Path] : [OriginalPath, Path];
}

public sealed record CommitInfo(string Hash, string ShortHash, string Author, string Date, string Subject, string Parents, string Message = "")
{
    public string FullMessage => string.IsNullOrEmpty(Message) ? Subject : Message.TrimEnd('\r', '\n');
    public string DisplayDate => DateTimeOffset.TryParse(Date, out var time) ? time.ToLocalTime().ToString("MM-dd HH:mm") : Date;
    public string FirstParent => Parents.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
}

public sealed record HistoryFile(string Path, string State);
public sealed record RecoveryPoint(string Hash, string Reference, string Message);
public sealed record GitResult(int ExitCode, string Output, string Error);
public sealed record RepositorySnapshot(string Branch, List<string> Branches, List<ChangedFile> Changes, List<CommitInfo> History, string Remote, string UserName, string UserEmail, bool HasHead);
public sealed record FileComparison(string Before, string After, string Patch, string BeforeLabel, string AfterLabel, string? Notice = null);
public sealed record HistoryPreview(FileComparison Comparison, List<DiffRow> Rows);
