using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
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
    private int _page;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        int pages = (DeviceService.Snapshot.Count + 4) / 5;
        if (pages <= 1) return;
        _page = (_page + 1) % pages;
        InvalidateVisual();
        e.Handled = true;
    }

    private double Ppd => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    /// <summary>电量分级：充电绿 / ≤20 红 / ≤40 橙 / 其余主题文字色。</summary>
    private static Color LevelColor(DeviceInfo d, Palette pal)
    {
        if (d.Charging) return Color.FromRgb(0x3F, 0xC1, 0x5E);
        if (d.LevelText is "有线" or "未知") return pal.TextDim;
        if (d.Percent <= 20) return pal.Up;
        if (d.Percent <= 40) return pal.Warning;
        return pal.Text;
    }

    private static string StatusText(DeviceInfo d) =>
        d.Charging ? "充电中" :
        d.ObservedAt is { } at ? $"读取于 {at:HH:mm}" :
        d.LevelText == "未知" ? "电量未知" :
        d.Kind == "gamepad" ? (d.LevelText == "有线" ? "有线" : "无线") :
        d.Percent <= 20 ? "电量低" :
        d.Percent <= 40 ? "电量偏低" : KindLabel(d.Kind);

    private static string KindLabel(string kind) => kind switch
    {
        "mouse" => "鼠标", "keyboard" => "键盘", "headset" => "耳机",
        "gamepad" => "手柄", "ble" => "蓝牙设备", "battery" => "本机电池", _ => kind,
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
            // 空状态：一枚图标 + 一句话（以前是一行括号里塞着技术名词的长句）
            Glyphs.Draw(dc, GlyphKind.Battery, RenderKit.SafeRect(W / 2 - 14, H / 2 - 34, 28, 28), pal.TextTertiary, 1.2);
            var empty = RenderKit.Text("没有可显示电量的设备", 12, pal.TextDim, ppd: ppd);
            dc.DrawText(empty, new Point((W - empty.Width) / 2, H / 2 + 6));
            return;
        }

        // 每页最多五列；点击页脚或页面切换，所有设备均可访问。
        int pages = (devices.Count + 4) / 5;
        _page = Math.Clamp(_page, 0, pages - 1);
        int first = _page * 5;
        int n = Math.Min(devices.Count - first, 5);
        if (pages > 1)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            var pager = RenderKit.Text($"{_page + 1}/{pages} 页 · 共 {devices.Count} 台 · 点击切换", 10, pal.TextDim, ppd: ppd);
            dc.DrawText(pager, new Point((W - pager.Width) / 2, H - 18));
        }
        double colW = Math.Clamp(W / n, 96, 130);
        double totalW = colW * n;
        double x0 = (W - totalW) / 2;
        double topY = Math.Max(10, (H - 178) / 2);   // 整组垂直居中（环 64 + 三行文字）

        for (int i = 0; i < n; i++)
        {
            var d = devices[first + i];
            var color = LevelColor(d, pal);
            double cx = x0 + colW * i + colW / 2;
            double y = topY;

            // ---- 大电量环：轨道 + 分级色圆弧（圆头端点靠线帽近似） ----
            const double ringR = 30, stroke = 5;
            double ringCy = y + ringR;
            dc.DrawEllipse(null, RenderKit.PenOf(pal.Track, stroke, 0.85), new Point(cx, ringCy), ringR, ringR);
            if (d.Percent >= 100)
                dc.DrawEllipse(null, RenderKit.PenOf(color, stroke), new Point(cx, ringCy), ringR, ringR);
            else if (d.Percent > 0)
                dc.DrawGeometry(null, RenderKit.PenOf(color, stroke),
                    Arc(cx, ringCy, ringR, d.Percent / 100.0));

            // 环内设备图标（矢量剪影，BrimDeck 设备页同款；认不出的类型给电池轮廓）
            Glyphs.Draw(dc, Glyphs.IconOf(d.Kind), RenderKit.SafeRect(cx - 8, ringCy - 8, 16, 16), pal.Text);

            // 充电角标：绿色圆 + 矢量闪电（不用 emoji：线粗和配色跟主题走）
            if (d.Charging)
            {
                double bx = cx + ringR * 0.72, by = ringCy + ringR * 0.72;
                dc.DrawEllipse(RenderKit.BrushOf(pal.Down), null, new Point(bx, by), 9, 9);
                Glyphs.Draw(dc, GlyphKind.Bolt, RenderKit.SafeRect(bx - 5, by - 5.5, 10, 11), pal.IsDark ? pal.Surface : Colors.White);
            }
            y += ringR * 2 + 14;

            // ---- 大号读数：档位设备显示"空/低/中/满"（系统只给档位，换算成百分比就是伪装）；
            //      百分比设备显示 数字 20px 粗 + % 11px（BrimDeck 同款字重对比） ----
            if (d.LevelText is { } levelText)
            {
                var level = RenderKit.Text(levelText, 20, color, true, ppd);
                RenderKit.Baseline(dc, level, cx - level.WidthIncludingTrailingWhitespace / 2,
                    Math.Round(y + 20 * 0.8));
            }
            else
            {
                var num = RenderKit.Text($"{d.Percent}", 20, color, true, ppd);
                var pct = RenderKit.Text("%", 11, color, ppd: ppd);
                double numW = num.WidthIncludingTrailingWhitespace + pct.WidthIncludingTrailingWhitespace;
                RenderKit.Baseline(dc, num, cx - numW / 2, Math.Round(y + 20 * 0.8));
                RenderKit.Baseline(dc, pct, cx - numW / 2 + num.WidthIncludingTrailingWhitespace + 1,
                    Math.Round(y + 20 * 0.8));
            }
            y += 30;

            // ---- 设备名（超长截断） ----
            var name = RenderKit.Trimmed(d.Name, 11, pal.Text, colW - 10, false, ppd);
            dc.DrawText(name, new Point(cx - name.Width / 2, y));
            y += 18;

            // ---- 状态行（按状态配色；档位设备的百分比颜色已按档位取好） ----
            var statusColor = d.Charging || d.Percent <= 40 ? color : pal.TextDim;
            var status = RenderKit.Text(StatusText(d), 9.5, statusColor, ppd: ppd);
            dc.DrawText(status, new Point(cx - status.Width / 2, y));
        }
    }

    /// <summary>从顶部顺时针的电量弧。</summary>
    private static StreamGeometry Arc(double cx, double cy, double radius, double fraction)
    {
        fraction = Easing.Clamp(fraction, 0, 1);
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
