namespace GitManager.Services.Remote;

public sealed class RemoteHostsOptions
{
    public bool Enabled { get; set; } = true;
    public string SshExecutablePath { get; set; } = @"C:\Windows\System32\OpenSSH\ssh.exe";
    public int DiscoveryPort { get; set; } = 49527;
    public int ConnectTimeoutSeconds { get; set; } = 8;
    public List<RemoteHostProfile> Hosts { get; set; } = [];

    public RemoteHostProfile? Find(string alias) =>
        Hosts.FirstOrDefault(host => string.Equals(host.Alias, alias, StringComparison.OrdinalIgnoreCase));
}

public sealed class RemoteHostProfile
{
    public string Alias { get; set; } = "";
    public string Label { get; set; } = "Mac";
    public string SshHost { get; set; } = "";
    public string HostKeyAlias { get; set; } = "";
    public string User { get; set; } = "";
    public int Port { get; set; } = 22;
    public string IdentityFile { get; set; } = "";
    public List<string> FallbackAddresses { get; set; } = [];
    public List<string> Roots { get; set; } = [];
    public int MaxDepth { get; set; } = 4;
    public string GitPath { get; set; } = "/usr/bin/git";
    public string ForwardProxyUrl { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public string EffectiveLabel => string.IsNullOrWhiteSpace(Label) ? Alias : Label.Trim();
    public string EffectiveSshHost => string.IsNullOrWhiteSpace(SshHost) ? Alias.Trim() : SshHost.Trim();
    public string EffectiveGitPath => string.IsNullOrWhiteSpace(GitPath) ? "/usr/bin/git" : GitPath.Trim();
}
