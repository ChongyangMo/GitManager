namespace GitManager.Services.Repositories;

public sealed class AutoMergeOptions
{
    // 默认合并远程更新，手动关闭后按仓库保存。
    public List<string> DisabledRepositories { get; set; } = [];
}
