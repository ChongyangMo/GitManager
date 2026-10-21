namespace GitManager.Services.Repositories;

public sealed class AutoUploadOptions
{
    // 开启自动上传的仓库路径；默认不开启任何仓库。
    public List<string> Repositories { get; set; } = [];
    // 改动文件停止修改达到此时长后才提交，避免把编辑中途的内容提交上去。
    public int QuietSeconds { get; set; } = 60;
    public int CheckIntervalSeconds { get; set; } = 30;
    // 提交或推送失败后，内容不变时等待此时长再重试，避免反复调用模型和网络。
    public int RetryMinutes { get; set; } = 10;
}
