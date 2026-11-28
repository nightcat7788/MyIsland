using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Island.App.Services;
using Island.App.Themes;

namespace Island.Avalonia;

/// <summary>
/// macOS 独立图形化设置中心窗口（对齐 Windows 版 SettingsWindow）：
/// 提供主题、收起视图、展开方式、胶囊造型与装饰、小形象款式、剪贴板保留策略与天气城市的一站式可视化配置。
/// </summary>
public sealed class MacSettingsWindow : Window
{
    public sealed class Host
    {
        public Action<string> SetTheme = _ => { };
        public Action<string> SetView = _ => { };
        public Action<string> SetExpandMode = _ => { };
        public Action<string> SetPetStyle = _ => { };
        public Action<bool> SetAutoCycle = _ => { };
        public Action<bool> SetDecorNotch = _ => { };
        public Action<bool> SetDecorLetters = _ => { };
        public Action<bool> SetComboNumericHero = _ => { };
        public Action<string> SetClipRetention = _ => { };
        public Action<string> SetWeatherCity = _ => { };
        public Action<bool> SetAlwaysOnTop = _ => { };
        public Action<bool> SetClickThrough = _ => { };
        public Action<bool> SetAutoCloseBuiltin = _ => { };
    }

    private static readonly (string Id, string Label)[] Themes =
    {
        ("Dark", "深色"), ("Light", "浅色"), ("OneDark", "One Dark Pro"),
        ("GitHubDark", "GitHub Dark"), ("Dracula", "Dracula"), ("NightOwl", "Night Owl"),
        ("IslandAuto", "海岛假日 · 自动日夜"), ("IslandDay", "海岛假日 · 白天"), ("IslandNight", "海岛假日 · 夜晚"),
        ("Cyberpunk", "赛博朋克"), ("Pixel", "像素方块"),
    };

    private static readonly (string Id, string Label)[] Views =
    {
        ("clock", "时间胶囊"), ("combo", "信息胶囊"), ("dash", "仪表盘"), ("media", "媒体律动"), ("pet", "小形象"),
    };

    private static readonly (string Id, string Label)[] ExpandModes =
    {
        ("hover", "悬停展开"), ("always", "常驻展开"), ("never", "仅点击展开"),
    };

    private static readonly (string Id, string Label)[] Retentions =
    {
        ("session", "退出时清除（纯内存）"), ("1d", "保留 1 天"), ("7d", "保留 7 天"), ("30d", "保留 30 天"),
    };

    public MacSettingsWindow(Settings settings, Palette pal, Host host)
    {
        Title = $"MyIsland 设置中心 · v{Settings.AppVersion}";
        Width = 560;
        Height = 620;
        MinWidth = 480;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(pal.Surface);
        Foreground = new SolidColorBrush(pal.Text);

        var fg = new SolidColorBrush(pal.Text);
        var dim = new SolidColorBrush(pal.TextDim);
        var cardBg = new SolidColorBrush(pal.Elevated);
        var border = new SolidColorBrush(pal.Rim);

        Border Section(string title, Control body)
        {
            var inner = new StackPanel { Spacing = 10 };
            inner.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(pal.Accent),
            });
            inner.Children.Add(body);
            return new Border
            {
                Background = cardBg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 12),
                Child = inner,
            };
        }

        Grid Row(string label, Control ctl)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var tb = new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(tb, 0);
            Grid.SetColumn(ctl, 1);
            g.Children.Add(tb);
            g.Children.Add(ctl);
            return g;
        }

        ComboBox Combo((string Id, string Label)[] items, string current, Action<string> onPick)
        {
            var cb = new ComboBox { Width = 210 };
            int sel = 0;
            for (int i = 0; i < items.Length; i++)
            {
                cb.Items.Add(items[i].Label);
                if (items[i].Id == current) sel = i;
            }
            cb.SelectedIndex = sel;
            cb.SelectionChanged += (_, _) =>
            {
                if (cb.SelectedIndex >= 0 && cb.SelectedIndex < items.Length)
                    onPick(items[cb.SelectedIndex].Id);
            };
            return cb;
        }

        CheckBox Switch(bool initial, Action<bool> onChanged)
        {
            var chk = new CheckBox { IsChecked = initial };
            chk.IsCheckedChanged += (_, _) => onChanged(chk.IsChecked == true);
            return chk;
        }

        // 1. 外观与收起胶囊
        var appear = new StackPanel { Spacing = 10 };
        appear.Children.Add(Row("面板主题", Combo(Themes, settings.Theme, host.SetTheme)));
        appear.Children.Add(Row("默认收起样式", Combo(Views, settings.CompactView, host.SetView)));
        appear.Children.Add(Row("小形象款式", Combo(PetFaceAv.Styles, settings.PetStyle, host.SetPetStyle)));
        appear.Children.Add(Row("刘海融合造型（顶部凹弧贴边）", Switch(settings.DecorNotch, host.SetDecorNotch)));
        appear.Children.Add(Row("字母标识环 C / M / G / D", Switch(settings.DecorLetters, host.SetDecorLetters)));
        appear.Children.Add(Row("信息胶囊：数字主角大字版", Switch(settings.ComboNumericHero, host.SetComboNumericHero)));

        // 2. 行为与交互
        var behavior = new StackPanel { Spacing = 10 };
        behavior.Children.Add(Row("展开方式", Combo(ExpandModes, settings.ExpandMode, host.SetExpandMode)));
        behavior.Children.Add(Row("自动轮换（小形象 ⇄ 信息胶囊）", Switch(settings.AutoCycleViews, host.SetAutoCycle)));
        behavior.Children.Add(Row("常驻置顶", Switch(settings.AlwaysOnTop, host.SetAlwaysOnTop)));
        behavior.Children.Add(Row("鼠标穿透（游戏防误触）", Switch(settings.ClickThrough, host.SetClickThrough)));
        if (OperatingSystem.IsMacOS())
            behavior.Children.Add(Row("启动时自动关闭内建屏", Switch(settings.AutoCloseBuiltin, host.SetAutoCloseBuiltin)));

        // 3. 内容、天气与剪贴板
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(Row("剪贴板保留策略", Combo(Retentions, settings.ClipboardRetention, host.SetClipRetention)));
        var cityBox = new TextBox
        {
            Width = 150,
            Text = settings.City,
            Watermark = "留空自动定位",
        };
        var cityApply = new Button { Content = "应用", Margin = new Thickness(6, 0, 0, 0) };
        cityApply.Click += (_, _) => host.SetWeatherCity(cityBox.Text?.Trim() ?? "");
        var cityPanel = new StackPanel { Orientation = Orientation.Horizontal };
        cityPanel.Children.Add(cityBox);
        cityPanel.Children.Add(cityApply);
        content.Children.Add(Row("天气城市（中/英文）", cityPanel));

        var hotkeys = new TextBlock
        {
            Text = "⌘ + ⌥ + I：展开 / 收起灵动岛\n⌘ + ⌥ + D：循环切换收起胶囊样式\n⌘ + ⌥ + S：循环切换展开页签\n胶囊上双指滚动：切换视图；拖拽文件到灵动岛：暂存至剪贴板页底部中转栏",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(pal.TextDim),
            TextWrapping = TextWrapping.Wrap,
        };

        var stack = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        stack.Children.Add(Section("外观与时间胶囊", appear));
        stack.Children.Add(Section("交互与行为", behavior));
        stack.Children.Add(Section("剪贴板与天气", content));
        stack.Children.Add(Section("全局快捷键与手势", hotkeys));

        var closeBtn = new Button
        {
            Content = "完成",
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(18, 0, 18, 14),
        };
        closeBtn.Click += (_, _) => Close();

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(closeBtn, Dock.Bottom);
        root.Children.Add(closeBtn);
        root.Children.Add(new ScrollViewer { Content = stack });
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
