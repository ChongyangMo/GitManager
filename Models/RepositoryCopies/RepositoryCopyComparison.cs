namespace GitManager.Models.RepositoryCopies;

public sealed record RepositoryCopySnapshot(RepositoryBookmark Bookmark, int Number, string Remote, string Branch,
    string Commit, string Changes, IReadOnlyDictionary<string, string> Files, string Error)
{
    public string Title => $"{Number} · {Bookmark.Name}";
    public string Path => Bookmark.Path;
    public bool CanDeleteLocal => Error.Length == 0;
}

public sealed record RepositoryCopyDifference(string Path, string Difference);
public sealed record RepositoryCopyComparison(IReadOnlyList<RepositoryCopySnapshot> Copies,
    IReadOnlyList<RepositoryCopyDifference> Differences)
{
    public string FileStatus => Copies.Any(copy => copy.Error.Length > 0) ? "文件比较未完成"
        : (Differences.Count == 0 ? "Git 文件内容相同" : $"Git 文件差异 {Differences.Count}")
            + (Copies.Any(copy => copy.Files.Values.Contains("子模块未比较")) ? "（子模块未比较）" : "");
}
