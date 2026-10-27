using System.IO;

namespace GitManager.Checks;

// 本次运行的所有临时目录都放在同一个根目录下，检查结束后整体删除；截图和报告不放这里。
internal static class CheckDirectories
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "GitManagerChecks", "run-" + Guid.NewGuid().ToString("N"));

    public static string Create(string name) => Path.Combine(Root, name, Guid.NewGuid().ToString("N"));

    public static void Clean()
    {
        if (!Directory.Exists(Root)) return;
        try
        {
            // Git 对象文件是只读的，先去掉只读属性才能删除。
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"临时目录未能完全删除：{Root}，{ex.Message}");
        }
    }
}
