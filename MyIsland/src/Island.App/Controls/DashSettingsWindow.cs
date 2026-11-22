using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Island.App.Services;
using Island.App.Themes;
using Island.Core.Monitoring;

namespace Island.App.Controls;

/// <summary>
/// dash 显示项设置窗口：勾选要显示的信息模块，顶部实时渲染预览
/// （复用 IslandCanvas 的同一套绘制，所见即所得；预览 1s 刷新真实数据）。
/// </summary>
public sealed class DashSettingsWindow : Window
{
    /// <summary>可配置模块（key → 显示名）。</summary>
    private static readonly (string Key, string Label)[] Modules =
    {
        ("cores", "核心柱"),
        ("cputemp", "CPU 温度"),
        ("gputemp", "GPU 温度"),
        ("mem", "内存"),
        ("gpu", "GPU 使用率"),
        ("disk", "硬盘活动"),
        ("ssd", "SSD 温度"),
        ("battery", "电池"),
        ("net", "网速 ↓↑"),
    };

    private readonly Settings _settings;
    private readonly Action _apply;
    private readonly IslandCanvas _preview = new();
    private readonly DispatcherTimer _timer;

    /// <param name="current">取当前快照（预览用真实数据）。</param>
    /// <param name="apply">勾选变化后回调（宿主刷新岛体与设置持久化）。</param>
    public DashSettingsWindow(Settings settings, Palette palette,
        Func<(MonitorSnapshot? Snap, ThermalSnapshot? Thermal)> current, Action apply)
    {
        _settings = settings;
        _apply = apply;
        this.current = current;

        Title = "仪表盘显示项";
        Width = 560; Height = 340;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Background = new SolidColorBrush(palette.Surface);
        Foreground = new SolidColorBrush(palette.Text);

        // ---- 预览（岛体同一套渲染，实时数据 1s 一刷） ----
        _preview.CompactView = "dash";
        _preview.Palette = palette;
        _preview.IsHitTestVisible = false;
        _preview.Width = 660; _preview.Height = 69;
        _preview.LayoutTransform = new ScaleTransform(0.78, 0.78);
        var previewHost = new Border
        {
            Margin = new Thickness(16, 12, 16, 8),
            Height = 70,
            Child = _preview,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // ---- 勾选区 ----
        var itemsPanel = new WrapPanel { Margin = new Thickness(16, 4, 16, 0) };
        var selected = Parse(_settings.DashItems);
        foreach (var (key, label) in Modules)
        {
            var cb = new CheckBox
            {
                Content = label,
                IsChecked = selected is null || selected.Contains(key),
                Margin = new Thickness(0, 6, 18, 6),
                Foreground = new SolidColorBrush(palette.Text),
                FontSize = 13,
            };
            cb.Checked += (_, _) => Toggle(key, true);
            cb.Unchecked += (_, _) => Toggle(key, false);
            itemsPanel.Children.Add(cb);
        }

        var hint = new TextBlock
        {
            Text = "勾选 dash 仪表盘要显示的模块；上方为实时预览（真实数据）。",
            Foreground = new SolidColorBrush(palette.TextDim),
            FontSize = 11,
            Margin = new Thickness(16, 6, 16, 0),
        };

        var close = new Button
        {
            Content = "完成", Width = 80, Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 16, 0),
            IsDefault = true,
        };
        close.Click += (_, _) => Close();

        var root = new StackPanel();
        root.Children.Add(previewHost);
        root.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 0) });
        root.Children.Add(itemsPanel);
        root.Children.Add(hint);
        root.Children.Add(close);
        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshPreview();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        var (snap, thermal) = current();
        _preview.Snapshot = snap;
        _preview.Thermal = thermal;
        _preview.DashItems = Parse(_settings.DashItems);
        _preview.InvalidateVisual();
    }

    private readonly Func<(MonitorSnapshot? Snap, ThermalSnapshot? Thermal)> current;

    /// <summary>空 = 全部；非空 = 只显示集合内的模块。</summary>
    private static HashSet<string>? Parse(string dashItems)
    {
        if (string.IsNullOrWhiteSpace(dashItems)) return null;
        var set = new HashSet<string>(dashItems.Split(',', StringSplitOptions.RemoveEmptyEntries));
        return set.Count == 0 ? null : set;
    }

    private void Toggle(string key, bool on)
    {
        var set = Parse(_settings.DashItems)
            ?? new HashSet<string>(Modules.Select(m => m.Key));
        if (on) set.Add(key); else set.Remove(key);
        // 全选时存空（=默认全部），否则存显式集合
        _settings.DashItems = set.Count == Modules.Length ? "" : string.Join(',', Modules.Where(m => set.Contains(m.Key)).Select(m => m.Key));
        _settings.Save();
        _apply();
        RefreshPreview();
    }
}
