using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Diagnostics;

namespace Island.Avalonia;

/// <summary>
/// 剪贴板条目的「查看详情」窗口（第五十九轮，右键菜单项）。
/// 完整显示这一条的内容，并给出类型、来源时间、行数 / 尺寸等元信息。
/// 用标准 Avalonia Window（非岛体的无边框透明窗口），有标题栏、可缩放、Esc 关闭。
/// </summary>
public sealed class ClipDetailWindow : Window
{
    public ClipDetailWindow(ClipItem item)
    {
        Title = "剪贴板详情";
        Width = 720; Height = 520;
        MinWidth = 380; MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(28, 28, 32));
        Foreground = Brushes.White;

        // ---- 元信息栏：类型 / 时间 / 行数 / 字符数 ----
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(16, 12, 16, 0) };
        void Meta(string label, string value)
        {
            var sp = new StackPanel { Spacing = 1 };
            sp.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x8C, 0x98)),
            });
            sp.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 13,
                Foreground = Brushes.White,
            });
            meta.Children.Add(sp);
        }
        Meta("类型", KindLabel(item.Kind));
        Meta("复制时间", item.At.ToString("yyyy-MM-dd HH:mm:ss"));
        if (item.Kind == ClipKind.Text || item.Kind == ClipKind.Code)
        {
            Meta("行数", item.Lines.ToString());
            Meta("字符数", item.Text.Length.ToString());
        }
        else if (item.Kind == ClipKind.Image && item.Image is { } img)
        {
            Meta("大小", $"{img.Length / 1024} KB");
        }
        else if (item.Kind == ClipKind.Link)
        {
            Meta("链接", item.Text);
        }
        if (item.Pinned) Meta("状态", "常驻（不按保留期清除）");

        // ---- 正文：等宽字体、保留原始换行与缩进 ----
        var body = new TextBox
        {
            Text = item.Kind == ClipKind.Image && item.Image is { } png2
                ? $"（图片条目，{png2.Length / 1024} KB）"
                : item.Text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Menlo, SF Mono, Consolas, Monaco, monospace"),
            FontSize = 13,
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 24)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 12),
            Margin = new Thickness(16, 10, 16, 16),
        };

        var root = new Panel();
        root.Children.Add(meta);
        var scroll = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        root.Children.Add(scroll);
        Content = root;
    }

    private static string KindLabel(ClipKind k) => k switch
    {
        ClipKind.Link => "链接",
        ClipKind.Code => "代码",
        ClipKind.Color => "颜色",
        ClipKind.Image => "图片",
        _ => "文字",
    };
}
