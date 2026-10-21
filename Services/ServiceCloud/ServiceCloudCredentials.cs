namespace GitManager.Services.ServiceCloud;

public sealed class ServiceCloudCredentials(GitRunner runner)
{
    private static string Input(Uri uri, string username, string? password = null)
    {
        if (username.Any(char.IsControl) || password?.Any(c => c is '\r' or '\n' or '\0') == true)
            throw new InvalidOperationException("账号或密码不能包含换行或控制字符。");
        return $"protocol=https\nhost={uri.Authority}\nusername={username}\n" + (password is null ? "" : $"password={password}\n") + "\n";
    }

    public async Task SaveAsync(string baseUrl, string username, string password)
    {
        var uri = ServiceCloudClient.NormalizeBaseUrl(baseUrl);
        var result = await runner.RunAsync(null, ["-c", "credential.credentialStore=wincredman", "-c", "credential.provider=generic", "credential-manager", "store"],
            allowFailure: true, standardInput: Input(uri, username, password));
        if (result.ExitCode != 0) throw new InvalidOperationException("无法保存服务云凭据，请检查 Git Credential Manager 是否已安装。");
        AppLog.Write("服务云凭据已保存到 Windows 凭据管理器");
    }

    public async Task<string?> ReadPasswordAsync(string baseUrl, string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var uri = ServiceCloudClient.NormalizeBaseUrl(baseUrl);
        var result = await runner.RunAsync(null, ["-c", "credential.credentialStore=wincredman", "-c", "credential.provider=generic",
            "-c", "credential.interactive=false", "credential-manager", "get"], allowFailure: true, standardInput: Input(uri, username));
        if (result.ExitCode != 0) return null;
        return result.Output.Split('\n').FirstOrDefault(line => line.StartsWith("password=", StringComparison.Ordinal))?[9..].TrimEnd('\r');
    }
}
