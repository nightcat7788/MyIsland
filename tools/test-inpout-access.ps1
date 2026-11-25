# Test: can a NON-elevated process open the inpoutx64 device (installed by TM package)?
# If yes -> we can read SMN (CPU temp) ourselves with zero new drivers, zero elevation.
$code = @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class InpOutTest
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    public static string TryOpen()
    {
        string[] names = new string[] { "\\\\.\\inpoutx64", "\\\\.\\inpout32" };
        foreach (string name in names)
        {
            SafeFileHandle h = CreateFileW(name, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!h.IsInvalid)
            {
                h.Close();
                return "OPEN OK: " + name + " (non-elevated!)";
            }
        }
        return "ACCESS DENIED (err=" + Marshal.GetLastWin32Error() + ")";
    }
}
'@
Add-Type -TypeDefinition $code
[InpOutTest]::TryOpen()
