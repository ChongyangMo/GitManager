using System.IO;
using System.Text;

namespace GitManager.Services.CommitMessages;

public static class LocalCommitSummary
{
    public static string Generate(IReadOnlyList<CommitSummaryFile> files)
    {
        if (files.Count == 0) throw new InvalidOperationException("请先勾选需要提交的文件。");
        var groups = files.GroupBy(file => Category(file.Path)).ToArray();
        string verb = files.Select(file => file.Action).Distinct().Count() == 1 ? files[0].Action : "更新";
        string title = files.Count == 1 ? $"{verb} {files[0].Path}"
            : verb + string.Join("、", groups.Take(3).Select(group => group.Key)) + (groups.Length > 3 ? $"等 {groups.Length} 类文件" : "相关文件");
        var message = new StringBuilder(title);
        message.AppendLine().AppendLine();
        foreach (var group in groups)
        {
            var items = group.ToArray();
            string details = items.Length == 1 ? items[0].Action + " " + DisplayPath(items[0])
                : string.Join("、", items.GroupBy(file => file.Action).Select(action => $"{action.Key} {action.Count()} 个文件"))
                    + "（" + string.Join("、", items.Take(3).Select(DisplayPath)) + (items.Length > 3 ? $" 等 {items.Length} 个" : "") + "）";
            string stats = items.All(file => file.Added.HasValue && file.Removed.HasValue)
                ? $"（+{items.Sum(file => (long)file.Added!.Value)}/-{items.Sum(file => (long)file.Removed!.Value)} 行）"
                : items.Length == 1 && items[0].Binary ? "（二进制文件）" : "";
            message.AppendLine($"- {group.Key}：{details}{stats}");
        }
        return message.ToString().TrimEnd();
    }

    private static string DisplayPath(CommitSummaryFile file)
        => file.OriginalPath is null ? file.Path : file.OriginalPath + " → " + file.Path;

    private static string Category(string path)
    {
        string normalized = path.Replace('\\', '/').ToLowerInvariant();
        string extension = Path.GetExtension(normalized);
        string name = Path.GetFileName(normalized);
        if (normalized.StartsWith("checks/") || normalized.StartsWith("tests/") || normalized.Contains("/tests/")
            || name.EndsWith("tests.cs") || name.EndsWith("test.cs") || name.EndsWith("_test.go")
            || name.StartsWith("test_") || name.Contains(".test.") || name.Contains(".spec.")) return "检查用例";
        if (extension is ".md" or ".rst" or ".txt" || name.StartsWith("readme")) return "文档";
        if (extension is ".json" or ".toml" or ".yaml" or ".yml" or ".ini" or ".config" || name.Contains("settings")) return "配置";
        if (normalized.Contains("commitmessages/") || name.Contains("commitsummary")) return "提交说明";
        if (normalized.StartsWith("models/") || normalized.Contains("/models/")) return "数据模型";
        if (normalized.StartsWith("views/") || normalized.StartsWith("viewmodels/") || normalized.StartsWith("themes/")
            || name.StartsWith("mainwindow") || extension is ".xaml" or ".vue" or ".tsx" or ".jsx" or ".css" or ".scss" or ".html") return "界面";
        if (normalized.StartsWith("services/git") || normalized.StartsWith("services/versions/")) return "Git 操作";
        if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico" or ".ttf" or ".woff" or ".woff2") return "资源";
        if (normalized.StartsWith("services/")) return "业务逻辑";
        return "项目文件";
    }
}
