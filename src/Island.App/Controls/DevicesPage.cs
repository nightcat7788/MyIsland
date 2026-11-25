using System.Windows;
using System.Windows.Media;
using Island.App.Animation;
using Island.App.Services;
using Island.App.Themes;

namespace Island.App.Controls;

/// <summary>
/// 设备电量页（BrimDeck devices 页同款）：每设备一列 —— 大电量环（分级配色，轨道+圆弧）
/// + 环内设备图标 + 大号百分比（数字大、% 小）+ 设备名 + 状态行（充电中/电量低/连接方式）。
/// 整页自绘，读数变化只触发重绘；数据直接读 DeviceService.Snapshot（静态，零接线）。
/// </summary>
public sealed class DevicesPage : FrameworkElement
{
    public Palette? Palette { get; set; }

    private double Ppd => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    /// <summary>电量分级：充电绿 / ≤20 红 / ≤40 橙 / 其余主题文字色。</summary>
    private static Color LevelColor(DeviceInfo d, Palette pal)
    {
        if (d.Charging) return Color.FromRgb(0x3F, 0xC1, 0x5E);
        if (d.Percent <= 20) return pal.Up;
        if (d.Percent <= 40) return pal.Warning;
        return pal.Text;
    }

    private static string StatusText(DeviceInfo d) =>
        d.Charging ? "充电中" :
        d.Percent <= 20 ? "电量低" :
        d.Percent <= 40 ? "电量偏低" : KindLabel(d.Kind);

    private static string KindLabel(string kind) => kind switch
    {
        "mouse" => "鼠标", "keyboard" => "键盘", "headset" => "耳机",
        "gamepad" => "手柄", "battery" => "本机电池", _ => kind,
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
            var empty = RenderKit.Text("未检测到无线设备（蓝牙/HID 电量）", 11, pal.TextDim, ppd: ppd);
            dc.DrawText(empty, new Point((W - empty.Width) / 2, H / 2 - empty.Height / 2));
            return;
        }

        // 每列宽度：最多 5 列，列宽 clamp 在 [96, 130]
        int n = Math.Min(devices.Count, 5);
        double colW = Math.Clamp(W / n, 96, 130);
        double totalW = colW * n;
        double x0 = (W - totalW) / 2;
        double topY = Math.Max(10, (H - 178) / 2);   // 整组垂直居中（环 64 + 三行文字）

        for (int i = 0; i < n; i++)
        {
            var d = devices[i];
            var color = LevelColor(d, pal);
            double cx = x0 + colW * i + colW / 2;
            double y = topY;

            // ---- 大电量环：轨道 + 分级色圆弧（圆头端点靠线帽近似） ----
            const double ringR = 30, stroke = 5;
            double ringCy = y + ringR;
            dc.DrawEllipse(null, RenderKit.PenOf(pal.Track, stroke, 0.85), new Point(cx, ringCy), ringR, ringR);
            dc.DrawGeometry(null, RenderKit.PenOf(color, stroke),
                Arc(cx, ringCy, ringR, d.Percent / 100.0));

            // 环内设备图标（白色，居中）
            var glyph = RenderKit.Text(DeviceService.Glyph(d.Kind), 15, pal.Text, true, ppd);
            dc.DrawText(glyph, new Point(cx - glyph.Width / 2, ringCy - glyph.Height / 2));

            // 充电角标：环右下绿色圆 + 闪电
            if (d.Charging)
            {
                double bx = cx + ringR * 0.72, by = ringCy + ringR * 0.72;
                dc.DrawEllipse(RenderKit.BrushOf(Color.FromRgb(0x3F, 0xC1, 0x5E)), null,
                    new Point(bx, by), 9, 9);
                var bolt = RenderKit.Text("⚡", 9, Color.FromRgb(0x0D, 0x11, 0x17), true, ppd);
                dc.DrawText(bolt, new Point(bx - bolt.Width / 2, by - bolt.Height / 2));
            }
            y += ringR * 2 + 14;

            // ---- 大号百分比：数字 20px 粗 + % 11px（BrimDeck 同款字重对比） ----
            var num = RenderKit.Text($"{d.Percent}", 20, color, true, ppd);
            var pct = RenderKit.Text("%", 11, color, ppd: ppd);
            double numW = num.WidthIncludingTrailingWhitespace + pct.WidthIncludingTrailingWhitespace;
            RenderKit.Baseline(dc, num, cx - numW / 2, Math.Round(y + 20 * 0.8));
            RenderKit.Baseline(dc, pct, cx - numW / 2 + num.WidthIncludingTrailingWhitespace + 1,
                Math.Round(y + 20 * 0.8));
            y += 30;

            // ---- 设备名（超长截断） ----
            var name = RenderKit.Trimmed(d.Name, 11, pal.Text, colW - 10, false, ppd);
            dc.DrawText(name, new Point(cx - name.Width / 2, y));
            y += 18;

            // ---- 状态行（按状态配色） ----
            var statusColor = d.Charging || d.Percent <= 40 ? color : pal.TextDim;
            var status = RenderKit.Text(StatusText(d), 9.5, statusColor, ppd: ppd);
            dc.DrawText(status, new Point(cx - status.Width / 2, y));
        }
    }

    /// <summary>从顶部顺时针的电量弧。</summary>
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
