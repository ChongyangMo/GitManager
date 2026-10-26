using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GitManager.Services.LuxAi;

internal static class LuxAiCredentialStore
{
    public static T? Read<T>(string target, string label) where T : class
    {
        if (!CredRead(target, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw new InvalidOperationException("读取" + label + "失败。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.Blob == IntPtr.Zero || credential.BlobSize == 0) return null;
            string text = Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2)) ?? "";
            return JsonSerializer.Deserialize<T>(text);
        }
        catch (JsonException) { throw new InvalidOperationException(label + "已损坏，请重新登录。"); }
        finally { CredFree(pointer); }
    }

    public static void Save<T>(string target, string user, T value, string label)
    {
        string text = JsonSerializer.Serialize(value);
        if (Encoding.Unicode.GetByteCount(text) > 5120)
            throw new InvalidOperationException(label + "超过 Windows 凭据长度限制，未保存。");
        var blob = Marshal.StringToCoTaskMemUni(text);
        try
        {
            var credential = new Credential { Type = 1, TargetName = target, BlobSize = (uint)(text.Length * 2),
                Blob = blob, Persist = 2, UserName = user };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException("保存" + label + "失败。", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    public static void Delete(string target)
    {
        if (!CredDelete(target, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new InvalidOperationException("清除集团 AI 自动登录账号失败。");
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
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
}
