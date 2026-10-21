using System.IO;
using GitManager.Models.RepositoryCopies;

namespace GitManager.Services.Repositories.Copies.Analysis;

public sealed record CopyGitEvidence(int Number, string Path, string Head, string CommonDirectory, bool LinkedWorktree,
    bool Shallow, string[] IgnoredEntries, string[] LocalBranches, int Stashes, string Worktrees, string Error);

// LeftOnly/RightOnly 均相对于本次比较的另一份 HEAD，不表示整个仓库中的独有提交。
public sealed record CopyHistoryRelation(int Left, int Right, bool SharedGitDirectory, int? LeftOnly, int? RightOnly, string State);
public sealed record CopyAnalysisEvidence(IReadOnlyList<CopyGitEvidence> Repositories, IReadOnlyList<CopyHistoryRelation> Relations);

public sealed class RepositoryCopyEvidenceReader(GitRepositoryService git)
{
    public async Task<CopyAnalysisEvidence> ReadAsync(RepositoryCopyComparison comparison, CancellationToken token)
    {
        var repositories = new List<CopyGitEvidence>();
        foreach (var copy in comparison.Copies)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (copy.Error.Length > 0) throw new IOException(copy.Error);
                string root = copy.Path;
                string head = (await git.Runner.RunAsync(root, ["rev-parse", "--verify", "HEAD"], true, token)).Output.Trim();
                if (head.Length > 0 && !copy.Commit.StartsWith(head + "\n", StringComparison.Ordinal)
                    && !copy.Commit.StartsWith(head + "\r\n", StringComparison.Ordinal))
                    throw new IOException("读取期间提交已变化，请重新分析。");
                string common = RepositoryDiscovery.Normalize(await ReadAsync(root, ["rev-parse", "--path-format=absolute", "--git-common-dir"], token));
                string gitDirectory = RepositoryDiscovery.Normalize(await ReadAsync(root, ["rev-parse", "--absolute-git-dir"], token));
                bool linked = File.Exists(System.IO.Path.Combine(root, ".git"))
                    && gitDirectory.StartsWith(common + System.IO.Path.DirectorySeparatorChar + "worktrees" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                bool shallow = await ReadAsync(root, ["rev-parse", "--is-shallow-repository"], token) == "true";
                string[] ignored = (await ReadAsync(root, ["ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z"], token))
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                string[] branches = Lines(await ReadAsync(root, ["for-each-ref", "--format=%(refname:short) %(objectname)", "refs/heads/"], token));
                int stashes = Lines(await ReadAsync(root, ["stash", "list", "--format=%H"], token)).Length;
                string worktrees = await ReadAsync(root, ["worktree", "list", "--porcelain"], token);
                repositories.Add(new(copy.Number, root, head, common, linked, shallow, ignored, branches, stashes, worktrees, ""));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                AppLog.Write("读取仓库分析依据失败：" + copy.Path, ex);
                repositories.Add(new(copy.Number, copy.Path, "", "", false, false, [], [], 0, "", AppLog.Clean(ex.Message)));
            }
        }
        var relations = new List<CopyHistoryRelation>();
        for (int i = 0; i < repositories.Count; i++)
            for (int j = i + 1; j < repositories.Count; j++)
                relations.Add(await RelateAsync(repositories[i], repositories[j], token));
        return new(repositories, relations);
    }

    private async Task<CopyHistoryRelation> RelateAsync(CopyGitEvidence left, CopyGitEvidence right, CancellationToken token)
    {
        bool shared = left.CommonDirectory.Length > 0 && left.CommonDirectory.Equals(right.CommonDirectory, StringComparison.OrdinalIgnoreCase);
        CopyHistoryRelation Unknown(string reason) => new(left.Number, right.Number, shared, null, null, reason);
        if (left.Error.Length > 0 || right.Error.Length > 0) return Unknown("读取失败，提交关系未知");
        if (left.Head.Length == 0 || right.Head.Length == 0) return Unknown("至少一份尚无提交，不能判断提交包含关系");
        if (left.Shallow || right.Shallow) return Unknown("浅克隆历史不完整，不能确认独有提交和包含关系");
        try
        {
            // 不 fetch、不修改对象库；独立克隆可能缺少对方提交，尝试两边已有的对象。
            string? graph = null;
            foreach (string root in new[] { left.Path, right.Path })
            {
                var hasLeft = await git.Runner.RunAsync(root, ["--no-lazy-fetch", "cat-file", "-e", left.Head + "^{commit}"], true, token);
                var hasRight = await git.Runner.RunAsync(root, ["--no-lazy-fetch", "cat-file", "-e", right.Head + "^{commit}"], true, token);
                if (hasLeft.ExitCode == 0 && hasRight.ExitCode == 0) { graph = root; break; }
            }
            if (graph is null) return Unknown("本地缺少对方提交对象，未联网，不能确认提交关系");
            var ancestor = await git.Runner.RunAsync(graph, ["--no-lazy-fetch", "merge-base", left.Head, right.Head], true, token);
            if (ancestor.ExitCode == 1) return Unknown("没有共同祖先，属于不同提交历史");
            if (ancestor.ExitCode != 0) return Unknown("共同祖先读取失败，提交关系未知");
            string[] counts = (await ReadAsync(graph, ["rev-list", "--left-right", "--count", left.Head + "..." + right.Head], token))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (counts.Length != 2 || !int.TryParse(counts[0], out int onlyLeft) || !int.TryParse(counts[1], out int onlyRight))
                return Unknown("提交数量读取失败，提交关系未知");
            string state = (onlyLeft, onlyRight) switch
            {
                (0, 0) => "两份 HEAD 相同",
                (_, 0) => "左侧 HEAD 包含右侧 HEAD 的全部提交",
                (0, _) => "右侧 HEAD 包含左侧 HEAD 的全部提交",
                _ => "双方各有另一份 HEAD 未包含的提交"
            };
            return new(left.Number, right.Number, shared, onlyLeft, onlyRight, state);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        { return Unknown("提交关系读取失败：" + AppLog.Clean(ex.Message)); }
    }

    private async Task<string> ReadAsync(string root, string[] arguments, CancellationToken token)
        => (await git.Runner.RunAsync(root, new[] { "--no-lazy-fetch" }.Concat(arguments), cancellationToken: token)).Output.TrimEnd('\r', '\n');
    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
