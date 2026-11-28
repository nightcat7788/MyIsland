// LogoGen: draw the MyIsland logo as vector geometry and export PNG (multi-size) + ICO.
// Geometry mirrors branding/island-logo.svg so the two stay visually identical.
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static void Main()
    {
        string outDir = @"C:\Users\fengl\WorkBuddy\2026-11-21-13-46-35\MyIsland\branding";
        Directory.CreateDirectory(outDir);

        int[] sizes = { 16, 24, 32, 48, 64, 128, 256, 512, 1024 };
        var pngs = new List<string>();
        foreach (int s in sizes)
        {
            string path = Path.Combine(outDir, $"island-logo-{s}.png");
            RenderPng(s, path);
            pngs.Add(path);
            Console.WriteLine($"png {s}: {path}");
        }

        // ICO with 16/24/32/48/64/128/256
        var icoSizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        string ico = Path.Combine(outDir, "island-logo.ico");
        WriteIco(ico, icoSizes);
        Console.WriteLine($"ico: {ico}");

        // 应用图标专用（16 托盘 / 32 列表）
        Console.WriteLine("done");
    }

    // ---------- 几何绘制 ----------
    private static DrawingVisual BuildVisual(int size)
    {
        var group = new DrawingGroup();

        // 底板：深色圆角方形（应用图标标准形态，保证在任意壁纸/任务栏上清晰）
        var plate = new LinearGradientBrush(
            Color.FromRgb(0x1E, 0x22, 0x2C), Color.FromRgb(0x12, 0x14, 0x1A), 70);
        group.Children.Add(new GeometryDrawing(plate, null,
            new RectangleGeometry(new Rect(16, 16, 480, 480), 108, 108)));

        // 底板内柔光
        group.Children.Add(new GeometryDrawing(MakeRadial(Color.FromArgb(70, 0x5A, 0x96, 0xFF), 0.5),
            null, new EllipseGeometry(new Rect(40, 60, 432, 390))));

        // 外围柔光（溢出底板）
        var halo = MakeRadial(Color.FromArgb(90, 0x5A, 0x96, 0xFF), 0.5);
        group.Children.Add(new GeometryDrawing(halo, null, new EllipseGeometry(new Rect(30, 56, 452, 400))));

        // 主体：超椭圆 squircle 轮廓（更贴近 macOS/iOS 质感）
        var bodyGeo = MakeSquircle(new Rect(72, 104, 368, 304), 132, 4.2);
        var body = new LinearGradientBrush(
            Color.FromRgb(0x46, 0x55, 0x70), Color.FromRgb(0x16, 0x19, 0x21), 62);
        body.GradientStops.Insert(1, new GradientStop(Color.FromRgb(0x26, 0x2A, 0x36), 0.42));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0x25, 0x30), 0.88));
        group.Children.Add(new GeometryDrawing(body, null, bodyGeo));

        // 内部律动辉光（被玻璃包裹）
        group.Children.Add(new GeometryDrawing(MakeRadial(Color.FromArgb(210, 0xE8, 0xA2, 0x4A), 0.5),
            null, new EllipseGeometry(new Rect(96, 236, 168, 132))));
        group.Children.Add(new GeometryDrawing(MakeRadial(Color.FromArgb(215, 0x5A, 0x96, 0xFF), 0.5),
            null, new EllipseGeometry(new Rect(232, 168, 186, 150))));
        group.Children.Add(new GeometryDrawing(MakeRadial(Color.FromArgb(150, 0x3C, 0xBE, 0x78), 0.5),
            null, new EllipseGeometry(new Rect(204, 266, 136, 106))));

        // 底部内阴影（玻璃厚度感）
        var inner = new LinearGradientBrush(
            Color.FromArgb(0, 0, 0, 0), Color.FromArgb(105, 0, 0, 0), 90);
        inner.GradientStops.Insert(1, new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.62));
        group.Children.Add(new GeometryDrawing(inner, null, MakeSquircle(new Rect(78, 110, 356, 292), 127, 4.2)));

        // 顶部内高光（玻璃反射）
        var rim = new LinearGradientBrush(
            Color.FromArgb(165, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 78);
        rim.GradientStops.Insert(1, new GradientStop(Color.FromArgb(20, 255, 255, 255), 0.42));
        group.Children.Add(new GeometryDrawing(rim, null, MakeSquircle(new Rect(78, 110, 356, 292), 127, 4.2)));

        // 左上镜面高光斑
        var spec = MakeRadial(Color.FromArgb(120, 255, 255, 255), 0.5);
        group.Children.Add(new GeometryDrawing(spec, null, new EllipseGeometry(new Rect(112, 128, 150, 74))));

        // 边缘描边
        group.Children.Add(new GeometryDrawing(null,
            new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 2.5),
            MakeSquircle(new Rect(73, 105, 366, 302), 131, 4.2)));

        // 底板描边
        group.Children.Add(new GeometryDrawing(null,
            new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 2),
            new RectangleGeometry(new Rect(17, 17, 478, 478), 107, 107)));

        // 4 根律动条：白→微蓝渐变，模拟发光
        var barBrush = new LinearGradientBrush(
            Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xD8, 0xE6, 0xFF), 90);
        double[][] bars = {
            new[]{163.0,196.0,120.0}, new[]{210.0,226.0,90.0},
            new[]{257.0,256.0,60.0},  new[]{304.0,166.0,150.0},
        };
        foreach (var b in bars)
            group.Children.Add(new GeometryDrawing(barBrush, null,
                new RectangleGeometry(new Rect(b[0], b[1], 30, b[2]), 15, 15)));

        // 律动条外发光（每根独立圆角矩形，避免方形路径露馅）
        foreach (var b in bars)
            group.Children.Add(new GeometryDrawing(null,
                new Pen(new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), 6),
                new RectangleGeometry(new Rect(b[0] - 1.5, b[1] - 1.5, 33, b[2] + 3), 16, 16)));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 512.0, size / 512.0));
            dc.DrawDrawing(group);
            dc.Pop();
        }
        return visual;
    }

    /// <summary>超椭圆（squircle）：|x|^n + |y|^n = 1 的参数化近似，比圆角矩形更"软"。</summary>
    private static StreamGeometry MakeSquircle(Rect r, double radius, double n)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double w = r.Width, h = r.Height;
            double cx = r.X + w / 2, cy = r.Y + h / 2;
            const int steps = 96;
            for (int i = 0; i <= steps; i++)
            {
                double t = i / (double)steps * 2 * Math.PI;
                double ct = Math.Cos(t), st = Math.Sin(t);
                double x = cx + Math.Sign(ct) * Math.Pow(Math.Abs(ct), 2.0 / n) * (w / 2);
                double y = cy + Math.Sign(st) * Math.Pow(Math.Abs(st), 2.0 / n) * (h / 2);
                if (i == 0) ctx.BeginFigure(new Point(x, y), true, false);
                else ctx.LineTo(new Point(x, y), true, false);
            }
        }
        geo.Freeze();
        return geo;
    }

    private static StreamGeometry MakeBarsPath(double[][] bars)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            foreach (var b in bars)
            {
                ctx.BeginFigure(new Point(b[0] + 15, b[1]), true, false);
                ctx.LineTo(new Point(b[0] + 30, b[1]), true, false);
                ctx.LineTo(new Point(b[0] + 30, b[1] + b[2]), true, false);
                ctx.LineTo(new Point(b[0] + 15, b[1] + b[2]), true, false);
            }
        }
        geo.Freeze();
        return geo;
    }

    private static RadialGradientBrush MakeRadial(Color inner, double r)
    {
        var b = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5),
            RadiusX = r, RadiusY = r,
        };
        b.GradientStops.Add(new GradientStop(inner, 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, inner.R, inner.G, inner.B), 1));
        b.Freeze();
        return b;
    }

    private static void RenderPng(int size, string path)
    {
        var visual = BuildVisual(size);
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ---------- ICO 打包（PNG 压缩条目，Vista+ 支持） ----------
    private static void WriteIco(string path, int[] sizes)
    {
        var entries = new List<(int size, byte[] data)>();
        foreach (int s in sizes)
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"ico_{s}.png");
            RenderPng(s, tmp);
            entries.Add((s, File.ReadAllBytes(tmp)));
            File.Delete(tmp);
        }

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // type = icon
        w.Write((ushort)entries.Count);
        int offset = 6 + 16 * entries.Count;
        foreach (var (size, data) in entries)
        {
            int dim = size >= 256 ? 0 : size;
            w.Write((byte)dim);
            w.Write((byte)dim);
            w.Write((byte)0);            // palette
            w.Write((byte)0);            // reserved
            w.Write((ushort)1);          // color planes
            w.Write((ushort)32);         // bits per pixel
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in entries) w.Write(data);
    }
}
