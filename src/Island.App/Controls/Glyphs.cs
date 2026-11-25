using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Island.App.Controls;

/// <summary>界面图标的种类（见 <see cref="Glyphs"/>）。</summary>
public enum GlyphKind
{
    Monitor, Clipboard, Battery, Sliders, Close, Bolt, File, Image, Swatch, Grid, Gear,
    Mouse, Keyboard, Headset, Gamepad,
}

/// <summary>
/// 界面用的矢量图标（2026-11-24 视觉重做）。以前页签 / 剪贴板 / 充电用的是 emoji（📈 ⎘ 🔋 🎛 ⚡）：
/// 不同 emoji 的线粗、基线、配色各不相同，12px 下一半糊成一团，也没法跟主题变色。
/// 这里统一成 16×16 网格上的线条图标：圆头描边、颜色由调用方给，随主题走。
/// </summary>
internal static class Glyphs
{
    /// <summary>把图标画进 box（等比缩放、居中）。</summary>
    public static void Draw(DrawingContext dc, GlyphKind kind, Rect box, Color color, double thickness = 1.5)
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
        // 描边矩形：向内收半个线宽，描边不会溢出 16×16 网格
        void Box(double x, double y, double w, double h, double r)
            => dc.DrawRoundedRectangle(null, pen, new Rect(ox + x * s + th / 2, oy + y * s + th / 2, w * s - th, h * s - th), r * s, r * s);

        switch (kind)
        {
            case GlyphKind.Monitor:
                // 四根高低不一的竖条：和产品图标同构
                Line(3.2, 9.5, 3.2, 12.6);
                Line(6.6, 5.2, 6.6, 12.6);
                Line(10.0, 8.0, 10.0, 12.6);
                Line(13.4, 3.4, 13.4, 12.6);
                break;
            case GlyphKind.Clipboard:
                Box(3.8, 2.6, 8.4, 11.4, 1.8);
                Box(6.0, 1.2, 4.0, 2.6, 1.2);
                Line(6.0, 8.2, 10.0, 8.2);
                Line(6.0, 10.8, 8.8, 10.8);
                break;
            case GlyphKind.Battery:
                Box(1.8, 5.0, 10.6, 6.0, 1.6);
                Line(13.9, 7.0, 13.9, 9.0);
                dc.DrawRoundedRectangle(brush, null, new Rect(ox + 3.3 * s, oy + 6.5 * s, 4.6 * s, 3.0 * s), 0.8 * s, 0.8 * s);
                break;
            case GlyphKind.Sliders:
                Line(2.2, 4.6, 8.0, 4.6);
                Line(12.9, 4.6, 13.8, 4.6);
                dc.DrawEllipse(null, pen, P(10.5, 4.6), 1.9 * s, 1.9 * s);
                Line(2.2, 11.4, 3.1, 11.4);
                Line(8.0, 11.4, 13.8, 11.4);
                dc.DrawEllipse(null, pen, P(5.5, 11.4), 1.9 * s, 1.9 * s);
                break;
            case GlyphKind.Close:
                Line(4.2, 4.2, 11.8, 11.8);
                Line(11.8, 4.2, 4.2, 11.8);
                break;
            case GlyphKind.Bolt:
                var bolt = new StreamGeometry();
                using (var ctx = bolt.Open())
                {
                    ctx.BeginFigure(P(9.3, 1.4), true, true);
                    ctx.LineTo(P(4.0, 9.2), true, false);
                    ctx.LineTo(P(7.6, 9.2), true, false);
                    ctx.LineTo(P(6.6, 14.6), true, false);
                    ctx.LineTo(P(12.0, 6.8), true, false);
                    ctx.LineTo(P(8.4, 6.8), true, false);
                }
                bolt.Freeze();
                dc.DrawGeometry(brush, null, bolt);
                break;
            case GlyphKind.File:
                Box(3.6, 1.8, 8.8, 12.4, 1.6);
                Line(8.2, 1.8, 8.2, 5.4);
                Line(8.2, 5.4, 12.4, 5.4);
                break;
            case GlyphKind.Image:
                Box(2.4, 3.4, 11.2, 9.2, 1.6);
                dc.DrawEllipse(brush, null, P(5.8, 6.4), 1.1 * s, 1.1 * s);
                Line(4.0, 11.2, 6.9, 8.2);
                Line(6.9, 8.2, 9.3, 10.6);
                Line(9.3, 10.6, 10.9, 9.0);
                Line(10.9, 9.0, 13.2, 11.2);
                break;
            // 设置窗口的导航图标（2026-11-25 设置窗口重做：换掉 emoji ◐ ▦ ⚙）
            case GlyphKind.Swatch:
                // 调色盘：一个圆环，里面三点
                dc.DrawEllipse(null, pen, P(8.0, 8.0), 5.8 * s, 5.8 * s);
                dc.DrawEllipse(brush, null, P(8.0, 5.0), 1.05 * s, 1.05 * s);
                dc.DrawEllipse(brush, null, P(5.2, 9.6), 1.05 * s, 1.05 * s);
                dc.DrawEllipse(brush, null, P(10.8, 9.6), 1.05 * s, 1.05 * s);
                break;
            case GlyphKind.Grid:
                Box(2.4, 2.4, 4.8, 4.8, 1.3);
                Box(8.8, 2.4, 4.8, 4.8, 1.3);
                Box(2.4, 8.8, 4.8, 4.8, 1.3);
                Box(8.8, 8.8, 4.8, 4.8, 1.3);
                break;
            case GlyphKind.Gear:
                // 齿轮：外圈 + 轴心 + 八个齿
                dc.DrawEllipse(null, pen, P(8.0, 8.0), 4.6 * s, 4.6 * s);
                dc.DrawEllipse(null, pen, P(8.0, 8.0), 1.7 * s, 1.7 * s);
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    Line(8.0 + 4.6 * Math.Cos(a), 8.0 + 4.6 * Math.Sin(a), 8.0 + 6.9 * Math.Cos(a), 8.0 + 6.9 * Math.Sin(a));
                }
                break;
            // 设备图标（2026-11-25 UI 对齐 BrimDeck：设备电量环里的剪影图标，换掉单字"鼠/键/耳/柄"）
            case GlyphKind.Mouse:
                // 鼠标：圆角机身 + 顶部滚轮
                Box(4.6, 2.6, 6.8, 10.8, 3.2);
                Line(8.0, 4.6, 8.0, 6.8);
                break;
            case GlyphKind.Keyboard:
                // 键盘：圆角面板 + 四颗键点 + 空格条
                Box(1.8, 5.2, 12.4, 6.6, 1.6);
                dc.DrawEllipse(brush, null, P(4.4, 7.6), 0.6 * s, 0.6 * s);
                dc.DrawEllipse(brush, null, P(6.8, 7.6), 0.6 * s, 0.6 * s);
                dc.DrawEllipse(brush, null, P(9.2, 7.6), 0.6 * s, 0.6 * s);
                dc.DrawEllipse(brush, null, P(11.6, 7.6), 0.6 * s, 0.6 * s);
                Line(5.2, 10.0, 10.8, 10.0);
                break;
            case GlyphKind.Headset:
                // 耳机：头梁弧 + 两只耳罩
                var headband = new StreamGeometry();
                using (var ctx = headband.Open())
                {
                    ctx.BeginFigure(P(2.4, 10.0), false, false);
                    ctx.ArcTo(P(13.6, 10.0), new Size(5.6 * s, 5.6 * s), 0, false, SweepDirection.Clockwise, true, false);
                }
                headband.Freeze();
                dc.DrawGeometry(null, pen, headband);
                Box(2.2, 8.6, 2.6, 4.6, 1.2);
                Box(11.2, 8.6, 2.6, 4.6, 1.2);
                break;
            case GlyphKind.Gamepad:
                // 手柄：圆角机身 + 左十字键 + 右两颗键
                Box(2.0, 5.2, 12.0, 6.4, 3.0);
                Line(4.6, 8.4, 6.6, 8.4);
                Line(5.6, 7.4, 5.6, 9.4);
                dc.DrawEllipse(brush, null, P(10.0, 7.6), 0.7 * s, 0.7 * s);
                dc.DrawEllipse(brush, null, P(11.2, 9.2), 0.7 * s, 0.7 * s);
                break;
        }
    }

    /// <summary>设备类型（DeviceService 的 kind）→ 设备图标。认不出给电池轮廓，不硬安。</summary>
    public static GlyphKind IconOf(string kind) => kind switch
    {
        "mouse" => GlyphKind.Mouse,
        "keyboard" => GlyphKind.Keyboard,
        "headset" => GlyphKind.Headset,
        "gamepad" => GlyphKind.Gamepad,
        _ => GlyphKind.Battery,
    };
}

/// <summary>
/// XAML 里放矢量图标用的控件：图标颜色跟随文字色（TextElement.Foreground 沿可视树继承，
/// 所以按钮改 Foreground 图标就跟着变）。宽高由布局给（建议 16×16）。
/// </summary>
public sealed class GlyphIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(GlyphKind), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(GlyphKind.Monitor, FrameworkPropertyMetadataOptions.AffectsRender));

    public GlyphKind Kind { get => (GlyphKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var brush = TextElement.GetForeground(this);
        Glyphs.Draw(dc, Kind, new Rect(0, 0, ActualWidth, ActualHeight),
            (brush as SolidColorBrush)?.Color ?? Colors.White);
    }
}
