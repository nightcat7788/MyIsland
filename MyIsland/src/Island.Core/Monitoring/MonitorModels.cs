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
    /// <summary>GPU 核心频率 MHz。</summary>
    public double? GpuCoreClockMHz { get; init; }

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

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1) return "0 B/s";
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        int i = 0;
        double v = bytesPerSec;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:F1} {units[i]}";
    }

    /// <summary>紧凑网速（BrimDeck 刘海同款）：3.40M / 420K / 123B。</summary>
    public static string CompactSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024) return $"{bytesPerSec / 1048576:F2}M";
        if (bytesPerSec >= 1024) return $"{bytesPerSec / 1024:F0}K";
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
