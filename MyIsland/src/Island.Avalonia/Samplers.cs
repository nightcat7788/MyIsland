using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Island.Core.Monitoring;

namespace Island.Avalonia;

/// <summary>平台采样器：产出与 Windows 版相同的 MonitorSnapshot。</summary>
public interface ISampler : IDisposable
{
    event Action<MonitorSnapshot>? Snapshot;
    void Start();
}

public static class Samplers
{
    public static ISampler Create(int intervalMs) =>
        OperatingSystem.IsMacOS() ? new MacSampler(intervalMs) : new WinSampler(intervalMs);
}

/// <summary>Windows：直接复用 Island.Native.SystemMonitor（PDH 全套）+ WMI 电池 + LHM 温度。</summary>
public sealed class WinSampler : ISampler
{
    private readonly Island.Native.SystemMonitor _inner;
    private readonly Island.App.Services.ThermalService _thermal = new();
    public event Action<MonitorSnapshot>? Snapshot;

    private DateTime _battChecked = DateTime.MinValue;
    private double? _battPct;
    private bool _battCharging;

    public WinSampler(int intervalMs)
    {
        _inner = new Island.Native.SystemMonitor(intervalMs);
        // 跨平台版没有"收起/展开"之分，快照里的进程榜等明细始终用得上：
        // SystemMonitor 的明细采样默认关（2026-11-24 空闲降耗，Windows 版只在面板展开时开），这里显式打开。
        _inner.SetDetailWanted(true);
        _thermal.SetWanted(true);   // 常驻采样 CPU/GPU/SSD 温度（2s）
        _inner.Snapshot += s =>
        {
            SampleBattery();
            var th = _thermal.Current;
            Snapshot?.Invoke(s with
            {
                BatteryPercent = _battPct,
                BatteryCharging = _battCharging,
                CpuTemp = th?.CpuTemp,
                GpuTemp = th?.GpuTemp,
                SsdTemp = th?.SsdTemp,
                MotherboardTemp = th?.MotherboardTemp,
                GpuMemoryUsedGB = th?.GpuMemoryUsedGB,
                GpuMemoryTotalGB = th?.GpuMemoryTotalGB,
                GpuCoreClockMHz = th?.GpuCoreClockMHz,
            });
        };
    }

    /// <summary>本机电池（WMI Win32_Battery，30 秒一采；台式机无电池给 null）。</summary>
    private void SampleBattery()
    {
        if ((DateTime.Now - _battChecked).TotalSeconds < 30) return;
        _battChecked = DateTime.Now;
        try
        {
            using var q = new System.Management.ManagementObjectSearcher(
                "SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
            foreach (var o in q.Get())
            {
                _battPct = Convert.ToDouble(o["EstimatedChargeRemaining"]);
                _battCharging = Convert.ToInt32(o["BatteryStatus"]) == 2;
                return;
            }
            _battPct = null;   // 无电池
        }
        catch { /* 保留上次 */ }
    }

    public void Start() => _inner.Start();
    public void Dispose() { _inner.Dispose(); _thermal.Dispose(); }
}

/// <summary>
/// macOS 采样器：
/// - CPU：mach host_statistics64(HOST_CPU_LOAD_INFO) 差分（与 GetSystemTimes 同原理）
/// - 内存：host_statistics64(HOST_VM_INFO64) + sysctlbyname("hw.memsize")
/// - 网速：.NET NetworkInterface（跨平台，与 Windows 版同口径）
/// GPU/温度/磁盘：Apple Silicon 需要 IOReport/SMC（受限），v0.1 给 0/"--"，不伪造。
/// </summary>
public sealed class MacSampler : ISampler
{
    private readonly System.Timers.Timer _timer;
    private long _lastCpuUser, _lastCpuSystem, _lastCpuIdle, _lastCpuNice;
    private bool _cpuInit;
    private long _lastIn, _lastOut;
    private DateTime _lastNetTime;
    private double _smoothDown, _smoothUp;
    private bool _netInit;

    public event Action<MonitorSnapshot>? Snapshot;

    public MacSampler(int intervalMs)
    {
        _timer = new System.Timers.Timer(intervalMs);
        _timer.Elapsed += (_, _) =>
        {
            try { Sample(); } catch { /* 采样失败保留上一帧 */ }
        };
    }

    public void Start() { _timer.Start(); Sample(); }
    public void Dispose() { _timer.Stop(); _timer.Dispose(); }

    private void Sample()
    {
        double cpu = SampleCpu();
        var (memPct, memUsed, memTotal) = SampleMemory();
        ReadNetwork(out double down, out double up);
        var (batt, charging) = SampleBattery();
        SampleTopProcesses();
        Snapshot?.Invoke(new MonitorSnapshot
        {
            CpuUsage = cpu,
            MemoryUsagePercent = memPct,
            MemoryUsedGB = memUsed,
            MemoryTotalGB = memTotal,
            BatteryPercent = batt,
            BatteryCharging = charging,
            TopProcesses = _topProcesses,
            DownloadSpeed = down,
            UploadSpeed = up,
        });
    }

    // ---------- 高占用进程（TotalProcessorTime 差分，跨平台 .NET API，3 秒一采） ----------
    private DateTime _procChecked = DateTime.MinValue;
    private Dictionary<int, (string Name, TimeSpan Cpu)> _procPrev = new();
    private TopProcess[] _topProcesses = Array.Empty<TopProcess>();

    private void SampleTopProcesses()
    {
        var now = DateTime.Now;
        if ((now - _procChecked).TotalSeconds < 3) return;
        bool first = _procChecked == DateTime.MinValue;
        double wall = first ? 0 : (now - _procChecked).TotalSeconds;
        _procChecked = now;

        var prev = _procPrev;
        var next = new Dictionary<int, (string, TimeSpan)>(256);
        var tops = new List<TopProcess>(64);
        int coresN = Math.Max(1, Environment.ProcessorCount);

        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                string name = p.ProcessName;
                if (name is "Idle" or "System" or "kernel_task" or "launchd") continue;
                var cpu = p.TotalProcessorTime;
                long ws = 0;
                try { ws = p.WorkingSet64; } catch { }
                next[p.Id] = (name, cpu);
                if (wall > 0.5 && prev.TryGetValue(p.Id, out var old))
                {
                    double pct = (cpu - old.Cpu).TotalSeconds / wall / coresN * 100;
                    if (pct >= 0.05)
                        tops.Add(new TopProcess { Name = name, CpuPercent = Math.Min(pct, 100), MemoryMB = ws / 1048576.0 });
                }
            }
            catch { /* 进程已退出/无权限 */ }
            finally { p.Dispose(); }
        }
        _procPrev = next;
        if (tops.Count > 0)
            _topProcesses = tops.OrderByDescending(t => t.CpuPercent).Take(5).ToArray();
    }

    // ---------- 电池：IOKit IOPSCopyPowerSourcesInfo（30 秒一采；台式 Mac 无电池给 null） ----------
    private DateTime _battChecked = DateTime.MinValue;
    private double? _battPct;
    private bool _battCharging;

    private (double? pct, bool charging) SampleBattery()
    {
        if ((DateTime.Now - _battChecked).TotalSeconds >= 30)
        {
            _battChecked = DateTime.Now;
            try { (_battPct, _battCharging) = ReadBatteryIOKit(); }
            catch { _battPct = null; }
        }
        return (_battPct, _battCharging);
    }

    private static (double? pct, bool charging) ReadBatteryIOKit()
    {
        IntPtr blob = IOPSCopyPowerSourcesInfo();
        if (blob == IntPtr.Zero) return (null, false);
        IntPtr list = IntPtr.Zero;
        try
        {
            list = IOPSCopyPowerSourcesList(blob);
            int n = CFArrayGetCount(list);
            for (int i = 0; i < n; i++)
            {
                IntPtr ps = CFArrayGetValueAtIndex(list, i);
                IntPtr desc = IOPSGetPowerSourceDescription(blob, ps);
                if (desc == IntPtr.Zero) continue;
                if (CfString(CfDictGet(desc, "Type")) != "InternalBattery") continue;
                long cur = CfNumber(CfDictGet(desc, "Current Capacity"));
                long max = CfNumber(CfDictGet(desc, "Max Capacity"));
                bool charging = CfBool(CfDictGet(desc, "Is Charging"));
                if (max <= 0) return (null, false);
                return (Math.Clamp(cur * 100.0 / max, 0, 100), charging);
            }
            return (null, false);   // 无内置电池
        }
        finally
        {
            if (list != IntPtr.Zero) CFRelease(list);
            CFRelease(blob);
        }
    }

    private static IntPtr CfDictGet(IntPtr dict, string key)
    {
        IntPtr k = CFStringCreateWithCString(IntPtr.Zero, key, 0x08000100);
        try { return dict == IntPtr.Zero ? IntPtr.Zero : CFDictionaryGetValue(dict, k); }
        finally { if (k != IntPtr.Zero) CFRelease(k); }
    }

    private static long CfNumber(IntPtr num)
        => num != IntPtr.Zero && CFNumberGetValue(num, 4 /*kCFNumberSInt64Type*/, out long v) ? v : 0;

    private static bool CfBool(IntPtr b) => b != IntPtr.Zero && CFBooleanGetValue(b);

    private static string? CfString(IntPtr s)
    {
        if (s == IntPtr.Zero) return null;
        var buf = new byte[256];
        return CFStringGetCString(s, buf, buf.Length, 0x08000100)
            ? System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0') : null;
    }

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern IntPtr IOPSCopyPowerSourcesInfo();
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern IntPtr IOPSCopyPowerSourcesList(IntPtr blob);
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern IntPtr IOPSGetPowerSourceDescription(IntPtr blob, IntPtr ps);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern int CFArrayGetCount(IntPtr array);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, int idx);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern bool CFStringGetCString(IntPtr str, byte[] buffer, long bufferSize, uint encoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern bool CFNumberGetValue(IntPtr number, int theType, out long valuePtr);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern bool CFBooleanGetValue(IntPtr boolean);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    // ---------- CPU：host_statistics64(HOST_CPU_LOAD_INFO=3)，4×u64 ----------
    private double SampleCpu()
    {
        var buf = Marshal.AllocHGlobal(8 * 4);
        try
        {
            int count = 4;
            if (host_statistics64(mach_host_self(), 3, buf, ref count) != 0) return 0;
            long user = Marshal.ReadInt64(buf, 0);
            long system = Marshal.ReadInt64(buf, 8);
            long idle = Marshal.ReadInt64(buf, 16);
            long nice = Marshal.ReadInt64(buf, 24);
            if (!_cpuInit)
            {
                _lastCpuUser = user; _lastCpuSystem = system; _lastCpuIdle = idle; _lastCpuNice = nice;
                _cpuInit = true;
                return 0;
            }
            long dUser = user - _lastCpuUser, dSys = system - _lastCpuSystem,
                 dIdle = idle - _lastCpuIdle, dNice = nice - _lastCpuNice;
            _lastCpuUser = user; _lastCpuSystem = system; _lastCpuIdle = idle; _lastCpuNice = nice;
            long total = dUser + dSys + dIdle + dNice;
            return total <= 0 ? 0 : Math.Clamp((total - dIdle) * 100.0 / total, 0, 100);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---------- 内存：HOST_VM_INFO64=4（vm_statistics64_data_t） ----------
    // 布局要点（vm_statistics64_data_t，XNU osfmk/mach/vm_statistics.h）：
    //   开头 4 个是 32 位 natural_t（free/active/inactive/wire），
    //   之后是 64 位计数器，但中间还夹着 6 个 32 位 natural_t
    //   （purgeable/speculative/compressor/throttled/external/internal）——
    //   整个结构体 160 字节，不能假设"第 5 个起就全是 64 位"。
    //   本函数只读 active@4 与 wire@12（这两个偏移是对的）。
    //
    // ⚠️ 2026-11-23 修复：原来只给了 8*15=120 字节缓冲、count=15。
    //   host_statistics64 的 count 以 4 字节为单位，最少要覆盖 160 字节的结构体；
    //   count 不足时内核直接返回 KERN_INVALID_ARGUMENT ⇒ Mac 上内存面板永远显示 0%。
    //   现在缓冲 256 字节、count=64（=256/4），两边都够。
    private (double pct, double used, double total) SampleMemory()
    {
        var buf = Marshal.AllocHGlobal(256);
        try
        {
            int count = 64;   // 256 / 4：足够覆盖 160 字节的 vm_statistics64_data_t
            if (host_statistics64(mach_host_self(), 4, buf, ref count) != 0) return (0, 0, 0);
            if (host_page_size(mach_host_self(), out uint pageSize) != 0 || pageSize == 0) pageSize = 16384;
            // natural_t 是 32 位无符号整数：free@0 / active@4 / inactive@8 / wire@12
            ulong active = (uint)Marshal.ReadInt32(buf, 4);
            ulong wire = (uint)Marshal.ReadInt32(buf, 12);
            // 近似口径：已用 ≈ 活跃页 + 联动页（未含压缩页，与活动监视器口径略有差异）
            double usedGb = (active + wire) * (double)pageSize / 1073741824.0;

            ulong memsize = 0;
            try
            {
                IntPtr len = (IntPtr)8;
                if (sysctlbyname("hw.memsize", out memsize, ref len, IntPtr.Zero, (IntPtr)0) != 0) memsize = 0;
            }
            catch { /* sysctl 缺失：只给百分比 0 */ }
            double totalGb = memsize / 1073741824.0;
            double pct = totalGb > 0 ? Math.Clamp(usedGb / totalGb * 100, 0, 100) : 0;
            return (pct, usedGb, totalGb);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---------- 网速：.NET NetworkInterface 差分（与 Windows 版同口径） ----------
    private void ReadNetwork(out double down, out double up)
    {
        down = up = 0;
        long inBytes = 0, outBytes = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                inBytes += stats.BytesReceived;
                outBytes += stats.BytesSent;
            }
        }
        catch { return; }

        var now = DateTime.Now;
        if (!_netInit) { _lastIn = inBytes; _lastOut = outBytes; _lastNetTime = now; _netInit = true; return; }
        double sec = (now - _lastNetTime).TotalSeconds;
        if (sec <= 0) return;
        if (inBytes < _lastIn || outBytes < _lastOut)
        { _lastIn = inBytes; _lastOut = outBytes; _lastNetTime = now; return; }

        double rawDown = (inBytes - _lastIn) / sec, rawUp = (outBytes - _lastOut) / sec;
        _lastIn = inBytes; _lastOut = outBytes; _lastNetTime = now;
        _smoothDown = _smoothDown * 0.6 + rawDown * 0.4;
        _smoothUp = _smoothUp * 0.6 + rawUp * 0.4;
        down = _smoothDown; up = _smoothUp;
    }

    // ---------- libSystem P/Invoke ----------
    [DllImport("libSystem.dylib")] private static extern IntPtr mach_host_self();
    [DllImport("libSystem.dylib")] private static extern int host_statistics64(IntPtr host, int flavor, IntPtr info, ref int count);
    [DllImport("libSystem.dylib")] private static extern int host_page_size(IntPtr host, out uint pageSize);
    [DllImport("libSystem.dylib")] private static extern int sysctlbyname(string name, out ulong oldp, ref IntPtr oldlenp, IntPtr newp, IntPtr newlen);
}
