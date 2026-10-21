using GitManager.Models;
using GitManager.Services.Repositories;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private async Task LoadComparisonCoreAsync()
    {
        Comparison = ActiveTab switch
        {
            ChangesTab => SelectedChange is null ? null : await Git.CompareWorkingAsync(Root, SelectedChange, _hasHead),
            HistoryTab => SelectedCommit is null || SelectedHistoryFile is null ? null : await Git.CompareHistoryAsync(Root, SelectedCommit, SelectedHistoryFile),
            RemoteTab => SelectedRemoteCommit is null || SelectedRemoteFile is null ? null : await Git.CompareHistoryAsync(Root, SelectedRemoteCommit, SelectedRemoteFile),
            ReadmeTab => null,
            _ => await ReadVersionComparisonCoreAsync()
        };
    }

    public Task ShowChangeAsync() => !HasRepository ? Task.CompletedTask : ActiveTab is HistoryTab or RemoteTab
        ? NavigateHistoryAsync(ActiveTab == RemoteTab, loadFiles: false) : ExecuteAsync("正在读取文件差异", LoadComparisonCoreAsync);
    public Task ShowActiveTabAsync() => !HasRepository ? Task.CompletedTask : ActiveTab is HistoryTab or RemoteTab
        ? NavigateHistoryAsync(ActiveTab == RemoteTab, loadFiles: true) : ExecuteAsync("正在读取版本", async () =>
    {
        if (ActiveTab == RemoteTab) await LoadRemoteFilesCoreAsync();
        if (ActiveTab == VersionTab) await LoadVersionFilesCoreAsync();
        if (ActiveTab == ReadmeTab) Readme = await ReadmeReader.ReadAsync(Root);
        await LoadComparisonCoreAsync();
    });
    public Task ShowCommitAsync() => NavigateHistoryAsync(remote: false, loadFiles: true);

    public Task RestoreAsync(bool fileOnly) => ExecuteAsync("正在恢复代码", async () =>
    {
        if (SelectedCommit is null) throw new InvalidOperationException("请先选择历史版本。");
        if (fileOnly && SelectedHistoryFile is null) throw new InvalidOperationException("请先选择文件。");
        string? checkpoint = await Git.RestoreVersionAsync(Root, SelectedCommit.Hash, fileOnly ? SelectedHistoryFile!.Path : null);
        ActiveTab = ChangesTab;
        await RefreshCoreAsync();
        Status = checkpoint is null ? "已恢复，检查改动后可提交为新版本" : "已恢复，原修改已保存在“恢复点”";
    });

    public Task ApplyRecoveryPointAsync(RecoveryPoint point) => ExecuteAsync("正在取回恢复点", async () =>
    {
        try { await Git.ApplyRecoveryPointAsync(Root, point.Hash); }
        finally { await RefreshCoreAsync(); }
        Status = "已取回修改，恢复点记录继续保留";
    });
}
