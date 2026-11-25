using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Diagnostics;

namespace Island.Avalonia;

/// <summary>
/// 剪贴板图片的大图预览窗口。点剪贴板里的图片条目时弹出——
/// 之前只能写回剪贴板再去别处 ⌘V，"看一眼截图"很绕；这里直接显示原图，
/// 并提供「存到下载」「复制图片」两个按钮。
/// 用标准 Avalonia Window（非岛体的无边框透明窗口），有标题栏、可缩放、Esc 关闭。
/// </summary>
public sealed class ImagePreviewWindow : Window
{
    private readonly byte[] _png;
    private readonly string _sourceName;

    public ImagePreviewWindow(byte[] png, string sourceName)
    {
        _png = png;
        _sourceName = sourceName;

        Title = $"剪贴板图片 · {sourceName}";
        Width = 900; Height = 640;
        MinWidth = 420; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(28, 28, 32));
        Foreground = Brushes.White;

        // 解码原图（不缩放，缩放交给 Image 控件的 Stretch）
        // 用 global:: 前缀：本命名空间叫 Island.Avalonia，直接写 Avalonia.Controls 会被解析成 Island.Avalonia.Controls
        global::Avalonia.Controls.Image picture;
        try
        {
            using var ms = new MemoryStream(png);
            picture = new global::Avalonia.Controls.Image
            {
                Source = new Bitmap(ms),
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        catch (Exception ex)
        {
            picture = new global::Avalonia.Controls.Image { Stretch = Stretch.Uniform };
            Console.Error.WriteLine($"[clipboard] preview decode: {ex.Message}");
        }

        var imgScroll = new ScrollViewer
        {
            Content = picture,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 24)),
        };

        status = new TextBlock
        {
            Text = $"{png.Length / 1024} KB · 点「存到下载」保存为 PNG 文件",
            Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 160)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var saveBtn = new Button { Content = "存到下载" };
        saveBtn.Click += (_, _) => SaveToDownloads();
        var copyBtn = new Button { Content = "复制图片" };
        copyBtn.Click += async (_, _) =>
        {
            try
            {
                var dobj = new global::Avalonia.Input.DataObject();
                dobj.Set("public.png", _png);
                dobj.Set("image/png", _png);
                if (Clipboard is not null) await Clipboard.SetDataObjectAsync(dobj);
                status.Text = "已复制，去目标应用按 ⌘V 粘贴";
            }
            catch (Exception ex) { status.Text = "复制失败：" + ex.Message; }
        };
        var closeBtn = new Button { Content = "关闭" };
        closeBtn.Click += (_, _) => Close();

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10),
            Children = { saveBtn, copyBtn, closeBtn, status },
        };

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 68)),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Child = bar,
                    [DockPanel.DockProperty] = Dock.Bottom,
                },
                imgScroll,
            },
        };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private readonly TextBlock status;

    /// <summary>把 PNG 写到 ~/Downloads/剪贴板-yyyyMMdd-HHmmss.png，并在 Finder 里显示。</summary>
    private void SaveToDownloads()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"剪贴板-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            File.WriteAllBytes(file, _png);
            status.Text = $"已保存：{file}";
            Console.Error.WriteLine($"[clipboard] saved image → {file}");
            if (OperatingSystem.IsMacOS()) RevealInFinder(file);
        }
        catch (Exception ex)
        {
            status.Text = "保存失败：" + ex.Message;
            Console.Error.WriteLine($"[clipboard] save image failed: {ex.Message}");
        }
    }

    private static void RevealInFinder(string file)
    {
        try
        {
            var psi = new ProcessStartInfo("open")
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "-R", file },
            };
            Process.Start(psi);
        }
        catch { /* Finder 打不开不影响已保存的文件 */ }
    }
}
