using System;
using System.IO;
using System.Runtime.InteropServices;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace Island.App.Services;

/// <summary>
/// AMD SMN 直读（CPU 温度）——**默认关闭的高级通道**。
///
/// ⚠️ 2026-11-23 实机教训（本机 Intel Core Ultra 5 225H + Arc 130T）：这条路径曾三次把整机送进蓝屏。
///   1) 以管理员运行 → 首次温度采样 → 这里 LoadLibrary("inpoutx64.dll")
///      → 该 dll 会把**未签名的 inpoutx64.sys 内核驱动**落地并加载
///      （物证：System32\drivers\inpoutx64.sys，创建于 11/22 14:04:49 = 第二次蓝屏前 31 秒）；
///   2) 随后 MapPhysToLin 把 PCIe ECAM / MMIO 物理区（0xE0000000 / 0xC0000000 / 0xF0000000）
///      映射进本进程并读取 → 在 Intel 机器上 0xE0000000 正是 ECAM、0xC0000000 常是核显 BAR，
///      映射+读会把 PCIe 主机桥/核显拖死 → 调用线程卡死在驱动里
///      → 0x1EA THREAD_STUCK_IN_DEVICE_DRIVER 蓝屏（11/22 13:52、11/22 14:05、11/23 04:13）；
///   3) 致命的是**顺序**：厂商判断（0x1022 = AMD）原本在**映射之后**才做，
///      而 SMN 本来就是 AMD 专有 —— 在 Intel 上它永远读不到温度（诊断日志："ECAM probe fail"），
///      零收益、整机风险。
///
/// 现在的规矩（别再加回来）：
///   - **先判厂商再碰驱动**：不是 AMD 直接返回，绝不 LoadLibrary、绝不装驱动、绝不映射物理内存；
///   - **不猜基址**：只认 0xE0000000（AMD Zen 的 ECAM 标准基址），
///     不再试 0xC0000000 / 0xF0000000（那两处在很多机器上是显卡 BAR / 其它 MMIO 区）；
///   - **默认关闭**：Settings.EnableSmnDirectRead 为 false 时根本不创建本对象；
///   - **不随包发布** inpoutx64.dll（用户机器上不该留着一个"一加载就装未签名驱动"的文件）。
///
/// CPU 温度的正常来源：LHM（PawnIO，微软签名）+ WMI 热区兜底；读不到就显示 "--"，绝不伪造。
/// </summary>
public sealed class InpOutSmnService : IDisposable
{
#if WINDOWS
    private bool _tried, _ok;
    private IntPtr _lib;
    private IntPtr _ecamLin = IntPtr.Zero, _ecamHandle = IntPtr.Zero;
    private unsafe uint* _ecam;

    private const uint SMN_THM_TCON_CUR_TMP = 0x00059800;
    private const uint TEMP_RANGE_SEL = 0x80000;
    private const uint TEMP_TJ_SEL = 0x30000;

    /// <summary>只认 AMD Zen 的 ECAM 标准基址。0xC0000000/0xF0000000 在很多机器上是显卡 BAR/其它 MMIO 区，碰了会挂总线。</summary>
    private static readonly ulong[] EcamBases = { 0xE0000000UL };

    /// <summary>CPU 厂商判定：读注册表 VendorIdentifier（AuthenticAMD / GenuineIntel）。</summary>
    public static bool IsAmdCpu()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string? vendor = key?.GetValue("VendorIdentifier") as string;
            return vendor is not null && vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string DllPath
    {
        get
        {
            string? TryOne(string? d) => d is null ? null : Path.Combine(d, "inpoutx64.dll");
            // 内存加载模式下 BaseDirectory 是 pwsh 目录——优先 ISLAND_DIR 环境变量
            string? c = TryOne(Environment.GetEnvironmentVariable("ISLAND_DIR"))
                     ?? TryOne(AppContext.BaseDirectory);
            if (c is not null && File.Exists(c)) return c;
            string repo = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "inpout", "inpoutx64.dll"));
            if (File.Exists(repo)) return repo;
            return c ?? repo;
        }
    }

    private bool EnsureLoaded()
    {
        if (_tried) return _ok;
        _tried = true;
        try
        {
            // ① 先判厂商：不是 AMD 就不许碰驱动/物理内存（这是 0x1EA 蓝屏的直接教训）
            if (!IsAmdCpu())
            {
                _note = "not AMD (SMN is AMD-only) - skipped before loading the driver";
                return false;
            }
            string path = DllPath;
            if (!File.Exists(path)) { _note = "dll not found"; return false; }
            _lib = LoadLibrary(path);
            if (_lib == IntPtr.Zero) { _note = $"LoadLibrary fail {Marshal.GetLastWin32Error()}"; return false; }

            var map = GetProcAddress(_lib, "MapPhysToLin");
            var unmap = GetProcAddress(_lib, "UnmapPhysicalMemory");
            if (map == IntPtr.Zero || unmap == IntPtr.Zero) { _note = "exports missing"; return false; }
            _mapPhys = (MapPhysDel)Marshal.GetDelegateForFunctionPointer(map, typeof(MapPhysDel));
            _unmapPhys = (UnmapDel)Marshal.GetDelegateForFunctionPointer(unmap, typeof(UnmapDel));

            // ② 探测 ECAM：只映射确认的基址，读到 AMD 主桥才继续
            foreach (ulong b in EcamBases)
            {
                IntPtr lin = _mapPhys((IntPtr)b, 0x100000, out IntPtr h);
                if (lin == IntPtr.Zero || lin == new IntPtr(-1)) continue;
                unsafe
                {
                    uint vd = *(uint*)lin;   // B0/D0/F0 偏移 0 = vendor/device
                    if ((vd & 0xFFFF) == 0x1022)   // AMD
                    {
                        _ecamLin = lin; _ecamHandle = h;
                        _ecam = (uint*)lin;
                        _ok = true;
                        _note = $"ECAM@0x{b:X} mapped, AMD confirmed";
                        return true;
                    }
                }
                _unmapPhys(lin, h);
            }
            _note = "ECAM probe fail (no AMD at candidate bases)";
        }
        catch (Exception ex) { _note = ex.Message; }
        return _ok;
    }

    /// <summary>读 CPU 温度（Tctl/Tdie ℃）。基础设施不可用返回 false。</summary>
    public bool TryReadCpuTemperature(out double celsius)
    {
        celsius = 0;
        if (!EnsureLoaded()) return false;
        unsafe
        {
            try
            {
                // B0/D0/F0 配置空间：0x60(字节)=dword 索引 0x18 写 SMN 地址；0x64=索引 0x19 读数据
                _ecam[0x18] = SMN_THM_TCON_CUR_TMP;
                uint raw = _ecam[0x19];

                bool offsetFlag = (raw & TEMP_RANGE_SEL) != 0 || (raw & TEMP_TJ_SEL) == TEMP_TJ_SEL;
                double t = (raw >> 21) * 125 * 0.001;
                if (offsetFlag) t -= 49.0;
                if (t is < 0 or > 150) return false;   // 异常防御
                celsius = t;
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>诊断：加载/映射状态。</summary>
    public string Status => _note ?? "not tried";

    public void Dispose()
    {
        try
        {
            if (_ecamLin != IntPtr.Zero && _unmapPhys is not null)
                _unmapPhys(_ecamLin, _ecamHandle);
        }
        catch { }
        if (_lib != IntPtr.Zero) { FreeLibrary(_lib); _lib = IntPtr.Zero; }
    }

    private delegate IntPtr MapPhysDel(IntPtr phys, uint size, out IntPtr handle);
    private delegate bool UnmapDel(IntPtr lin, IntPtr handle);
    private MapPhysDel? _mapPhys;
    private UnmapDel? _unmapPhys;
    private string? _note;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string name);
#else
    // 非 Windows（Mac 版）：没有 SMN 这回事，直接什么都不做
    public bool TryReadCpuTemperature(out double celsius) { celsius = 0; return false; }
    public string Status => "not supported on this platform";
    public void Dispose() { }
#endif
}
