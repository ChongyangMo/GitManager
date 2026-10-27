using System.IO;
using System.Text;
using GitManager.Models;
using GitManager.Models.RepositoryCopies;
using GitManager.Services.Repositories.Copies;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunLineEndingChecksAsync()
    {
        static RepositoryFileFingerprint Of(byte[] bytes) => RepositoryCopyReader.Fingerprint(new MemoryStream(bytes));
        static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);
        var crlf = Of(Text("第一行\r\n第二行\r\n"));
        var lf = Of(Text("第一行\n第二行\n"));
        Check(crlf.Hash != lf.Hash && crlf.Content == lf.Content && crlf.LineEnding == "CRLF" && lf.LineEnding == "LF", "CRLF 与 LF 内容一致时识别为仅换行符不同");
        Check(Of(Text("a\r\nb\n")).LineEnding == "CRLF/LF 混合" && Of(Text("a")).LineEnding == "无换行", "混合换行和无换行分别标注");
        Check(Of(Text("a\rb")).Content != Of(Text("ab")).Content && Of(Text("a\rb\r")).Content != Of(Text("a\nb\n")).Content, "单独的 CR 保留，不被当作换行吞掉");
        // CR 恰好落在读取缓冲区末尾、LF 在下一块开头，仍应识别为一个 CRLF。
        byte[] boundary = Enumerable.Repeat((byte)'x', 81919).Concat(Text("\r\ny\r\n")).ToArray();
        byte[] boundaryLf = Enumerable.Repeat((byte)'x', 81919).Concat(Text("\ny\n")).ToArray();
        Check(Of(boundary).Content == Of(boundaryLf).Content && Of(boundary).LineEnding == "CRLF", "跨缓冲区的 CRLF 正确归一");
        var binary = Of([0, 1, 13, 10, 2]);
        Check(binary.Content == binary.Hash && binary.LineEnding.Length == 0 && Of([0, 1, 10, 2]).Content != binary.Content, "二进制文件不做换行归一");

        string root = CheckDirectories.Create("Checks");
        string source = Path.Combine(root, "source"), windows = Path.Combine(root, "copy-crlf"), unix = Path.Combine(root, "copy-lf");
        Directory.CreateDirectory(source);
        await Git.InitializeAsync(source);
        await Git.SaveIdentityAsync(source, "GitManager 校验", "gitmanager-checks@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(source, "说明.md"), "第一行\n第二行\n");
        await File.WriteAllTextAsync(Path.Combine(source, "代码.cs"), "class A\n{\n}\n");
        await File.WriteAllBytesAsync(Path.Combine(source, "图片.bin"), [0, 1, 10, 2]);
        await CommitAllAsync(source, "初始提交");
        await Runner.RunAsync(null, ["clone", "-c", "core.autocrlf=true", "--", source, windows]);
        await Runner.RunAsync(null, ["clone", "-c", "core.autocrlf=false", "--", source, unix]);
        Check(File.ReadAllText(Path.Combine(windows, "说明.md")).Contains("\r\n") && !File.ReadAllText(Path.Combine(unix, "说明.md")).Contains('\r'),
            "两份真实副本分别以 CRLF 和 LF 检出");

        var reader = new RepositoryCopyReader(Git);
        var same = await reader.ReadAsync([new RepositoryBookmark(windows), new RepositoryBookmark(unix)]);
        Check(same.Differences.Count == 0 && same.LineEndingDifferences.Select(item => item.Path).Order(StringComparer.Ordinal).SequenceEqual(["代码.cs", "说明.md"])
            && same.LineEndingDifferences.All(item => item.Difference == "1：CRLF；2：LF"), "仅换行符不同的文件不计入差异，并标明各副本的换行方式");
        Check(same.FileStatus == "Git 文件内容相同，另有 2 个文件仅换行符不同（不计入差异）", "状态说明内容相同且列出换行差异数量");

        await File.WriteAllTextAsync(Path.Combine(unix, "说明.md"), "第一行\n改过的第二行\n");
        await File.WriteAllTextAsync(Path.Combine(unix, "新增.txt"), "新文件\n");
        var changed = await reader.ReadAsync([new RepositoryBookmark(windows), new RepositoryBookmark(unix)]);
        Check(changed.Differences.Any(item => item.Path == "说明.md" && item.Difference == "1：内容 1；2：内容 2")
            && changed.Differences.Any(item => item.Path == "新增.txt" && item.Difference == "1：缺失；2：存在")
            && changed.LineEndingDifferences.Select(item => item.Path).SequenceEqual(["代码.cs"]), "真实内容改动和缺失文件仍计入差异");
        Console.WriteLine($"换行符比较检查通过，共 {_count} 项。临时仓库：{root}");
    }
}
