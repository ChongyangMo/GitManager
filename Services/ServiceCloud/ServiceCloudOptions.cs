namespace GitManager.Services.ServiceCloud;

public sealed class ServiceCloudOptions
{
    public string BaseUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string ProxyUrl { get; set; } = "";
}

public sealed record CloudRepository(string Name, bool IsPrivate)
{
    public override string ToString() => Name;
}

public sealed record CloudConnection(string BaseUrl, string Username, string RepositoryUrl, string? CloneDirectory, string ProxyUrl = "");
