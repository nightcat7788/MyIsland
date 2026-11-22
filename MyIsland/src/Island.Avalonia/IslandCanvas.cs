using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Island.App.Animation;
using Island.App.Themes;
using Island.Core.Monitoring;

namespace Island.Avalonia;

/// <summary>
/// 岛体自绘（Avalonia 版，与 Windows 版 IslandCanvas/MonitorPage 同源）：
/// 收起态 = dash 仪表盘 / clock 时钟；展开态 = ftop 式系统监控页
/// （CPU 表盘行 → 核心柱 → MEM 进度条 → GPU/硬盘行 → 网速行 → 右栏 Top 进程）。
/// </summary>
public sealed class IslandCanvas : Control
{
    public Rect BodyRect { get; set; }          // 窗口 DIP 坐标
    public bool IsExpanded { get; set; }
    public string View { get; set; } = "dash";  // dash / clock / pet
    public Point LookPoint { get; set; }        // 光标相对本体中心（pet 视线）
    public bool PointerNear { get; set; }
    public PetFaceAv Pet { get; } = new();
    public string Page { get; set; } = "monitor";   // monitor / quota（展开态页）
    public MonitorSnapshot? Snapshot { get; set; }
    public Island.Core.Quota.QuotaSnapshot? Quota { get; set; }
    public Island.App.Services.WeatherInfo? Weather { get; set; }   // 时钟视图右缘
    public IReadOnlyList<ClipItem> Clips { get; set; } = Array.Empty<ClipItem>();
    public double[]? NetHistory { get; set; }
    public Palette Palette { get; set; } = Palette.Dark;

    // 页签热区（绘制时记录，宿主点击命中用）
    private Rect _tabMonitor, _tabQuota, _tabClips;

    /// <summary>页签点击命中；命中返回 true 并切页。</summary>
    public bool HandleTabClick(Point pos)
    {
        if (!IsExpanded) return false;
        if (_tabMonitor.Contains(pos)) { Page = "monitor"; InvalidateVisual(); return true; }
        if (_tabQuota.Contains(pos)) { Page = "quota"; InvalidateVisual(); return true; }
        if (_tabClips.Contains(pos)) { Page = "clips"; InvalidateVisual(); return true; }
        return false;
    }

    private static readonly Color CpuC = Color.FromRgb(0x2E, 0x9B, 0xFF);
    private static readonly Color MemC = Color.FromRgb(0x4F, 0xE3, 0xD7);
    private static readonly Color GpuC = Color.FromRgb(0xF2, 0x72, 0xA8);
    private static readonly Color DiskC = Color.FromRgb(0x8F, 0xA8, 0xF0);
    private static readonly Color DownC = Color.FromRgb(0x60, 0xC0, 0x78);
    private static readonly Color UpC = Color.FromRgb(0xD8, 0x90, 0x78);
    private static readonly Color PowerC = Color.FromRgb(0xE8, 0xC8, 0x8F);

    private static Color TempColor(double? t, Palette pal) => t switch
    {
        null => Color.FromRgb(0x90, 0xA8, 0xF0),
        <= 60 => Color.FromRgb(0x60, 0xC0, 0x78),
        <= 75 => pal.Warning,
        _ => pal.Up,
    };

    public override void Render(DrawingContext dc)
    {
        var pal = Palette;
        var r = BodyRect;
        if (r.Width < 40 || r.Height < 20) return;

        if (IsExpanded && r.Width > 300)
        {
            DrawExpanded(dc, pal, r);
            return;
        }

        double corner = r.Height / 2;
        dc.DrawRectangle(RenderKit.Faint(pal.Surface, 0.92), RenderKit.PenOf(pal.Rim, 1, 0.45),
            new RoundedRect(r, corner));

        double cy = r.Y + r.Height / 2;
        if (View == "pet")
        {
            Pet.DiskActive = Snapshot?.DiskUsagePercent ?? 0;   // 情境（晕眩）
            Pet.SurfaceColor = pal.Surface;                     // 眼睑遮罩跟随主题
            Pet.Draw(dc, r.X + r.Width / 2, cy, 1.0, 1, LookPoint, PointerNear);
        }
        else if (View == "clock") DrawClock(dc, pal, r, cy);
        else if (View == "combo") DrawCombo(dc, pal, r, cy);
        else DrawDash(dc, pal, r, cy);
    }

    /// <summary>combo 信息胶囊（与 Windows 版同源）：配额环 + 时间 + 读数表盘 + 网速。</summary>
    private void DrawCombo(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        var snap = Snapshot;
        if (snap is null) return;
        dc.DrawRectangle(RenderKit.Faint(Color.FromRgb(0x0D, 0x11, 0x17), 0.92), null,
            new RoundedRect(r, 16));
        double x = r.X + 14;

        // ① 配额环（满环 + 首字母）
        if (Quota is { } q)
        {
            double rx0 = x;
            foreach (var p in q.Providers)
            {
                if (p.Status is not Island.Core.Quota.ProviderStatus.Connected) continue;
                var baseColor = QuotaColor(p.Id, pal);
                var arc = p.UsedPercent is { } upct ? RenderKit.LevelColor(upct, baseColor, pal) : baseColor;
                RenderKit.Ring(dc, new Point(x + 10, cy), 20, 2.4,
                    p.UsedPercent is { } u ? u / 100 : null,
                    pal.Track, arc, pal.Text,
                    p.DisplayName.Length > 0 ? p.DisplayName[..1] : "?");
                x += 20 + 7;
            }
            if (x > rx0) x += 6;
        }

        // ② 时间
        var clock = RenderKit.Text(DateTime.Now.ToString("HH:mm"), 15, pal.Text, true);
        RenderKit.Baseline(dc, clock, x, Math.Round(cy + 15 * 0.36));
        x += Math.Ceiling(RenderKit.Text("00:00", 15, pal.Text, true).WidthIncludingTrailingWhitespace) + 16;

        // ③ 读数表盘
        x += DrawDialGauge(dc, pal, x, cy, snap.CpuUsage, CpuC, "C") + 13;
        x += DrawDialGauge(dc, pal, x, cy, snap.MemoryUsagePercent, MemC, "M") + 13;
        if (snap.GpuUsage > 0)
            x += DrawDialGauge(dc, pal, x, cy, snap.GpuUsage, GpuC, "G") + 13;

        // ④ 右缘网速
        var down = RenderKit.Text("↓" + MonitorSnapshot.FormatSpeed(snap.DownloadSpeed), 10, DownC);
        var up = RenderKit.Text("↑" + MonitorSnapshot.FormatSpeed(snap.UploadSpeed), 10, UpC);
        double rx = r.Right - 14;
        dc.DrawText(up, new Point(rx - up.Width, cy - up.Height - 1));
        dc.DrawText(down, new Point(rx - down.Width, cy + 1));
    }

    /// <summary>读数表盘 + 右侧数字（最宽样本预留），返回占用宽度。</summary>
    private double DrawDialGauge(DrawingContext dc, Palette pal, double x, double yc,
        double? usage, Color arc, string letter)
    {
        const double dialSize = 18, font = 11.5;
        RenderKit.Dial(dc, new Point(x + dialSize / 2, yc), dialSize, Math.Max(1.4, dialSize * 0.11),
            usage / 100, pal.Track, arc, pal.Text, letter);
        double at = x + dialSize + 4;
        string figure = usage is { } u ? $"{Math.Round(u)}%" : "—";
        var color = usage is { } u2 ? RenderKit.LevelColor(u2, pal.Text, pal) : pal.TextTertiary;
        RenderKit.Baseline(dc, RenderKit.Text(figure, font, color, true), at, Math.Round(yc + font * 0.36));
        double slot = Math.Ceiling(RenderKit.Text("100%", font, pal.Text, true).WidthIncludingTrailingWhitespace);
        return dialSize + 4 + slot;
    }

    // ==================== 展开态：页签 + 监控页 / AI 用量页 ====================
    private void DrawExpanded(DrawingContext dc, Palette pal, Rect r)
    {
        dc.DrawRectangle(RenderKit.Faint(pal.Surface, 0.96), RenderKit.PenOf(pal.Rim, 1, 0.45),
            new RoundedRect(r, 20));

        // ---- 页签（BrimDeck 式分段切换的极简版） ----
        double tabX = r.X + 20, tabY = r.Y + 14;
        void Tab(string label, string page, ref Rect rect)
        {
            var t = RenderKit.Text(label, 12, Page == page ? pal.Text : pal.TextDim, Page == page);
            rect = new Rect(tabX - 6, tabY - 4, t.Width + 12, t.Height + 8);
            if (Page == page)
                dc.DrawRectangle(RenderKit.Faint(pal.Accent, 0.22), null, new RoundedRect(rect, 7));
            dc.DrawText(t, new Point(tabX, tabY));
            tabX += t.Width + 24;
        }
        Tab("监控", "monitor", ref _tabMonitor);
        Tab("AI 用量", "quota", ref _tabQuota);
        Tab("剪贴板", "clips", ref _tabClips);

        double contentY = tabY + 24 + 16;
        if (Page == "quota") { DrawQuotaPage(dc, pal, r, contentY); return; }
        if (Page == "clips") { DrawClipsPage(dc, pal, r, contentY); return; }
        DrawMonitorPage(dc, pal, r, contentY);
    }

    /// <summary>剪贴板页：最近 20 条（纯本地内存，不写盘）。</summary>
    private void DrawClipsPage(DrawingContext dc, Palette pal, Rect r, double y0)
    {
        double x = r.X + 24, y = y0;
        if (Clips.Count == 0)
        {
            var t = RenderKit.Text("复制文字后自动记录（仅本地内存）", 12, pal.TextDim);
            dc.DrawText(t, new Point(x, y + 30));
            return;
        }
        foreach (var c in Clips.Take(9))
        {
            var time = RenderKit.Text(c.At.ToString("HH:mm"), 10, pal.TextTertiary);
            dc.DrawText(time, new Point(x, y + 2));
            var prev = RenderKit.Trimmed(c.Preview, 11.5, pal.Text, r.Width - 130, false);
            dc.DrawText(prev, new Point(x + 52, y));
            y += 26;
        }
    }

    /// <summary>AI 用量页（BrimDeck 分列式；数据来自 Island.Core.Quota 本地 JSONL 扫描）。</summary>
    private void DrawQuotaPage(DrawingContext dc, Palette pal, Rect r, double y0)
    {
        var q = Quota;
        double x = r.X + 24;
        if (q is null)
        {
            var t = RenderKit.Text("正在读取本地 AI 工具用量…", 12, pal.TextDim);
            dc.DrawText(t, new Point(x, y0 + 40));
            return;
        }
        foreach (var p in q.Providers.Take(3))
        {
            var name = RenderKit.Text(p.DisplayName, 15, pal.Text, true);
            dc.DrawText(name, new Point(x, y0));
            var status = RenderKit.Text(p.StatusText, 9.5, pal.TextTertiary);
            dc.DrawText(status, new Point(x, y0 + 22));

            double? used = p.UsedPercent;
            var pctColor = used is { } u ? RenderKit.LevelColor(u, QuotaColor(p.Id, pal), pal) : pal.TextTertiary;
            var pct = RenderKit.Text(used is { } u2 ? $"{Math.Round(u2)}%" : "—", 22, pctColor, true);
            dc.DrawText(pct, new Point(x, y0 + 38));

            double barY = y0 + 72;
            const double bw = 132;
            dc.DrawRectangle(RenderKit.BrushOf(pal.Track), null, new RoundedRect(new Rect(x, barY, bw, 4), 2));
            if (used is { } u3 && u3 > 0)
                dc.DrawRectangle(RenderKit.BrushOf(pctColor), null,
                    new RoundedRect(new Rect(x, barY, Math.Max(3, bw * Math.Min(1, u3 / 100)), 4), 2));

            var today = RenderKit.Text($"今日 {FmtTokens(p.TokensToday)}", 10, pal.TextDim);
            dc.DrawText(today, new Point(x, y0 + 86));
            if (p.CostToday > 0)
            {
                var cost = RenderKit.Text($"${p.CostToday:F2}（估）", 9.5, pal.TextTertiary);
                dc.DrawText(cost, new Point(x, y0 + 102));
            }
            if (!string.IsNullOrEmpty(p.Detail))
            {
                var det = RenderKit.Text(p.Detail, 9, pal.TextTertiary);
                dc.DrawText(det, new Point(x, y0 + 120));
            }
            x += 164;
        }
    }

    private static Color QuotaColor(string id, Palette pal) => id switch
    {
        "claude" => Color.FromRgb(0xD9, 0x77, 0x57),
        "codex" => Color.FromRgb(0x10, 0xA3, 0x7F),
        "cursor" => Color.FromRgb(0x5A, 0x96, 0xFF),
        _ => pal.Accent,
    };

    private static string FmtTokens(long n) =>
        n >= 1_000_000 ? $"{n / 1e6:F1}M" : n >= 1000 ? $"{n / 1000:F0}K" : n.ToString();

    // ==================== 展开态：系统监控页（ftop 式，与 Windows 版 MonitorPage 同源） ====================
    private void DrawMonitorPage(DrawingContext dc, Palette pal, Rect r, double y0)
    {
        var s = Snapshot;
        // 左右双栏：左 62% 读数区，右 Top 进程
        double x0 = r.X + 20, lw = r.X + 20 + (r.Width - 40) * 0.62, w = lw - x0 - 6;
        double y = y0;
        const double dial = 26;

        // ---------- CPU 行 ----------
        RenderKit.Dial(dc, new Point(x0 + dial / 2, y + dial / 2), dial, 2.6,
            s is null ? null : s.CpuUsage / 100, pal.Track, CpuC, pal.Text, "C");
        double x = x0 + dial + 10;
        double baseline = Math.Round(y + dial / 2 + 21 * 0.36);
        var num = RenderKit.Text(s is null ? "—" : $"{Math.Round(s.CpuUsage)}", 21, pal.Text, true);
        RenderKit.Baseline(dc, num, x, baseline); x += num.WidthIncludingTrailingWhitespace + 2;
        var pctU = RenderKit.Text("%", 11, pal.TextDim);
        RenderKit.Baseline(dc, pctU, x, baseline); x += pctU.WidthIncludingTrailingWhitespace + 10;
        if (s is not null && s.CpuFrequencyGHz > 0.1)
        {
            var d = RenderKit.Text($"{s.CpuFrequencyGHz:F1} GHz", 11, pal.TextDim);
            RenderKit.Baseline(dc, d, x, baseline); x += d.WidthIncludingTrailingWhitespace + 10;
        }
        // CPU 温度（分级配色，读不到 "—"）
        if (s is not null)
        {
            var dt = RenderKit.Text(s.CpuTemp is { } t2 ? $"{Math.Round(t2)}°" : "—", 11,
                s.CpuTemp is { } t3 ? TempColor(t3, pal) : pal.TextTertiary, true);
            RenderKit.Baseline(dc, dt, x, baseline);
        }
        RenderKit.DrawSpark(dc, new Rect(x0 + w * 0.52, y, w - w * 0.52, 26), null, CpuC, 36);
        y += 34;

        // ---------- 核心柱 ----------
        var cores = s?.CoreUsages;
        if (cores is not null && cores.Length > 0)
        {
            int n = Math.Min(16, cores.Length);
            double gap = 4, bw = (w - gap * (n - 1)) / n, bh = 42;
            for (int i = 0; i < n; i++)
            {
                double v = Math.Clamp(cores[i] / 100.0, 0, 1);
                double bx = x0 + i * (bw + gap);
                dc.DrawRectangle(RenderKit.BrushOf(pal.Track), null, new RoundedRect(new Rect(bx, y, bw, bh), 3));
                double fill = Math.Max(2, v * bh);
                dc.DrawRectangle(
                    RenderKit.BrushOf(RenderKit.LevelColor(v * 100, v > 0.6 ? CpuC : MemC, pal)), null,
                    new RoundedRect(new Rect(bx, y + bh - fill, bw, fill), 3));
            }
        }
        y += 50;

        // ---------- MEM 行 + 全宽进度条 ----------
        RenderKit.Dial(dc, new Point(x0 + dial / 2, y + dial / 2), dial, 2.6,
            s is null ? null : s.MemoryUsagePercent / 100, pal.Track, MemC, pal.Text, "M");
        if (s is not null)
        {
            x = x0 + dial + 10;
            double bl = Math.Round(y + dial / 2 + 15 * 0.36);
            var used = RenderKit.Text($"{s.MemoryUsedGB:F1}", 15, pal.Text, true);
            var tot = RenderKit.Text($" / {s.MemoryTotalGB:F0} GB", 11, pal.TextDim);
            RenderKit.Baseline(dc, used, x, bl); x += used.WidthIncludingTrailingWhitespace;
            RenderKit.Baseline(dc, tot, x, bl);
            var mp = RenderKit.Text($"{Math.Round(s.MemoryUsagePercent)}%", 11,
                RenderKit.LevelColor(s.MemoryUsagePercent, pal.TextDim, pal), true);
            RenderKit.Baseline(dc, mp, lw - 6 - mp.WidthIncludingTrailingWhitespace, bl);
        }
        y += 30;
        double memFrac = s is null ? 0 : Math.Clamp(s.MemoryUsagePercent / 100, 0, 1);
        dc.DrawRectangle(RenderKit.BrushOf(pal.Track), null, new RoundedRect(new Rect(x0, y, w - 6, 5), 2.5));
        if (memFrac > 0.002)
            dc.DrawRectangle(RenderKit.BrushOf(MemC), null,
                new RoundedRect(new Rect(x0, y, Math.Max(4, (w - 6) * memFrac), 5), 2.5));
        y += 14;

        // ---------- GPU 行（有读数才画） ----------
        if (s is not null && s.GpuUsage > 0)
        {
            RenderKit.Dial(dc, new Point(x0 + dial / 2, y + dial / 2), dial, 2.6,
                s.GpuUsage / 100, pal.Track, GpuC, pal.Text, "G");
            x = x0 + dial + 10;
            double bl = Math.Round(y + dial / 2 + 15 * 0.36);
            var gnum = RenderKit.Text($"{Math.Round(s.GpuUsage)}%", 15, pal.Text, true);
            RenderKit.Baseline(dc, gnum, x, bl); x += gnum.WidthIncludingTrailingWhitespace + 10;
            var gtT = RenderKit.Text(s.GpuTemp is { } g2 ? $"{Math.Round(g2)}°" : "—", 11,
                s.GpuTemp is { } g3 ? TempColor(g3, pal) : pal.TextTertiary, true);
            RenderKit.Baseline(dc, gtT, x, bl); x += gtT.WidthIncludingTrailingWhitespace + 10;
            // 显存
            if (s.GpuMemoryUsedGB is { } gmu)
            {
                var gm = RenderKit.Text(
                    $"显存 {gmu:F1}{(s.GpuMemoryTotalGB is { } gtot ? $"/{gtot:F0}" : "")} GB",
                    10, pal.TextDim);
                RenderKit.Baseline(dc, gm, x, bl);
            }
            if (!string.IsNullOrEmpty(s.GpuName))
            {
                var name = RenderKit.Trimmed(s.GpuName, 10, pal.TextTertiary, w * 0.3, false);
                RenderKit.Baseline(dc, name, lw - 6 - name.WidthIncludingTrailingWhitespace, bl);
            }
            y += 32;
        }

        // ---------- 硬盘行（有读数才画） ----------
        if (s is not null && (s.DiskUsagePercent > 0 || s.DriveTotalGB > 0))
        {
            RenderKit.Dial(dc, new Point(x0 + dial / 2, y + dial / 2), dial, 2.6,
                s.DiskUsagePercent / 100, pal.Track, DiskC, pal.Text, "D");
            x = x0 + dial + 10;
            double bl = Math.Round(y + dial / 2 + 15 * 0.36);
            var dnum = RenderKit.Text($"{Math.Round(s.DiskUsagePercent)}%", 15,
                RenderKit.LevelColor(s.DiskUsagePercent, pal.Text, pal), true);
            RenderKit.Baseline(dc, dnum, x, bl); x += dnum.WidthIncludingTrailingWhitespace + 12;
            var rw = RenderKit.Text(
                $"读 {MonitorSnapshot.FormatSpeed(s.DiskReadSpeed)}  写 {MonitorSnapshot.FormatSpeed(s.DiskWriteSpeed)}",
                10, pal.TextDim);
            RenderKit.Baseline(dc, rw, x, bl); x += rw.WidthIncludingTrailingWhitespace + 10;
            if (s.SsdTemp is { } st)
            {
                var stT = RenderKit.Text($"{Math.Round(st)}°", 11, TempColor(st, pal), true);
                RenderKit.Baseline(dc, stT, x, bl);
            }
            if (s.DriveTotalGB > 0)
            {
                string F(double gb) => gb >= 1024 ? $"{gb / 1024:F1} TB" : $"{gb:F0} GB";
                var drv = RenderKit.Text($"{s.DriveName} {F(s.DriveUsedGB)} / {F(s.DriveTotalGB)}",
                    10, pal.TextTertiary);
                RenderKit.Baseline(dc, drv, lw - 6 - drv.WidthIncludingTrailingWhitespace, bl);
            }
            y += 32;
        }

        // ---------- 电池行（有电池才画）：表盘 + 百分比 + 充电状态 ----------
        if (s?.BatteryPercent is { } bp)
        {
            var bc = s.BatteryCharging ? DownC
                   : bp <= 20 ? pal.Up
                   : bp <= 40 ? pal.Warning
                   : DownC;
            RenderKit.Dial(dc, new Point(x0 + dial / 2, y + dial / 2), dial, 2.6,
                bp / 100, pal.Track, bc, pal.Text, "B");
            x = x0 + dial + 10;
            double bl = Math.Round(y + dial / 2 + 15 * 0.36);
            var bnum = RenderKit.Text($"{Math.Round(bp)}%", 15, pal.Text, true);
            RenderKit.Baseline(dc, bnum, x, bl); x += bnum.WidthIncludingTrailingWhitespace + 10;
            var bstate = RenderKit.Text(s.BatteryCharging ? "充电中" : "电池", 10, pal.TextDim);
            RenderKit.Baseline(dc, bstate, x, bl);
            y += 32;
        }

        // ---------- 网速行 ----------
        if (s is not null)
        {
            double bl = Math.Round(y + 14);
            var di = RenderKit.Text("↓", 13, DownC, true);
            RenderKit.Baseline(dc, di, x0, bl);
            var dr = RenderKit.Text(MonitorSnapshot.FormatSpeed(s.DownloadSpeed), 13, pal.Text, true);
            RenderKit.Baseline(dc, dr, x0 + 16, bl);
            double sparkX = x0 + 16 + dr.WidthIncludingTrailingWhitespace + 12;
            double sparkW = x0 + w / 2 - sparkX - 10;
            if (sparkW > 30)
                RenderKit.DrawSpark(dc, new Rect(sparkX, y, sparkW, 16), NetHistory, DownC, 36);

            double x2 = x0 + w / 2 + 8;
            var ui = RenderKit.Text("↑", 13, UpC, true);
            RenderKit.Baseline(dc, ui, x2, bl);
            var ur = RenderKit.Text(MonitorSnapshot.FormatSpeed(s.UploadSpeed), 13, pal.Text, true);
            RenderKit.Baseline(dc, ur, x2 + 16, bl);
            y += 26;
        }

        // ---------- 右栏：Top 进程（有数据才画） ----------
        var tops = s?.TopProcesses;
        if (tops is { Length: > 0 })
        {
            double rx0 = lw + 14, rw = r.Right - 16 - rx0;
            if (rw > 110)
            {
                dc.DrawLine(RenderKit.PenOf(pal.Track, 1, 0.7),
                    new Point(lw + 4, r.Y + 14), new Point(lw + 4, r.Bottom - 14));
                double ry = r.Y + 16;
                var head = RenderKit.Text("进程占用 Top 5", 9.5, pal.TextTertiary);
                RenderKit.Baseline(dc, head, rx0, Math.Round(ry + 9));
                ry += 18;
                foreach (var p in tops.Take(5))
                {
                    double bl = Math.Round(ry + 11);
                    var pct = RenderKit.Text($"{p.CpuPercent:F1}%", 11,
                        RenderKit.LevelColor(p.CpuPercent, pal.Text, pal), true);
                    var mem = RenderKit.Text(p.MemoryMB >= 1024
                        ? $"{p.MemoryMB / 1024:F1} GB" : $"{p.MemoryMB:F0} MB", 9.5, pal.TextTertiary);
                    double pctSlot = Math.Max(pct.WidthIncludingTrailingWhitespace, 42);
                    var name = RenderKit.Trimmed(p.Name, 11, pal.Text,
                        rw - pctSlot - mem.WidthIncludingTrailingWhitespace - 14, false);
                    RenderKit.Baseline(dc, name, rx0, bl);
                    RenderKit.Baseline(dc, mem, rx0 + name.WidthIncludingTrailingWhitespace + 7, bl);
                    RenderKit.Baseline(dc, pct, r.Right - 16 - pct.WidthIncludingTrailingWhitespace, bl);
                    ry += 22;
                }
            }
        }
    }

    // ==================== 收起态 ====================
    private void DrawDash(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        var snap = Snapshot;
        if (snap is null) return;
        dc.DrawRectangle(RenderKit.Faint(Color.FromRgb(0x0D, 0x11, 0x17), 0.92), null,
            new RoundedRect(r, 16));

        double x = r.X + 14;
        void Item(string glyph, Color color, string value, string sample, Color? valueColor = null)
        {
            var icon = RenderKit.Text(glyph, 10, color, true);
            dc.DrawText(icon, new Point(x, cy - 13));
            var val = RenderKit.Text(value, 13, valueColor ?? Colors.White, true);
            dc.DrawText(val, new Point(x, cy + 3));
            double slot = Math.Max(RenderKit.Text(sample, 13, pal.Text, true).Width, icon.Width);
            x += Math.Max(slot, val.Width) + 26;
        }

        var cores = snap.CoreUsages;
        if (cores is not null && cores.Length > 0)
        {
            int n = Math.Min(8, cores.Length);
            double bw = 4, gap = 2.6, maxH = 26;
            double baseY = cy + maxH / 2 - 4;
            for (int i = 0; i < n; i++)
            {
                double v = Easing.Clamp(cores[i] / 100.0, 0, 1);
                dc.DrawRectangle(RenderKit.BrushOf(pal.Track), null,
                    new RoundedRect(new Rect(x + i * (bw + gap), baseY - maxH, bw, maxH), 1.6));
                double h = 2 + v * maxH;
                dc.DrawRectangle(
                    RenderKit.BrushOf(RenderKit.LevelColor(v * 100, Color.FromRgb(0x60, 0xA8, 0xF0), pal)), null,
                    new RoundedRect(new Rect(x + i * (bw + gap), baseY - h, bw, h), 1.6));
            }
            x += n * (bw + gap) + 26;
        }

        // ① CPU 温度 ② GPU 温度（读不到 "--"，Mac 上 LHM 无传感器自动隐藏读数）
        Item("CPU", TempColor(snap.CpuTemp, pal),
            snap.CpuTemp is { } c1 ? $"{Math.Round(c1)}°" : "--", "125°");
        Item("GPU", TempColor(snap.GpuTemp, pal),
            snap.GpuTemp is { } g1 ? $"{Math.Round(g1)}°" : "--", "125°");
        Item("MEM", Color.FromRgb(0xA8, 0xF0, 0xF0), $"{snap.MemoryUsagePercent:F0}%", "100%",
            RenderKit.LevelColor(snap.MemoryUsagePercent, Colors.White, pal));
        // 电池（有电池才显示；充电中绿色，低电量分级）
        if (snap.BatteryPercent is { } bp)
        {
            var bc = snap.BatteryCharging ? DownC
                   : bp <= 20 ? pal.Up
                   : bp <= 40 ? pal.Warning
                   : Color.FromRgb(0xA8, 0xF0, 0xA8);
            Item(snap.BatteryCharging ? "电+" : "电", bc, $"{Math.Round(bp)}%", "100%",
                bp <= 20 ? pal.Up : bp <= 40 ? pal.Warning : Colors.White);
        }
        if (snap.GpuUsage > 0)
            Item("GPU%", Color.FromRgb(0xF0, 0xF0, 0xA8), $"{snap.GpuUsage:F0}%", "100%",
                RenderKit.LevelColor(snap.GpuUsage, Colors.White, pal));
        if (snap.DiskUsagePercent > 0)
            Item("盘", Color.FromRgb(0x8F, 0xA8, 0xF0), $"{snap.DiskUsagePercent:F0}%", "100%",
                RenderKit.LevelColor(snap.DiskUsagePercent, Colors.White, pal));
        // SSD 温度（有读数或有磁盘才显示；读不到 "--"）
        if (snap.SsdTemp is not null || snap.DiskUsagePercent > 0)
            Item("SSD", TempColor(snap.SsdTemp, pal),
                snap.SsdTemp is { } s1 ? $"{Math.Round(s1)}°" : "--", "125°");
        Item("↓", Color.FromRgb(0x60, 0xA8, 0xF0), MonitorSnapshot.FormatSpeed(snap.DownloadSpeed).Replace(" ", ""), "9.9MB/s");
        Item("↑", Color.FromRgb(0xD8, 0x90, 0x30), MonitorSnapshot.FormatSpeed(snap.UploadSpeed).Replace(" ", ""), "9.9MB/s");
    }

    private void DrawClock(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        var t = RenderKit.Text(DateTime.Now.ToString("HH:mm"), 15, pal.Text, true);
        dc.DrawText(t, new Point(r.X + 16, cy - t.Height / 2));
        var snap = Snapshot;
        double rx = r.Right - 14;
        if (snap is not null)
        {
            var down = RenderKit.Text("↓" + MonitorSnapshot.FormatSpeed(snap.DownloadSpeed), 10, pal.TextDim);
            var up = RenderKit.Text("↑" + MonitorSnapshot.FormatSpeed(snap.UploadSpeed), 10, pal.TextDim);
            dc.DrawText(up, new Point(rx - up.Width, cy - up.Height - 1));
            dc.DrawText(down, new Point(rx - down.Width, cy + 1));
            rx -= Math.Max(up.Width, down.Width) + 12;
        }
        // 天气（与 Windows 版同款：emoji + 温度，空间够才画）
        if (Weather is { } w)
        {
            var temp = RenderKit.Text($"{Math.Round(w.Temp)}°", 12, pal.Text, true);
            var emoji = RenderKit.Text(w.Emoji, 12, pal.Text);
            double wx = rx - emoji.Width - 3 - temp.Width;
            if (wx > r.X + 16 + t.Width + 8)
            {
                dc.DrawText(emoji, new Point(wx, cy - emoji.Height / 2));
                dc.DrawText(temp, new Point(wx + emoji.Width + 3, cy - temp.Height / 2));
            }
        }
    }
}
