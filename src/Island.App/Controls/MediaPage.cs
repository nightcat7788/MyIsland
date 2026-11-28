using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Island.App.Services;
using Island.App.Themes;

namespace Island.App.Controls;

/// <summary>
/// 展开面板「媒体播放」页（对齐 BrimDeck 媒体面板）：
/// 左侧圆角专辑封面（无封面时自绘黑胶唱片矢量图）+ 右侧歌名/歌手/来源标签
/// + 封面主色实时 16 段音频律动柱 + 进度条 + 上一曲/播放暂停/下一曲控制圆钮。
/// </summary>
public sealed class MediaPage : FrameworkElement
{
    public MediaSnapshot? Media { get; set; }
    public Palette? Palette { get; set; }

    public Action? OnPrev;
    public Action? OnPlayPause;
    public Action? OnNext;

    private readonly double[] _bars = new double[16];
    public void SetAudioBars(ReadOnlySpan<double> src)
    {
        int n = Math.Min(_bars.Length, src.Length);
        for (int i = 0; i < n; i++) _bars[i] = src[i];
    }

    private Rect _prevRect, _playRect, _nextRect;
    private int _hoverBtn = -1; // 0=prev, 1=play, 2=next

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

    public MediaPage()
    {
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        int hit = _prevRect.Contains(p) ? 0 : _playRect.Contains(p) ? 1 : _nextRect.Contains(p) ? 2 : -1;
        if (hit != _hoverBtn)
        {
            _hoverBtn = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Arrow;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverBtn != -1)
        {
            _hoverBtn = -1;
            Cursor = Cursors.Arrow;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var p = e.GetPosition(this);
        if (_prevRect.Contains(p)) { OnPrev?.Invoke(); e.Handled = true; }
        else if (_playRect.Contains(p)) { OnPlayPause?.Invoke(); e.Handled = true; }
        else if (_nextRect.Contains(p)) { OnNext?.Invoke(); e.Handled = true; }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var pal = Palette ?? Palette.Dark;
        double W = ActualWidth, H = ActualHeight;
        if (W < 120 || H < 100) return;
        double ppd = Ppd;

        // 透明底保障鼠标移动事件全区域命中
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));

        var m = Media;
        Color accent = m?.AccentColor ?? pal.Accent;

        const double coverSize = 136;
        double leftX = 18;
        double coverY = Math.Max(14, (H - coverSize) / 2 - 6);
        var coverBox = RenderKit.SafeRect(leftX, coverY, coverSize, coverSize);

        // 1. 左侧专辑封面（圆角裁切 + 柔和主色光晕底）
        dc.DrawRoundedRectangle(RenderKit.Faint(accent, 0.14), null,
            RenderKit.SafeRect(leftX - 3, coverY - 3, coverSize + 6, coverSize + 6), 20, 20);
        if (m?.Cover is not null)
        {
            dc.PushClip(new RectangleGeometry(coverBox, 16, 16));
            dc.DrawImage(m.Cover, coverBox);
            dc.Pop();
            dc.DrawRoundedRectangle(null, RenderKit.PenOf(pal.Rim, 1, 0.55), coverBox, 16, 16);
        }
        else
        {
            dc.DrawRoundedRectangle(RenderKit.BrushOf(pal.SurfaceAlt), RenderKit.PenOf(pal.Rim, 1, 0.55), coverBox, 16, 16);
            Point discCenter = new(leftX + coverSize / 2, coverY + coverSize / 2);
            dc.DrawEllipse(RenderKit.Faint(accent, 0.12), RenderKit.PenOf(pal.Track, 1.5), discCenter, 42, 42);
            dc.DrawEllipse(null, RenderKit.PenOf(pal.Track, 1, 0.6), discCenter, 28, 28);
            dc.DrawEllipse(RenderKit.BrushOf(accent), null, discCenter, 10, 10);
            dc.DrawEllipse(RenderKit.BrushOf(pal.Surface), null, discCenter, 3.5, 3.5);
        }

        // 2. 右侧信息与控制区
        double rx = leftX + coverSize + 24;
        double rw = Math.Max(120, W - rx - 18);
        double y = coverY + 2;

        if (m is null || !m.HasMedia)
        {
            var emptyTitle = RenderKit.Text("暂无正在播放的媒体", 16, pal.Text, true, ppd);
            var emptySub = RenderKit.Text("在网易云音乐 / QQ音乐 / Spotify / 浏览器播放音频或视频后自动同步", 11, pal.TextDim, false, ppd);
            dc.DrawText(emptyTitle, new Point(rx, coverY + 34));
            dc.DrawText(RenderKit.Trimmed(
                "支持专辑封面提取主色、实时音频频谱律动与切歌暂停控制",
                11, pal.TextTertiary, rw, false, ppd), new Point(rx, coverY + 62));
            _prevRect = _playRect = _nextRect = default;
            return;
        }

        // 来源应用小胶囊
        if (!string.IsNullOrEmpty(m.SourceApp))
        {
            var appTxt = RenderKit.Text(m.SourceApp, 9.5, accent, true, ppd);
            var pill = RenderKit.SafeRect(rx, y, appTxt.WidthIncludingTrailingWhitespace + 14, 18);
            dc.DrawRoundedRectangle(RenderKit.Faint(accent, 0.16), null, pill, 9, 9);
            dc.DrawText(appTxt, new Point(rx + 7, y + (18 - appTxt.Height) / 2));
            y += 24;
        }

        // 歌曲名 + 歌手
        var titleTxt = RenderKit.Trimmed(m.Title, 17, pal.Text, rw, true, ppd);
        dc.DrawText(titleTxt, new Point(rx, y));
        y += titleTxt.Height + 4;

        string sub = string.IsNullOrEmpty(m.Artist)
            ? (string.IsNullOrEmpty(m.Album) ? "正在播放" : m.Album)
            : (string.IsNullOrEmpty(m.Album) ? m.Artist : $"{m.Artist} · {m.Album}");
        var subTxt = RenderKit.Trimmed(sub, 12, pal.TextDim, rw, false, ppd);
        dc.DrawText(subTxt, new Point(rx, y));
        y += subTxt.Height + 14;

        // 实时音频律动频谱柱（16 根，封面主色）
        double specH = 26;
        int nBars = _bars.Length;
        double gap = 4;
        double bw = Math.Clamp((rw - gap * (nBars - 1)) / nBars, 3, 10);
        for (int i = 0; i < nBars; i++)
        {
            double v = m.IsPlaying ? Math.Clamp(_bars[i], 0.08, 1.0) : 0.10;
            double bh = Math.Max(3, v * specH);
            double bx = rx + i * (bw + gap);
            dc.DrawRoundedRectangle(RenderKit.BrushOf(pal.Track), null,
                RenderKit.SafeRect(bx, y, bw, specH), bw / 2, bw / 2);
            dc.DrawRoundedRectangle(RenderKit.BrushOf(accent), null,
                RenderKit.SafeRect(bx, y + specH - bh, bw, bh), bw / 2, bw / 2);
        }
        y += specH + 14;

        // 时间轴进度条
        double frac = m.DurationSec > 0.5 ? Math.Clamp(m.PositionSec / m.DurationSec, 0, 1) : 0;
        dc.DrawRoundedRectangle(RenderKit.BrushOf(pal.Track), null, RenderKit.SafeRect(rx, y, rw, 4.5), 2.2, 2.2);
        if (frac > 0.005)
        {
            dc.DrawRoundedRectangle(RenderKit.BrushOf(accent), null,
                RenderKit.SafeRect(rx, y, Math.Max(5, rw * frac), 4.5), 2.2, 2.2);
        }
        var posT = RenderKit.Text(FormatTime(m.PositionSec), 10, pal.TextTertiary, false, ppd);
        var durT = RenderKit.Text(m.DurationSec > 0.5 ? FormatTime(m.DurationSec) : "--:--", 10, pal.TextTertiary, false, ppd);
        dc.DrawText(posT, new Point(rx, y + 7));
        dc.DrawText(durT, new Point(rx + rw - durT.WidthIncludingTrailingWhitespace, y + 7));
        y += 26;

        // 播放控制圆钮（居中排布：上一曲 / 播放暂停 / 下一曲）
        double btnCy = Math.Min(H - 24, y + 16);
        double center = rx + rw / 2;
        Point prevC = new(center - 54, btnCy);
        Point playC = new(center, btnCy);
        Point nextC = new(center + 54, btnCy);

        _prevRect = RenderKit.SafeRect(prevC.X - 18, prevC.Y - 18, 36, 36);
        _playRect = RenderKit.SafeRect(playC.X - 22, playC.Y - 22, 44, 44);
        _nextRect = RenderKit.SafeRect(nextC.X - 18, nextC.Y - 18, 36, 36);

        dc.DrawEllipse(_hoverBtn == 0 ? RenderKit.Faint(pal.Text, 0.14) : RenderKit.BrushOf(pal.SurfaceAlt),
            RenderKit.PenOf(pal.Rim, 1, 0.5), prevC, 17, 17);
        DrawSkipGlyph(dc, prevC, forward: false, pal.Text);

        dc.DrawEllipse(RenderKit.BrushOf(_hoverBtn == 1 ? pal.Text : accent), null, playC, 21, 21);
        DrawPlayPauseGlyph(dc, playC, m.IsPlaying, pal.Surface);

        dc.DrawEllipse(_hoverBtn == 2 ? RenderKit.Faint(pal.Text, 0.14) : RenderKit.BrushOf(pal.SurfaceAlt),
            RenderKit.PenOf(pal.Rim, 1, 0.5), nextC, 17, 17);
        DrawSkipGlyph(dc, nextC, forward: true, pal.Text);
    }

    private static void DrawPlayPauseGlyph(DrawingContext dc, Point c, bool playing, Color color)
    {
        var brush = RenderKit.BrushOf(color);
        if (playing)
        {
            dc.DrawRoundedRectangle(brush, null, RenderKit.SafeRect(c.X - 5.5, c.Y - 6.5, 3.6, 13), 1.2, 1.2);
            dc.DrawRoundedRectangle(brush, null, RenderKit.SafeRect(c.X + 1.9, c.Y - 6.5, 3.6, 13), 1.2, 1.2);
        }
        else
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X - 4, c.Y - 7), true, true);
                ctx.LineTo(new Point(c.X + 7, c.Y), false, false);
                ctx.LineTo(new Point(c.X - 4, c.Y + 7), false, false);
            }
            g.Freeze();
            dc.DrawGeometry(brush, null, g);
        }
    }

    private static void DrawSkipGlyph(DrawingContext dc, Point c, bool forward, Color color)
    {
        double dir = forward ? 1 : -1;
        var brush = RenderKit.BrushOf(color);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X - 5 * dir, c.Y - 5.2), true, true);
            ctx.LineTo(new Point(c.X + 2.5 * dir, c.Y), false, false);
            ctx.LineTo(new Point(c.X - 5 * dir, c.Y + 5.2), false, false);
        }
        g.Freeze();
        dc.DrawGeometry(brush, null, g);
        dc.DrawRoundedRectangle(brush, null,
            RenderKit.SafeRect(forward ? c.X + 3.2 : c.X - 5.2, c.Y - 5.2, 2.0, 10.4), 0.8, 0.8);
    }

    private static string FormatTime(double sec)
    {
        if (!double.IsFinite(sec) || sec < 0) sec = 0;
        var ts = TimeSpan.FromSeconds(sec);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes}:{ts.Seconds:D2}";
    }
}
