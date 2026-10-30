namespace GitManager.Services.ServiceCloud;

public static class ServiceCloudGitAddress
{
    public static string RepositoryUrl(Uri serviceBase, string name, string gitBaseUrl = "")
    {
        string[] parts = ServiceCloudClient.NormalizeRepositoryName(name).Split('/');
        string path = string.Join('/', parts.Select(Uri.EscapeDataString)) + ".git";
        // 原生 Git 地址使用“所有者/仓库”两级名称，多级历史目录继续使用旧入口。
        return parts.Length == 2 && !string.IsNullOrWhiteSpace(gitBaseUrl)
            ? new Uri(ServiceCloudClient.NormalizeBaseUrl(gitBaseUrl), path).AbsoluteUri
            : new Uri(serviceBase, "git/" + path).AbsoluteUri;
    }

    public static bool BelongsTo(Uri serviceBase, Uri repository, ServiceCloudOptions configured)
    {
        if (repository.Scheme != "https" || repository.UserInfo.Length > 0 || repository.Query.Length > 0 || repository.Fragment.Length > 0
            || !repository.AbsolutePath.EndsWith(".git", StringComparison.Ordinal)) return false;
        if (serviceBase.IsBaseOf(repository) && repository.AbsolutePath.StartsWith(serviceBase.AbsolutePath + "git/", StringComparison.Ordinal)) return true;
        if (string.IsNullOrWhiteSpace(configured.GitBaseUrl) || string.IsNullOrWhiteSpace(configured.BaseUrl)
            || ServiceCloudClient.NormalizeBaseUrl(configured.BaseUrl) != serviceBase) return false;
        var nativeBase = ServiceCloudClient.NormalizeBaseUrl(configured.GitBaseUrl);
        return nativeBase.IsBaseOf(repository) && nativeBase.MakeRelativeUri(repository).ToString().Split('/').Length == 2;
    }
}
