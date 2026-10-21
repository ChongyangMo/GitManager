using System.Collections.Specialized;
using System.ComponentModel;
using GitManager.Models;
using GitManager.Services;

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
        if (e.PropertyName == nameof(RepositoryBookmark.Name)) UpdateRepositoryNameConflicts();
    }

    private void UpdateRepositoryNameConflicts()
    {
        foreach (var group in Repositories.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var copies = group.ToArray();
            if (copies.Length > 1 && copies.Any(item => !item.HasNameConflict))
                AppLog.Write($"检测到同名仓库：{group.Key}，共 {copies.Length} 个独立目录");
            foreach (var bookmark in copies) bookmark.UpdateNameConflict(copies);
        }
    }

    public IReadOnlyList<RepositoryBookmark> GetSameNameRepositories(RepositoryBookmark bookmark)
        => Repositories.Where(item => string.Equals(item.Name, bookmark.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
}
