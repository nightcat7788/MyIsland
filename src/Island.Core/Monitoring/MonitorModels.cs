using System.IO;
namespace Island.Core.Monitoring;

/// <summary>一次系统监控采样的不可变快照。UI 只消费快照，不碰采集线程。</summary>
public sealed record MonitorSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;

    public double CpuUsage { get; init; }
    public double[]? CoreUsages { get; init; }
    public double CpuFrequencyGHz { get; init; }

    public double MemoryUsagePercent { get; init; }
    public double MemoryUsedGB { get; init; }
    public double MemoryTotalGB { get; init; }

    public double GpuUsage { get; init; }
    public string GpuName { get; init; } = "";

    /// <summary>磁盘活动时间百分比（PDH PhysicalDisk(_Total)\% Disk Time，LiteMonitor 同款）。</summary>
    public double DiskUsagePercent { get; init; }
    /// <summary>磁盘读/写速度（字节/秒）。</summary>
    public double DiskReadSpeed { get; init; }
    public double DiskWriteSpeed { get; init; }
    /// <summary>系统盘已用/总空间 GB（30 秒采样一次）。</summary>
    public double DriveUsedGB { get; init; }
    public double DriveTotalGB { get; init; }
    public string DriveName { get; init; } = "C:";

    /// <summary>每块物理盘一行的读数（PDH PhysicalDisk 实例，最多 4 块；空数组=无实例）。</summary>
    public DiskStat[]? Disks { get; init; }

    /// <summary>CPU 占用最高的进程 Top5（3 秒一采；首采为空数组）。</summary>
    public TopProcess[]? TopProcesses { get; init; }
    /// <summary>内存占用最高的进程 Top5（BrimDeck 进程榜的内存档）。</summary>
    public TopProcess[]? TopProcessesByMem { get; init; }

    /// <summary>本机电池电量 %（无电池/台式机为 null，30 秒一采）。</summary>
    public double? BatteryPercent { get; init; }
    /// <summary>是否充电中。</summary>
    public bool BatteryCharging { get; init; }

    /// <summary>CPU/GPU/SSD 温度 ℃（Avalonia 版由 ThermalService 合并注入；WPF 版走独立 ThermalSnapshot）。</summary>
    public double? CpuTemp { get; init; }
    public double? GpuTemp { get; init; }
    public double? SsdTemp { get; init; }
    /// <summary>主板温度 ℃。</summary>
    public double? MotherboardTemp { get; init; }
    /// <summary>显存已用/总量 GB。</summary>
    public double? GpuMemoryUsedGB { get; init; }
    public double? GpuMemoryTotalGB { get; init; }
    /// <summary>
    /// 显存读数的**口径**："vendor"（厂商显存）/ "dedicated"（D3D 专用显存）/ "shared"（D3D 共享内存）/ ""（未知）。
    ///
    /// ⚠️ 共享口径下那个"总量"是**共享内存池大小（≈ 系统内存）**，不是显存容量 ——
    /// 显示层必须靠它决定要不要显示"总量"，否则用户会以为核显有十几 GB 显存（误导性读数）。
    /// 取值定义见 Island.App.Services.GpuMemoryKinds（判不出来就留空，**不猜**）。
    /// </summary>
    public string GpuMemoryKind { get; init; } = "";
    /// <summary>GPU 核心频率 MHz。</summary>
    public double? GpuCoreClockMHz { get; init; }

    /// <summary>
    /// 系统热状态档位（macOS 专用：NSProcessInfo.thermalState）。
    /// ""= 不适用（Windows / 读取失败）；"正常"/"偏高"/"较高"/"过高"。
    /// 温度数值在 macOS 无 root 不可读（2026-11-23 实测），UI 用它替代 "--" 提供热信息，
    /// serious 以上按过热配色。
    /// </summary>
    public string ThermalState { get; init; } = "";

    /// <summary>下行速度（字节/秒）。</summary>
    public double DownloadSpeed { get; init; }
    /// <summary>上行速度（字节/秒）。</summary>
    public double UploadSpeed { get; init; }

    /// <summary>本次会话累计下载（字节）。</summary>
    public long SessionDownload { get; init; }
    /// <summary>本次会话累计上传（字节）。</summary>
    public long SessionUpload { get; init; }
    /// <summary>今日累计下载（字节，跨天归零）。</summary>
    public long TodayDownload { get; init; }
    /// <summary>今日累计上传（字节，跨天归零）。</summary>
    public long TodayUpload { get; init; }
    /// <summary>当前活跃连接类型：wifi / ethernet / ""。</summary>
    public string LinkType { get; init; } = "";

    /// <summary>系统运行时长（小时，TickCount64）。</summary>
    public double UptimeHours { get; init; }
    /// <summary>进程数（PDH System\Processes）。</summary>
    public int ProcessCount { get; init; }
    /// <summary>线程数（PDH System\Threads）。</summary>
    public int ThreadCount { get; init; }
    /// <summary>已提交内存 GB（commit charge，pagefile 口径）。</summary>
    public double CommitGB { get; init; }
    /// <summary>提交上限 GB。</summary>
    public double CommitLimitGB { get; init; }

    /// <summary>网络延迟 ms（ping 223.5.5.5；-1 = 未知/离线）。</summary>
    public long PingMs { get; init; } = -1;

    /// <summary>各读数的可用性与读不到的原因（Reading&lt;T&gt; 三态）。老代码仍可直接读数值字段。</summary>
    public ReadingStatus Status { get; init; } = ReadingStatus.Empty;

    public string SessionText =>
        $"今日 ↓{FormatBytes(TodayDownload)} ↑{FormatBytes(TodayUpload)} · 会话 ↓{FormatBytes(SessionDownload)} ↑{FormatBytes(SessionUpload)}";

    public static string FormatBytes(double bytes)
    {
        if (bytes < 1024) return $"{bytes:F0} B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double v = bytes / 1024;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:F1} {units[i]}";
    }

    public override string ToString() =>
        $"CPU {CpuUsage:F0}% | MEM {MemoryUsagePercent:F0}% | ↓{FormatSpeed(DownloadSpeed)} ↑{FormatSpeed(UploadSpeed)}";

    /// <summary>
    /// 网速格式化：4.7 MB/s。
    ///
    /// 关键：单位进位基于**显示值**（四舍五入后）判断，否则 1048575.9 B/s 会显示成
    /// "1024.0 KB/s"——看着像 1MB/s，实际小 1000 倍（原实现 scale-then-round 的经典 bug）。
    ///
    /// 算法：先按 1024 逐级缩小求出候选单位；若候选值四舍五入后仍然 ≥1024
    /// （如 1023.99995 → 1024.0），说明该进位了，再进一级。
    /// </summary>
    public static string FormatSpeed(double bytesPerSec)
    {
        if (double.IsNaN(bytesPerSec) || double.IsInfinity(bytesPerSec) || bytesPerSec < 1) return "0 B/s";
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
        double v = bytesPerSec;
        int i = 0;
        while (i < units.Length - 1)
        {
            double next = v / 1024;
            if (next < 1)
            {
                // 下一级不足 1，本来该停；但当前值若四舍五入后正好 1024（如 1023.99995），
                // 显示会是 "1024.0 KB/s" —— 看着像 1MB/s 但小 1000 倍，必须再进一级。
                if (Math.Round(v, 1) >= 1024) { v = next; i++; }
                break;
            }
            v = next; i++;
            if (Math.Round(v, 1) < 1024) break;          // 四舍五入后不到 1024，单位对了
        }
        return $"{v:F1} {units[i]}";
    }

    /// <summary>
    /// 定宽网速（胶囊用）：固定 6 字符，永不因数值变化改变宽度。
    ///
    /// 为什么需要：胶囊里每个读数后面跟着一串元素，宽度一变整排就抖。
    /// 原实现靠"占位样本 + 实际宽度取大"来凑，输出长度在 4~10 字符间跳变时仍会抖，
    /// 而且占位字符串是手写的、和格式规则脱钩。
    ///
    /// 形态：数值固定 5 字符（含小数点，不足左补空格），单位固定 1 字符。
    ///   " 1.5M"  "  12K"  " 999B"  "1.00G"
    /// </summary>
    public static string FormatSpeedFixed(double bytesPerSec)
    {
        if (double.IsNaN(bytesPerSec) || double.IsInfinity(bytesPerSec) || bytesPerSec < 1) return "  0.0B";
        string[] units = { "B", "K", "M", "G", "T" };
        double v = bytesPerSec;
        int i = 0;
        while (i < units.Length - 1)
        {
            double next = v / 1024;
            if (next < 1)
            {
                if (Math.Round(v, 1) >= 1024) { v = next; i++; }
                break;
            }
            v = next; i++;
            if (Math.Round(v, 1) < 1024) break;
        }
        double shown = Math.Min(v, 999.9);
        string num = shown >= 100 ? $"{shown:F1}" : shown >= 10 ? $" {shown:F1}" : $"  {shown:F1}";
        return num + units[i];
    }

    /// <summary>
    /// 紧凑网速（BrimDeck 刘海同款）：3.40M / 420K / 123B。
    /// Windows 侧的 clock / monitor 视图与展开后的监控页用这一档（宽度靠占位样本兜住）；
    /// combo / dash 两个胶囊已改用 <see cref="FormatSpeedFixed"/>（恒 6 字符，与 mac 的胶囊口径一致）。
    /// 两者是刻意分开的两种格式，不要互相替换。
    /// </summary>
    public static string CompactSpeed(double bytesPerSec)
    {
        if (double.IsNaN(bytesPerSec) || double.IsInfinity(bytesPerSec) || bytesPerSec < 1) return "0B";
        if (bytesPerSec >= 1073741824.0) return $"{bytesPerSec / 1073741824.0:F2}G";
        if (bytesPerSec >= 1048576.0) return $"{bytesPerSec / 1048576.0:F2}M";
        if (bytesPerSec >= 1024.0) return $"{bytesPerSec / 1024.0:F0}K";
        return $"{bytesPerSec:F0}B";
    }
}

/// <summary>一块物理盘的读数快照（活动% + 读/写速度 + 空间）。</summary>
public sealed record DiskStat
{
    public string Name { get; init; } = "";       // "C:" / "D:"（表盘字母取其首字符）
    public double ActivePercent { get; init; }    // 活动时间 %（0..100）
    public double ReadSpeed { get; init; }        // 字节/秒
    public double WriteSpeed { get; init; }
    public double UsedGB { get; init; }
    public double TotalGB { get; init; }
}

/// <summary>一个高占用进程（CPU% 全机口径 + 内存 MB + 可执行文件路径）。</summary>
public sealed record TopProcess
{
    public string Name { get; init; } = "";
    public double CpuPercent { get; init; }       // 0..100（已按核心数归一）
    public double MemoryMB { get; init; }
    /// <summary>可执行文件路径（图标提取用；无权限时为空）。</summary>
    public string ExePath { get; init; } = "";
}
