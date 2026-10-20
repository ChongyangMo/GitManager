using System.IO;
using GitManager.Services;
using GitManager.Services.Repositories;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunDiscoveryChecksAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "GitManagerDiscoveryChecks", Guid.NewGuid().ToString("N"));
        string primary = Path.Combine(root, "主仓库"), nested = Path.Combine(primary, "tools", "嵌套仓库");
        string ignored = Path.Combine(root, "排除仓库"), dependency = Path.Combine(root, "node_modules", "依赖仓库");
        string linked = Path.Combine(root, "工作树"), invalid = Path.Combine(root, "无效标记");
        foreach (string path in new[] { primary, nested, ignored, dependency })
        {
            Directory.CreateDirectory(path);
            await Git.InitializeAsync(path);
        }
        await Git.SaveIdentityAsync(primary, "GitManager 校验", "gitmanager-checks@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(primary, "README.txt"), "本地仓库发现检查\n");
        await Runner.RunAsync(primary, ["add", "--", "README.txt"]);
        await Runner.RunAsync(primary, ["commit", "-m", "建立工作树检查版本"]);
        await Runner.RunAsync(primary, ["worktree", "add", "--detach", linked, "HEAD"]);
        Directory.CreateDirectory(invalid);
        await File.WriteAllTextAsync(Path.Combine(invalid, ".git"), "无效的 Git 标记\n");

        var discovery = new RepositoryDiscovery(Git);
        var options = new RepositoryDiscoveryOptions { MaxDepth = 6, MaxDirectories = 500 };
        var result = await discovery.DiscoverAsync([root, root.ToUpperInvariant(), primary], options, [ignored + Path.DirectorySeparatorChar]);
        var paths = result.Repositories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(paths.SetEquals([primary, nested, linked]), "真实仓库、嵌套仓库和工作树均能识别，忽略排除项及依赖目录");
        Check(result.Repositories.Count == 3, "重复扫描路径及大小写差异不会产生重复记录");
        Check(!result.Incomplete && !paths.Contains(invalid), "无效标记不会被当作仓库");
        var shallow = await discovery.DiscoverAsync([primary], new() { MaxDepth = 0 }, []);
        Check(shallow.Repositories.SequenceEqual([primary]), "扫描深度边界生效");
        var bounded = await discovery.DiscoverAsync([root], new() { MaxDirectories = 1 }, []);
        Check(bounded.Incomplete, "扫描目录数上限生效并明确标记未完成");
        Console.WriteLine("真实临时仓库检查目录：" + root);
    }

    private static async Task DiscoverLocalAsync()
    {
        var settings = new SettingsStore().Value;
        var result = await new RepositoryDiscovery(Git).DiscoverAsync(
            RepositoryDiscovery.GetRoots(settings.RepositoryDiscovery, settings.Repositories), settings.RepositoryDiscovery, settings.IgnoredRepositories);
        foreach (string path in result.Repositories) Console.WriteLine(path);
        Console.WriteLine($"本机实际识别 {result.Repositories.Count} 个仓库，达到上限：{result.Incomplete}");
    }
}
