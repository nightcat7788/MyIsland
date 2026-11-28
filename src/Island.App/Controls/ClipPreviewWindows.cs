using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Island.App.Services;

namespace Island.App.Controls;

/// <summary>
/// 剪贴板条目的「查看详情」弹窗（对齐 macOS 版 ClipDetailWindow）：
/// 展示类型、时间、行数、字符数/大小、常驻状态及完整等宽正文，支持一键复制与 Esc 关闭。
/// </summary>
public sealed class ClipDetailWindow : Window
{
    public ClipDetailWindow(ClipboardItem item)
    {
        Title = "剪贴板详情";
        Width = 720;
        Height = 520;
        MinWidth = 400;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(28, 28, 32));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");

        var meta = new WrapPanel { Margin = new Thickness(16, 12, 16, 0) };
        void AddMeta(string label, string value)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 20, 6) };
            sp.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x92, 0xA0)),
            });
            sp.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
            });
            meta.Children.Add(sp);
        }

        AddMeta("类型", CategoryLabel(item.Category));
        AddMeta("复制时间", item.At.ToString("yyyy-MM-dd HH:mm:ss"));
        if (item.Category == "image" && item.ImagePng is { Length: > 0 } png)
        {
            AddMeta("图片大小", $"{Math.Max(1, png.Length / 1024)} KB");
        }
        else
        {
            AddMeta("行数", item.Lines.ToString());
            AddMeta("字符数", item.Full.Length.ToString());
        }
        if (item.Pinned) AddMeta("状态", "★ 常驻（不按保留期淘汰）");

        var body = new TextBox
        {
            Text = item.Category == "image" && item.ImagePng is { Length: > 0 } p2
                ? $"（图片条目，{Math.Max(1, p2.Length / 1024)} KB）"
                : item.Full,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = 13,
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 24)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(16, 8, 16, 10),
        };

        var status = new TextBlock
        {
            Text = "按 Esc 关闭窗口",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 154, 166)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var copyBtn = MakeButton("复制全部", primary: true);
        copyBtn.Click += (_, _) =>
        {
            status.Text = ClipboardService.CopyBack(item)
                ? "✓ 已复制到剪贴板"
                : "剪贴板被占用，请稍后重试";
        };
        var closeBtn = MakeButton("关闭", primary: false);
        closeBtn.Click += (_, _) => Close();

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(16, 0, 16, 12),
        };
        footer.Children.Add(copyBtn);
        footer.Children.Add(closeBtn);
        footer.Children.Add(status);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(meta, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(meta);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    private static string CategoryLabel(string cat) => cat switch
    {
        "link" => "链接",
        "code" => "代码",
        "color" => "颜色",
        "image" => "图片",
        _ => "文本",
    };

    internal static Button MakeButton(string text, bool primary)
    {
        return new Button
        {
            Content = text,
            Height = 28,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 12,
            Cursor = Cursors.Hand,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(primary
                ? Color.FromRgb(0x0A, 0x84, 0xFF)
                : Color.FromRgb(0x3A, 0x3A, 0x3E)),
            BorderThickness = new Thickness(0),
        };
    }
}

/// <summary>
/// 剪贴板图片的大图预览弹窗（对齐 macOS 版 ImagePreviewWindow）：
/// 点击剪贴板图片卡片直接查看原图，支持「存到下载」「复制图片」与 Esc 关闭。
/// </summary>
public sealed class ImagePreviewWindow : Window
{
    private readonly byte[] _png;
    private readonly TextBlock _status;

    public ImagePreviewWindow(byte[] png, string sourceName)
    {
        _png = png;
        Title = $"剪贴板图片 · {sourceName}";
        Width = 900;
        Height = 640;
        MinWidth = 440;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(28, 28, 32));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");

        var img = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        try
        {
            using var ms = new MemoryStream(png);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            img.Source = bmp;
        }
        catch { }

        var scroll = new ScrollViewer
        {
            Content = img,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 24)),
            Padding = new Thickness(12),
        };

        _status = new TextBlock
        {
            Text = $"{Math.Max(1, png.Length / 1024)} KB · 点「存到下载」保存为 PNG 文件",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(154, 158, 168)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        var saveBtn = ClipDetailWindow.MakeButton("存到下载", primary: true);
        saveBtn.Click += (_, _) => SaveToDownloads();

        var copyBtn = ClipDetailWindow.MakeButton("复制图片", primary: false);
        copyBtn.Click += (_, _) =>
        {
            var fake = new ClipboardItem(DateTime.Now, "image", sourceName, "")
            {
                Category = "image",
                ImagePng = _png,
            };
            _status.Text = ClipboardService.CopyBack(fake)
                ? "✓ 已复制图片，可直接 Ctrl+V 粘贴"
                : "复制失败，剪贴板正被占用";
        };

        var closeBtn = ClipDetailWindow.MakeButton("关闭", primary: false);
        closeBtn.Click += (_, _) => Close();

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 10, 12, 10),
        };
        bar.Children.Add(saveBtn);
        bar.Children.Add(copyBtn);
        bar.Children.Add(closeBtn);
        bar.Children.Add(_status);

        var barHost = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 62)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = bar,
        };

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(barHost, Dock.Bottom);
        root.Children.Add(barHost);
        root.Children.Add(scroll);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    private void SaveToDownloads()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"剪贴板-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            File.WriteAllBytes(path, _png);
            _status.Text = $"✓ 已保存到 {path}";
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                });
            }
            catch { }
        }
        catch (Exception ex)
        {
            _status.Text = $"保存失败：{ex.Message}";
        }
    }
}
