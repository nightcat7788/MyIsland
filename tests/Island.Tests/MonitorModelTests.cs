using Island.Core.Monitoring;

namespace Island.Tests;

/// <summary>监控快照模型的用例：默认值、不可变语义与格式化。</summary>
internal static class MonitorModelTests
{
    public static void Run(Action<string, bool> check)
    {
        var snap = new MonitorSnapshot();
        check("MonitorSnapshot 默认读数是空的",
            snap.CpuUsage == 0 && snap.MemoryUsagePercent == 0 && snap.GpuUsage == 0
            && snap.DiskUsagePercent == 0 && snap.DownloadSpeed == 0 && snap.UploadSpeed == 0
            && snap.UptimeHours == 0 && snap.ProcessCount == 0 && snap.ThreadCount == 0);
        check("MonitorSnapshot 默认文本字段",
            snap.GpuName == "" && snap.LinkType == "" && snap.DriveName == "C:" && snap.PingMs == -1);
        check("MonitorSnapshot 默认没有进程/磁盘/核心列表",
            snap.TopProcesses is null && snap.TopProcessesByMem is null && snap.Disks is null
            && snap.CoreUsages is null && snap.BatteryPercent is null && snap.CpuTemp is null);
        check("MonitorSnapshot 默认时间戳是当前时间",
            Math.Abs((DateTime.Now - snap.Timestamp).TotalMinutes) < 1);

        var a = new MonitorSnapshot { CpuUsage = 10, GpuName = "RTX" };
        var b = a with { CpuUsage = 20 };
        check("MonitorSnapshot 是不可变快照（with 不改原对象）",
            a.CpuUsage == 10 && b.CpuUsage == 20 && b.GpuName == "RTX" && b.Timestamp == a.Timestamp);
        var stamp = new DateTime(2026, 1, 1);
        check("内容相同的两个快照按值相等",
            new MonitorSnapshot { Timestamp = stamp, CpuUsage = 5, GpuName = "X" }
            == new MonitorSnapshot { Timestamp = stamp, CpuUsage = 5, GpuName = "X" });

        check("FormatBytes 按单位换算",
            MonitorSnapshot.FormatBytes(512) == "512 B"
            && MonitorSnapshot.FormatBytes(2048) == "2.0 KB"
            && MonitorSnapshot.FormatBytes(5 * 1024 * 1024) == "5.0 MB"
            && MonitorSnapshot.FormatBytes(3.0 * 1024 * 1024 * 1024) == "3.0 GB");
        // 胶囊实际在用的是 FormatSpeedFixed（恒 6 字符、防排版抖动）。
        // 原断言测的是已 [Obsolete] 的 CompactSpeed，且期望值（两位小数）与实现（F1）对不上 ——
        // 门禁在 Mac 上跑不起来后，这条脱节一直没被发现。
        check("FormatSpeedFixed 输出定宽网速（数值 5 字符 + 单位 1 字符）",
            MonitorSnapshot.FormatSpeedFixed(3.4 * 1024 * 1024) == "  3.4M"
            && MonitorSnapshot.FormatSpeedFixed(420 * 1024) == "420.0K"
            && MonitorSnapshot.FormatSpeedFixed(999) == "999.0B"
            && MonitorSnapshot.FormatSpeedFixed(1024) == "  1.0K");
        check("FormatSpeedFixed 宽度恒定（防排版抖动的核心契约）",
            new[] { 0.5, 999.0, 1024.0, 999.9 * 1024, 1024 * 1024.0, 999.9 * 1024 * 1024, 1073741824.0 }
                .Select(MonitorSnapshot.FormatSpeedFixed).All(s => s.Length == 6));
        check("FormatSpeedFixed 坏值不抛（NaN / Inf / 负数）",
            MonitorSnapshot.FormatSpeedFixed(double.NaN) == "  0.0B"
            && MonitorSnapshot.FormatSpeedFixed(double.PositiveInfinity) == "  0.0B"
            && MonitorSnapshot.FormatSpeedFixed(-5) == "  0.0B");
        check("FormatSpeed 输出带单位的速率",
            MonitorSnapshot.FormatSpeed(0) == "0 B/s"
            && MonitorSnapshot.FormatSpeed(1536) == "1.5 KB/s"
            && MonitorSnapshot.FormatSpeed(2 * 1024 * 1024) == "2.0 MB/s");
        var text = new MonitorSnapshot
        {
            TodayDownload = 1024, TodayUpload = 2048, SessionDownload = 4096, SessionUpload = 8192,
        }.SessionText;
        check("SessionText 含今日与会话累计",
            text.Contains("今日") && text.Contains("会话")
            && text.Contains("1.0 KB") && text.Contains("2.0 KB"));

        var disk = new DiskStat();
        check("DiskStat 默认值",
            disk.Name == "" && disk.ActivePercent == 0 && disk.ReadSpeed == 0
            && disk.WriteSpeed == 0 && disk.UsedGB == 0 && disk.TotalGB == 0);
        var proc = new TopProcess();
        check("TopProcess 默认值",
            proc.Name == "" && proc.CpuPercent == 0 && proc.MemoryMB == 0 && proc.ExePath == "");
        check("ToString 汇总 CPU/内存/网速",
            new MonitorSnapshot { CpuUsage = 42, MemoryUsagePercent = 60 }
                .ToString().Contains("CPU 42%"));
    }
}
