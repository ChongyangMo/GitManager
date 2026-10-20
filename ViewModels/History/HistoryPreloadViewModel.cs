using GitManager.Models;
using GitManager.Services;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private async Task WarmNearbyHistoryAsync(string root, CommitInfo[] history, CommitInfo selected, HistoryFile[] files, string? selectedPath)
    {
        long generation = ++_historyWarmupGeneration;
        bool Current() => !_disposed && generation == _historyWarmupGeneration && Repository?.Path == root;
        try
        {
            int index = Array.FindIndex(history, item => item.Hash == selected.Hash);
            foreach (int nearby in new[] { index + 1, index - 1, index + 2, index - 2 })
            {
                if (!Current()) return;
                if (nearby < 0 || nearby >= history.Length) continue;
                var commit = history[nearby];
                var changed = await Git.ReadHistoryFilesAsync(root, commit);
                if (!Current()) return;
                if (changed.FirstOrDefault() is { } file) await Git.ReadHistoryPreviewAsync(root, commit, file);
            }
            foreach (var file in files.Where(item => item.Path != selectedPath).Take(2))
            {
                if (!Current()) return;
                await Git.ReadHistoryPreviewAsync(root, selected, file);
            }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { AppLog.Write("预读历史版本失败", ex); }
    }
}
