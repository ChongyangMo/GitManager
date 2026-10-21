namespace GitManager.Services.CommitMessages;

public sealed class QwenOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://dashscope.aliyuncs.com/compatible-mode/v1";
    public string Model { get; set; } = "qwen3.8-flash";
    public int MaxOutputTokens { get; set; } = 512;
    public int MaxInputCharacters { get; set; } = 96000;
    public int MaxFileCharacters { get; set; } = 16000;
    public int TimeoutSeconds { get; set; } = 45;
    public QwenTokenPricing? Pricing { get; set; } = new();
    public List<string> PublicGitHosts { get; set; } = ["github.com", "ssh.github.com", "gitlab.com", "gitee.com", "bitbucket.org"];

    public Uri Endpoint()
    {
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || !(uri.Host is "dashscope.aliyuncs.com" or "dashscope-intl.aliyuncs.com"
                || uri.Host.EndsWith(".maas.aliyuncs.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("请填写百炼官方 HTTPS 接口地址。");
        if (string.IsNullOrWhiteSpace(Model) || Model.Any(char.IsControl))
            throw new InvalidOperationException("请填写有效的千问模型名称。");
        if (MaxOutputTokens is < 64 or > 4096 || MaxInputCharacters is < 1024 or > 500000
            || MaxFileCharacters is < 256 or > 100000 || TimeoutSeconds is < 5 or > 180)
            throw new InvalidOperationException("千问配置中的长度或超时设置超出有效范围。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/chat/completions");
    }
}
