using System.Text.RegularExpressions;

namespace Island.Avalonia;

/// <summary>
/// 启动时的内核 panic 侦测（2026-11-25 14:59 事件后加的）。
///
/// 背景：那天发生 DCPEXT1 固件 panic（dual_pipe.c:321）整机重启。归因结论是
/// 苹果显示协处理器固件 bug，但我们的「自动关闭内建屏」让机器长期处于
/// 「内建屏禁用 + 外接屏」的双管道拓扑，无法排除诱因关联（一次样本定不了论）。
/// 这个类的目的不是预防 panic（用户态做不到），而是**每次启动自动留证据**：
/// 上次会话以来的 panic 会被写进 app.log，复发时用户拿着日志就能对上，
/// 不用等他想起去翻 /Library/Logs/DiagnosticReports。
///
/// 签名（如 dual_pipe.c:321）是比对的关键：若下次还是同一函数，基本坐实
/// 「拓扑诱因」，届时应移除/重做自动关屏功能；换函数则更可能是独立固件 bug。
/// </summary>
internal static partial class PanicWatch
{
    /// <summary>上次会话结束（或本次启动前）之后新出现的内核 panic。null = 没有。</summary>
    public static PanicInfo? ScanSince(DateTime sessionStartUtc)
    {
        try
        {
            var dir = new DirectoryInfo("/Library/Logs/DiagnosticReports");
            if (!dir.Exists) return null;   // 目录不存在：干净机器 / 权限异常都算「没有」

            // panic 文件的文件名带秒级时间戳：panic-full-2026-11-25-150019.0002.panic
            // 用文件名时间比 LastWriteTime 稳（panic 文件由 root 写，时间即 panic 时刻）。
            // mtime 会在某些归档/同步工具下变，文件名不会。
            var rx = FileNameTime();
            foreach (var f in dir.EnumerateFiles("*.panic").OrderByDescending(f => f.Name))
            {
                var m = rx.Match(f.Name);
                if (!m.Success) continue;
                if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd-HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var at)) continue;
                // 本地时区已换算成 UTC；与调用方给的 UTC 比较
                if (at <= sessionStartUtc) continue;   // 旧 panic（上上次会话的事，app.log.1 里已经记过）
                return ReadSignature(f, at);
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panic] scan failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>从 panic 文件头部提取签名行。文件可能很大（2MB+）且由 root 写——读不了就如实说。</summary>
    private static PanicInfo? ReadSignature(FileInfo f, DateTime at)
    {
        try
        {
            // panicString 在文件头部；只读前 64KB 足够，也避免大文件整读
            using var fs = f.OpenRead();
            var buf = new byte[Math.Min(64 * 1024, fs.Length)];
            var got = fs.Read(buf, 0, buf.Length);
            var head = System.Text.Encoding.UTF8.GetString(buf, 0, got);
            // JSON 化的 panic 文件里 panicString 是带 \n 转义的字符串；裸 panic 文件则是原文。
            // 两种都取第一行 panic(...)。
            var m = PanicLine().Match(head);
            string sig = m.Success ? m.Groups[1].Value.Trim() : "(panicString 未能解析)";
            return new PanicInfo(at.ToLocalTime(), sig, f.Name);
        }
        catch (Exception ex)
        {
            // 典型：权限（root 属主 644 通常读得到，但不赌）
            return new PanicInfo(at.ToLocalTime(), $"(无法读取 panic 文件: {ex.GetType().Name})", f.Name);
        }
    }

    internal sealed record PanicInfo(DateTime At, string Signature, string File);

    [GeneratedRegex(@"panic-(?:full-)?(\d{4}-\d{2}-\d{2}-\d{6})")]
    private static partial Regex FileNameTime();

    [GeneratedRegex(@"panic\(([^""]{1,300}?)(?:\n|\\n|"")")]
    private static partial Regex PanicLine();
}
