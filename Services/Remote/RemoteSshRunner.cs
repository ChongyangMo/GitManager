using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GitManager.Models;

namespace GitManager.Services.Remote;

public sealed class RemoteSshRunner(Func<RemoteHostsOptions> optionsAccessor, CancellationToken lifetime = default, Func<GitExecutionOptions>? executionOptions = null)
{
    // 限制同时打开的 SSH 会话数，避免 Mac sshd MaxStartups 限流或本机端口压力。
    private static readonly SemaphoreSlim Slots = new(4);
    private static readonly HashSet<string> NetworkGitCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "fetch", "pull", "push", "ls-remote", "clone", "submodule"
    };
    private static int _forwardPortCounter;

    private sealed record ReverseTunnel(int RemotePort, string LocalHost, int LocalPort, string Scheme);

    public RemoteHostsOptions Options => optionsAccessor();

    public static string QuotePosix(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public async Task<GitResult> RunGitAsync(string root, IEnumerable<string> arguments, bool allowFailure = false,
        CancellationToken cancellationToken = default, bool strictUtf8 = false, string? standardInput = null, bool binaryOutput = false)
    {
        var (alias, remoteDir) = RemoteRepositoryPath.Parse(root);
        var host = ResolveHost(alias);
        string[] args = arguments.ToArray();

        ReverseTunnel? tunnel = null;
        if (IsNetworkGitCommand(args))
            tunnel = await PrepareProxyTunnelAsync(host, args, cancellationToken).ConfigureAwait(false);

        List<string> gitParts =
        [
            QuotePosix(host.EffectiveGitPath),
            "--literal-pathspecs",
            "-c", "core.quotepath=false",
            "-c", "color.ui=false",
            "-c", "core.pager=cat"
        ];
        if (tunnel is not null)
        {
            string proxyUrl = $"{tunnel.Scheme}://127.0.0.1:{tunnel.RemotePort}";
            gitParts.Add("-c");
            gitParts.Add(QuotePosix("http.proxy=" + proxyUrl));
            gitParts.Add("-c");
            gitParts.Add(QuotePosix("https.proxy=" + proxyUrl));
        }
        gitParts.Add("-C");
        gitParts.Add(QuotePosix(remoteDir));

        for (int i = 0; i < args.Length; i++)
        {
            // 若调用方传入了指向本机回环的 -c http.proxy=... 且已建立反向隧道，改由隧道代理接管，避免远端直连自身的错误端口。
            if (tunnel is not null && i + 1 < args.Length && args[i] == "-c"
                && TryExtractLoopbackProxy(args[i + 1], out _, out _, out _))
            {
                i++;
                continue;
            }
            gitParts.Add(QuotePosix(args[i]));
        }

        string remoteCommand = string.Join(' ', gitParts);
        TimeSpan timeout = (executionOptions?.Invoke() ?? new()).TimeoutFor(args);
        var result = await RunCommandCoreAsync(alias, remoteCommand, timeout, allowFailure, cancellationToken, strictUtf8, standardInput, binaryOutput, tunnel).ConfigureAwait(false);
        if (result.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException("远端 Git 操作失败：" + (string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim()));
        return result;
    }

    public Task<GitResult> RunCommandAsync(string alias, string remoteCommand, TimeSpan? timeout = null, bool allowFailure = false,
        CancellationToken cancellationToken = default, bool strictUtf8 = false, string? standardInput = null, bool binaryOutput = false)
        => RunCommandCoreAsync(alias, remoteCommand, timeout, allowFailure, cancellationToken, strictUtf8, standardInput, binaryOutput, null);

    private async Task<GitResult> RunCommandCoreAsync(string alias, string remoteCommand, TimeSpan? timeout, bool allowFailure,
        CancellationToken cancellationToken, bool strictUtf8, string? standardInput, bool binaryOutput, ReverseTunnel? tunnel)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lifetime.ThrowIfCancellationRequested();
        var options = optionsAccessor();
        string ssh = string.IsNullOrWhiteSpace(options.SshExecutablePath) ? @"C:\Windows\System32\OpenSSH\ssh.exe" : options.SshExecutablePath;
        if (!File.Exists(ssh)) throw new FileNotFoundException("未找到 OpenSSH 客户端（ssh.exe）。", ssh);
        var host = ResolveHost(alias);

        // 优先使用局域网发现的实时地址；若连接失败（255）且用了缓存地址，失效缓存并按默认配置重试一次。
        var cached = RemoteEndpointDiscovery.TryGetCachedEndpoint(alias);
        var result = await RunWithSlotAsync(ssh, options, host, cached, remoteCommand, timeout ?? TimeSpan.FromSeconds(60),
            cancellationToken, strictUtf8, standardInput, binaryOutput, tunnel).ConfigureAwait(false);

        if (result.ExitCode == 255 && cached is not null)
        {
            RemoteEndpointDiscovery.InvalidateEndpoint(alias);
            AppLog.Write($"远端主机 {host.EffectiveLabel} 缓存地址 {cached.Address} 连接失败，改用默认 SSH 配置重试");
            result = await RunWithSlotAsync(ssh, options, host, null, remoteCommand, timeout ?? TimeSpan.FromSeconds(60),
                cancellationToken, strictUtf8, standardInput, binaryOutput, tunnel).ConfigureAwait(false);
        }

        if (result.ExitCode == 255 && !allowFailure)
        {
            string reason = string.IsNullOrWhiteSpace(result.Error) ? "无法通过局域网 SSH 连接到远端主机" : result.Error.Trim();
            throw new IOException($"无法连接到 {host.EffectiveLabel}：{reason}");
        }
        if (result.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException("远端命令执行失败：" + (string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim()));
        return result;
    }

    private static bool IsNetworkGitCommand(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "-c" || arg == "-C" || arg == "--git-dir" || arg == "--work-tree") { i++; continue; }
            if (arg.StartsWith('-')) continue;
            return NetworkGitCommands.Contains(arg);
        }
        return false;
    }

    private static async Task<ReverseTunnel?> PrepareProxyTunnelAsync(RemoteHostProfile host, string[] args, CancellationToken token)
    {
        string? candidate = null;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "-c" && TryExtractLoopbackProxy(args[i + 1], out string explicitScheme, out string explicitHost, out int explicitPort))
            {
                candidate = $"{explicitScheme}://{explicitHost}:{explicitPort}";
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(candidate) && !string.IsNullOrWhiteSpace(host.ForwardProxyUrl))
            candidate = host.ForwardProxyUrl.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            try
            {
                var probe = new Uri("https://github.com/");
                var systemProxy = WebRequest.DefaultWebProxy?.GetProxy(probe);
                if (systemProxy is not null && systemProxy != probe)
                    candidate = systemProxy.AbsoluteUri;
            }
            catch { }
        }
        if (string.IsNullOrWhiteSpace(candidate) || !TryParseLoopbackProxyUri(candidate, out string scheme, out string localHost, out int localPort))
            return null;
        if (!await IsLocalPortOpenAsync(localHost, localPort, token).ConfigureAwait(false))
            return null;
        int offset = (Interlocked.Increment(ref _forwardPortCounter) & 0x7FFFFFFF) % 50;
        int remotePort = 18950 + offset;
        return new(remotePort, localHost, localPort, scheme);
    }

    private static bool TryExtractLoopbackProxy(string configArg, out string scheme, out string host, out int port)
    {
        scheme = ""; host = ""; port = 0;
        if (configArg.StartsWith("http.proxy=", StringComparison.OrdinalIgnoreCase))
            return TryParseLoopbackProxyUri(configArg[11..], out scheme, out host, out port);
        if (configArg.StartsWith("https.proxy=", StringComparison.OrdinalIgnoreCase))
            return TryParseLoopbackProxyUri(configArg[12..], out scheme, out host, out port);
        return false;
    }

    private static bool TryParseLoopbackProxyUri(string raw, out string scheme, out string host, out int port)
    {
        scheme = ""; host = ""; port = 0;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "socks5" or "socks5h")) return false;
        if (uri.Port is <= 0 or > 65535) return false;
        bool loopback = uri.IsLoopback || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal) || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (!loopback) return false;
        scheme = uri.Scheme;
        host = "127.0.0.1";
        port = uri.Port;
        return true;
    }

    private static async Task<bool> IsLocalPortOpenAsync(string host, int port, CancellationToken token)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(250);
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch { return false; }
    }

    private RemoteHostProfile ResolveHost(string alias) =>
        optionsAccessor().Find(alias) ?? new RemoteHostProfile { Alias = alias, Label = alias, SshHost = alias };

    private async Task<GitResult> RunWithSlotAsync(string ssh, RemoteHostsOptions options, RemoteHostProfile host,
        RemoteEndpointRecord? discovered, string remoteCommand, TimeSpan timeoutDuration,
        CancellationToken cancellationToken, bool strictUtf8, string? standardInput, bool binaryOutput, ReverseTunnel? tunnel)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        await Slots.WaitAsync(waiting.Token).ConfigureAwait(false);
        try
        {
            var start = new ProcessStartInfo(ssh)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = binaryOutput ? Encoding.Latin1 : new UTF8Encoding(false, strictUtf8),
                StandardInputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = Encoding.UTF8
            };
            int connectTimeout = Math.Clamp(options.ConnectTimeoutSeconds, 2, 30);
            start.ArgumentList.Add("-o"); start.ArgumentList.Add("BatchMode=yes");
            start.ArgumentList.Add("-o"); start.ArgumentList.Add($"ConnectTimeout={connectTimeout}");
            start.ArgumentList.Add("-o"); start.ArgumentList.Add("ServerAliveInterval=15");
            start.ArgumentList.Add("-o"); start.ArgumentList.Add("ServerAliveCountMax=3");
            start.ArgumentList.Add("-o"); start.ArgumentList.Add("StrictHostKeyChecking=accept-new");
            start.ArgumentList.Add("-T");
            if (tunnel is not null)
            {
                start.ArgumentList.Add("-o"); start.ArgumentList.Add("ExitOnForwardFailure=yes");
                start.ArgumentList.Add("-R"); start.ArgumentList.Add($"127.0.0.1:{tunnel.RemotePort}:{tunnel.LocalHost}:{tunnel.LocalPort}");
            }
            if (!string.IsNullOrWhiteSpace(host.HostKeyAlias))
            {
                start.ArgumentList.Add("-o"); start.ArgumentList.Add($"HostKeyAlias={host.HostKeyAlias.Trim()}");
            }
            if (discovered is not null)
            {
                start.ArgumentList.Add("-o"); start.ArgumentList.Add($"HostName={discovered.Address}");
                if (discovered.SshPort is > 0 and <= 65535)
                {
                    start.ArgumentList.Add("-p"); start.ArgumentList.Add(discovered.SshPort.ToString());
                }
            }
            else if (host.Port is > 0 and <= 65535 && host.Port != 22)
            {
                start.ArgumentList.Add("-p"); start.ArgumentList.Add(host.Port.ToString());
            }
            if (!string.IsNullOrWhiteSpace(host.User))
            {
                start.ArgumentList.Add("-l"); start.ArgumentList.Add(host.User.Trim());
            }
            if (!string.IsNullOrWhiteSpace(host.IdentityFile))
            {
                start.ArgumentList.Add("-i"); start.ArgumentList.Add(host.IdentityFile.Trim());
            }
            start.ArgumentList.Add(host.EffectiveSshHost);
            start.ArgumentList.Add(remoteCommand);

            using var process = new Process { StartInfo = start };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
            timeout.CancelAfter(timeoutDuration);
            process.Start();
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
                    if (standardInput is not null)
                        await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeout.Token).ConfigureAwait(false);
                }
                finally { process.StandardInput.Close(); }
            }
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await input.ConfigureAwait(false);
                return new(process.ExitCode, await output.ConfigureAwait(false), AppLog.Clean(await error.ConfigureAwait(false)));
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                try { await Task.WhenAll(output, error, input).ConfigureAwait(false); } catch (OperationCanceledException) { }
                cancellationToken.ThrowIfCancellationRequested();
                lifetime.ThrowIfCancellationRequested();
                throw new TimeoutException($"远端主机 {host.EffectiveLabel} 操作超时，请检查局域网连接或 SSH 状态。");
            }
        }
        finally { Slots.Release(); }
    }
}
