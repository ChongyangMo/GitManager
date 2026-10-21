using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GitManager.Services.CommitMessages;

public static class QwenCredentials
{
    private static string Target(QwenOptions options) => "GitManager/Qwen/" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Endpoint().AbsoluteUri)));

    public static string? Read(QwenOptions options)
    {
        if (!CredRead(Target(options), 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw new InvalidOperationException("读取千问密钥失败，请检查 Windows 凭据管理器。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.Blob == IntPtr.Zero || credential.BlobSize == 0) return null;
            return Marshal.PtrToStringUni(credential.Blob, (int)credential.BlobSize / 2);
        }
        finally { CredFree(pointer); }
    }

    public static void Save(QwenOptions options, string apiKey)
    {
        apiKey = apiKey.Trim();
        if (apiKey.Length == 0 || apiKey.Any(char.IsWhiteSpace) || apiKey.Length > 1200)
            throw new InvalidOperationException("请填写有效的千问 API Key。");
        var blob = Marshal.StringToCoTaskMemUni(apiKey);
        try
        {
            var credential = new Credential { Type = 1, TargetName = Target(options), BlobSize = (uint)(apiKey.Length * 2),
                Blob = blob, Persist = 2, UserName = "api-key" };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException("保存千问密钥失败，请检查 Windows 凭据管理器。", new Win32Exception(Marshal.GetLastWin32Error()));
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
