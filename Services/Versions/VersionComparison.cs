using GitManager.Models;

namespace GitManager.Services;

public sealed partial class GitRepositoryService
{
    public async Task<List<HistoryFile>> ReadVersionDifferencesAsync(string root, string before, string after)
    {
        await Run(root, "cat-file", "-e", before + "^{commit}");
        await Run(root, "cat-file", "-e", after + "^{commit}");
        string[] fields = (await Run(root, "diff", "--name-status", "--no-renames", "-z", before, after, "--")).Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        List<HistoryFile> files = [];
        for (int i = 0; i + 1 < fields.Length; i += 2) files.Add(new(fields[i + 1], fields[i] switch { "A" => "新增", "D" => "删除", _ => "修改" }));
        return files;
    }

    public async Task<FileComparison> CompareVersionsAsync(string root, VersionChoice before, VersionChoice after, HistoryFile file)
    {
        try
        {
            string left = await ReadBlobAsync(root, before.Commit.Hash, file.Path);
            string right = await ReadBlobAsync(root, after.Commit.Hash, file.Path);
            return new(left, right, await CompareTextsAsync(left, right), before.Label, after.Label);
        }
        catch (PreviewException ex) { return new("", "", "", before.Label, after.Label, ex.Message); }
    }
}
