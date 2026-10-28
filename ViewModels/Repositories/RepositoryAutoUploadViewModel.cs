using System.IO;
using System.Windows.Threading;
using GitManager.Services;
using GitManager.Services.CommitMessages;
using GitManager.Services.Repositories;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private DispatcherTimer? _autoUploadTimer;
    private Task? _autoUploadScan;
    private readonly Dictionary<string, string> _autoUploadStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _autoUploadRetryAt = new(StringComparer.OrdinalIgnoreCase);

    private AutoUploadOptions AutoUpload => Settings.Value.AutoUpload;
    public string AutoUploadDescription => $"改动停止 {RepositoryAutoUpload.DescribeQuiet(AutoUpload.QuietSeconds)}后自动提交并推送到远程";
    public string AutoUploadStatus => HasRepository && IsAutoUploadEnabled ? _autoUploadStates.GetValueOrDefault(Root, "") : "";
    public bool IsAutoUploadEnabled
    {
        get => HasRepository && !AutoUpload.DisabledRepositories.Contains(Root, StringComparer.OrdinalIgnoreCase);
        set
        {
            if (!HasRepository || value == IsAutoUploadEnabled) return;
            string root = Root;
            if (value) AutoUpload.DisabledRepositories.RemoveAll(path => string.Equals(path, root, StringComparison.OrdinalIgnoreCase));
            else
            {
                AutoUpload.DisabledRepositories.Add(root);
                AutoUpload.Repositories.RemoveAll(path => string.Equals(path, root, StringComparison.OrdinalIgnoreCase));
            }
            _autoUploadStates.Remove(root);
            _autoUploadRetryAt.Remove(root);
            Settings.Save();
            AppLog.Write((value ? "已开启自动上传：" : "已关闭自动上传：") + root);
            NotifyAutoUpload();
            if (value) _ = RunAutoUploadAsync();
        }
    }

    private void NotifyAutoUpload()
    {
        Notify(nameof(IsAutoUploadEnabled));
        Notify(nameof(AutoUploadStatus));
    }

    private void StartAutoUploadMonitoring()
    {
        if (_disposed || _autoUploadTimer is not null) return;
        _autoUploadTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(Math.Max(5, AutoUpload.CheckIntervalSeconds)) };
        _autoUploadTimer.Tick += (_, _) => _ = RunAutoUploadAsync();
        _autoUploadTimer.Start();
        _ = RunAutoUploadAsync();
    }

    public Task RunAutoUploadAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_autoUploadScan is { IsCompleted: false }) return _autoUploadScan;
        return _autoUploadScan = ScanAutoUploadAsync();
    }

    private async Task ScanAutoUploadAsync()
    {
        var roots = Settings.Value.Repositories.Concat(AutoUpload.Repositories)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(root => !AutoUpload.DisabledRepositories.Contains(root, StringComparer.OrdinalIgnoreCase)
                && !Settings.Value.IgnoredRepositories.Contains(root, StringComparer.OrdinalIgnoreCase)).ToArray();
        foreach (string root in roots)
        {
            if (_disposed) return;
            if (!Directory.Exists(root) || _autoUploadRetryAt.TryGetValue(root, out var retry) && DateTime.UtcNow < retry) continue;
            try { await UploadRepositoryAsync(root, _lifetimeCancellation.Token); }
            catch (OperationCanceledException) when (_disposed) { return; }
            catch (Exception ex)
            {
                if (_disposed) return;
                _autoUploadRetryAt[root] = DateTime.UtcNow.AddMinutes(Math.Max(1, AutoUpload.RetryMinutes));
                string message = AppLog.Clean(ex.Message);
                SetAutoUploadState(root, $"{DateTime.Now:MM-dd HH:mm} 未完成：{message}");
                if (IsCurrentRepository(root) && !IsBusy) Status = "自动上传未完成：" + message;
                AppLog.Write("自动上传失败：" + root, ex);
            }
        }
    }

    private bool IsCurrentRepository(string root) => string.Equals(Repository?.Path, root, StringComparison.OrdinalIgnoreCase);

    private async Task UploadRepositoryAsync(string root, CancellationToken token)
    {
        // 当前仓库正在执行操作、生成说明或处理合并时，留到下一轮。
        if (IsBusy || IsCurrentRepository(root) && (IsGeneratingCommitMessage || IsMerging)) return;
        var quiet = TimeSpan.FromSeconds(Math.Max(0, AutoUpload.QuietSeconds));
        var files = await Task.Run(() => RepositoryAutoUpload.ReadSettledChangesAsync(Git, root, quiet, token), token);
        if (files is null) return;
        bool hasRemote = await RepositoryAutoUpload.HasRemoteAsync(Git, root, token);
        GeneratedCommitMessage? summary = null;
        if (files.Count > 0)
        {
            await Git.EnsureNoOperationAsync(root);
            summary = await Task.Run(() => new CommitMessageGenerator(Git, Settings).GenerateAsync(root, files, token), token);
        }
        else
        {
            // 没有新改动时，只补推之前未推送成功的提交；远程分支尚未获取时也尝试推送一次。
            if (!hasRemote) return;
            int? pending = await RepositoryPushStatus.ReadAsync(Git, root);
            if (pending == 0) return;
        }
        if (_disposed || IsBusy || AutoUpload.DisabledRepositories.Contains(root, StringComparer.OrdinalIgnoreCase)
            || Settings.Value.IgnoredRepositories.Contains(root, StringComparer.OrdinalIgnoreCase)
            || IsCurrentRepository(root) && IsGeneratingCommitMessage) return;

        await WriteAutoUploadAsync(root, files, summary, hasRemote, token);
    }

    private void SetAutoUploadState(string root, string text)
    {
        _autoUploadStates[root] = text;
        if (IsCurrentRepository(root)) Notify(nameof(AutoUploadStatus));
    }
}
