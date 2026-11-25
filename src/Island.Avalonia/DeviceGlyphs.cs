using Avalonia;
using Avalonia.Media;

namespace Island.Avalonia;

/// <summary>设备页的 per-type 图标种类（与设备类型词表 battery/ble/mouse/keyboard/headset/gamepad/other 一一对应）。</summary>
internal enum DeviceGlyph
{
    /// <summary>本机电池。</summary>
    Battery,
    /// <summary>蓝牙设备（认不出具体型号）。</summary>
    Bluetooth,
    /// <summary>鼠标。</summary>
    Mouse,
    /// <summary>键盘。</summary>
    Keyboard,
    /// <summary>耳机 / 头戴式耳机。</summary>
    Headset,
    /// <summary>游戏手柄。</summary>
    Gamepad,
    /// <summary>认不出型号的通用外设（兜底）。</summary>
    Other,
}

/// <summary>
/// 设备页的 per-type 矢量图标（16×16 网格、圆头描边）。
///
/// **图形语言抄 Windows 端**（src\Island.App\Controls\Glyphs.cs 的设备四件套 + 电池）：
/// 同样的 16 单位网格、同样的描边粗细算法（thickness×缩放后夹到 0.9~2px）、
/// 同样的"描边矩形向内收半个线宽"处理、同样的圆角处理、同样用主题色画描边与填充。
/// 鼠标 / 键盘 / 耳机 / 手柄 / 电池的坐标与 Windows 端**逐点相同**，两端设备页看上去是同一批图标。
///
/// mac 端多出的两枚（Windows 端没有：它认不出类型时统一给电池轮廓）：
///  · <see cref="DeviceGlyph.Bluetooth"/>：蓝牙符文（竖笔 + 右侧两枚尖角），表示"是蓝牙设备但认不出型号"；
///  · <see cref="DeviceGlyph.Other"/>：通用外设（扁平圆角机身 + 居中圆点），表示"认不出型号的外设"。
///
/// 画法只用 Avalonia 的 DrawingContext（跨平台），不依赖任何 Windows 专属 API。
/// </summary>
internal static class DeviceGlyphs
{
    /// <summary>设备类型词表 → 图标。未知 / 空 / 认不出的类型一律兜底成"通用外设"，不抛异常、不画空白。</summary>
    public static DeviceGlyph Of(string? kind) => kind switch
    {
        "battery" => DeviceGlyph.Battery,
        "ble" => DeviceGlyph.Bluetooth,
        "mouse" => DeviceGlyph.Mouse,
        "keyboard" => DeviceGlyph.Keyboard,
        "headset" => DeviceGlyph.Headset,
        "gamepad" => DeviceGlyph.Gamepad,
        _ => DeviceGlyph.Other,
    };

    /// <summary>
    /// 把图标画进 box（等比缩放、居中）。
    /// 网格与坐标与 Windows 端 Glyphs.Draw 一致：16 单位网格 + 圆头描边 + 描边矩形内收半线宽。
    /// </summary>
    public static void Draw(DrawingContext dc, DeviceGlyph kind, Rect box, Color color, double thickness = 1.5)
    {
        if (!double.IsFinite(box.Width) || !double.IsFinite(box.Height)) return;
        double s = Math.Min(box.Width, box.Height) / 16.0;
        if (s <= 0) return;
        double th = Math.Clamp(thickness * s, 0.9, 2);
        double ox = box.X + (box.Width - 16 * s) / 2, oy = box.Y + (box.Height - 16 * s) / 2;
        Point P(double x, double y) => new(ox + x * s, oy + y * s);
        var pen = RenderKit.PenOf(color, th);
        var brush = RenderKit.BrushOf(color);
        void Line(double x1, double y1, double x2, double y2) => dc.DrawLine(pen, P(x1, y1), P(x2, y2));
        void Dot(double cx, double cy, double r) => dc.DrawEllipse(brush, null, P(cx, cy), r * s, r * s);

        // 描边矩形：向内收半个线宽，描边不会溢出 16×16 网格（与 Windows 端 Box() 同款）。
        // 圆角按轴夹到该轴半边以内 —— WPF 的 RectangleGeometry 会自己夹（半径大的一半），
        // Avalonia 的 RoundedRect 不夹，这里显式夹一次：两端画出来的圆角半径因此一致。
        void Box(double x, double y, double w, double h, double r)
        {
            var rect = new Rect(ox + x * s + th / 2, oy + y * s + th / 2, w * s - th, h * s - th);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            double rx = Math.Min(r * s, rect.Width / 2), ry = Math.Min(r * s, rect.Height / 2);
            dc.DrawRectangle(null, pen, new RoundedRect(rect, rx, ry));
        }

        // 填充矩形（无描边），同样夹圆角。
        void FillBox(double x, double y, double w, double h, double r)
        {
            var rect = new Rect(ox + x * s, oy + y * s, w * s, h * s);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            double rx = Math.Min(r * s, rect.Width / 2), ry = Math.Min(r * s, rect.Height / 2);
            dc.DrawRectangle(brush, null, new RoundedRect(rect, rx, ry));
        }

        switch (kind)
        {
            case DeviceGlyph.Battery:
                // 电池：机身 + 触点 + 内部电量条（与 Windows 端逐点相同）
                Box(1.8, 5.0, 10.6, 6.0, 1.6);
                Line(13.9, 7.0, 13.9, 9.0);
                FillBox(3.3, 6.5, 4.6, 3.0, 0.8);
                break;
            case DeviceGlyph.Bluetooth:
                // 蓝牙符文：中间竖笔 + 右侧两枚尖角（经典蓝牙标志的线条化画法，16 网格）
                Line(8.0, 1.5, 8.0, 14.5);
                Line(8.0, 1.5, 13.0, 4.5);
                Line(13.0, 4.5, 8.0, 8.0);
                Line(8.0, 8.0, 13.0, 11.5);
                Line(13.0, 11.5, 8.0, 14.5);
                break;
            case DeviceGlyph.Mouse:
                // 鼠标：圆角机身 + 顶部滚轮（与 Windows 端逐点相同）
                Box(4.6, 2.6, 6.8, 10.8, 3.2);
                Line(8.0, 4.6, 8.0, 6.8);
                break;
            case DeviceGlyph.Keyboard:
                // 键盘：圆角面板 + 四颗键点 + 空格条（与 Windows 端逐点相同）
                Box(1.8, 5.2, 12.4, 6.6, 1.6);
                Dot(4.4, 7.6, 0.6);
                Dot(6.8, 7.6, 0.6);
                Dot(9.2, 7.6, 0.6);
                Dot(11.6, 7.6, 0.6);
                Line(5.2, 10.0, 10.8, 10.0);
                break;
            case DeviceGlyph.Headset:
                // 耳机：头梁弧 + 两只耳罩（与 Windows 端逐点相同）
                var headband = new StreamGeometry();
                using (var ctx = headband.Open())
                {
                    ctx.BeginFigure(P(2.4, 10.0), false);
                    ctx.ArcTo(P(13.6, 10.0), new Size(5.6 * s, 5.6 * s), 0, false, SweepDirection.Clockwise);
                    ctx.EndFigure(false);
                }
                dc.DrawGeometry(null, pen, headband);
                Box(2.2, 8.6, 2.6, 4.6, 1.2);
                Box(11.2, 8.6, 2.6, 4.6, 1.2);
                break;
            case DeviceGlyph.Gamepad:
                // 手柄：圆角机身 + 左十字键 + 右两颗键（与 Windows 端逐点相同）
                Box(2.0, 5.2, 12.0, 6.4, 3.0);
                Line(4.6, 8.4, 6.6, 8.4);
                Line(5.6, 7.4, 5.6, 9.4);
                Dot(10.0, 7.6, 0.7);
                Dot(11.2, 9.2, 0.7);
                break;
            case DeviceGlyph.Other:
                // 通用外设：扁平圆角机身 + 居中圆点（认不出型号的外设兜底）
                Box(2.2, 5.4, 11.6, 5.2, 2.2);
                Dot(8.0, 8.0, 1.4);
                break;
        }
    }
}
