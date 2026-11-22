using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Island.App.Services;

/// <summary>
/// CPU 温度（零新驱动、零提权）：复用系统已装的 inpoutx64 内核驱动（TM 包安装）
/// + LHM 0.9.4 内嵌的 inpoutx64.dll（用户态，DlPort*/MapPhysToLin 导出）。
///
/// 路线（实测打通）：MapPhysToLin 映射 PCIe ECAM（物理 0xE0000000）
/// → B0/D0/F0 配置空间偏移 0x60 写 SMN 地址（THM_TCON_CUR_TMP=0x59800）
/// → 0x64 读温度寄存器 → [31:21]*0.125℃，RANGE_SEL/TJ_SEL 时 -49（与 LHM Amd17Cpu 同源解码）
/// 注：CF8/CFC 端口方式被现代固件锁定，必须走 ECAM 内存映射（0xC0000000/0xE0000000/0xF0000000 逐一探测）。
/// dll 提取自 Downloads\TrafficMonitor 的 LibreHardwareMonitorLib.dll 嵌入资源
/// （Resources.inpoutx64.gz = 0xFF 前缀 gzip，提取工具 tools/DumpTypes）。
/// </summary>
public sealed class InpOutSmnService : IDisposable
{
    private bool _tried, _ok;
    private IntPtr _lib;
    private IntPtr _ecamLin = IntPtr.Zero, _ecamHandle = IntPtr.Zero;
    private unsafe uint* _ecam;

    private const uint SMN_THM_TCON_CUR_TMP = 0x00059800;
    private const uint TEMP_RANGE_SEL = 0x80000;
    private const uint TEMP_TJ_SEL = 0x30000;
    private static readonly ulong[] EcamBases = { 0xE0000000UL, 0xC0000000UL, 0xF0000000UL };

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
            string path = DllPath;
            if (!File.Exists(path)) { _note = "dll not found"; return false; }
            _lib = LoadLibrary(path);
            if (_lib == IntPtr.Zero) { _note = $"LoadLibrary fail {Marshal.GetLastWin32Error()}"; return false; }

            var map = GetProcAddress(_lib, "MapPhysToLin");
            var unmap = GetProcAddress(_lib, "UnmapPhysicalMemory");
            if (map == IntPtr.Zero || unmap == IntPtr.Zero) { _note = "exports missing"; return false; }
            _mapPhys = (MapPhysDel)Marshal.GetDelegateForFunctionPointer(map, typeof(MapPhysDel));
            _unmapPhys = (UnmapDel)Marshal.GetDelegateForFunctionPointer(unmap, typeof(UnmapDel));

            // 探测并映射 ECAM
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
}
