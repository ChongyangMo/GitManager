using GitManager.Services;
using GitManager.Services.Repositories;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    // 说明页直接读取本地文件，不等待 Git 状态、提交文件列表和远程版本关系。
    private async Task LoadWorkspacePreviewAsync(string root, long generation)
    {
        try
        {
            var readme = await Task.Run(() => ReadmeReader.ReadAsync(root, _lifetimeCancellation.Token));
            if (_disposed || generation != _workspaceGeneration || Repository?.Path != root || !_workspaceLoading) return;
            Readme = readme;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { if (!_disposed) AppLog.Write("预读仓库说明失败", ex); }
    }
}
