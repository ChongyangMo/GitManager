using System.Text;
using System.Text.Json;
using GitManager.Services;
using GitManager.Services.LuxAi;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunLuxAiRecoveryAsync()
    {
        var options = new SettingsStore().Value.LuxAi;
        var account = LuxAiAccountCredentials.Read(options)
            ?? throw new InvalidOperationException("请在内置窗口用真实账号密码登录，并勾选断线自动登录。");
        // 使用真实已保存账号核对网页登录格式，不输出账号密码或请求体。
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            code = Convert.ToBase64String(Encoding.UTF8.GetBytes(account.Code)),
            pwd = Convert.ToBase64String(Encoding.UTF8.GetBytes(account.Password)),
            isEncrypted = true, companyCode = account.CompanyCode
        }));
        var decoded = LuxAiAccountCredentials.ReadLoginRequest(body.RootElement);
        Check(decoded?.Code == account.Code && decoded.Password == account.Password,
            "真实账号密码按网页登录格式完整读取");
        Check(account.ToString().Contains("已隐藏") && !account.ToString().Contains(account.Password), "账号对象不会显示密码");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var client = new LuxAiClient(options);
        var restored = await client.ReloginAsync(timeout.Token);
        await client.ValidateSessionAsync(restored, timeout.Token);
        Check(restored.EmployeeCode == account.EmployeeCode && restored.RenewedAt is not null,
            "真实账号密码登录取得正确账号的新会话，并通过实际接口验证");
        Check(LuxAiCredentials.Read(options)?.AiToken == restored.AiToken, "重新登录的会话已保存至 Windows 凭据管理器");
        using var nextClient = new LuxAiClient(options);
        Check((await nextClient.EnsureSessionAsync(timeout.Token)).AiToken == restored.AiToken,
            "新客户端直接复用已恢复会话");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool stopped = false;
        try { await client.ReloginAsync(canceled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped && LuxAiCredentials.Read(options)?.AiToken == restored.AiToken, "取消自动登录不会覆盖当前会话");
    }
}
