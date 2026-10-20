namespace GitManager.Services.Repositories;

public sealed class RepositoryDiscoveryOptions
{
    public bool Enabled { get; set; } = true;
    public List<string> Roots { get; set; } = [];
    public int MaxDepth { get; set; } = 6;
    public int MaxDirectories { get; set; } = 20000;
}
