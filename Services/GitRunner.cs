using System.Diagnostics;
using System.IO;
using System.Text;
using GitManager.Models;

namespace GitManager.Services;

public sealed class GitRunner(Func<string> executable)
{
    public async Task<GitResult> RunAsync(string? directory, IEnumerable<string> arguments, bool allowFailure = false, CancellationToken cancellationToken = default, bool strictUtf8 = false)
    {
        string git = executable();
        if (!File.Exists(git)) throw new FileNotFoundException("未找到 Git，请在设置中选择 git.exe。", git);
        var start = new ProcessStartInfo(git)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false, strictUtf8), StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "--literal-pathspecs", "-c", "core.quotepath=false", "-c", "color.ui=false", "-c", "core.pager=cat" }) start.ArgumentList.Add(argument);
        if (directory is not null) { start.ArgumentList.Add("-C"); start.ArgumentList.Add(directory); }
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        process.Start();
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var result = new GitResult(process.ExitCode, await output, AppLog.Clean(await error));
            if (result.ExitCode != 0 && !allowFailure) throw new InvalidOperationException("Git 操作失败：" + (string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim()));
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
            throw new TimeoutException("Git 操作已取消或等待超时，请检查网络、登录授权或仓库状态。");
        }
    }
}
