using System.IO;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunCommitIdentityChecksAsync(string source)
    {
        string clone = Path.Combine(Path.GetDirectoryName(source)!, "GitManager", "artifacts", "email-check-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        await Git.CloneAsync(source, clone);
        Check((await Git.ReadAsync(clone)).UserEmail == "", "仓库未设置邮箱时默认留空");
        string name = (await Git.ReadAsync(clone)).UserName;
        await Git.SaveIdentityAsync(clone, name, "");
        string path = Path.Combine(clone, ".gitignore");
        await File.AppendAllTextAsync(path, "\n");
        await Git.CommitAsync(clone, (await Git.ReadChangesAsync(clone)).Where(file => file.Path == ".gitignore").ToArray(), "验证空邮箱提交");
        string identity = (await Runner.RunAsync(clone, ["log", "-1", "--format=%ae%x00%ce"])).Output.TrimEnd('\r', '\n');
        Check(identity == "\0", "实际提交的作者邮箱与提交者邮箱均为空");
        Console.WriteLine("提交邮箱检查通过。");
    }
}
