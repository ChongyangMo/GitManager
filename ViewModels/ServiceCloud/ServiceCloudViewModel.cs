using GitManager.Services.ServiceCloud;
using GitManager.Services;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    public Task ConnectServiceCloudAsync(CloudConnection connection) => ExecuteAsync("正在同步服务云", async () =>
    {
        if (connection.CloneDirectory is { } directory)
            await OpenCoreAsync(await Git.CloneAsync(connection.RepositoryUrl, directory, connection.ProxyUrl, connection.Username));
        if (!HasRepository) throw new InvalidOperationException("请先选择本地仓库。");
        try
        {
            SelectedRemoteBranch = await Git.ConnectServiceCloudAsync(Root, connection.BaseUrl, connection.Username, connection.RepositoryUrl, connection.ProxyUrl);
            await RefreshVersionsCoreAsync();
            var outcome = connection.CloneDirectory is null
                ? await Git.SynchronizeVersionsAsync(Root, SelectedRemoteBranch, "sync", fetch: false)
                : new Models.SyncOutcome("服务云仓库已克隆");
            Status = outcome.Message;
            AppLog.Write("服务云仓库同步完成");
        }
        finally { _workspaceCache.Remove(Root); await RefreshCoreAsync(); }
    });
}
