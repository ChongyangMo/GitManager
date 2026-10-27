using System.IO;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunCommitIdentityChecksAsync(string source)
    {
        string clone = CheckDirectories.Create("email-check");
        await Git.CloneAsync(source, clone);
        Check((await Git.ReadAsync(clone)).UserEmail == ChecksCommitEmail, "界面显示统一提交邮箱");
        string name = (await Git.ReadAsync(clone)).UserName;
        await Git.SaveIdentityAsync(clone, name, "other-local@example.invalid");
        string path = Path.Combine(clone, ".gitignore");
        await File.AppendAllTextAsync(path, "\n");
        await Git.CommitAsync(clone, (await Git.ReadChangesAsync(clone)).Where(file => file.Path == ".gitignore").ToArray(), "验证统一邮箱提交");
        string identity = (await Runner.RunAsync(clone, ["log", "-1", "--format=%ae%x00%ce"])).Output.TrimEnd('\r', '\n');
        Check(identity == ChecksCommitEmail + "\0" + ChecksCommitEmail, "仓库本地邮箱不同时，作者与提交者仍使用统一邮箱");

        // 未设置统一邮箱时，从真实 GitHub 登录账号取得关联的 noreply 邮箱。
        var live = new GitManager.Services.GitRepositoryService(Runner);
        string email = await live.ResolveCommitEmailAsync(clone);
        Check(email.EndsWith("@users.noreply.github.com", StringComparison.Ordinal), "从 GitHub 账号取得关联邮箱");
        Console.WriteLine("提交邮箱检查通过：" + email);
    }
}
