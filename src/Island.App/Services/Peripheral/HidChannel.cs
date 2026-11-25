using System;
using System.Collections.Generic;
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

    /// <summary>孤儿 I/O 条数上限（见 <see cref="TrackStuck"/>）。</summary>
    private const int MaxStuckIo = 8;

    /// <summary>
    /// 驱动赖着不结束的 I/O（CancelIoEx 之后仍不完成）。
    /// 内核可能还引用着那块缓冲 —— 提前释放 GCHandle 会让数组被 GC 搬移，
    /// 驱动随后写进已搬移的内存 = 堆损坏。所以这些孤儿 handle 收在这里，
    /// 等 I/O 真正结束（事件被置位）之后再释放。
    /// </summary>
    private sealed class StuckIo
    {
        public GCHandle Pin;
        public IntPtr Event;
    }

    private static readonly List<StuckIo> Stuck = new();
    private static readonly object StuckGate = new();

    private readonly object _sync = new();
    private readonly bool _queryOnly;
    private IntPtr _handle;
    private IntPtr _preparsed;
    private int _disposed;
    private int _invalid;   // 通道失效（驱动赖着不结束）：之后一律拒绝 I/O

    public HidInterface Info { get; }

    /// <summary>
    /// 打开一个 HID 集合。<paramref name="queryOnly"/> = true 时用 dwDesiredAccess=0 打开：
    /// 系统独占的集合（键鼠/笔/触摸屏）用普通权限打不开，但 0 权限仍能读属性、能力表与 Feature 报告。
    /// </summary>
    public HidChannel(HidInterface info, bool queryOnly = false)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _queryOnly = queryOnly;
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
            byte[]? buffer = Pad(report, Info.OutputReportLength);
            if (buffer is null) return false;   // 帧比集合的报告长度还长：判失败，不截断
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
            byte[]? buffer = Pad(report, Info.OutputReportLength);
            if (buffer is null) return false;   // 帧超长：判失败，不截断
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
            byte[]? buffer = Pad(report, Info.FeatureReportLength);
            if (buffer is null) return false;   // 帧超长：判失败，不截断
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

        // 只查询打开（dwDesiredAccess=0）的句柄**没有** FILE_FLAG_OVERLAPPED：
        // 这种句柄上的 ReadFile 是同步的，调用方给的超时**完全不生效**，
        // 后台扫描线程可能被永久阻塞。所以只读句柄一律不做读，直接返回 null。
        if (_queryOnly) return null;

        int length = Info.InputReportLength > 0 ? Info.InputReportLength : 64;
        var buffer = new byte[length];
        lock (_sync)
        {
            if (!Usable) return null;
            SweepStuck();   // 上一轮遗留的孤儿 I/O：真结束了的就地回收

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
                        // 驱动赖着不结束：缓冲与事件都必须保活（交给 TrackStuck 收着），
                        // 绝不能在这里释放 —— 否则数组被 GC 搬移，驱动就写已搬移的内存。
                        keepAlive = true;
                        TrackStuck(pin, evt);
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
                // keepAlive 时 pin 与 evt 已交给 TrackStuck 保管，这里**绝不能**再释放
                if (!keepAlive)
                {
                    HidNative.CloseHandle(evt);
                    pin.Free();
                }
            }
        }
    }

    /// <summary>按 HID 描述符解析某个 usage 的数值（标准电量的读法，不猜字节偏移）。</summary>
    public bool TryReadUsageValue(ushort usagePage, ushort usage, byte[] report, out int value)
        => TryReadUsageValue(HidNative.HIDP_FEATURE, usagePage, usage, report, out value);

    /// <summary>
    /// 按 HID 描述符解析某个 usage 的数值，**按报告类型**解析：
    /// Feature 报告用 HIDP_FEATURE，Input 报告用 HIDP_INPUT。
    /// 报告类型给错，描述符解析器根本找不到那个 usage（只漏读，不会假读）。
    /// </summary>
    public bool TryReadUsageValue(int reportType, ushort usagePage, ushort usage, byte[] report,
        out int value)
    {
        value = 0;
        if (_preparsed == IntPtr.Zero) return false;
        return HidNative.TryGetUsageValue(_preparsed, reportType, usagePage, usage, report, out value);
    }

    private bool WriteOverlapped(byte[] data)
    {
        if (!Usable) return false;
        SweepStuck();
        GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        IntPtr evt = HidNative.CreateEvent(IntPtr.Zero, true, false, null);
        if (evt == IntPtr.Zero)
        {
            pin.Free();
            return false;
        }
        bool keepAlive = false;
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
                if (HidNative.WaitForSingleObject(evt, CancelGraceMs) != HidNative.WAIT_OBJECT_0)
                {
                    // 写请求还挂在内核里：缓冲**不能**释放 ——
                    // 释放 pin 等于允许数组被 GC 搬移，驱动随后写的就是已搬移的内存
                    // （use-after-free / 堆损坏）。与 Read 路径同一口径：保活，
                    // 等 I/O 真结束再回收。
                    keepAlive = true;
                    TrackStuck(pin, evt);
                    return false;
                }
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
            // keepAlive 时 pin 与 evt 已交给 TrackStuck 保管，这里**绝不能**再释放
            if (!keepAlive)
            {
                HidNative.CloseHandle(evt);
                pin.Free();
            }
        }
    }

    /// <summary>
    /// 把短帧补齐到集合的报告长度（Windows 要求整包长度）。
    /// 帧比集合的报告长度还长 ⇒ **判失败**（返回 null）：截断等于悄悄改协议帧，
    /// 设备会收到一条内容不同的命令 —— 那比发不出去更糟。
    /// </summary>
    private static byte[]? Pad(byte[] report, int declaredLength)
    {
        if (declaredLength > 0 && report.Length > declaredLength) return null;
        int size = declaredLength > 0 ? declaredLength : report.Length;
        if (size == report.Length) return report;
        var buffer = new byte[size];
        Array.Copy(report, buffer, report.Length);
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

    /// <summary>通道还能不能做 I/O（没被 Dispose、没被标记失效、句柄还在）。</summary>
    private bool Usable => !IsDisposed && Volatile.Read(ref _invalid) == 0 && _handle != IntPtr.Zero;

    /// <summary>
    /// 登记一个"驱动赖着不结束"的 I/O：缓冲与事件都保活，等 I/O 真结束后再释放。
    ///
    /// 集合有上限：超过上限说明驱动彻底不动了 —— 按既有做法把本通道标记失效
    /// （关句柄会连带取消内核里挂起的 I/O，此后这个集合不再发起任何 I/O），
    /// 于是溢出项以"设备数"为界，不会无上限增长。
    /// </summary>
    private void TrackStuck(GCHandle pin, IntPtr evt)
    {
        bool overCap;
        lock (StuckGate)
        {
            overCap = Stuck.Count >= MaxStuckIo;
            Stuck.Add(new StuckIo { Pin = pin, Event = evt });
        }
        if (overCap)
            Invalidate("驱动长时间不结束挂起的 HID I/O，通道已标记失效");
    }

    /// <summary>回收已经真正结束的孤儿 I/O（事件被置位 = 内核不再引用那块缓冲）。</summary>
    private static void SweepStuck()
    {
        lock (StuckGate)
        {
            for (int i = Stuck.Count - 1; i >= 0; i--)
            {
                StuckIo s = Stuck[i];
                if (HidNative.WaitForSingleObject(s.Event, 0) != HidNative.WAIT_OBJECT_0) continue;
                s.Pin.Free();
                HidNative.CloseHandle(s.Event);
                Stuck.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// 关通道时再宽限一会儿：句柄已关，内核里挂起的 I/O 会被取消 → 事件很快置位 →
    /// 那些孤儿缓冲就能安全回收。没结束的继续保活（绝不提前释放），下一轮再收。
    /// </summary>
    private static void DrainStuck(int graceMs)
    {
        int deadline = Environment.TickCount + graceMs;
        lock (StuckGate)
        {
            for (int i = Stuck.Count - 1; i >= 0; i--)
            {
                StuckIo s = Stuck[i];
                int left = Math.Max(0, deadline - Environment.TickCount);
                if (HidNative.WaitForSingleObject(s.Event, (uint)left) != HidNative.WAIT_OBJECT_0)
                    continue;   // 还没结束：继续保活
                s.Pin.Free();
                HidNative.CloseHandle(s.Event);
                Stuck.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// 把通道标记失效：关掉句柄（连带取消内核里挂起的 I/O），之后一律拒绝 I/O。
    /// 这是"驱动彻底坏掉"时的既有做法 —— 与 Dispose 不同，失效的通道只是不能再用。
    /// </summary>
    private void Invalidate(string reason)
    {
        if (Interlocked.Exchange(ref _invalid, 1) != 0) return;
        DeviceService.LogLineThrottled("HID 通道失效：" + reason + "（" + Info.Key + "）",
            TimeSpan.FromMinutes(10));
        lock (_sync)
        {
            if (_handle != IntPtr.Zero && _handle != HidNative.InvalidHandleValue)
                HidNative.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

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
        // 句柄已关：内核里挂起的 I/O 会被取消 → 事件很快置位 → 孤儿缓冲可以回收
        DrainStuck(CancelGraceMs);
    }
}
