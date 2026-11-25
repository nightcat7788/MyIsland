# Probe inpoutx64 (Highrez InpOut32) IOCTL variants.
# Success test: write CF8 config address for B0/D0/F0 vendor-id, read CFC -> must be 0x1022 (AMD)
$code = @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class InpOutProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string fn, uint ga, uint sm, IntPtr sa, uint cd, uint fa, IntPtr tf);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint ctl,
        byte[] inBuf, uint inLen, byte[] outBuf, uint outLen, out int ret, IntPtr ov);

    static uint CTL(uint dev, uint fn, uint method, uint access)
    { return (dev << 16) | (access << 14) | (fn << 2) | method; }

    public static string Probe()
    {
        SafeFileHandle h = CreateFileW("\\\\.\\inpoutx64", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return "open fail";
        // candidate functions from Highrez docs: 0x900..0x921 range
        uint[] writeFns = { 0x910, 0x916, 0x902, 0x908, 0x90E, 0x914, 0x91A };
        uint[] readFns  = { 0x911, 0x917, 0x903, 0x909, 0x90F, 0x915, 0x91B };
        // Highrez CTL codes documented: write uchar=0x910 read uchar=0x911 (some builds use different)
        foreach (uint method in new uint[] { 2, 1 })
        {
            for (int i = 0; i < writeFns.Length; i++)
            {
                uint wCtl = CTL(40000, writeFns[i], method, 0);
                uint rCtl = CTL(40000, readFns[i], method, 0);
                try
                {
                    // TRY A: ULONG buffer {port, value} / read ULONG {port} -> ULONG {value}
                    // write CF8 = 0x80000000 (vendor id addr), then read CFC
                    byte[] wbuf = new byte[8];
                    BitConverter.GetBytes((uint)0xCF8).CopyTo(wbuf, 0);
                    BitConverter.GetBytes((uint)0x80000000).CopyTo(wbuf, 4);
                    uint dummy;
                    if (DeviceIoControl(h, wCtl, wbuf, 8, null, 0, out dummy, IntPtr.Zero))
                    {
                        byte[] rbuf = BitConverter.GetBytes((uint)0xCFC);
                        byte[] rout = new byte[4];
                        if (DeviceIoControl(h, rCtl, rbuf, 4, rout, 4, out dummy, IntPtr.Zero))
                        {
                            uint vid = BitConverter.ToUInt32(rout, 0) & 0xFFFF;
                            if (vid == 0x1022)
                                return string.Format("OK ULONG: w=0x{0:X} r=0x{1:X} method={2} -> AMD!", writeFns[i], readFns[i], method);
                        }
                    }
                    // TRY B: byte-packed {port16, value8} / {port16} -> {value8}
                    byte[] wbuf2 = new byte[3];
                    wbuf2[0] = 0xF8; wbuf2[1] = 0x0C; wbuf2[2] = 0x00;
                    uint dummy2;
                    DeviceIoControl(h, wCtl, wbuf2, 3, null, 0, out dummy2, IntPtr.Zero);
                    byte[] rbuf2 = new byte[2];
                    rbuf2[0] = 0xFC; rbuf2[1] = 0x0C;
                    byte[] rout2 = new byte[4];
                    uint dummy3;
                    if (DeviceIoControl(h, rCtl, rbuf2, 2, rout2, 4, out dummy3, IntPtr.Zero))
                    {
                        uint vid2 = rout2[0] | (rout2[1] << 8);
                        if (vid2 == 0x1022)
                            return string.Format("OK BYTE: w=0x{0:X} r=0x{1:X} method={2} -> AMD!", writeFns[i], readFns[i], method);
                    }
                }
                catch { }
            }
        }
        h.Close();
        return "no combination worked";
    }
}
'@
Add-Type -TypeDefinition $code
[InpOutProbe]::Probe()
