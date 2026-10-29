using System.Collections.ObjectModel;
using System.Windows;
using GitManager.Models;
using GitManager.Services;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public SettingsStore Settings { get; }
    public GitRepositoryService Git { get; }
    public ObservableCollection<RepositoryBookmark> Repositories { get; } = [];
    public ObservableCollection<ChangedFile> Changes { get; } = [];
    public ObservableCollection<CommitInfo> History { get; } = [];
    public ObservableCollection<HistoryFile> HistoryFiles { get; } = [];
    public ObservableCollection<string> Branches { get; } = [];
    private bool _busy, _hasHead;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private string _status = "添加本地仓库开始使用", _branch = "", _remote = "", _userName = "", _userEmail = "", _commitMessage = "";
    private RepositoryBookmark? _repository;
    private string? _snapshotRoot;
    private ChangedFile? _selectedChange;
    private CommitInfo? _selectedCommit;
    private HistoryFile? _selectedHistoryFile;
    private FileComparison? _comparison;
    private List<DiffRow> _diffRows = [];
    // 工作区标签页顺序，与 MainWindow.xaml 中 TabItem 的排列一致。
    public const int ReadmeTab = 0, ChangesTab = 1, HistoryTab = 2, RemoteTab = 3, VersionTab = 4;
    private int _activeTab;
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); Notify(nameof(IsIdle)); Notify(nameof(CanOperate)); Notify(nameof(CanWriteRepository)); UpdateBusyFeedback(); } }
    public bool IsIdle => !IsBusy;
    public bool HasRepository => Repository is not null;
    public bool CanOperate => HasRepository && IsIdle && !_workspaceLoading;
    public string Root => Repository?.Path ?? throw new InvalidOperationException("请先选择仓库。");
    public RepositoryBookmark? Repository { get => _repository; private set { if (Set(ref _repository, value)) SetSummaryCost(null); Notify(nameof(HasRepository)); Notify(nameof(CanOperate)); NotifyAutoUpload(); NotifyAutoUploadActivity(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Branch { get => _branch; set => Set(ref _branch, value); }
    public string Remote { get => _remote; private set { if (Set(ref _remote, value)) { Notify(nameof(RemoteAddresses)); Notify(nameof(HasRemoteAddresses)); } } }
    public string UserName { get => _userName; private set => Set(ref _userName, value); }
    public string UserEmail { get => _userEmail; private set => Set(ref _userEmail, value); }
    public string CommitMessage { get => _commitMessage; set { if (Set(ref _commitMessage, value)) _commitMessageRevision++; } }
    public ChangedFile? SelectedChange { get => _selectedChange; set { Set(ref _selectedChange, value); Notify(nameof(CanResolveSelected)); } }
    public CommitInfo? SelectedCommit { get => _selectedCommit; set => Set(ref _selectedCommit, value); }
    public HistoryFile? SelectedHistoryFile { get => _selectedHistoryFile; set => Set(ref _selectedHistoryFile, value); }
    public int ActiveTab { get => _activeTab; set => Set(ref _activeTab, value); }
    public string ChangesTitle => $"改动 {Changes.Count}";
    public string HistoryTitle => $"本地 {History.Count}";
    public List<DiffRow> DiffRows { get => _diffRows; private set => Set(ref _diffRows, value); }
    public FileComparison? Comparison
    {
        get => _comparison;
        private set { if (Set(ref _comparison, value)) DiffRows = value is null ? [] : DiffBuilder.Build(value); }
    }

    public MainViewModel(SettingsStore settings)
    {
        Settings = settings;
        Git = new(new GitRunner(() => Settings.Value.GitExecutablePath, _lifetimeCancellation.Token), Settings.Value.GitHub, () => Settings.Value.ServiceCloud);
        Repositories.CollectionChanged += RepositoryBookmarksChanged;
        foreach (string path in settings.Value.Repositories.Distinct(StringComparer.OrdinalIgnoreCase)) Repositories.Add(new(path));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelCommitSummary();
        InvalidateHistoryNavigation();
        _workspaceGeneration++;
        _backgroundFetchCancellation?.Cancel();
        _busyFeedbackTimer?.Stop();
        _repositoryPushTimer?.Stop();
        _autoUploadTimer?.Stop();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    public async Task ExecuteAsync(string message, Func<Task> action)
    {
        if (IsBusy) return;
        if (IsRepositoryAutoUploading)
        {
            Status = "当前仓库正在自动上传，请稍后再执行写操作";
            return;
        }
        _repositoryOpenGeneration++;
        InvalidateHistoryNavigation();
        IsBusy = true;
        Status = message;
        try
        {
            await PauseWorkspaceRefreshAsync();
            await StopBackgroundFetchAsync();
            await action();
            if (Status == message) Status = "就绪";
        }
        catch (Exception ex)
        {
            Status = AppLog.Clean(ex.Message);
            AppLog.Write(message + "失败", ex);
            MessageBox.Show(Status, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            CacheCurrentWorkspace();
            IsBusy = false;
            if (_refreshAfterOperation)
            {
                _refreshAfterOperation = false;
                StartWorkspaceRefresh(fetchAfter: false);
            }
            if (_fetchAfterOperation)
            {
                _fetchAfterOperation = false;
                StartBackgroundFetch();
            }
        }
    }

    // 只为当前仓库读取对应服务的提交身份。
    private async Task EnsureCommitEmailAsync()
    {
        if (Repository is null) return;
        string root = Root;
        try
        {
            var identity = await Git.ResolveCommitIdentityAsync(root, token: _lifetimeCancellation.Token);
            if (!_disposed && IsCurrentRepository(root)) { UserName = identity.Name; UserEmail = identity.Email; }
            Settings.Save();
        }
        catch (Exception ex) { if (!_disposed) AppLog.Write("读取仓库提交身份失败", ex); }
    }

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        T[] items = source.ToArray();
        for (int i = 0; i < items.Length; i++)
        {
            if (i < destination.Count && EqualityComparer<T>.Default.Equals(destination[i], items[i])) continue;
            int existingIndex = destination.IndexOf(items[i]);
            if (existingIndex >= 0) destination.Move(existingIndex, i);
            else destination.Insert(i, items[i]);
        }
        while (destination.Count > items.Length) destination.RemoveAt(destination.Count - 1);
    }
}
