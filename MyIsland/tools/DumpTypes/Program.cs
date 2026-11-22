// Probe inpoutx64.dll exports directly: IsInpOutDriverOpen / DlPortWritePortUlong / DlPortReadPortUlong
using System;
using System.Runtime.InteropServices;

internal static class Program
{
    private delegate void WriteUlongDel(uint port, uint value);
    private delegate uint ReadUlongDel(uint port);
    private delegate int IsOpenDel();

    private static unsafe void Main()
    {
        string dll = @"C:\Users\fengl\WorkBuddy\2026-11-21-13-46-35\MyIsland\tools\inpout\inpoutx64.dll";
        IntPtr lib = LoadLibrary(dll);
        Console.WriteLine($"LoadLibrary: {lib}");
        if (lib == IntPtr.Zero) return;

        var isOpen = (IsOpenDel)Marshal.GetDelegateForFunctionPointer(
            GetProcAddress(lib, "IsInpOutDriverOpen"), typeof(IsOpenDel));
        var w = (WriteUlongDel)Marshal.GetDelegateForFunctionPointer(
            GetProcAddress(lib, "DlPortWritePortUlong"), typeof(WriteUlongDel));
        var r = (ReadUlongDel)Marshal.GetDelegateForFunctionPointer(
            GetProcAddress(lib, "DlPortReadPortUlong"), typeof(ReadUlongDel));

        Console.WriteLine($"IsInpOutDriverOpen: {isOpen()}");

        w(0xCF8, 0x80000000u);
        uint vid = r(0xCFC);
        w(0xCF8, 0);
        Console.WriteLine($"PCI B0D0F0 vendor/device: 0x{vid:X8} (expect low16=0x1022 AMD)");

        if ((vid & 0xFFFF) == 0x1022)
        {
            foreach (uint fnBase in new uint[] { 0x80000000u, 0x80000800u })
            {
                w(0xCF8, fnBase | 0x60u);
                w(0xCFC, 0x00059800u);
                _ = r(0xCFC);              // settle
                w(0xCF8, fnBase | 0x64u);
                _ = r(0xCFC);              // settle
                uint raw = r(0xCFC);
                w(0xCF8, 0);
                Console.WriteLine($"[fn=0x{fnBase:X8}] THM raw: 0x{raw:X8}");
                if (raw != vid && raw != 0 && raw != 0xFFFFFFFF)
                {
                    double t = (raw >> 21) * 125 * 0.001;
                    bool off = (raw & 0x80000) != 0 || (raw & 0x30000) == 0x30000;
                    if (off) t -= 49;
                    if (t > 0 && t < 150)
                    {
                        Console.WriteLine($"  ==> CPU temp: {t:F1} C (offsetFlag={off})");
                        break;
                    }
                }
            }
        }

        EcamProbe();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string f);

    // --- MapPhysToLin ECAM probe (append to Main flow via reflection-friendly static) ---
    public static void EcamProbe()
    {
        string dll = @"C://Users//fengl//WorkBuddy//2026-11-21-13-46-35//MyIsland//tools//inpout//inpoutx64.dll";
        IntPtr lib = LoadLibrary(dll);
        _mapPhys = (MapPhysDel)Marshal.GetDelegateForFunctionPointer(GetProcAddress(lib, "MapPhysToLin"), typeof(MapPhysDel));
        _unmapPhys = (UnmapDel)Marshal.GetDelegateForFunctionPointer(GetProcAddress(lib, "UnmapPhysicalMemory"), typeof(UnmapDel));

        ulong ecamBase = 0xE0000000UL; // typical MCFG base; try a few
        foreach (ulong base_ in new ulong[] { 0xE0000000UL, 0xC0000000UL, 0xF0000000UL })
        {
            IntPtr lin = _mapPhys((IntPtr)base_, 0x100000, out IntPtr handle);
            Console.WriteLine($"MapPhys(0x{base_:X}) -> lin=0x{lin:X}");
            if (lin == IntPtr.Zero || lin == (IntPtr)0xFFFFFFFF) continue;
            try
            {
                unsafe
                {
                    uint* cfg = (uint*)lin;
                    // B0D0F0 offset 0: vendor/device
                    uint vd = cfg[0];
                    Console.WriteLine($"  ECAM B0D0F0 vendor: 0x{vd:X8}");
                    if ((vd & 0xFFFF) == 0x1022)
                    {
                        // SMN via config regs 0x60/0x64 (byte offsets) -> dword idx 0x18/0x19
                        cfg[0x18] = 0x00059800u;  // SMN addr
                        uint raw = cfg[0x19];      // SMN data
                        Console.WriteLine($"  THM raw: 0x{raw:X8}");
                        double t = (raw >> 21) * 125 * 0.001;
                        bool off = (raw & 0x80000) != 0 || (raw & 0x30000) == 0x30000;
                        if (off) t -= 49;
                        Console.WriteLine($"  ==> CPU temp: {t:F1} C");
                        _unmapPhys(lin, handle);
                        return;
                    }
                }
            }
            finally { }
            _unmapPhys(lin, handle);
        }
    }

    private delegate IntPtr MapPhysDel(IntPtr phys, uint size, out IntPtr handle);
    private delegate bool UnmapDel(IntPtr lin, IntPtr handle);
    private static MapPhysDel _mapPhys;
    private static UnmapDel _unmapPhys;
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr m, string n);
}
