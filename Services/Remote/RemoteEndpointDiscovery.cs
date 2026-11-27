using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitManager.Services.Remote;

public sealed record DiscoveredRemoteHost(
    string Alias,
    string Label,
    string EndpointAddress,
    string Hostname,
    string User,
    int SshPort,
    string GitPath,
    IReadOnlyList<string> Repositories);

public sealed record RemoteEndpointRecord(string Address, int SshPort, DateTimeOffset DiscoveredAt);

public static class RemoteEndpointDiscovery
{
    private static readonly ConcurrentDictionary<string, RemoteEndpointRecord> Endpoints = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan EndpointTtl = TimeSpan.FromMinutes(10);

    public static RemoteEndpointRecord? TryGetCachedEndpoint(string alias)
    {
        if (!Endpoints.TryGetValue(alias, out var record)) return null;
        if (DateTimeOffset.UtcNow - record.DiscoveredAt > EndpointTtl)
        {
            Endpoints.TryRemove(alias, out _);
            return null;
        }
        return record;
    }

    public static void RememberEndpoint(string alias, string address, int sshPort)
    {
        if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(address)) return;
        Endpoints[alias.Trim()] = new(address.Trim(), sshPort > 0 ? sshPort : 22, DateTimeOffset.UtcNow);
    }

    public static void InvalidateEndpoint(string alias)
    {
        if (!string.IsNullOrWhiteSpace(alias)) Endpoints.TryRemove(alias.Trim(), out _);
    }

    public static async Task<IReadOnlyList<DiscoveredRemoteHost>> ProbeAsync(RemoteHostsOptions options, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || options.DiscoveryPort is <= 0 or > 65535) return [];
        string nonce = Guid.NewGuid().ToString("N")[..12];
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            type = "gitmanager-discover",
            version = 1,
            nonce,
            refresh = forceRefresh
        }));

        var results = new Dictionary<string, DiscoveredRemoteHost>(StringComparer.OrdinalIgnoreCase);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        foreach (var target in BuildProbeTargets(options))
        {
            try { await udp.SendAsync(payload, target, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
        while (!timeout.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }

            var host = TryParseAnnouncement(packet.Buffer, packet.RemoteEndPoint.Address.ToString(), nonce);
            if (host is null) continue;
            RememberEndpoint(host.Alias, host.EndpointAddress, host.SshPort);
            results[host.Alias] = host;
        }
        return results.Values.ToArray();
    }

    private static IReadOnlyList<IPEndPoint> BuildProbeTargets(RemoteHostsOptions options)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var endpoints = new List<IPEndPoint>();
        void Add(IPAddress address)
        {
            string key = address.ToString();
            if (targets.Add(key)) endpoints.Add(new(address, options.DiscoveryPort));
        }

        Add(IPAddress.Broadcast);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null) continue;
                    byte[] ip = unicast.Address.GetAddressBytes();
                    byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                    if (ip.Length != 4 || mask.Length != 4) continue;
                    byte[] broadcast = new byte[4];
                    for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException) { }

        foreach (var host in options.Hosts.Where(item => item.Enabled))
        {
            if (TryGetCachedEndpoint(host.Alias) is { } cached && IPAddress.TryParse(cached.Address, out var cachedIp))
                Add(cachedIp);
            foreach (string raw in host.FallbackAddresses)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (IPAddress.TryParse(raw.Trim(), out var ip)) Add(ip);
                else
                {
                    try
                    {
                        foreach (var resolved in Dns.GetHostAddresses(raw.Trim()).Where(addr => addr.AddressFamily == AddressFamily.InterNetwork))
                            Add(resolved);
                    }
                    catch (SocketException) { }
                }
            }
        }
        return endpoints;
    }

    private static DiscoveredRemoteHost? TryParseAnnouncement(byte[] buffer, string senderIp, string expectedNonce)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<AnnouncementDto>(buffer);
            if (dto is null || !string.Equals(dto.Type, "gitmanager-announce", StringComparison.Ordinal)) return null;
            if (!string.IsNullOrEmpty(dto.Nonce) && !string.Equals(dto.Nonce, expectedNonce, StringComparison.Ordinal)) return null;
            if (string.IsNullOrWhiteSpace(dto.Alias)) return null;
            string[] repos = (dto.Repositories ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path) && path.Trim().StartsWith('/'))
                .Select(RemoteRepositoryPath.NormalizePosix)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new(
                dto.Alias.Trim(),
                string.IsNullOrWhiteSpace(dto.Label) ? dto.Alias.Trim() : dto.Label.Trim(),
                senderIp,
                dto.Hostname?.Trim() ?? "",
                dto.User?.Trim() ?? "",
                dto.SshPort is > 0 and <= 65535 ? dto.SshPort : 22,
                string.IsNullOrWhiteSpace(dto.GitPath) ? "/usr/bin/git" : dto.GitPath.Trim(),
                repos);
        }
        catch (JsonException) { return null; }
    }

    private sealed class AnnouncementDto
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("nonce")] public string? Nonce { get; set; }
        [JsonPropertyName("alias")] public string? Alias { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("hostname")] public string? Hostname { get; set; }
        [JsonPropertyName("user")] public string? User { get; set; }
        [JsonPropertyName("sshPort")] public int SshPort { get; set; }
        [JsonPropertyName("gitPath")] public string? GitPath { get; set; }
        [JsonPropertyName("repositories")] public List<string>? Repositories { get; set; }
    }
}
