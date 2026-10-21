using GitManager.Services.CommitMessages;

namespace GitManager.Checks;

internal static partial class Program
{
    private static async Task RunClaudeChecksAsync()
    {
        if (ClaudeClient.FindExecutable() is not { } executable) { Console.WriteLine("本机未安装 Claude Code，将直接使用千问。"); return; }
        bool loggedIn = await ClaudeClient.IsLoggedInAsync(executable, default);
        Console.WriteLine($"Claude Code：{executable}，登录状态 {(loggedIn ? "已登录" : "未登录")}");
        if (!loggedIn) return;
        try
        {
            var reply = await ClaudeClient.GenerateAsync(new ClaudeOptions(), executable, "文件：README.md（已修改，+1 -0）\n+ 增加安装说明一行", default);
            Check(reply.Message.Length > 0 && reply.Cost.Minimum == 0m, "Claude 返回提交说明且不计入千问费用");
            Console.WriteLine("Claude 总结结果：" + reply.Message);
        }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.StartsWith("Claude 调用失败", StringComparison.Ordinal), "Claude 调用失败时给出可回退的错误");
            Console.WriteLine("Claude 当前不可用，将回退到千问：" + ex.Message);
        }
    }
}
