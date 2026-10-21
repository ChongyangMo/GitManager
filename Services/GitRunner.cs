using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GitManager.Models;

namespace GitManager.Services;

public sealed class GitRunner(Func<string> executable, CancellationToken lifetime = default)
{
    // 进程初始化失败（STATUS_DLL_INIT_FAILED），常见于同时启动过多进程，此时 Git 尚未执行任何操作。
    private const int DllInitFailed = unchecked((int)0xC0000142);
    private const uint FailCriticalErrors = 0x0001, NoGpFaultErrorBox = 0x0002;
    // 一次刷新会并发读取多项状态，每个 git 还会带起包装进程和控制台宿主，限制总数以免瞬间耗尽系统资源。
    private static readonly SemaphoreSlim Slots = new(6);

    static GitRunner()
    {
        // 子进程继承错误模式：启动失败只返回退出码，不再弹出系统错误对话框。
        if (OperatingSystem.IsWindows()) SetErrorMode(GetErrorMode() | FailCriticalErrors | NoGpFaultErrorBox);
    }

    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll")] private static extern uint GetErrorMode();

    public async Task<GitResult> RunAsync(string? directory, IEnumerable<string> arguments, bool allowFailure = false, CancellationToken cancellationToken = default, bool strictUtf8 = false, string? standardInput = null, bool binaryOutput = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lifetime.ThrowIfCancellationRequested();
        string git = executable();
        if (!File.Exists(git)) throw new FileNotFoundException("未找到 Git，请在设置中选择 git.exe。", git);
        string[] args = arguments.ToArray();
        for (int attempt = 1; ; attempt++)
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
            await Slots.WaitAsync(waiting.Token).ConfigureAwait(false);
            GitResult result;
            try { result = await RunOnceAsync(git, directory, args, cancellationToken, strictUtf8, standardInput, binaryOutput).ConfigureAwait(false); }
            finally { Slots.Release(); }
            if (result.ExitCode == DllInitFailed && attempt < 3)
            {
                AppLog.Write($"Git 进程启动失败（0xC0000142），第 {attempt} 次重试");
                await Task.Delay(300 * attempt, waiting.Token).ConfigureAwait(false);
                continue;
            }
            if (result.ExitCode == DllInitFailed) throw new InvalidOperationException("Git 进程无法启动（0xC0000142），系统资源可能暂时不足，请稍后重试。");
            if (result.ExitCode != 0 && !allowFailure) throw new InvalidOperationException("Git 操作失败：" + (string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim()));
            return result;
        }
    }

    private async Task<GitResult> RunOnceAsync(string git, string? directory, string[] arguments, CancellationToken cancellationToken, bool strictUtf8, string? standardInput, bool binaryOutput)
    {
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
            return new GitResult(process.ExitCode, await output.ConfigureAwait(false), AppLog.Clean(await error.ConfigureAwait(false)));
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
