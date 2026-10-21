using System.IO;

namespace GitManager.Services.Repositories;

public static class RepositoryPaths
{
    public static string Actual(string path)
    {
        try
        {
            string fullPath = RepositoryDiscovery.Normalize(path);
            if (!Directory.Exists(fullPath)) return path;
            string root = Path.GetPathRoot(fullPath)!;
            string current = root.Length >= 2 && root[1] == ':' ? char.ToUpperInvariant(root[0]) + root[1..] : root;
            var options = new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseInsensitive,
                IgnoreInaccessible = false,
                AttributesToSkip = 0
            };
            foreach (string part in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                var actual = new DirectoryInfo(current).EnumerateDirectories(part, options)
                    .FirstOrDefault(item => string.Equals(item.Name, part, StringComparison.OrdinalIgnoreCase));
                if (actual is null) return path;
                current = actual.FullName;
            }
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLog.Write("读取仓库目录的实际名称失败，保留原记录", ex);
            return path;
        }
    }

    public static bool Refresh(AppSettings settings)
    {
        var repositories = settings.Repositories.Select(Actual).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ignored = settings.IgnoredRepositories.Select(Actual).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? last = settings.LastRepository is null ? null : Actual(settings.LastRepository);
        bool changed = !repositories.SequenceEqual(settings.Repositories, StringComparer.Ordinal)
            || !ignored.SequenceEqual(settings.IgnoredRepositories, StringComparer.Ordinal) || last != settings.LastRepository;
        settings.Repositories = repositories;
        settings.IgnoredRepositories = ignored;
        settings.LastRepository = last;
        return changed;
    }
}
