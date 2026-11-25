param(
    # 要结束的进程 PID；留空则结束所有名为 Island.App 的进程
    [int]$ProcessId = 0
)
$ErrorActionPreference = 'Continue'
# 结束旧的 MyIsland 宿主进程。
# 原实现把 PID 写死成 33816 —— PID 会被系统复用，运行它等于"杀一个随机进程"。
# 现在改为参数化；不给 PID 时按进程名匹配 Island.App。

$code = @'
using System;
using System.Runtime.InteropServices;
public static class Killer
{
    [DllImport("advapi32.dll", SetLastError=true)]
    static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr tok);
    [DllImport("advapi32.dll", SetLastError=true)]
    static extern bool LookupPrivilegeValue(string sys, string name, ref long luid);
    [DllImport("advapi32.dll", SetLastError=true)]
    static extern bool AdjustTokenPrivileges(IntPtr tok, bool disable, ref TOKEN_PRIVILEGES newst, int len, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool TerminateProcess(IntPtr h, uint code);
    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES { public int PrivilegeCount; public long Luid; public int Attributes; }
    public static string Kill(int pid)
    {
        IntPtr tok;
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x28, out tok)) return "OpenProcessToken err " + Marshal.GetLastWin32Error();
        TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
        tp.PrivilegeCount = 1; tp.Attributes = 2;
        if (!LookupPrivilegeValue(null, "SeDebugPrivilege", ref tp.Luid)) return "Lookup err";
        if (!AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return "Adjust err " + Marshal.GetLastWin32Error();
        IntPtr h = OpenProcess(1, false, pid);
        if (h == IntPtr.Zero) return "OpenProcess err " + Marshal.GetLastWin32Error() + " (need elevation)";
        bool ok = TerminateProcess(h, 0);
        return ok ? "KILLED " + pid : "Terminate err " + Marshal.GetLastWin32Error();
    }
}
'@
Add-Type -TypeDefinition $code

if ($ProcessId -gt 0) {
    "target pid=$ProcessId -> " + [Killer]::Kill($ProcessId)
} else {
    $procs = @(Get-Process -Name 'Island.App' -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { "no Island.App process running"; exit 0 }
    foreach ($p in $procs) {
        "target pid=$($p.Id) -> " + [Killer]::Kill($p.Id)
    }
}
