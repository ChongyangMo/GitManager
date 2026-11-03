using System.Collections.Specialized;
using System.ComponentModel;
using GitManager.Models;
using GitManager.Services;
using GitManager.Services.Repositories.Copies;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    private void RepositoryBookmarksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (RepositoryBookmark bookmark in e.OldItems) bookmark.PropertyChanged -= RepositoryBookmarkChanged;
        if (e.NewItems is not null)
            foreach (RepositoryBookmark bookmark in e.NewItems) bookmark.PropertyChanged += RepositoryBookmarkChanged;
        UpdateRepositoryNameConflicts();
    }

    private void RepositoryBookmarkChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepositoryBookmark.Name) or nameof(RepositoryBookmark.RepositoryTarget)) UpdateRepositoryNameConflicts();
    }

    private void UpdateRepositoryNameConflicts()
    {
        foreach (var nameGroup in Repositories.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var targetGroup in nameGroup.GroupBy(item => item.RepositoryTarget, StringComparer.OrdinalIgnoreCase))
            {
                var copies = targetGroup.ToArray();
                var unresolved = copies.ToDictionary(bookmark => bookmark, bookmark => copies.Where(other => other == bookmark
                    || !RepositoryCopyRetention.Contains(Settings.Value.RetainedRepositoryPairs, bookmark.Path, other.Path)).ToArray());
                if (copies.Any(item => unresolved[item].Length > 1 && !item.HasNameConflict))
                    AppLog.Write($"检测到同名仓库：{nameGroup.Key}，共 {copies.Length} 个独立目录");
                foreach (var bookmark in copies) bookmark.UpdateNameConflict(unresolved[bookmark]);
            }
        }
    }

    public IReadOnlyList<RepositoryBookmark> GetSameNameRepositories(RepositoryBookmark bookmark)
        => Repositories.Where(item => string.Equals(item.Name, bookmark.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.RepositoryTarget, bookmark.RepositoryTarget, StringComparison.OrdinalIgnoreCase)).ToArray();
}
