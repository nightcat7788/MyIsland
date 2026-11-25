using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Island.App.Services.Peripheral;

/// <summary>
/// 已打开的 HID 通道：三种发命令的方式 + 带超时的读输入报告。
///
/// 三种传输方式必须都试（同一条命令可能只认其中一种端点）：
///  · 控制端点  —— HidD_SetOutputReport：最省事，但不少设备不回应
///  · 中断 OUT  —— WriteFile 整包：只认中断端点的设备（部分 2.4G 键盘）只有这条路
///  · Feature   —— HidD_SetFeature/SetFeature：部分厂商把命令放在 feature 报告里
///
/// 读用 overlapped + WaitForSingleObject 实现毫秒级超时，超时后 CancelIoEx：
/// 阻塞读叠上并发写会把进程直接杀掉（这是 Windows HID 的老规矩，不是危言耸听）。
/// </summary>
internal sealed class HidChannel : IDisposable
{
    private const int WriteTimeoutMs = 1000;
    private const int CancelGraceMs = 300;

    private readonly object _sync = new();
    private IntPtr _handle;
    private IntPtr _preparsed;
    private int _disposed;

    public HidInterface Info { get; }

    /// <summary>
    /// 打开一个 HID 集合。<paramref name="queryOnly"/> = true 时用 dwDesiredAccess=0 打开：
    /// 系统独占的集合（键鼠/笔/触摸屏）用普通权限打不开，但 0 权限仍能读属性、能力表与 Feature 报告。
    /// </summary>
    public HidChannel(HidInterface info, bool queryOnly = false)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _handle = queryOnly ? HidNative.OpenForQuery(info.Path) : HidNative.OpenForIo(info.Path);
        if (_handle == HidNative.InvalidHandleValue || _handle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            _handle = IntPtr.Zero;
            throw new System.ComponentModel.Win32Exception(err, "打开 HID 设备失败: " + info.Path);
        }
        if (HidNative.HidD_GetPreparsedData(_handle, ref _preparsed))
        {
            // 能力表已随接口描述带在 Info 里，这里只是让 HidP_GetUsageValue 能用
        }
    }

    /// <summary>读到的报告里能否按 usage 解析（能力表已就绪）。</summary>
    public bool CanParseUsages => _preparsed != IntPtr.Zero;

    /// <summary>控制端点发送（HidD_SetOutputReport）：首字节是报告 ID。</summary>
    public bool WriteControl(byte[] report)
    {
        if (report is null || report.Length == 0) return false;
        lock (_sync)
        {
            if (IsDisposed || _handle == IntPtr.Zero) return false;
            byte[] buffer = Pad(report, Info.OutputReportLength);
            try
            {
                if (HidNative.HidD_SetOutputReport(_handle, buffer, buffer.Length)) return true;
            }
            catch
            {
                // 某些驱动对 SetOutputReport 直接抛异常：不算失败，接着走中断 OUT
            }
            return false;
        }
    }

    /// <summary>中断 OUT 端点发送（WriteFile）：整包长度必须等于集合的 output report 长度。</summary>
    public bool WriteInterrupt(byte[] report)
    {
        if (report is null || report.Length == 0) return false;
        lock (_sync)
        {
            if (IsDisposed || _handle == IntPtr.Zero) return false;
            byte[] buffer = Pad(report, Info.OutputReportLength);
            return WriteOverlapped(buffer);
        }
    }

    /// <summary>Feature 报告发送。</summary>
    public bool WriteFeature(byte[] report)
    {
        if (report is null || report.Length == 0) return false;
        lock (_sync)
        {
            if (IsDisposed || _handle == IntPtr.Zero) return false;
            byte[] buffer = Pad(report, Info.FeatureReportLength);
            try
            {
                return HidNative.HidD_SetFeature(_handle, buffer, buffer.Length);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>读 Feature 报告（首字节填报告 ID）。</summary>
    public byte[]? GetFeature(byte reportId, int length)
    {
        lock (_sync)
        {
            if (IsDisposed || _handle == IntPtr.Zero) return null;
            int size = Info.FeatureReportLength > 0 ? Info.FeatureReportLength : Math.Max(length, 2);
            var buffer = new byte[size];
            buffer[0] = reportId;
            try
            {
                if (!HidNative.HidD_GetFeature(_handle, buffer, buffer.Length)) return null;
            }
            catch
            {
                return null;
            }
            return buffer;
        }
    }

    /// <summary>读一个输入报告；超时返回 null。</summary>
    public byte[]? Read(int timeoutMs)
    {
        if (timeoutMs < 0) timeoutMs = 0;
        int length = Info.InputReportLength > 0 ? Info.InputReportLength : 64;
        var buffer = new byte[length];
        lock (_sync)
        {
            if (IsDisposed || _handle == IntPtr.Zero) return null;

            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            IntPtr evt = HidNative.CreateEvent(IntPtr.Zero, true, false, null);
            if (evt == IntPtr.Zero)
            {
                pin.Free();
                return null;
            }
            bool keepAlive = false;
            try
            {
                var overlapped = new NativeOverlapped { EventHandle = evt };
                bool ok = HidNative.ReadFile(_handle, pin.AddrOfPinnedObject(), length, out int read, ref overlapped);
                if (ok) return Trim(buffer, read);

                int err = Marshal.GetLastWin32Error();
                if (err != HidNative.ERROR_IO_PENDING) return null;

                int wait = HidNative.WaitForSingleObject(evt, (uint)timeoutMs);
                if (wait != HidNative.WAIT_OBJECT_0)
                {
                    // 超时：取消挂起的 I/O 并短暂等待它真正结束，避免缓冲区被提前释放
                    HidNative.CancelIoEx(_handle, ref overlapped);
                    if (HidNative.WaitForSingleObject(evt, CancelGraceMs) != HidNative.WAIT_OBJECT_0)
                    {
                        // 驱动赖着不结束：缓冲区只能保活，不能释放
                        keepAlive = true;
                        return null;
                    }
                    return null;
                }

                if (!HidNative.GetOverlappedResult(_handle, ref overlapped, out uint transferred, false)) return null;
                return Trim(buffer, (int)transferred);
            }
            catch
            {
                return null;
            }
            finally
            {
                HidNative.CloseHandle(evt);
                if (!keepAlive) pin.Free();
            }
        }
    }

    /// <summary>按 HID 描述符解析某个 usage 的数值（标准电量的读法，不猜字节偏移）。</summary>
    public bool TryReadUsageValue(ushort usagePage, ushort usage, byte[] report, out int value)
    {
        value = 0;
        if (_preparsed == IntPtr.Zero) return false;
        return HidNative.TryGetUsageValue(_preparsed, HidNative.HIDP_FEATURE, usagePage, usage, report, out value);
    }

    private bool WriteOverlapped(byte[] data)
    {
        GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        IntPtr evt = HidNative.CreateEvent(IntPtr.Zero, true, false, null);
        if (evt == IntPtr.Zero)
        {
            pin.Free();
            return false;
        }
        try
        {
            var overlapped = new NativeOverlapped { EventHandle = evt };
            bool ok = HidNative.WriteFile(_handle, pin.AddrOfPinnedObject(), data.Length, out int written, ref overlapped);
            if (ok) return true;
            int err = Marshal.GetLastWin32Error();
            if (err != HidNative.ERROR_IO_PENDING) return false;
            if (HidNative.WaitForSingleObject(evt, WriteTimeoutMs) != HidNative.WAIT_OBJECT_0)
            {
                HidNative.CancelIoEx(_handle, ref overlapped);
                HidNative.WaitForSingleObject(evt, CancelGraceMs);
                return false;
            }
            return HidNative.GetOverlappedResult(_handle, ref overlapped, out uint transferred, false);
        }
        catch
        {
            return false;
        }
        finally
        {
            HidNative.CloseHandle(evt);
            pin.Free();
        }
    }

    /// <summary>把短帧补齐到集合的报告长度（Windows 要求整包长度）。</summary>
    private static byte[] Pad(byte[] report, int declaredLength)
    {
        int size = declaredLength > 0 ? declaredLength : report.Length;
        if (size == report.Length) return report;
        var buffer = new byte[size];
        Array.Copy(report, buffer, Math.Min(report.Length, size));
        return buffer;
    }

    private static byte[]? Trim(byte[] buffer, int count)
    {
        if (count <= 0) return null;
        if (count >= buffer.Length) return buffer;
        var result = new byte[count];
        Array.Copy(buffer, result, count);
        return result;
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_sync)
        {
            if (_preparsed != IntPtr.Zero)
            {
                HidNative.HidD_FreePreparsedData(_preparsed);
                _preparsed = IntPtr.Zero;
            }
            if (_handle != IntPtr.Zero && _handle != HidNative.InvalidHandleValue)
                HidNative.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
