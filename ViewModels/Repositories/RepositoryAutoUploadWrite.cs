using System.IO;
using GitManager.Models;
using GitManager.Services;
using GitManager.Services.CommitMessages;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private string? _autoUploadWriteRoot;
    public bool IsRepositoryAutoUploading => _autoUploadWriteRoot is not null && IsCurrentRepository(_autoUploadWriteRoot);
    public bool CanWriteRepository => CanOperate && !IsRepositoryAutoUploading;

    private void NotifyAutoUploadActivity()
    {
        Notify(nameof(IsRepositoryAutoUploading));
        Notify(nameof(CanWriteRepository));
        Notify(nameof(CanResolveSelected));
        NotifyBusyFeedback();
    }

    private async Task WriteAutoUploadAsync(string root, IReadOnlyList<ChangedFile> files, GeneratedCommitMessage? summary, bool hasRemote, CancellationToken token)
    {
        // 只保护正在上传的仓库，不占用前台导航和其他仓库的操作。
        _autoUploadWriteRoot = root;
        NotifyAutoUploadActivity();
        try
        {
            if (IsCurrentRepository(root))
            {
                Status = "正在自动上传";
                await StopBackgroundFetchAsync();
            }
            string? result = await Task.Run(async () =>
            {
                if (summary is not null)
                {
                    var latest = await Git.ReadChangesAsync(root, token);
                    if (!latest.Select(file => file.Path).ToHashSet(StringComparer.Ordinal).SetEquals(files.Select(file => file.Path))) return null;
                    await Git.CommitAsync(root, files, summary.Message);
                    AppLog.Write($"自动上传已通过{summary.Source}总结并提交 {files.Count} 个文件：{root}");
                }
                string message = summary is null ? "已推送" : $"已提交 {files.Count} 个文件";
                if (hasRemote)
                {
                    var outcome = await Git.SynchronizeVersionsAsync(root, null, "sync");
                    message = summary is null ? outcome.Message : message + "，" + outcome.Message;
                }
                else message += "，未设置远程，仅保存到本地";
                return message;
            }, token);
            if (result is null) return;
            _autoUploadRetryAt.Remove(root);
            SetAutoUploadState(root, $"{DateTime.Now:MM-dd HH:mm} {result}");
            if (!_disposed && IsCurrentRepository(root) && !IsBusy)
            {
                if (summary is not null) SetSummaryCost(summary.Cost);
                Status = $"{Path.GetFileName(root.TrimEnd('\\', '/'))} 自动上传：{result}";
            }
        }
        finally
        {
            _autoUploadWriteRoot = null;
            NotifyAutoUploadActivity();
            if (!_disposed)
            {
                // 丢弃提交前开始的读取；完成时重新判断当前仓库，避免切换后串页或覆盖草稿。
                _workspaceReads.Remove(root);
                if (IsCurrentRepository(root))
                {
                    if (IsBusy) _refreshAfterOperation = true;
                    else StartWorkspaceRefresh(fetchAfter: false);
                }
                _ = RefreshRepositoryPushIndicatorsAsync();
            }
        }
    }
}
