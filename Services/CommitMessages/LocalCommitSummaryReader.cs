using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitManager.Models;

namespace GitManager.Services.CommitMessages;

public sealed record CommitSummaryFile(string Path, string? OriginalPath, string Action, int? Added, int? Removed, bool Binary);
public sealed record CommitSummarySource(List<CommitSummaryFile> Files, string Fingerprint);

public sealed class LocalCommitSummaryReader(GitRepositoryService git)
{
    public async Task<CommitSummarySource> ReadAsync(string root, IReadOnlyList<ChangedFile> selected, CancellationToken cancellationToken = default)
    {
        if (selected.Count == 0) throw new InvalidOperationException("请先勾选需要提交的文件。");
        cancellationToken.ThrowIfCancellationRequested();
        var actual = (await git.ReadChangesAsync(root, cancellationToken)).ToDictionary(file => file.Path, StringComparer.Ordinal);
        var files = selected.OrderBy(file => file.Path, StringComparer.Ordinal).Select(picked => actual.TryGetValue(picked.Path, out var file)
            ? file : throw new InvalidOperationException("勾选文件的状态已变化，请刷新后重新总结。")).ToArray();
        if (files.Any(file => file.IsConflict)) throw new InvalidOperationException("请先处理未解决的冲突，再生成提交说明。");
        var head = await git.Runner.RunAsync(root, ["rev-parse", "--verify", "HEAD"], true, cancellationToken);
        var statistics = new Dictionary<string, (int? Added, int? Removed, bool Binary)>(StringComparer.Ordinal);
        if (head.ExitCode == 0)
        {
            // 只统计勾选路径；分批避免 Windows 命令行长度限制，不调用外部差异工具。
            foreach (var batch in PathBatches(files.SelectMany(file => file.Paths).Distinct(StringComparer.Ordinal)))
            {
                var args = new[] { "diff", "--numstat", "--no-renames", "--no-ext-diff", "--no-textconv", "-z", head.Output.Trim(), "--" }.Concat(batch);
                string output = (await git.Runner.RunAsync(root, args, cancellationToken: cancellationToken)).Output;
                foreach (string entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = entry.Split('\t', 3);
                    if (parts.Length != 3) throw new InvalidOperationException("读取改动行数失败。");
                    if (parts[0] == "-" && parts[1] == "-") statistics[parts[2]] = (null, null, true);
                    else if (int.TryParse(parts[0], out int added) && int.TryParse(parts[1], out int removed)) statistics[parts[2]] = (added, removed, false);
                    else throw new InvalidOperationException("改动行数格式无效。");
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var changes = files.Select(file =>
        {
            statistics.TryGetValue(file.Path, out var stats);
            string action = file.IndexState == 'C' || file.WorktreeState == 'C' ? "复制" : file.State;
            // 未跟踪文件、初次提交和重命名不填造增删行数。
            bool countable = file.IndexState != '?' && head.ExitCode == 0 && file.OriginalPath is null;
            return new CommitSummaryFile(file.Path, file.OriginalPath, action, countable ? stats.Added : null, countable ? stats.Removed : null, stats.Binary);
        }).ToList();
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(head.Output + JsonSerializer.Serialize(changes))));
        return new(changes, fingerprint);
    }

    private static IEnumerable<List<string>> PathBatches(IEnumerable<string> paths)
    {
        List<string> batch = [];
        int length = 0;
        foreach (string path in paths)
        {
            if (length + path.Length > 6000 && batch.Count > 0) { yield return batch; batch = []; length = 0; }
            batch.Add(path);
            length += path.Length + 4;
        }
        if (batch.Count > 0) yield return batch;
    }
}
