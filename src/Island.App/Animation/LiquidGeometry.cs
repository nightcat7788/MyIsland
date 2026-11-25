using System.Windows;
using System.Windows.Media;

namespace Island.App.Animation;

/// <summary>
/// 液态岛的几何：胶囊本体 + 贴边内凹圆角（表面张力）+ 颈液桥 + 断开的小水滴。
/// 参考 wisland/ui/shape.py，用 WPF StreamGeometry 绘制。
/// 坐标系：u 沿吸附边，v 垂直离开吸附边（v=0 即屏幕边缘）。
/// </summary>
public static class LiquidGeometry
{
    private const double K = 0.5523; // 1/4 圆弧的三次贝塞尔近似系数

    /// <summary>把 (u,v) 沿边坐标映射到屏幕/窗口坐标。</summary>
    public static Point EdgeMap(string edge, double line, double u, double v) => edge switch
    {
        "top" => new Point(u, line + v),
        "bottom" => new Point(u, line - v),
        "left" => new Point(line + v, u),
        _ => new Point(line - v, u), // right
    };

    /// <summary>圆角矩形路径（四角独立半径，顺时针 tl,tr,br,bl）。</summary>
    public static StreamGeometry RoundedRect(Rect r, double tl, double tr, double br, double bl)
    {
        var g = new StreamGeometry();
        double x = r.X, y = r.Y, w = r.Width, h = r.Height;
        double m = Math.Min(w, h) / 2;
        tl = Math.Clamp(tl, 0, m); tr = Math.Clamp(tr, 0, m);
        br = Math.Clamp(br, 0, m); bl = Math.Clamp(bl, 0, m);
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x + tl, y), true, true);
            c.LineTo(new Point(x + w - tr, y), true, false);
            if (tr > 0)
                c.BezierTo(new Point(x + w - tr + tr * K, y),
                                new Point(x + w, y + tr - tr * K),
                                new Point(x + w, y + tr), true, false);
            c.LineTo(new Point(x + w, y + h - br), true, false);
            if (br > 0)
                c.BezierTo(new Point(x + w, y + h - br + br * K),
                                new Point(x + w - br + br * K, y + h),
                                new Point(x + w - br, y + h), true, false);
            c.LineTo(new Point(x + bl, y + h), true, false);
            if (bl > 0)
                c.BezierTo(new Point(x + bl - bl * K, y + h),
                                new Point(x, y + h - bl + bl * K),
                                new Point(x, y + h - bl), true, false);
            c.LineTo(new Point(x, y + tl), true, false);
            if (tl > 0)
                c.BezierTo(new Point(x, y + tl - tl * K),
                                new Point(x + tl - tl * K, y),
                                new Point(x + tl, y), true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>
    /// 液桥：从屏幕边缘连到本体的沙漏形（两侧内凹曲线 = 表面张力圆角）。
    /// t: 拉伸程度 0(贴合)..1(将断)，strength: 出现/消失过渡 0..1。
    /// </summary>
    public static StreamGeometry? Neck(string edge, double line, double c, double length,
                                       double gap, double t, double strength, double fillet, double corner)
    {
        if (strength <= 0.01 || gap <= 0.5) return null;
        Point M(double u, double v) => EdgeMap(edge, line, u, v);
        double half = length / 2;
        double tc = Math.Clamp(t, 0, 1);
        double ts = Math.Sqrt(tc);
        double wz = Easing.Lerp(half, Math.Max(2.5, Math.Min(7.0, length * 0.035)), ts);
        wz = Easing.Lerp(1.0, wz, strength);
        double fe = Easing.Lerp(fillet, fillet * 0.75, tc) * strength;
        double fb = fillet * Easing.Smooth(tc * 3.0) * strength;
        double e = wz + fe;
        double b = Math.Min(wz + fb, Math.Max(wz, half - corner * 0.5));
        double ov = Math.Max(Math.Min(8.0, corner + 3.0), fillet * (1 - tc));
        double ym = Math.Max(gap * 0.5, fe);
        double yb = gap + 0.5;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(M(c - e, -1.5), true, true);
            ctx.LineTo(M(c + e, -1.5), true, false);
            ctx.LineTo(M(c + e, 0), true, false);
            // 右侧曲线：边缘 -> 腰 -> 本体
            ctx.BezierTo(M(c + e - (e - wz) * K, 0), M(c + wz, ym * (1 - K)), M(c + wz, ym), true, false);
            if (yb > ym + 0.5)
                ctx.BezierTo(M(c + wz, ym + (yb - ym) * K), M(c + b - (b - wz) * K, yb), M(c + b, yb), true, false);
            ctx.LineTo(M(c + b, gap + ov), true, false);
            ctx.LineTo(M(c - b, gap + ov), true, false);
            // 左侧反向
            if (yb > ym + 0.5)
                ctx.BezierTo(M(c - b + (b - wz) * K, yb), M(c - wz, ym + (yb - ym) * K), M(c - wz, ym), true, false);
            ctx.BezierTo(M(c - wz, ym * (1 - K)), M(c - e + (e - wz) * K, 0), M(c - e, 0), true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>断开后残留在边缘、正在缩回的半个小水滴。</summary>
    public static StreamGeometry? Drip(string edge, double line, double c, double r)
    {
        if (r <= 0.4) return null;
        Point M(double u, double v) => EdgeMap(edge, line, u, v);
        double f = r * 0.8;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(M(c - r - f, -1.5), true, true);
            ctx.LineTo(M(c + r + f, -1.5), true, false);
            ctx.LineTo(M(c + r + f, 0), true, false);
            ctx.BezierTo(M(c + r + f * (1 - K), 0), M(c + r, f * (1 - K)), M(c + r, f * 0.9), true, false);
            ctx.BezierTo(M(c + r, f + r * 0.9), M(c - r, f + r * 0.9), M(c - r, f * 0.9), true, false);
            ctx.BezierTo(M(c - r, f * (1 - K)), M(c - r - f * (1 - K), 0), M(c - r - f, 0), true, false);
        }
        g.Freeze();
        return g;
    }
}
