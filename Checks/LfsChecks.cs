using System.IO;
using System.Security.Cryptography;
using GitManager.Services;
using GitManager.Services.ServiceCloud;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunLfsLiveChecksAsync(string sourceRoot)
    {
        var settings = new SettingsStore();
        var execution = settings.Value.GitExecution;
        Check(execution.TimeoutFor(["status"]) == TimeSpan.FromSeconds(execution.ReadTimeoutSeconds)
            && execution.TimeoutFor(["push", "origin"]) == TimeSpan.FromSeconds(execution.WriteTimeoutSeconds)
            && execution.TimeoutFor(["-c", "http.proxy=http://127.0.0.1:1080", "clone", "--", "https://example.invalid/a/b.git", "unused"]) == TimeSpan.FromSeconds(execution.WriteTimeoutSeconds)
            && execution.TimeoutFor(["commit", "-m", "提交"]) == TimeSpan.FromSeconds(execution.WriteTimeoutSeconds), "读取和 LFS 写入操作分别使用配置中的等待时限");

        var options = settings.Value.ServiceCloud;
        var serviceBase = ServiceCloudClient.NormalizeBaseUrl(options.BaseUrl);
        string nativeUrl = ServiceCloudGitAddress.RepositoryUrl(serviceBase, "70063423/Maico", options.GitBaseUrl);
        Check(nativeUrl == new Uri(ServiceCloudClient.NormalizeBaseUrl(options.GitBaseUrl), "70063423/Maico.git").AbsoluteUri
            && ServiceCloudGitAddress.BelongsTo(serviceBase, new Uri(nativeUrl), options)
            && ServiceCloudGitAddress.BelongsTo(serviceBase, new Uri(ServiceCloudGitAddress.RepositoryUrl(serviceBase, "70063423/Maico")), options)
            && !ServiceCloudGitAddress.BelongsTo(serviceBase, new Uri("https://example.invalid/70063423/Maico.git"), options), "服务云允许已配置的原生 Git 地址，并拒绝其他服务器");
        Check(ServiceCloudGitAddress.RepositoryUrl(serviceBase, "历史/多级/仓库", options.GitBaseUrl).StartsWith(new Uri(serviceBase, "git/").AbsoluteUri, StringComparison.Ordinal), "多级历史仓库继续使用原有地址");

        var runner = new GitRunner(() => settings.Value.GitExecutablePath, executionOptions: () => execution);
        var git = new GitRepositoryService(runner, settings.Value.GitHub, () => options);
        sourceRoot = await git.OpenAsync(sourceRoot);
        Check(await git.ReadConfigAsync(sourceRoot, "remote.origin.url") == nativeUrl, "真实 Maico 仓库使用原生 Git 远程地址");
        await runner.RunAsync(sourceRoot, ["lfs", "version"]);
        await git.FetchVersionsAsync(sourceRoot, "origin");
        await git.SynchronizeVersionsAsync(sourceRoot, null, "push", fetch: false);
        Check((await git.ReadVersionsAsync(sourceRoot)).Ahead == 0, "通过 GitManager 的推送流程确认 Maico 与远端一致");

        var uri = new Uri(nativeUrl);
        var credential = await runner.RunAsync(sourceRoot, ["-c", "credential.interactive=false", "credential", "fill"],
            standardInput: $"protocol=https\nhost={uri.Authority}\n\n");
        string username = credential.Output.Split('\n').First(line => line.StartsWith("username=", StringComparison.Ordinal))[9..].TrimEnd('\r');
        string clone = CheckDirectories.Create("MaicoLfs");
        await runner.RunAsync(null, ["-c", "http.proxy=" + options.ProxyUrl, "clone", "--no-checkout", "--depth=1", "--filter=blob:none", "--", nativeUrl, clone]);
        await git.ConnectServiceCloudAsync(clone, serviceBase.AbsoluteUri, username, nativeUrl, options.ProxyUrl);
        Check(await git.ReadConfigAsync(clone, "http." + uri.GetLeftPart(UriPartial.Authority) + ".proxy") == options.ProxyUrl,
            "服务云连接为原生 Git 和 LFS 保存代理配置");

        const string path = "MaiCo/upx.exe";
        string pointer = (await runner.RunAsync(clone, ["show", "HEAD:" + path])).Output;
        Check(pointer.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal), "真实构建工具在 Git 中以 LFS 指针保存");
        await runner.RunAsync(clone, ["sparse-checkout", "set", "--no-cone", "--stdin"], standardInput: "/.gitattributes\n/" + path + "\n");
        await runner.RunAsync(clone, ["reset", "--hard", "HEAD"]);
        string sourceHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(sourceRoot, path))));
        string downloadedHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(clone, path))));
        Check(sourceHash == downloadedHash && pointer.Contains("oid sha256:" + downloadedHash, StringComparison.Ordinal),
            "GitManager 拉取的真实 LFS 文件与 Maico 原文件及指针哈希一致");
        await git.SynchronizeVersionsAsync(clone, null, "pull");
        await runner.RunAsync(clone, ["lfs", "push", "--object-id", "origin", downloadedHash]);
        Check((await git.ReadChangesAsync(clone)).Count == 0, "LFS 拉取和上传检查后工作区保持干净");
        Console.WriteLine($"Maico LFS 检查通过，共 {_count} 项；只下载一个真实构建工具，不重复下载全部依赖。");
    }
}
