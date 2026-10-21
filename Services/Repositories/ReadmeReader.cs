using System.IO;
using System.Text;

namespace GitManager.Services.Repositories;

public sealed record ReadmeDocument(string Path, string Text)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public static class ReadmeReader
{
    private const int MaxBytes = 512 * 1024;
    private static readonly string[] PreferredExtensions = [".md", ".markdown", "", ".txt", ".rst"];

    // 只读取仓库根目录的说明文件，优先 Markdown，过大的文件截断显示。
    public static async Task<ReadmeDocument?> ReadAsync(string root, CancellationToken cancellationToken = default)
    {
        try { return await ReadCoreAsync(root, cancellationToken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("读取仓库说明失败", ex);
            return null;
        }
    }

    private static async Task<ReadmeDocument?> ReadCoreAsync(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return null;
        string? path = Directory.EnumerateFiles(root, "readme*")
            .Where(file => Path.GetFileNameWithoutExtension(file).Equals("readme", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => Array.IndexOf(PreferredExtensions, Path.GetExtension(file).ToLowerInvariant()) is var index and >= 0 ? index : PreferredExtensions.Length)
            .ThenBy(file => file, StringComparer.Ordinal)
            .FirstOrDefault();
        if (path is null) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        byte[] buffer = new byte[(int)Math.Min(stream.Length, MaxBytes)];
        int read = 0;
        while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), cancellationToken) is var count and > 0) read += count;
        using var reader = new StreamReader(new MemoryStream(buffer, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string text = await reader.ReadToEndAsync(cancellationToken);
        if (stream.Length > MaxBytes) text += "\n\n……（文件过大，仅显示前 512 KB）";
        return new(path, text);
    }
}
