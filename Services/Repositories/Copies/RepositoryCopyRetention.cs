namespace GitManager.Services.Repositories.Copies;

public sealed record RetainedRepositoryPair(string FirstPath, string SecondPath);

public static class RepositoryCopyRetention
{
    public static bool Contains(IEnumerable<RetainedRepositoryPair> pairs, string firstPath, string secondPath)
    {
        string first = RepositoryDiscovery.Normalize(firstPath), second = RepositoryDiscovery.Normalize(secondPath);
        return pairs.Any(pair => Same(pair.FirstPath, first) && Same(pair.SecondPath, second)
            || Same(pair.FirstPath, second) && Same(pair.SecondPath, first));
    }

    // 按目录两两记住选择，新增同名目录时仍然提醒。
    public static List<RetainedRepositoryPair> Remember(IEnumerable<RetainedRepositoryPair> pairs, IEnumerable<string> paths)
    {
        var result = pairs.ToList();
        var roots = paths.Select(RepositoryDiscovery.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        for (int i = 0; i < roots.Length; i++)
            for (int j = i + 1; j < roots.Length; j++)
                if (!Contains(result, roots[i], roots[j])) result.Add(new(roots[i], roots[j]));
        return result;
    }

    private static bool Same(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
