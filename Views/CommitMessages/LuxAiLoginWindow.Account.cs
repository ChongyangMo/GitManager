using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GitManager.Services;
using GitManager.Services.LuxAi;
using Microsoft.Web.WebView2.Core;

namespace GitManager.Views.CommitMessages;

public sealed partial class LuxAiLoginWindow
{
    private readonly CheckBox _rememberPassword = new() { Content = "保存账号密码，断线后自动登录", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };

    private async Task<LuxAiAccount?> ReadLoginAccountAsync(CoreWebView2WebResourceRequest request)
    {
        try
        {
            // 请求已发送，只读取本应用登录接口的请求体，不读取外部浏览器资料。
            if (request.Method != "POST" || request.Content is not { } stream) return null;
            using var reader = new StreamReader(stream);
            var buffer = new char[16385];
            int count = await reader.ReadBlockAsync(buffer.AsMemory(), _lifetime.Token);
            if (count > 16384) return null;
            using var body = JsonDocument.Parse(new string(buffer, 0, count));
            return LuxAiAccountCredentials.ReadLoginRequest(body.RootElement);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception) { AppLog.Write("未能读取本次集团 AI 账号登录信息，请重新使用账号密码登录"); return null; }
    }

    private async Task SaveLoginAccountAsync(LuxAiSession session, LuxAiAccount? account)
    {
        if (_rememberPassword.IsChecked != true)
        {
            LuxAiAccountCredentials.Delete(_options);
            return;
        }
        // 网页记住账号时，从本应用同一来源的用户存储补取；只接受当前已验证工号。
        account ??= await ReadRememberedAccountAsync(session.EmployeeCode);
        if (_closed) return;
        if (account is not null)
        {
            LuxAiAccountCredentials.Save(_options, account with { EmployeeCode = session.EmployeeCode });
            AppLog.Write("集团 AI 自动登录账号已保存至 Windows 凭据管理器");
        }
        else if (LuxAiAccountCredentials.Read(_options) is not { } saved || saved.EmployeeCode != session.EmployeeCode)
        {
            LuxAiAccountCredentials.Delete(_options);
            MessageBox.Show(this, "本次已登录。请再使用账号密码登录一次，以启用断线自动恢复。", "集团 AI", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task<LuxAiAccount?> ReadRememberedAccountAsync(string employee)
    {
        if (employee.Length == 0) return null;
        try
        {
            // 只在本应用集团 AI 网页来源读取该网页自己记住的账号，不执行网页提供的脚本。
            string result = await _browser.CoreWebView2.ExecuteScriptAsync("""
                (() => {
                  if (location.origin !== 'https://ai.luxvisions-inno.com') return null;
                  try {
                    const account = JSON.parse(localStorage.getItem('user') || 'null')?.account;
                    return account ? {code:account.uid, pwd:account.pwd, companyCode:account.companyCode, isEncrypted:true} : null;
                  } catch { return null; }
                })()
                """);
            using var body = JsonDocument.Parse(result);
            var account = LuxAiAccountCredentials.ReadLoginRequest(body.RootElement);
            return account is not null && account.Code.Equals(employee, StringComparison.OrdinalIgnoreCase) ? account : null;
        }
        catch (Exception) { return null; }
    }
}
