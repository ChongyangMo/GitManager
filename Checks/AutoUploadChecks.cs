using System.IO;
using System.Windows;
using GitManager.Services;
using GitManager.ViewModels;

namespace GitManager.Checks;

internal static partial class Program
{
    private static void RunAutoUploadChecks()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            try { await RunAutoUploadChecksAsync(); }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (failure is not null) throw new InvalidOperationException("自动上传检查失败。", failure);
    }

    private static async Task RunAutoUploadChecksAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "GitManagerAutoUploadChecks", Guid.NewGuid().ToString("N"));
        string local = Path.Combine(directory, "本地"), remote = Path.Combine(directory, "远程.git"), other = Path.Combine(directory, "另一台电脑");
        Directory.CreateDirectory(local);
        await Git.InitializeAsync(local);
        await Git.SaveIdentityAsync(local, "自动上传校验", "auto-upload-checks@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(local, "文档.txt"), "初始内容\n");
        await CommitAllAsync(local, "初始版本");
        await Runner.RunAsync(null, ["init", "--bare", "--initial-branch=main", remote]);
        await Git.SaveRemoteAsync(local, remote);
        await Git.SynchronizeVersionsAsync(local, null, "push");

        var settings = new SettingsStore(Path.Combine(directory, "settings", "settings.json"));
        settings.Value.RepositoryDiscovery.Enabled = false;
        settings.Value.Repositories = [local];
        settings.Value.AutoUpload.QuietSeconds = 3600;
        using var model = new MainViewModel(settings);
        await model.InitializeAsync(local);
        Check(model.IsAutoUploadEnabled && model.AutoUploadStatus == "", "仓库默认开启自动上传");
        model.IsAutoUploadEnabled = false;
        Check(new SettingsStore(settings.FilePath).Value.AutoUpload.DisabledRepositories.Single() == model.Root, "手动关闭自动上传后写入配置");
        model.IsAutoUploadEnabled = true;
        Check(new SettingsStore(settings.FilePath).Value.AutoUpload.DisabledRepositories.Count == 0, "重新开启自动上传后清除关闭记录");

        await File.WriteAllTextAsync(Path.Combine(local, "文档.txt"), "正在编辑的内容\n");
        await model.RunAutoUploadAsync();
        Check((await Git.ReadChangesAsync(local)).Count == 1, "文件仍在修改期内时不提交");

        settings.Value.AutoUpload.QuietSeconds = 0;
        await File.WriteAllTextAsync(Path.Combine(local, "新增.txt"), "自动上传的新文件\n");
        await model.RunAutoUploadAsync();
        var state = await Git.ReadVersionsAsync(local);
        Check((await Git.ReadChangesAsync(local)).Count == 0 && state.LocalHead!.Hash == state.RemoteHead?.Hash && state.Ahead == 0,
            "改动停止后自动提交全部文件并推送到远程");
        Check(model.AutoUploadStatus.Contains("已提交 2 个文件") && model.Changes.Count == 0, "当前仓库页面显示自动上传结果并刷新改动");
        Console.WriteLine("自动上传状态：" + model.AutoUploadStatus);

        await model.RunAutoUploadAsync();
        Check((await Git.ReadVersionsAsync(local)).LocalHead!.Hash == state.LocalHead.Hash, "没有新改动时不产生空提交");

        await Git.CloneAsync(remote, other);
        await Git.SaveIdentityAsync(other, "另一端校验", "other-checks@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(other, "远程新增.txt"), "另一端的内容\n");
        await CommitAllAsync(other, "远程新增文件");
        await Git.SynchronizeVersionsAsync(other, null, "push");
        string remoteHead = (await Git.ReadVersionsAsync(other)).LocalHead!.Hash;
        await File.WriteAllTextAsync(Path.Combine(local, "本地新增.txt"), "本地的内容\n");
        await model.RunAutoUploadAsync();
        state = await Git.ReadVersionsAsync(local);
        Check((await Git.ReadChangesAsync(local)).Count == 0 && state.Diverged && state.RemoteHead?.Hash == remoteHead,
            "远程有新提交时只提交到本地，不覆盖远程");
        Check(model.AutoUploadStatus.Contains("未完成"), "推送失败时显示原因");
        Console.WriteLine("自动上传状态：" + model.AutoUploadStatus);

        model.IsAutoUploadEnabled = false;
        Check(new SettingsStore(settings.FilePath).Value.AutoUpload.DisabledRepositories.Single() == model.Root && model.AutoUploadStatus == "", "关闭自动上传后保存关闭记录");
    }
}
