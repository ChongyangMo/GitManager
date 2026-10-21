using System.Diagnostics;
using System.IO;
using System.Text;

namespace GitManager.Services.CommitMessages;

internal static class CodexProcess
{
    public static string? FindExecutable(CodexOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
            return Path.IsPathFullyQualified(options.ExecutablePath) && File.Exists(options.ExecutablePath) ? options.ExecutablePath : null;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        try
        {
            return Directory.Exists(root) ? Directory.EnumerateDirectories(root)
                .OrderByDescending(Directory.GetLastWriteTimeUtc).Select(path => Path.Combine(path, "codex.exe"))
                .FirstOrDefault(File.Exists) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(string executable, IEnumerable<string> arguments,
        string directory, string? input, int seconds, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        timeout.Token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        process.Start();
        using var cancellation = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task write = WriteAsync();
        async Task WriteAsync()
        {
            try { if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token).ConfigureAwait(false); }
            finally { process.StandardInput.Close(); }
        }
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await write.ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            try { await Task.WhenAll(output, error, write).ConfigureAwait(false); }
            catch (Exception pending) when (pending is IOException or OperationCanceledException) { }
            token.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new TimeoutException("Codex 总结等待超时。");
            throw new InvalidOperationException("Codex 进程意外退出。");
        }
    }
}
