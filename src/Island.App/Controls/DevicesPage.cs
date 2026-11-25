using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using Island.App.Services;
using Island.App.Themes;

namespace Island.App.Controls;

/// <summary>
/// 设备电量页（对齐 mac 版 <c>DrawDevicesPage</c> 的**卡片网格**）：每台设备一张 132×150 的小卡片，
/// 卡片里是 60px 电量环（分级配色）+ 环内设备图标 + 26px 大号读数 + 设备名 + 状态行。
///
/// 几何与结构抄 mac（数值全部引用 <see cref="ExpandedPanelSpec"/>，常量不在本文件里另抄一份）：
///  · 卡片 132×150、间距 12 —— 每行卡片数 = 内容区宽 / (132+12)，面板够宽时一行三张；
///  · 高度按行数自适应（mac 口径）：一行 150、两行 150+12+150；
///  · 大号读数 26px **单串**（"58%"），档位设备显示 20px 的档位文字（不画假的 0%）；
///  · 状态行 10px；空态是 mac 文案、11.5px TextDim，落在内容区左上。
///
/// 配色身份保持 Windows 的暗色科技（**不引入** mac 的"按资源配色"）：
/// ≤20 红（pal.Up）/ ≤40 橙（pal.Warning）/ 正常主题文字色 / 充电绿。
///
/// Windows 独有能力保留（平台专属能力不互相搬运）：
///  · 设备类型矢量图标（鼠标 / 键盘 / 耳机 / 手柄，<see cref="Glyphs.IconOf"/>）；
///  · 充电角标（右下绿圆 + 闪电）；
///  · 档位文字（空 / 低 / 中 / 满 / 有线）。
///
/// 电量环走共享的 <see cref="RenderKit.Ring"/>：死区（占比 ≤ 0.004 不画弧）与轨道透明度（0.9）
/// 跟收起态、mac 版都是同一口径，不再在页面里自写一份绕过去。
/// 整页自绘，读数变化只触发重绘；数据直接读 DeviceService.Snapshot（静态，零接线）。
/// </summary>
public sealed class DevicesPage : FrameworkElement
{
    public Palette? Palette { get; set; }

    // ---- 卡片规格（抄 mac，数值统一在 ExpandedPanelSpec 里） ----
    private const double CardW = ExpandedPanelSpec.DeviceCardWidth;    // 132
    private const double CardH = ExpandedPanelSpec.DeviceCardHeight;   // 150
    private const double Gap = ExpandedPanelSpec.DeviceCardGap;        // 12

    /// <summary>卡片左右各留的边距（卡片不贴内容区边缘）。</summary>
    private const double Inset = 4;

    /// <summary>裁剪用的矩形几何（按可视区大小缓存，冻结后可反复用）。</summary>
    private RectangleGeometry? _clipGeo;
    private Rect _clipRect;

    /// <summary>纵向滚动偏移：卡片比可视区高时靠滚轮翻（旧实现是翻页器，现在换成滚动）。</summary>
    private double _scroll;

    /// <summary>当前最多能滚多远（每次渲染按设备数重算）。</summary>
    private double _maxScroll;

    private double Ppd => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    /// <summary>
    /// 滚轮翻卡片行（旧的翻页器删掉了，设备多的时候还得能看完）。
    /// 一格 = 一行卡片；没有超出可视区时不管它。
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_maxScroll <= 0) return;
        double step = CardH + Gap;
        _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta) * step, 0, _maxScroll);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>电量分级：充电绿 / ≤20 红 / ≤40 橙 / 其余主题文字色（Windows 既有口径）。</summary>
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
        // "未知"必须排在 ObservedAt 前面：档位设备永远带 ObservedAt，
        // 以前这个分支永远走不到，外设读不到电量时显示的是"读取于 02:19"而不是"电量未知"（审查发现）。
        d.LevelText == "未知" ? "电量未知" :
        d.ObservedAt is { } at ? $"读取于 {at:HH:mm}" :
        d.Kind == "gamepad" ? (d.LevelText == "有线" ? "有线" : "无线") :
        d.Percent <= 20 ? "电量低" :
        d.Percent <= 40 ? "电量偏低" : KindLabel(d.Kind);

    private static string KindLabel(string kind) => kind switch
    {
        "mouse" => "鼠标", "keyboard" => "键盘", "headset" => "耳机",
        "gamepad" => "手柄", "ble" => "蓝牙设备", "battery" => "本机电池", "other" => "外设", _ => kind,
    };

    protected override void OnRender(DrawingContext dc)
    {
        var pal = Palette ?? Palette.Dark;
        double W = ActualWidth, H = ActualHeight;
        if (W < 100 || H < 80) return;
        double ppd = Ppd;

        var devices = DeviceService.Snapshot;
        double contentX = Inset;
        double contentW = Math.Max(1, W - Inset * 2);

        if (devices.Count == 0)
        {
            // 空状态：图标 + mac 文案（11.5px TextDim），落在内容区左上角（原来在正中间）
            Glyphs.Draw(dc, GlyphKind.Battery, RenderKit.SafeRect(contentX, 2, 16, 16), pal.TextTertiary);
            var empty = RenderKit.Text("未检测到设备（蓝牙 / 2.4G 接收器 / 鼠标键盘耳机，或本机内置电池）",
                ExpandedPanelSpec.DeviceEmptyFont, pal.TextDim, ppd: ppd);
            dc.DrawText(empty, new Point(contentX + 22, 2));
            return;
        }

        // ---- 网格布局 ----
        // 每行卡片数：内容区宽塞得下几张就是几张（132 + 12 一个卡位），至少一张。
        int perRow = Math.Max(1, (int)((contentW + Gap) / (CardW + Gap)));
        int rows = (devices.Count + perRow - 1) / perRow;
        // 高度自适应：行数决定网格高度（mac 口径）
        double gridH = rows * CardH + (rows - 1) * Gap;
        // 装得下就垂直居中，装不下就顶对齐 + 滚轮翻（面板高度由宿主定，这里不改宿主）
        double top = gridH <= H ? Math.Max(0, (H - gridH) / 2) : 0;
        _maxScroll = Math.Max(0, gridH - (H - top));
        _scroll = Math.Clamp(_scroll, 0, _maxScroll);

        // WPF 的 PushClip 返回 void（不像 Avalonia 返回 IDisposable），所以不写 using；
        // 裁剪只作用于本次渲染的后续绘制，正好是下面这圈卡片。
        dc.PushClip(ClipGeo(new Rect(0, 0, W, H)));
        for (int i = 0; i < devices.Count; i++)
        {
            int col = i % perRow, row = i / perRow;
            double cx = contentX + col * (CardW + Gap);
            double cy = top + row * (CardH + Gap) - _scroll;
            // 整张卡都在可视区外就不画（滚出去 / 滚进来时的无用绘制）
            if (cy + CardH < 0 || cy > H) continue;
            DrawCard(dc, pal, ppd, devices[i], cx, cy);
        }
    }

    /// <summary>
    /// 裁剪几何：WPF 的 PushClip 收 Geometry（不是 Rect），按可视区大小缓存一份冻结的矩形几何。
    /// 每次重绘都 new 的话是纯浪费（每帧一个对象）。
    /// </summary>
    private RectangleGeometry ClipGeo(Rect r)
    {
        if (_clipGeo is null || _clipRect != r)
        {
            _clipRect = r;
            _clipGeo = new RectangleGeometry(r);
            _clipGeo.Freeze();
        }
        return _clipGeo;
    }

    /// <summary>一张设备卡片：底板 + 电量环 + 环内图标 + 大号读数 + 设备名 + 状态行。</summary>
    private static void DrawCard(DrawingContext dc, Palette pal, double ppd, DeviceInfo d, double x, double y)
    {
        // 卡片底板：Windows 的卡片配方（3.5% 白 + 1px 发丝边 + 14px 圆角；像素主题直角）
        double r = pal.SquareCorners ? 3 : 14;
        dc.DrawRoundedRectangle(RenderKit.Faint(pal.Text, 0.035), RenderKit.PenOf(pal.Hairline, 1),
            RenderKit.SafeRect(x, y, CardW, CardH), r, r);

        Color color = LevelColor(d, pal);

        // ---- 电量环：直径 60、线宽 5、12 点起顺时针（共享 RenderKit.Ring） ----
        const double ring = 60, thickness = 5;
        double ccx = x + CardW / 2, ccy = y + 20 + ring / 2;
        RenderKit.Ring(dc, new Point(ccx, ccy), ring, thickness, d.Percent / 100.0, pal.Track, color);

        // 环内设备图标（Windows 独有：按设备类型的矢量剪影，认不出的类型给电池轮廓）
        Glyphs.Draw(dc, Glyphs.IconOf(d.Kind), RenderKit.SafeRect(ccx - 8, ccy - 8, 16, 16), pal.Text);

        // 充电角标（Windows 独有）：右下绿圆 + 矢量闪电（不用 emoji：线粗和配色跟主题走）
        if (d.Charging)
        {
            double bx = ccx + 21, by = ccy + 21;
            dc.DrawEllipse(RenderKit.BrushOf(pal.Down), null, new Point(bx, by), 9, 9);
            Glyphs.Draw(dc, GlyphKind.Bolt, RenderKit.SafeRect(bx - 5, by - 5.5, 10, 11),
                pal.IsDark ? pal.Surface : Colors.White);
        }

        // ---- 大号读数：百分比设备 26px 单串（"58%"）；档位设备显示 20px 档位文字（不画假的 0%） ----
        double readoutTop = y + 20 + ring + 6;
        if (d.LevelText is { } levelText)
        {
            var lv = RenderKit.Text(levelText, 20, color, true, ppd);
            dc.DrawText(lv, new Point(ccx - lv.WidthIncludingTrailingWhitespace / 2, readoutTop));
        }
        else
        {
            var pct = RenderKit.Text($"{d.Percent}%", ExpandedPanelSpec.DevicePercentFont, color, true, ppd);
            dc.DrawText(pct, new Point(ccx - pct.WidthIncludingTrailingWhitespace / 2, readoutTop));
        }

        // ---- 设备名（11px，超长截断，居中） ----
        var name = RenderKit.Trimmed(d.Name, 11, pal.Text, CardW - 16, false, ppd);
        dc.DrawText(name, new Point(ccx - name.WidthIncludingTrailingWhitespace / 2, y + 20 + ring + 38));

        // ---- 状态行（10px；档位设备的百分比颜色已按档位取好） ----
        var statusColor = d.Charging || d.Percent <= 40 ? color : pal.TextDim;
        var status = RenderKit.Text(StatusText(d), ExpandedPanelSpec.DeviceStatusFont, statusColor, ppd: ppd);
        dc.DrawText(status, new Point(ccx - status.WidthIncludingTrailingWhitespace / 2, y + 20 + ring + 56));
    }
}
