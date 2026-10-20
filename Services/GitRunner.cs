using System.Diagnostics;
using System.IO;
using System.Text;
using GitManager.Models;

namespace GitManager.Services;

public sealed class GitRunner(Func<string> executable, CancellationToken lifetime = default)
{
    public async Task<GitResult> RunAsync(string? directory, IEnumerable<string> arguments, bool allowFailure = false, CancellationToken cancellationToken = default, bool strictUtf8 = false, string? standardInput = null, bool binaryOutput = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lifetime.ThrowIfCancellationRequested();
        string git = executable();
        if (!File.Exists(git)) throw new FileNotFoundException("未找到 Git，请在设置中选择 git.exe。", git);
        var start = new ProcessStartInfo(git)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = binaryOutput ? Encoding.Latin1 : new UTF8Encoding(false, strictUtf8),
            StandardInputEncoding = new UTF8Encoding(false), StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "--literal-pathspecs", "-c", "core.quotepath=false", "-c", "color.ui=false", "-c", "core.pager=cat" }) start.ArgumentList.Add(argument);
        if (directory is not null) { start.ArgumentList.Add("-C"); start.ArgumentList.Add(directory); }
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        process.Start();
        // 退出界面时也立即结束后台进程，不依赖界面线程继续处理异步回调。
        using var cancellation = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        Task input = WriteInputAsync();
        async Task WriteInputAsync()
        {
            try
            {
                if (standardInput is not null) await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            finally { process.StandardInput.Close(); }
        }
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await input.ConfigureAwait(false);
            var result = new GitResult(process.ExitCode, await output.ConfigureAwait(false), AppLog.Clean(await error.ConfigureAwait(false)));
            if (result.ExitCode != 0 && !allowFailure) throw new InvalidOperationException("Git 操作失败：" + (string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim()));
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            try { await Task.WhenAll(output, error, input).ConfigureAwait(false); } catch (OperationCanceledException) { }
            cancellationToken.ThrowIfCancellationRequested();
            lifetime.ThrowIfCancellationRequested();
            throw new TimeoutException("Git 操作等待超时，请检查网络、登录授权或仓库状态。");
        }
    }
}
