using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GitManager.Services.Repositories.Copies;

public enum LockRelease { Automatic, NeedsConfirmation, Impossible }

public sealed record DirectoryLockHolder(int ProcessId, string Name, string Reason, LockRelease Release)
{
    public override string ToString() => $"{Name}（PID {ProcessId}）：{Reason}";
}

public sealed record DirectoryLockReport(IReadOnlyList<DirectoryLockHolder> Holders, int Uninspectable)
{
    public string Describe() => Holders.Count > 0
        ? string.Join("\n", Holders.Select(holder => "· " + holder))
        : "未找到占用的程序" + (Uninspectable > 0 ? $"；另有 {Uninspectable} 个进程无权限检查（可能是以管理员身份运行的程序或安全软件）" : "（可能是安全软件或系统索引正在短暂读取）");
}

// 找出占用目录的进程：打开了其中文件或加载了其中模块（重启管理器）、当前目录位于其中、程序本身位于其中、资源管理器窗口打开了它。
public static class DirectoryLocks
{
    private const int MaxRegisteredFiles = 20000;
    private static readonly HashSet<string> BuildServers = new(StringComparer.OrdinalIgnoreCase) { "VBCSCompiler", "MSBuild" };
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        { "System", "Idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "explorer", "MsMpEng", "dwm", "fontdrvhost", "Registry" };

    public static DirectoryLockReport Inspect(string root)
    {
        root = Path.GetFullPath(root).TrimEnd('\\');
        var reasons = new Dictionary<int, List<string>>();
        var names = new Dictionary<int, string>();
        var services = new HashSet<int>();
        void Add(int id, string name, string reason)
        {
            if (!reasons.TryGetValue(id, out var list)) reasons[id] = list = [];
            if (!list.Contains(reason)) list.Add(reason);
            names.TryAdd(id, name);
        }
        foreach (var (id, name, service) in FindFileUsers(root))
        {
            Add(id, name, service.Length > 0 ? $"系统服务 {service} 打开了其中的文件" : "打开了其中的文件或程序库");
            if (service.Length > 0) services.Add(id);
        }
        int uninspectable = 0;
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                if (process.Id is 0 or 4) continue;
                using var handle = OpenProcess(0x1000 | 0x0010, false, process.Id);
                if (handle.IsInvalid) { uninspectable++; continue; }
                string name;
                try { name = process.ProcessName; } catch (InvalidOperationException) { continue; }
                string? image = ImagePath(handle);
                if (image is not null && IsInside(root, image)) Add(process.Id, name, "程序文件位于此目录");
                var (directory, _) = ReadParameters(handle);
                if (directory is not null && IsInside(root, directory.TrimEnd('\\'))) Add(process.Id, name, "当前工作目录位于此目录（例如终端或命令行）");
            }
        foreach (var (id, path) in ExplorerWindows(root)) Add(id, "explorer", "资源管理器窗口打开了 " + path);
        var holders = reasons.Select(item => new DirectoryLockHolder(item.Key, names[item.Key], string.Join("；", item.Value), services.Contains(item.Key) ? LockRelease.Impossible : Classify(item.Key, names[item.Key])))
            .OrderBy(holder => holder.Release).ThenBy(holder => holder.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new(holders, uninspectable);
    }

    // 自动解除安全的占用；其余需用户确认后先请求正常关闭，超时再结束。返回解除后重新检查的结果。
    public static async Task<DirectoryLockReport> ReleaseAsync(string root, DirectoryLockReport report, Func<IReadOnlyList<DirectoryLockHolder>, bool>? confirm)
    {
        if (report.Holders.Count == 0) return report;
        var confirmable = report.Holders.Where(holder => holder.Release == LockRelease.NeedsConfirmation).ToList();
        bool approved = confirmable.Count > 0 && confirm?.Invoke(confirmable) == true;
        await Task.Run(() =>
        {
            foreach (var holder in report.Holders)
            {
                if (holder.ProcessId == Environment.ProcessId)
                {
                    if (IsInside(Path.GetFullPath(root).TrimEnd('\\'), Environment.CurrentDirectory.TrimEnd('\\'))) Environment.CurrentDirectory = AppContext.BaseDirectory;
                    continue;
                }
                if (holder.Name.Equals("explorer", StringComparison.OrdinalIgnoreCase)) { CloseExplorerWindows(root); continue; }
                if (holder.Release == LockRelease.Automatic || holder.Release == LockRelease.NeedsConfirmation && approved) Stop(holder);
            }
        });
        // 已处理的进程退出需要片刻；只等它们，不为无法解除的占用反复等待。
        var handled = report.Holders.Where(holder => holder.Release == LockRelease.Automatic || holder.Release == LockRelease.NeedsConfirmation && approved)
            .Select(holder => holder.ProcessId).ToHashSet();
        for (int attempt = 0; ; attempt++)
        {
            var current = await Task.Run(() => Inspect(root));
            if (attempt == 5 || !current.Holders.Any(holder => handled.Contains(holder.ProcessId))) return current;
            await Task.Delay(300);
        }
    }

    private static LockRelease Classify(int id, string name)
    {
        if (id == Environment.ProcessId || name.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return LockRelease.Automatic;
        if (Protected.Contains(name)) return LockRelease.Impossible;
        if (BuildServers.Contains(name)) return LockRelease.Automatic;
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            using var handle = OpenProcess(0x1000 | 0x0010, false, id);
            string commandLine = handle.IsInvalid ? "" : ReadParameters(handle).CommandLine ?? "";
            if (commandLine.Contains("MSBuild.dll", StringComparison.OrdinalIgnoreCase) || commandLine.Contains("VBCSCompiler.dll", StringComparison.OrdinalIgnoreCase)
                || commandLine.Contains("rzc.dll", StringComparison.OrdinalIgnoreCase)) return LockRelease.Automatic;
        }
        return LockRelease.NeedsConfirmation;
    }

    private static void Stop(DirectoryLockHolder holder)
    {
        try
        {
            using var process = Process.GetProcessById(holder.ProcessId);
            if (holder.Release == LockRelease.NeedsConfirmation && process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow() && process.WaitForExit(4000)) return;
            process.Kill();
            process.WaitForExit(5000);
            AppLog.Write("已结束占用目录的进程：" + holder);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("结束占用目录的进程失败：" + holder, ex);
        }
    }

    private static bool IsInside(string root, string path)
    {
        try { path = Path.GetFullPath(path); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<(int Id, string Name, string Service)> FindFileUsers(string root)
    {
        string[] files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(MaxRegisteredFiles).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        if (files.Length == 0 || RmStartSession(out uint session, 0, new StringBuilder(64)) != 0) return [];
        try
        {
            foreach (var chunk in files.Chunk(1000))
                if (RmRegisterResources(session, (uint)chunk.Length, chunk, 0, IntPtr.Zero, 0, null) != 0) return [];
            uint count = 0, reasons = 0;
            int result = RmGetList(session, out uint needed, ref count, null, ref reasons);
            if (result != 234 || needed == 0) return [];
            var info = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out _, ref count, info, ref reasons) != 0) return [];
            return info.Take((int)count).Select(item => (item.Process.ProcessId, ProcessName(item.Process.ProcessId) ?? item.AppName, item.ServiceShortName ?? "")).ToList();
        }
        finally { RmEndSession(session); }
    }

    private static string? ProcessName(int id)
    {
        try { using var process = Process.GetProcessById(id); return process.ProcessName; }
        catch (ArgumentException) { return null; }
    }

    private static List<(int Id, string Path)> ExplorerWindows(string root)
    {
        var found = new List<(int, string)>();
        foreach (var (window, path) in ShellWindows())
        {
            if (!IsInside(root, path)) continue;
            GetWindowThreadProcessId(new IntPtr((long)window.HWND), out int id);
            found.Add((id, path));
        }
        return found;
    }

    private static void CloseExplorerWindows(string root)
    {
        foreach (var (window, path) in ShellWindows())
            if (IsInside(Path.GetFullPath(root).TrimEnd('\\'), path))
                try { window.Quit(); AppLog.Write("已关闭打开该目录的资源管理器窗口：" + path); } catch (COMException) { }
    }

    private static List<(dynamic Window, string Path)> ShellWindows()
    {
        var result = new List<(dynamic, string)>();
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is not { } type) return result;
            dynamic shell = Activator.CreateInstance(type)!;
            foreach (dynamic window in shell.Windows())
            {
                string? path = null;
                try { path = window.Document?.Folder?.Self?.Path as string; } catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                if (!string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path)) result.Add((window, path));
            }
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException) { }
        return result;
    }

    private static string? ImagePath(SafeProcessHandle handle)
    {
        var builder = new StringBuilder(1024);
        int size = builder.Capacity;
        return QueryFullProcessImageName(handle, 0, builder, ref size) ? builder.ToString(0, size) : null;
    }

    // 从进程参数块读取当前目录和命令行；64 位与 32 位（WOW64）进程的结构偏移不同。
    private static (string? Directory, string? CommandLine) ReadParameters(SafeProcessHandle handle)
    {
        if (!Environment.Is64BitProcess) return (null, null);
        try
        {
            if (IsWow64Process(handle, out bool wow64) && wow64)
            {
                if (NtQueryInformationProcess(handle, 26, out IntPtr peb32, IntPtr.Size, out _) != 0 || peb32 == IntPtr.Zero) return (null, null);
                if (ReadInt(handle, peb32 + 0x10, 4) is not long parameters32) return (null, null);
                return (ReadString32(handle, (IntPtr)parameters32 + 0x24), ReadString32(handle, (IntPtr)parameters32 + 0x40));
            }
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return (null, null);
            if (ReadInt(handle, info.PebBaseAddress + 0x20, 8) is not long parameters) return (null, null);
            return (ReadString64(handle, (IntPtr)parameters + 0x38), ReadString64(handle, (IntPtr)parameters + 0x70));
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException) { return (null, null); }
    }

    private static long? ReadInt(SafeProcessHandle handle, IntPtr address, int size)
    {
        byte[] buffer = new byte[8];
        if (!ReadProcessMemory(handle, address, buffer, size, out nint read) || read != size) return null;
        return size == 4 ? BitConverter.ToUInt32(buffer) : BitConverter.ToInt64(buffer);
    }

    private static string? ReadString64(SafeProcessHandle handle, IntPtr address)
    {
        byte[] header = new byte[16];
        if (!ReadProcessMemory(handle, address, header, 16, out nint read) || read != 16) return null;
        return ReadChars(handle, (IntPtr)BitConverter.ToInt64(header, 8), BitConverter.ToUInt16(header, 0));
    }

    private static string? ReadString32(SafeProcessHandle handle, IntPtr address)
    {
        byte[] header = new byte[8];
        if (!ReadProcessMemory(handle, address, header, 8, out nint read) || read != 8) return null;
        return ReadChars(handle, (IntPtr)BitConverter.ToUInt32(header, 4), BitConverter.ToUInt16(header, 0));
    }

    private static string? ReadChars(SafeProcessHandle handle, IntPtr address, int length)
    {
        if (address == IntPtr.Zero || length is <= 0 or > 65534) return null;
        byte[] buffer = new byte[length];
        return ReadProcessMemory(handle, address, buffer, length, out nint read) && read == length ? Encoding.Unicode.GetString(buffer) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess { public int ProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME StartTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus, SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process(SafeProcessHandle process, out bool wow64);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, byte[] buffer, nint size, out nint read);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int size);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, ref ProcessBasicInformation information, int length, out int returned);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, out IntPtr information, int length, out int returned);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint files, string[] names, uint applications, IntPtr processes, uint services, string[]? serviceNames);
    [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? processes, ref uint reasons);
}
