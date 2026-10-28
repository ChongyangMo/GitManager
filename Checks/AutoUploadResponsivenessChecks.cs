using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitManager.Services;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunAutoUploadResponsivenessChecksAsync()
    {
        string directory = CheckDirectories.Create("AutoUploadResponsiveness");
        string local = Path.Combine(directory, "本地"), remote = Path.Combine(directory, "远程.git"), other = Path.Combine(directory, "另一仓库");
        Directory.CreateDirectory(local);
        await Git.InitializeAsync(local);
        await Git.SaveIdentityAsync(local, "自动上传检查", ChecksCommitEmail);
        await File.WriteAllTextAsync(Path.Combine(local, "README.md"), "初始版本\n");
        await CommitAllAsync(local, "初始版本");
        await Runner.RunAsync(null, ["init", "--bare", "--initial-branch=main", remote]);
        await Git.SaveRemoteAsync(local, remote);
        await Git.SynchronizeVersionsAsync(local, null, "push");
        await Git.CloneAsync(remote, other);
        await File.AppendAllTextAsync(Path.Combine(local, "README.md"), "待上传内容\n");
        await CommitAllAsync(local, "待上传版本");

        // 使用真正的 Git 推送钩子保持推送在途，检查界面能否继续操作。
        await Runner.RunAsync(local, ["config", "--local", "core.hooksPath", ".git/hooks"]);
        await File.WriteAllTextAsync(Path.Combine(local, ".git", "hooks", "pre-push"),
            "#!/bin/sh\n: > .git/push-started\nwhile [ ! -f .git/push-release ]; do sleep 0.05; done\n", new System.Text.UTF8Encoding(false));
        string release = Path.Combine(local, ".git", "push-release");
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        settings.Value.RepositoryDiscovery.Enabled = false;
        settings.Value.GitHub.CommitEmail = ChecksCommitEmail;
        settings.Value.Repositories = [local, other];
        settings.Value.AutoUpload.DisabledRepositories = [local, other];
        using var model = new MainViewModel(settings);
        var window = new MainWindow(model) { ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        Task upload = Task.CompletedTask;
        try
        {
            await model.InitializeAsync(local);
            await model.PreloadRepositoriesAsync();
            model.IsAutoUploadEnabled = true;
            upload = model.RunAutoUploadAsync();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (!File.Exists(Path.Combine(local, ".git", "push-started")) && !upload.IsCompleted && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20);
            Check(File.Exists(Path.Combine(local, ".git", "push-started")) && !upload.IsCompleted, "真实自动推送已进入钩子等待");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var list = (ListBox)window.FindName("RepositoryList");
            var tabs = (TabControl)window.FindName("WorkspaceTabs");
            var input = (TextBox)window.FindName("CommitInput");
            var commit = (Button)window.FindName("CommitButton");
            var branch = (ComboBox)window.FindName("BranchBox");
            Check(model.IsIdle && model.CanOperate && model.IsRepositoryAutoUploading && list.IsEnabled && tabs.IsEnabled && input.IsEnabled,
                "推送等待期间仓库列表、标签页和说明输入保持可用");
            Check(!model.CanWriteRepository && !commit.IsEnabled && !branch.IsEnabled && model.IsBusyProgressVisible,
                "仅禁用上传仓库的写操作，状态栏继续显示进度");
            await model.SwitchBranchAsync("不应创建的分支", create: true);
            Check((await Git.ReadAsync(local)).Branch == "main" && model.IsIdle, "同仓库写操作不进入等待，也不与自动上传争用");
            model.CommitMessage = "上传仓库的草稿";
            model.ActiveTab = MainViewModel.HistoryTab;
            await model.ShowActiveTabAsync();
            Check(!upload.IsCompleted && model.History.Count == 2 && model.IsIdle, "上传期间可查看当前仓库历史");

            list.SetCurrentValue(ListBox.SelectedItemProperty, model.Repositories.Single(item => item.Path == other));
            await WaitWorkspaceReadsAsync(model);
            Check(model.Root == other && model.CanWriteRepository && !model.IsRepositoryAutoUploading && !upload.IsCompleted,
                "推送未结束时可切到另一仓库并立即操作");
            model.CommitMessage = "另一仓库的草稿";
            await model.SwitchBranchAsync("upload-navigation-check", create: true);
            Check(!upload.IsCompleted && model.Branch == "upload-navigation-check", "另一仓库切换分支不等待后台上传");
            model.ActiveTab = MainViewModel.HistoryTab;
            await model.ShowActiveTabAsync();
            await File.WriteAllTextAsync(release, "");
            await upload.WaitAsync(TimeSpan.FromSeconds(20));
            await WaitWorkspaceReadsAsync(model);
            Check(model.Root == other && model.Branch == "upload-navigation-check" && model.History.Count == 1
                && model.CommitMessage == "另一仓库的草稿" && model.ActiveTab == MainViewModel.HistoryTab,
                "旧仓库上传完成不覆盖当前仓库、历史、标签页或草稿");
            await model.OpenRepositoryAsync(local);
            await WaitWorkspaceReadsAsync(model);
            var state = await Git.ReadVersionsAsync(local);
            Check(model.CanWriteRepository && state.Ahead == 0 && state.LocalHead?.Hash == state.RemoteHead?.Hash
                && model.History.Count == 2 && model.CommitMessage == "上传仓库的草稿", "切回后显示上传结果并保留原仓库草稿");
        }
        finally
        {
            await File.WriteAllTextAsync(release, "");
            try { await upload.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { model.Dispose(); window.Close(); }
        }
    }
}
