using System.IO;
using GitManager.Models;

namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    public async Task CommitAsync(string root, IReadOnlyList<ChangedFile> files, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("请填写提交说明。");
        if (files.Count == 0) throw new InvalidOperationException("请勾选需要提交的文件。");
        if ((await ReadChangesAsync(root)).Any(file => file.IsConflict)) throw new InvalidOperationException("仓库中存在冲突，请先解决冲突后再提交。");
        await EnsureNoOperationAsync(root);
        string[] paths = files.SelectMany(file => file.Paths).Distinct(StringComparer.Ordinal).ToArray();
        var indexedPaths = (await Run(root, "ls-files", "-z")).Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        string[] stagePaths = paths.Where(path => indexedPaths.Contains(path) || File.Exists(Path.Combine(root, path)) || Directory.Exists(Path.Combine(root, path))).ToArray();
        if (stagePaths.Length > 0) await RunWithPathsAsync(root, ["add", "-A"], stagePaths);
        await RunWithPathsAsync(root, ["commit", "--only", "-m", message.Trim()], paths);
        AppLog.Write("已保存本地提交：" + root);
    }

    public async Task SwitchBranchAsync(string root, string branch, bool create = false)
    {
        await EnsureNoOperationAsync(root);
        if ((await ReadChangesAsync(root)).Count > 0) throw new InvalidOperationException("请先提交当前修改，再切换分支。");
        await Run(root, "check-ref-format", "--branch", branch);
        await Runner.RunAsync(root, create ? ["switch", "-c", branch] : ["switch", branch]);
        AppLog.Write("已切换分支：" + branch);
    }

    public async Task SaveIdentityAsync(string root, string name, string email)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email)) throw new InvalidOperationException("请填写提交姓名和邮箱。");
        await Run(root, "config", "--local", "user.name", name.Trim());
        await Run(root, "config", "--local", "user.email", email.Trim());
    }

    public async Task SaveRemoteAsync(string root, string url)
    {
        ValidateRemote(url);
        var remotes = (await Run(root, "remote")).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await Runner.RunAsync(root, remotes.Contains("origin") ? ["remote", "set-url", "origin", url.Trim()] : ["remote", "add", "origin", url.Trim()]);
        AppLog.Write("已更新仓库远程地址");
    }

    public async Task SynchronizeAsync(string root, string operation)
    {
        await SynchronizeVersionsAsync(root, null, operation);
    }

    public async Task EnsureNoOperationAsync(string root)
    {
        string metadata = (await Run(root, "rev-parse", "--absolute-git-dir")).Output.Trim();
        foreach (string marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "BISECT_LOG" })
            if (File.Exists(Path.Combine(metadata, marker)) || Directory.Exists(Path.Combine(metadata, marker)))
                throw new InvalidOperationException("仓库正在合并、变基或执行其他操作，请先完成或取消该操作。");
    }

    public static void ValidateRemote(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('-') || url.Any(char.IsControl)) throw new InvalidOperationException("请输入有效的远程仓库地址。");
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("请勿在地址中填写密码或令牌，请使用 Git 凭据管理器登录。");
    }

    private async Task RunWithPathsAsync(string root, IEnumerable<string> command, IEnumerable<string> paths)
    {
        string directory = Path.Combine(SettingsStore.DataDirectory, "pathspecs");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(file, string.Join('\0', paths) + '\0', new System.Text.UTF8Encoding(false));
            await Runner.RunAsync(root, command.Concat(["--pathspec-from-file=" + file, "--pathspec-file-nul"]));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
