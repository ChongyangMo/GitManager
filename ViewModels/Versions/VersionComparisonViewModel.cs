using System.Collections.ObjectModel;
using GitManager.Models;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private VersionChoice? _comparisonBefore, _comparisonAfter;
    private HistoryFile? _selectedVersionFile;
    private bool _followHeads = true;
    public ObservableCollection<VersionChoice> VersionChoices { get; } = [];
    public ObservableCollection<HistoryFile> VersionFiles { get; } = [];
    public VersionChoice? ComparisonBefore { get => _comparisonBefore; set => Set(ref _comparisonBefore, value); }
    public VersionChoice? ComparisonAfter { get => _comparisonAfter; set => Set(ref _comparisonAfter, value); }
    public HistoryFile? SelectedVersionFile { get => _selectedVersionFile; set => Set(ref _selectedVersionFile, value); }
    public string VersionComparisonState => ComparisonBefore is null || ComparisonAfter is null ? "选择两个版本" : VersionFiles.Count == 0 ? "版本内容一致" : $"{VersionFiles.Count} 个文件存在差异";

    private void UpdateVersionChoices(bool sameRepository)
    {
        string localSource = "本地 " + _versions?.LocalBranch;
        string remoteSource = RemoteSource + " " + SelectedRemoteBranch?.Display;
        var left = ComparisonBefore; var right = ComparisonAfter;
        Replace(VersionChoices, History.Select(commit => new VersionChoice(localSource, commit)).Concat(RemoteHistory.Select(commit => new VersionChoice(remoteSource, commit))));
        if (!sameRepository) _followHeads = true;
        ComparisonBefore = !_followHeads && sameRepository ? VersionChoices.FirstOrDefault(item => item == left) : null;
        ComparisonAfter = !_followHeads && sameRepository ? VersionChoices.FirstOrDefault(item => item == right) : null;
        ComparisonBefore ??= VersionChoices.FirstOrDefault(item => item.Source == remoteSource) ?? VersionChoices.Where(item => item.Source == localSource).Skip(1).FirstOrDefault() ?? VersionChoices.FirstOrDefault();
        ComparisonAfter ??= VersionChoices.FirstOrDefault(item => item.Source == localSource);
    }

    private async Task LoadVersionFilesCoreAsync()
    {
        string? path = SelectedVersionFile?.Path;
        Replace(VersionFiles, ComparisonBefore is null || ComparisonAfter is null ? [] : await Git.ReadVersionDifferencesAsync(Root, ComparisonBefore.Commit.Hash, ComparisonAfter.Commit.Hash));
        SelectedVersionFile = VersionFiles.FirstOrDefault(item => item.Path == path) ?? VersionFiles.FirstOrDefault();
        Notify(nameof(VersionComparisonState));
    }

    private async Task<FileComparison?> ReadVersionComparisonCoreAsync()
    {
        if (ComparisonBefore is null || ComparisonAfter is null) return null;
        if (SelectedVersionFile is null) return new("", "", "", ComparisonBefore.Label, ComparisonAfter.Label, VersionFiles.Count == 0 ? "两个版本内容一致。" : "选择文件查看差异。");
        return await Git.CompareVersionsAsync(Root, ComparisonBefore, ComparisonAfter, SelectedVersionFile);
    }

    public Task CompareSelectedVersionsAsync() => ExecuteAsync("正在对比版本", async () =>
    {
        _followHeads = false;
        await LoadVersionFilesCoreAsync();
        await LoadComparisonCoreAsync();
    });

    public Task CompareHeadsAsync(bool selectedRemoteCommit = false) => ExecuteAsync("正在对比本地与远程版本", async () =>
    {
        var remote = selectedRemoteCommit ? SelectedRemoteCommit : _versions?.RemoteHead;
        if (remote is null || _versions?.LocalHead is null) throw new InvalidOperationException("需要先获取远程版本，并确保本地已有提交。");
        ComparisonBefore = VersionChoices.First(item => item.Commit.Hash == remote.Hash && item.Source.StartsWith(RemoteSource + " ", StringComparison.Ordinal));
        ComparisonAfter = VersionChoices.First(item => item.Commit.Hash == _versions.LocalHead.Hash && item.Source.StartsWith("本地 ", StringComparison.Ordinal));
        _followHeads = !selectedRemoteCommit;
        ActiveTab = 3;
        await LoadVersionFilesCoreAsync();
        await LoadComparisonCoreAsync();
    });
}
