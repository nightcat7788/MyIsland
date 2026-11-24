using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Island.App.Services;
using Island.App.Themes;
using Island.Core.Monitoring;

namespace Island.App.Controls;

/// <summary>
/// 通用「显示项设置」窗口：勾选模块，顶部实时渲染预览（与岛体同一套控件/数据管线）。
/// 用于 dash 仪表盘 / combo 信息胶囊 / 监控页 三处，构造时传入各自的模块清单与预览控件。
/// </summary>
public sealed class ModuleSettingsWindow : Window
{
    public sealed record Module(string Key, string Label);

    private readonly IReadOnlyList<Module> _modules;
    private readonly FrameworkElement _preview;
    private readonly Func<(MonitorSnapshot? Snap, ThermalSnapshot? Thermal)> _current;
    private readonly Func<string?> _getItems;
    private readonly Action<string> _saveItems;
    private readonly Action<HashSet<string>?> _applyFilter;
    private readonly Action _onChanged;
    private readonly DispatcherTimer _timer;

    /// <param name="getItems">读当前集合字符串（空=全部；"none"=一个都不选）。</param>
    /// <param name="saveItems">保存集合字符串（空=全部；"none"=一个都不选）。</param>
    /// <param name="applyFilter">把勾选集合应用到预览控件。</param>
    /// <param name="onChanged">集合变化后回调（宿主刷新岛体）。</param>
    public ModuleSettingsWindow(
        string title,
        IReadOnlyList<Module> modules,
        Palette palette,
        FrameworkElement preview,
        Func<(MonitorSnapshot? Snap, ThermalSnapshot? Thermal)> current,
        Func<string?> getItems,
        Action<string> saveItems,
        Action<HashSet<string>?> applyFilter,
        Action onChanged)
    {
        _modules = modules;
        _preview = preview;
        // 预览控件拿到尺寸后立刻画一次：构造时它的 ActualWidth/Height 还是 0（还没参与布局），
        // 不补这一下的话要等到第一次 1 秒刷新才出现内容。
        _preview.SizeChanged += (_, _) => RefreshPreview();
        _current = current;
        _getItems = getItems;
        _saveItems = saveItems;
        _applyFilter = applyFilter;
        _onChanged = onChanged;

        Title = title;
        Width = 560;
        Height = preview.Height + 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Background = new SolidColorBrush(palette.Surface);
        Foreground = new SolidColorBrush(palette.Text);

        var previewHost = new Border
        {
            Margin = new Thickness(16, 12, 16, 8),
            Height = preview.Height + 8,
            Child = preview,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var itemsPanel = new WrapPanel { Margin = new Thickness(16, 4, 16, 0) };
        var selected = Parse(getItems());
        foreach (var m in modules)
        {
            var cb = new CheckBox
            {
                Content = m.Label,
                IsChecked = selected is null || selected.Contains(m.Key),
                Margin = new Thickness(0, 6, 18, 6),
                Foreground = new SolidColorBrush(palette.Text),
                FontSize = 13,
            };
            string key = m.Key;
            cb.Checked += (_, _) => Toggle(key, true);
            cb.Unchecked += (_, _) => Toggle(key, false);
            itemsPanel.Children.Add(cb);
        }

        var hint = new TextBlock
        {
            Text = "勾选要显示的模块；上方为实时预览（真实数据，每秒刷新）。",
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
        var (snap, thermal) = _current();
        switch (_preview)
        {
            case IslandCanvas c:
                // 画布只画"本体矩形"里的内容，本体矩形由宿主每帧注入（IslandWindow.Frame_Tick）；
                // 预览没有宿主的帧循环，得在这里告诉它：本体 = 整个预览区。
                // 2026-11-24 发现：此前从来没有注入过，而画布有一道保护（本体矩形为空时直接不画，
                // 是上次审查为修首帧 NaN 崩窗加的）⇒ 仪表盘/信息胶囊两处预览一直是空白的。
                if (c.ActualWidth > 0 && c.ActualHeight > 0)
                    c.BodyRect = new Rect(0, 0, c.ActualWidth, c.ActualHeight);
                c.Snapshot = snap;
                c.Thermal = thermal;
                break;
            case MonitorPage m:
                m.Snapshot = snap;
                m.Thermal = thermal;
                break;
        }
        _applyFilter(Parse(_getItems()));
        _preview.InvalidateVisual();
    }

    /// <summary>
    /// 解析显示项集合字符串：
    /// null/空白 = 全部（返回 null）；"none" = 一个都不选（返回空集合，不是 null）；
    /// 其它 = 显式集合。宿主侧（IslandWindow.ParseItems）用同一套约定。
    /// </summary>
    private static HashSet<string>? Parse(string? items)
    {
        if (string.IsNullOrWhiteSpace(items)) return null;
        string text = items.Trim();
        if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
            return new HashSet<string>();
        var set = new HashSet<string>(text.Split(',', StringSplitOptions.RemoveEmptyEntries));
        return set.Count == 0 ? null : set;
    }

    private void Toggle(string key, bool on)
    {
        var set = Parse(_getItems())
            ?? new HashSet<string>(_modules.Select(m => m.Key));
        if (on) set.Add(key); else set.Remove(key);
        // 全选时存空串（=全部）；一个都不选时存哨兵 "none"（=空集合）。
        // 空串在宿主侧表示"全部"，所以"什么都不选"必须用独立的哨兵值区分，
        // 否则两者会互相覆盖（用户取消全部勾选 → 存空串 → 重启后又是全部显示）。
        _saveItems(set.Count == 0 ? "none"
            : set.Count == _modules.Count ? ""
            : string.Join(',', _modules.Where(m => set.Contains(m.Key)).Select(m => m.Key)));
        _onChanged();
        RefreshPreview();
    }
}
