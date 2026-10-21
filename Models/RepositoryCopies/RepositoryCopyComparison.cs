namespace GitManager.Models.RepositoryCopies;

public sealed record RepositoryCopySnapshot(RepositoryBookmark Bookmark, int Number, string Remote, string Branch,
    string Commit, string Changes, IReadOnlyDictionary<string, RepositoryFileFingerprint> Files, string Error)
{
    public string Title => $"{Number} · {Bookmark.Name}";
    public string Path => Bookmark.Path;
    public bool CanDeleteLocal => Error.Length == 0;
}

// Hash 按原始字节计算；Content 把 CRLF 统一为 LF 后计算（二进制文件与 Hash 相同），用于区分“仅换行符不同”。
public sealed record RepositoryFileFingerprint(string Hash, string Content, string LineEnding)
{
    public const string Submodule = "子模块未比较";
    public static RepositoryFileFingerprint Special(string label) => new(label, label, "");
}

public sealed record RepositoryCopyDifference(string Path, string Difference);
public sealed record RepositoryCopyComparison(IReadOnlyList<RepositoryCopySnapshot> Copies,
    IReadOnlyList<RepositoryCopyDifference> Differences, IReadOnlyList<RepositoryCopyDifference> LineEndingDifferences)
{
    public string FileStatus => Copies.Any(copy => copy.Error.Length > 0) ? "文件比较未完成"
        : (Differences.Count == 0 ? "Git 文件内容相同" : $"Git 文件差异 {Differences.Count}")
            + (LineEndingDifferences.Count > 0 ? $"，另有 {LineEndingDifferences.Count} 个文件仅换行符不同（不计入差异）" : "")
            + (Copies.Any(copy => copy.Files.Values.Any(file => file.Hash == RepositoryFileFingerprint.Submodule)) ? "（子模块未比较）" : "");
}
