using System.IO;
using GitManager.Models;
using GitManager.Services;
using GitManager.Services.CommitMessages;
using GitManager.Services.Remote;
using GitManager.Services.Repositories;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunRemoteMacChecksAsync()
    {
        // 1. 路径解析与转义单元测试
        Check(RemoteRepositoryPath.IsRemote("ssh://macmini/Users/chongyang/proj"), "识别 ssh:// 远端路径");
        Check(!RemoteRepositoryPath.IsRemote(@"C:\Users\Administrator\Desktop\Homenas"), "本地路径不误判为远端");
        Check(RemoteRepositoryPath.Normalize("ssh://macmini/Users/chongyang//proj/") == "ssh://macmini/Users/chongyang/proj", "远端路径规范化");
        Check(RemoteRepositoryPath.FileName("ssh://macmini/Users/chongyang/proj") == "proj", "提取远端仓库目录名");
        Check(RemoteSshRunner.QuotePosix("a'b c") == "'a'\\''b c'", "POSIX 单引号转义");

        var options = new RemoteHostsOptions
        {
            Enabled = true,
            DiscoveryPort = 49527,
            Hosts =
            [
                new RemoteHostProfile
                {
                    Alias = "macmini",
                    Label = "Mac",
                    SshHost = "macmini",
                    HostKeyAlias = "192.168.50.2",
                    User = "chongyang",
                    FallbackAddresses = ["192.168.50.2", "chongyang2.local"],
                    Roots = ["~/iPhone_Project", "~/Mac_Project", "~/Desktop", "~/Documents"],
                    MaxDepth = 4,
                    GitPath = "/usr/bin/git",
                    ForwardProxyUrl = "http://127.0.0.1:7897",
                    Enabled = true
                }
            ]
        };

        var ssh = new RemoteSshRunner(() => options);
        var runner = new GitRunner(() => @"C:\Program Files\Git\cmd\git.exe", remote: ssh);
        var git = new GitRepositoryService(runner, new() { CommitEmail = ChecksCommitEmail });

        // 2. UDP 局域网发现 Mac 后台服务
        var announced = await RemoteEndpointDiscovery.ProbeAsync(options, forceRefresh: true);
        Check(announced.Any(host => string.Equals(host.Alias, "macmini", StringComparison.OrdinalIgnoreCase)), "UDP 局域网发现 Mac 后台服务");
        var discovery = await new RemoteRepositoryDiscovery(git).DiscoverAsync(options, []);
        Check(discovery.Repositories.Count >= 1, "自动发现至少 1 个 Mac 仓库");
        Console.WriteLine("发现的 Mac 仓库：" + string.Join(", ", discovery.Repositories));

        // 3. 只读检验 Mac 上已有的真实仓库
        string existingMacRepo = discovery.Repositories[0];
        string openedExisting = await git.OpenAsync(existingMacRepo);
        Check(openedExisting == existingMacRepo, "打开 Mac 真实仓库");
        var existingSnap = await git.ReadAsync(existingMacRepo);
        Check(!string.IsNullOrWhiteSpace(existingSnap.Branch), "读取 Mac 真实仓库分支：" + existingSnap.Branch);
        var existingVersions = await git.ReadVersionsAsync(existingMacRepo, null, existingSnap);
        Check(existingVersions.LocalBranch == existingSnap.Branch, "读取 Mac 真实仓库版本信息");
        await git.FetchVersionsAsync(existingMacRepo, existingVersions.RemoteName);
        Check(true, "经 SSH 反向隧道代理成功 fetch Mac 真实 GitHub 仓库");
        var ghAddress = Services.GitHub.GitHubRepositoryAddress.Parse(existingVersions.RemoteUrl);
        Check(ghAddress is not null, "解析 Mac 仓库 GitHub 地址");
        var visibility = await git.ReadGitHubVisibilityAsync(existingMacRepo, ghAddress!);
        Check(visibility is not null, "读取 Mac 仓库 GitHub 可见性：" + visibility?.Label);
        var (_, remoteGroup) = await RepositoryRemoteGroup.ReadAsync(git, existingMacRepo, "", "");
        Console.WriteLine($"真实仓库状态：分支={existingSnap.Branch}，提交数={existingSnap.History.Count}，分组={remoteGroup}，可见性={visibility?.Label}");

        // 4. 在 Mac /tmp 建临时仓库，全流程读写验证后彻底清理
        string tempDir = "/tmp/gitmanager-remote-check-" + Guid.NewGuid().ToString("N")[..10];
        string tempRoot = RemoteRepositoryPath.Format("macmini", tempDir);
        try
        {
            await ssh.RunCommandAsync("macmini",
                $"mkdir -p {RemoteSshRunner.QuotePosix(tempDir)} && /usr/bin/git -C {RemoteSshRunner.QuotePosix(tempDir)} init -b main && " +
                $"printf '# 远端测试仓库\\n' > {RemoteSshRunner.QuotePosix(tempDir + "/README.md")} && " +
                $"printf '第一行\\n第二行\\n' > {RemoteSshRunner.QuotePosix(tempDir + "/中文 文件.txt")}");

            string openedTemp = await git.OpenAsync(tempRoot);
            Check(openedTemp.EndsWith("/" + Path.GetFileName(tempDir), StringComparison.Ordinal), "打开 Mac 临时仓库（兼容 /tmp 符号链接）");
            tempRoot = openedTemp;
            await git.SaveIdentityAsync(tempRoot, "GitManager 远端校验", ChecksCommitEmail);

            var readme = await ReadmeReader.ReadAsync(tempRoot, git: git);
            Check(readme is not null && readme.Text.Contains("远端测试仓库"), "读取远端 README");

            var changes = await git.ReadChangesAsync(tempRoot);
            Check(changes.Count == 2, "识别远端未跟踪文件（含中文空格文件名）");

            var preview = await git.CompareWorkingAsync(tempRoot, changes.First(f => f.Path == "中文 文件.txt"), hasHead: false);
            Check(preview.After.Contains("第一行"), "预览远端新增文本文件");

            var content = await new CommitContentReader(git).ReadAsync(tempRoot, changes, new QwenOptions(), CancellationToken.None);
            Check(content.Text.Contains("第一行") && !string.IsNullOrWhiteSpace(content.Fingerprint), "批量读取远端提交内容与指纹");

            await git.CommitAsync(tempRoot, changes, "初始化远端测试提交");
            var snap1 = await git.ReadAsync(tempRoot);
            Check(snap1.HasHead && snap1.History.Count == 1 && snap1.Changes.Count == 0, "远端提交成功且工作区干净");

            await ssh.RunCommandAsync("macmini",
                $"printf '第一行\\n修改后的第二行\\n' > {RemoteSshRunner.QuotePosix(tempDir + "/中文 文件.txt")}");
            var changes2 = await git.ReadChangesAsync(tempRoot);
            Check(changes2.Count == 1, "检测远端已跟踪文件修改");

            var diff2 = await git.CompareWorkingAsync(tempRoot, changes2[0], hasHead: true);
            Check(diff2.Before.Contains("第二行") && diff2.After.Contains("修改后的第二行") && diff2.Patch.Contains("-第二行"), "生成远端工作区差异");

            var settled = await RepositoryAutoUpload.ReadSettledChangesAsync(git, tempRoot, TimeSpan.Zero, CancellationToken.None);
            Check(settled is { Count: 1 }, "远端自动上传静默时间判断");

            await git.CommitAsync(tempRoot, changes2, "更新中文文件");
            var snap2 = await git.ReadAsync(tempRoot);
            Check(snap2.History.Count == 2, "二次远端提交成功");

            var historyFiles = await git.ReadHistoryFilesAsync(tempRoot, snap2.History[0]);
            var historyPreview = await git.ReadHistoryPreviewAsync(tempRoot, snap2.History[0], historyFiles[0]);
            Check(historyPreview.Comparison.After.Contains("修改后的第二行"), "读取远端历史版本差异");
        }
        finally
        {
            await ssh.RunCommandAsync("macmini", $"rm -rf {RemoteSshRunner.QuotePosix(tempDir)}", allowFailure: true);
        }

        Console.WriteLine($"远端 Mac 全链路检查通过，共 {_count} 项。");
    }
}
