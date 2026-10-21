using System.IO;
using System.Text.RegularExpressions;

namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    private async Task EnsureMergeAsync(string root)
    {
        string directory = (await Run(root, "rev-parse", "--absolute-git-dir")).Output.Trim();
        if (!File.Exists(Path.Combine(directory, "MERGE_HEAD"))) throw new InvalidOperationException("当前没有待完成的合并。");
    }

    public async Task ResolveConflictAsync(string root, string path, string side)
    {
        await EnsureMergeAsync(root);
        if (!(await ReadChangesAsync(root)).Any(file => file.Path == path && file.IsConflict)) throw new InvalidOperationException("请先选择仍有冲突的文件。");
        if (side is "ours" or "theirs")
        {
            string stage = side == "ours" ? "2" : "3";
            string[] entries = (await Run(root, "ls-files", "--unmerged", "-z", "--", path)).Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            bool exists = entries.Any(entry => entry.Split('\t')[0].EndsWith(" " + stage, StringComparison.Ordinal));
            if (!exists) { await Run(root, "rm", "-f", "--", path); return; }
            await Run(root, "checkout", side == "ours" ? "--ours" : "--theirs", "--", path);
        }
        else if (side == "edited")
        {
            string file = Path.Combine(root, path);
            if (File.Exists(file) && new FileInfo(file).Length <= PreviewLimit)
            {
                string text = await File.ReadAllTextAsync(file);
                if (Regex.IsMatch(text, @"^(<<<<<<< |=======\r?$|>>>>>>> )", RegexOptions.Multiline))
                    throw new InvalidOperationException("文件中仍有冲突标记，请编辑完成后再标记为已解决。");
            }
        }
        else throw new InvalidOperationException("不支持的冲突处理方式。");
        await RunWithPathsAsync(root, ["add", "-A"], [path]);
        AppLog.Write("已处理合并冲突：" + path);
    }

    public async Task FinishMergeAsync(string root, string message)
    {
        await EnsureMergeAsync(root);
        if ((await ReadChangesAsync(root)).Any(file => file.IsConflict)) throw new InvalidOperationException("仍有未处理的冲突，请先逐个解决。");
        await RunWithCommitIdentityAsync(root, string.IsNullOrWhiteSpace(message) ? ["commit", "--no-edit"] : ["commit", "-m", message.Trim()]);
        AppLog.Write("合并提交已保存到本地");
    }

    public async Task AbortMergeAsync(string root)
    {
        await EnsureMergeAsync(root);
        await Run(root, "merge", "--abort");
        AppLog.Write("已取消本次合并");
    }
}
