using System.Windows;
using System.Windows.Media;
using Island.App.Animation;
using Island.App.Services;
using Island.App.Themes;

namespace Island.App.Controls;

/// <summary>
/// 设备电量页（完全对齐 BrimDeck devices.png 参考图）：
/// 每设备一列 —— 大电量环（分级配色，轨道+圆弧）+ 环内纯白极简矢量剪影图标（告别彩色系统 Emoji）
/// + 矢量闪电充电角标 + 大号百分比（数字大、% 小）+ 设备名 + 状态行。
/// </summary>
public sealed class DevicesPage : FrameworkElement
{
    public Palette? Palette { get; set; }

    private double Ppd
    {
        get
        {
            try
            {
                double v = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                return double.IsFinite(v) && v > 0 ? v : 1.0;
            }
            catch { return 1.0; }
        }
    }

    private static Color LevelColor(DeviceInfo d, Palette pal)
    {
        if (d.Charging || d.PluggedIn) return Color.FromRgb(0x3F, 0xC1, 0x5E);
        if (d.Percent is >= 0 and <= 20) return pal.Up;
        if (d.Percent is >= 0 and <= 40) return pal.Warning;
        return d.Kind == "battery" ? Color.FromRgb(0x3F, 0xB8, 0xFF) : pal.Text;
    }

    private static string StatusText(DeviceInfo d)
    {
        if (d.Charging) return !string.IsNullOrWhiteSpace(d.Subtitle) ? d.Subtitle! : "充电中";
        if (d.Percent is >= 0 and <= 20) return "电量低";
        if (d.Percent is >= 0 and <= 40) return "电量偏低";
        if (!string.IsNullOrWhiteSpace(d.Subtitle)) return d.Subtitle!;
        return KindLabel(d.Kind);
    }

    private static string KindLabel(string kind) => kind switch
    {
        "mouse" => "无线鼠标",
        "keyboard" => "无线键盘",
        "headset" => "无线耳机",
        "earbuds" => "真无线耳机",
        "gamepad" => "无线控制器",
        "battery" => "本机电池",
        _ => kind,
    };

    protected override void OnRender(DrawingContext dc)
    {
        var pal = Palette ?? Palette.Dark;
        double W = ActualWidth, H = ActualHeight;
        if (W < 100 || H < 80) return;
        double ppd = Ppd;

        var devices = DeviceService.Snapshot;
        if (devices.Count == 0)
        {
            var empty = RenderKit.Text("未检测到带电量汇报的无线外设（蓝牙 / 2.4G HID）或本机电池", 11.5, pal.TextDim, ppd: ppd);
            dc.DrawText(empty, new Point((W - empty.Width) / 2, H / 2 - empty.Height / 2));
            return;
        }

        int n = Math.Min(devices.Count, 6);
        double colW = Math.Clamp(W / n, 88, 130);
        double totalW = colW * n;
        double x0 = (W - totalW) / 2;
        double topY = Math.Max(10, (H - 180) / 2);

        for (int i = 0; i < n; i++)
        {
            var d = devices[i];
            var color = LevelColor(d, pal);
            double cx = x0 + colW * i + colW / 2;
            double y = topY;

            // 1. 大电量环（充电中但固件未回报具体百分比时以完整绿环示意，不伪造 0%）
            const double ringR = 30, stroke = 5;
            double ringCy = y + ringR;
            double share = d.Percent >= 0 ? d.Percent / 100.0 : (d.Charging ? 1.0 : 0.05);
            dc.DrawEllipse(null, RenderKit.PenOf(pal.Track, stroke, 0.85), new Point(cx, ringCy), ringR, ringR);
            dc.DrawGeometry(null, RenderKit.PenOf(color, stroke), Arc(cx, ringCy, ringR, share));

            // 2. 环内纯白极简矢量设备剪影（与 devices.png 视觉对齐）
            DrawDeviceSilhouette(dc, d.Kind, d.Name, new Point(cx, ringCy), pal.Text, pal.Surface);

            // 3. 充电/接通电源角标：右下绿色实心圆 + 矢量闪电
            if (d.Charging || d.PluggedIn)
            {
                double bx = cx + ringR * 0.72, by = ringCy + ringR * 0.72;
                dc.DrawEllipse(RenderKit.BrushOf(pal.Surface), null, new Point(bx, by), 10.5, 10.5);
                dc.DrawEllipse(RenderKit.BrushOf(Color.FromRgb(0x3F, 0xC1, 0x5E)), null, new Point(bx, by), 8.5, 8.5);
                var bolt = new StreamGeometry();
                using (var bctx = bolt.Open())
                {
                    bctx.BeginFigure(new Point(bx + 0.8, by - 4.6), true, true);
                    bctx.LineTo(new Point(bx - 3.2, by + 0.5), false, false);
                    bctx.LineTo(new Point(bx - 0.2, by + 0.5), false, false);
                    bctx.LineTo(new Point(bx - 1.0, by + 4.6), false, false);
                    bctx.LineTo(new Point(bx + 3.2, by - 0.5), false, false);
                    bctx.LineTo(new Point(bx + 0.2, by - 0.5), false, false);
                }
                bolt.Freeze();
                dc.DrawGeometry(RenderKit.BrushOf(Color.FromRgb(0x0D, 0x11, 0x17)), null, bolt);
            }
            y += ringR * 2 + 14;

            // 4. 大号百分比（固件充电中未回报百分比时显示“充电”，绝不伪造数值）
            if (d.Percent >= 0)
            {
                var num = RenderKit.Text($"{d.Percent}", 20, color, true, ppd);
                var pct = RenderKit.Text("%", 11, color, ppd: ppd);
                double numW = num.WidthIncludingTrailingWhitespace + pct.WidthIncludingTrailingWhitespace;
                RenderKit.Baseline(dc, num, cx - numW / 2, Math.Round(y + 20 * 0.8));
                RenderKit.Baseline(dc, pct, cx - numW / 2 + num.WidthIncludingTrailingWhitespace + 1, Math.Round(y + 20 * 0.8));
            }
            else
            {
                var chg = RenderKit.Text(d.Charging ? "充电" : "--", 16, color, true, ppd);
                RenderKit.Baseline(dc, chg, cx - chg.Width / 2, Math.Round(y + 18 * 0.8));
            }
            y += 30;

            // 5. 设备名与状态行
            var name = RenderKit.Trimmed(d.Name, 11, pal.Text, colW - 10, false, ppd);
            dc.DrawText(name, new Point(cx - name.Width / 2, y));
            y += 18;

            var statusColor = d.Charging || d.PluggedIn || d.Percent <= 40 ? color : pal.TextDim;
            var status = RenderKit.Text(StatusText(d), 9.5, statusColor, ppd: ppd);
            dc.DrawText(status, new Point(cx - status.Width / 2, y));
        }
    }

    private static void DrawDeviceSilhouette(DrawingContext dc, string kind, string name, Point c, Color fg, Color cutout)
    {
        var fill = RenderKit.BrushOf(fg);
        var cut = RenderKit.BrushOf(cutout);
        var stroke = RenderKit.PenOf(fg, 2.0);
        var cutPen = RenderKit.PenOf(cutout, 1.4);

        bool isEarbuds = kind == "earbuds" || name.Contains("AirPods", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Buds", StringComparison.OrdinalIgnoreCase);

        if (isEarbuds)
        {
            // 双耳塞剪影（devices.png 第 4 列）
            dc.DrawEllipse(fill, null, new Point(c.X - 5.2, c.Y - 3.5), 4.2, 4.2);
            dc.DrawRoundedRectangle(fill, null, RenderKit.SafeRect(c.X - 4.2, c.Y - 1, 2.8, 10.5), 1.4, 1.4);
            dc.DrawEllipse(fill, null, new Point(c.X + 5.2, c.Y - 3.5), 4.2, 4.2);
            dc.DrawRoundedRectangle(fill, null, RenderKit.SafeRect(c.X + 1.4, c.Y - 1, 2.8, 10.5), 1.4, 1.4);
            return;
        }

        switch (kind)
        {
            case "mouse":
                // 无线鼠标实心剪影 + 按键分界线（devices.png 第 1 列）
                dc.DrawRoundedRectangle(fill, null, RenderKit.SafeRect(c.X - 7.5, c.Y - 11, 15, 22), 7.5, 7.5);
                dc.DrawLine(cutPen, new Point(c.X - 7.5, c.Y - 2), new Point(c.X + 7.5, c.Y - 2));
                dc.DrawLine(cutPen, new Point(c.X, c.Y - 11), new Point(c.X, c.Y - 2));
                break;

            case "keyboard":
                // 无线键盘实心剪影 + 键帽镂空点与空格条（devices.png 第 2 列）
                dc.DrawRoundedRectangle(fill, null, RenderKit.SafeRect(c.X - 11.5, c.Y - 7, 23, 14), 3.2, 3.2);
                for (int r = 0; r < 2; r++)
                {
                    for (int k = 0; k < 5; k++)
                        dc.DrawEllipse(cut, null, new Point(c.X - 7.6 + k * 3.8, c.Y - 3.6 + r * 3.4), 1.0, 1.0);
                }
                dc.DrawRoundedRectangle(cut, null, RenderKit.SafeRect(c.X - 5.5, c.Y + 2.8, 11, 2.0), 1, 1);
                break;

            case "headset":
                // 头戴耳机剪影（devices.png 第 3 列）
                {
                    var arc = new StreamGeometry();
                    using (var ctx = arc.Open())
                    {
                        ctx.BeginFigure(new Point(c.X - 8.5, c.Y + 2), false, false);
                        ctx.ArcTo(new Point(c.X + 8.5, c.Y + 2), new Size(8.5, 9), 0, false, SweepDirection.Clockwise, true, false);
                    }
                    arc.Freeze();
                    dc.DrawGeometry(null, stroke, arc);
                    dc.DrawRoundedRectangle(fill, null, RenderKit.SafeRect(c.X - 10, c.Y + 0.5, 4.2, 8.5), 2, 2);
                    dc.DrawRoundedRectangle(fill, null, RenderKit.SafeRect(c.X + 5.8, c.Y + 0.5, 4.2, 8.5), 2, 2);
                }
                break;

            case "gamepad":
                // 手柄剪影 + 十字键与按键镂空（devices.png 第 5 列）
                dc.DrawRoundedRectangle(fill, null, RenderKit.SafeRect(c.X - 11.5, c.Y - 6.5, 23, 13), 6, 6);
                dc.DrawLine(cutPen, new Point(c.X - 7, c.Y), new Point(c.X - 3, c.Y));
                dc.DrawLine(cutPen, new Point(c.X - 5, c.Y - 2), new Point(c.X - 5, c.Y + 2));
                dc.DrawEllipse(cut, null, new Point(c.X + 5, c.Y - 2), 1.0, 1.0);
                dc.DrawEllipse(cut, null, new Point(c.X + 7, c.Y), 1.0, 1.0);
                dc.DrawEllipse(cut, null, new Point(c.X + 5, c.Y + 2), 1.0, 1.0);
                dc.DrawEllipse(cut, null, new Point(c.X + 3, c.Y), 1.0, 1.0);
                break;

            default:
                // 笔记本/本机电池矢量框
                dc.DrawRoundedRectangle(null, stroke, RenderKit.SafeRect(c.X - 9.5, c.Y - 5.5, 17, 11), 2.5, 2.5);
                dc.DrawRoundedRectangle(fill, null, RenderKit.SafeRect(c.X + 8.5, c.Y - 2.5, 2.2, 5), 1, 1);
                dc.DrawRoundedRectangle(fill, null, RenderKit.SafeRect(c.X - 6.8, c.Y - 2.8, 11.5, 5.6), 1.2, 1.2);
                break;
        }
    }

    private static StreamGeometry Arc(double cx, double cy, double radius, double fraction)
    {
        fraction = Easing.Clamp(fraction, 0.02, 1);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double start = -Math.PI / 2;
            double end = start + fraction * 2 * Math.PI;
            var p0 = new Point(cx + radius * Math.Cos(start), cy + radius * Math.Sin(start));
            var p1 = new Point(cx + radius * Math.Cos(end), cy + radius * Math.Sin(end));
            ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        return geo;
    }
}
