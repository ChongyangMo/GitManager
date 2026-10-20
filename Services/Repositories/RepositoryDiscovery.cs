using System.IO;

namespace GitManager.Services.Repositories;

public sealed record RepositoryDiscoveryResult(IReadOnlyList<string> Repositories, bool Incomplete);

public sealed class RepositoryDiscovery(GitRepositoryService git)
{
    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".idea", "node_modules", "bin", "obj", "publish", "artifacts",
        "target", "dist", "build", ".venv", "venv", "__pycache__", ".cache", "packages"
    };

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static IReadOnlyList<string> GetRoots(RepositoryDiscoveryOptions options, IEnumerable<string> bookmarks)
    {
        List<string> roots = [Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)];
        string userDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots.AddRange(options.Roots);
        foreach (string bookmark in bookmarks)
        {
            try
            {
                if (Path.GetDirectoryName(Normalize(bookmark)) is { } parent && Directory.GetParent(parent) is not null
                    && !string.Equals(parent, userDirectory, StringComparison.OrdinalIgnoreCase)) roots.Add(parent);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { AppLog.Write("忽略无效的仓库扫描目录", ex); }
        }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && directory.Parent is not null; i++, directory = directory.Parent)
        {
            if (string.Equals(directory.FullName, userDirectory, StringComparison.OrdinalIgnoreCase)) break;
            if (HasGitMarker(directory.FullName)) { roots.Add(directory.FullName); break; }
        }
        return roots.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
    }

    public Task<RepositoryDiscoveryResult> DiscoverAsync(IEnumerable<string> roots, RepositoryDiscoveryOptions options,
        IEnumerable<string> ignored, CancellationToken cancellationToken = default)
    {
        string[] rootSnapshot = roots.ToArray(), ignoredSnapshot = ignored.ToArray();
        int maxDepth = Math.Clamp(options.MaxDepth, 0, 12), maxDirectories = Math.Clamp(options.MaxDirectories, 1, 100000);
        return Task.Run(async () =>
        {
            var queue = new Queue<(string Path, int Depth)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in ignoredSnapshot)
            {
                try { excluded.Add(Normalize(path)); }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { AppLog.Write("忽略无效的仓库排除记录", ex); }
            }
            foreach (string path in rootSnapshot)
            {
                try
                {
                    string normalized = Normalize(path);
                    if (!normalized.StartsWith(@"\\", StringComparison.Ordinal) && Directory.Exists(normalized) && visited.Add(normalized)) queue.Enqueue((normalized, 0));
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { AppLog.Write("无法访问仓库扫描目录", ex); }
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(15));
            int scanned = 0;
            bool incomplete = false;
            while (queue.TryDequeue(out var entry))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget.IsCancellationRequested || scanned >= maxDirectories) { incomplete = true; break; }
                scanned++;
                try
                {
                    if ((File.GetAttributes(entry.Path) & FileAttributes.ReparsePoint) != 0) continue;
                    if (!excluded.Contains(entry.Path) && HasGitMarker(entry.Path))
                    {
                        try { found.Add(await git.OpenAsync(entry.Path, budget.Token)); }
                        catch (TimeoutException) when (budget.IsCancellationRequested) { incomplete = true; break; }
                        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { incomplete = true; break; }
                        catch (Exception ex) when (ex is InvalidOperationException or IOException) { AppLog.Write("跳过无法读取的本地仓库：" + entry.Path, ex); }
                    }
                    if (entry.Depth >= maxDepth) continue;
                    foreach (string child in Directory.EnumerateDirectories(entry.Path, "*", new EnumerationOptions
                    {
                        IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
                    }))
                    {
                        if (budget.IsCancellationRequested || visited.Count >= maxDirectories) { incomplete = true; break; }
                        if (!SkippedNames.Contains(Path.GetFileName(child)) && visited.Add(child)) queue.Enqueue((child, entry.Depth + 1));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Write("跳过无法访问的扫描目录：" + entry.Path, ex); }
            }
            AppLog.Write($"本地仓库扫描完成，检查 {scanned} 个目录，识别 {found.Count} 个仓库" + (incomplete ? "，已达到扫描上限" : ""));
            return new RepositoryDiscoveryResult(found.Order(StringComparer.OrdinalIgnoreCase).ToList(), incomplete);
        }, cancellationToken);
    }

    private static bool HasGitMarker(string path) => Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));
}
