using GitManager.Services;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private bool AutoUploadEnabledFor(string root) => !AutoUpload.DisabledRepositories.Contains(root, StringComparer.OrdinalIgnoreCase);
    private bool AutoMergeEnabledFor(string root) => !Settings.Value.AutoMerge.DisabledRepositories.Contains(root, StringComparer.OrdinalIgnoreCase);

    public bool IsAutoMergeEnabled
    {
        get => HasRepository && AutoMergeEnabledFor(Root);
        set
        {
            if (!HasRepository || value == IsAutoMergeEnabled) return;
            string root = Root;
            var disabled = Settings.Value.AutoMerge.DisabledRepositories;
            if (value) disabled.RemoveAll(path => string.Equals(path, root, StringComparison.OrdinalIgnoreCase));
            else disabled.Add(root);
            _autoUploadStates.Remove(root);
            _autoUploadRetryAt.Remove(root);
            Settings.Save();
            AppLog.Write((value ? "已开启自动合并：" : "已关闭自动合并：") + root);
            NotifyAutoUpload();
            if (value) _ = RunAutoUploadAsync();
        }
    }
}
