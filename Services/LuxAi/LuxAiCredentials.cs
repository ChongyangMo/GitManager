using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitManager.Services.LuxAi;

public sealed record LuxAiSession(string AiToken, string RefreshToken, string EmployeeCode, DateTimeOffset? RenewedAt = null);

// 只保存 AI 会话，不保存工号密码；使用独立的 Windows 凭据条目。
public static class LuxAiCredentials
{
    private static string Target(LuxAiOptions options) => "GitManager/LuxAI/" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Endpoint().AbsoluteUri)));

    public static LuxAiSession? Read(LuxAiOptions options)
    {
        if (!CredRead(Target(options), 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw new InvalidOperationException("读取集团 AI 登录状态失败。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.Blob == IntPtr.Zero || credential.BlobSize == 0) return null;
            string text = Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2)) ?? "";
            return JsonSerializer.Deserialize<LuxAiSession>(text);
        }
        catch (JsonException) { throw new InvalidOperationException("集团 AI 登录状态已损坏，请重新连接。"); }
        finally { CredFree(pointer); }
    }

    public static void Save(LuxAiOptions options, LuxAiSession session)
    {
        if (string.IsNullOrWhiteSpace(session.AiToken) || session.AiToken.Any(char.IsWhiteSpace)
            || session.RefreshToken.Any(char.IsWhiteSpace)) throw new InvalidOperationException("集团 AI 返回的登录凭据无效。");
        string value = JsonSerializer.Serialize(session);
        if (Encoding.Unicode.GetByteCount(value) > 5120) throw new InvalidOperationException("集团 AI 会话超过 Windows 凭据长度限制，未保存。");
        var blob = Marshal.StringToCoTaskMemUni(value);
        try
        {
            var credential = new Credential { Type = 1, TargetName = Target(options), BlobSize = (uint)(value.Length * 2),
                Blob = blob, Persist = 2, UserName = session.EmployeeCode };
            if (!CredWrite(ref credential, 0)) throw new InvalidOperationException("保存集团 AI 登录状态失败。", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
}
