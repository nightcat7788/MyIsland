using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Island.App.Services;
using Island.App.Themes;

namespace Island.App.Controls;

/// <summary>
/// 设置中心：把主题/边缘/展开方式/视图/小形象/行为开关/天气城市/显示项/管理员重启
/// 收进一个窗口（BrimDeck 式设置页），所有改动即时生效并持久化。
/// </summary>
public sealed class SettingsWindow : Window
{
    /// <summary>宿主回调（由 App 注入，窗口不直接持有窗口/服务引用）。</summary>
    public sealed class Host
    {
        public Action<string> SetTheme = _ => { };
        public Action<string> SetEdge = _ => { };
        public Action<string> SetExpandMode = _ => { };
        public Action<string> SetCompactView = _ => { };
        public Action<string> SetPetStyle = _ => { };
        public Action<string> SetWeatherCity = _ => { };
        public Action ShowDashSettings = () => { };
        public Action ShowComboSettings = () => { };
        public Action ShowMonitorSettings = () => { };
        public Action RestartAsAdmin = () => { };
        public Action<bool> SetClickThrough = _ => { };
        public Func<string?> PromptCity = () => null;
        public bool IsElevated = true;
    }

    private readonly Settings _settings;
    private readonly Palette _palette;
    private readonly Host _host;
    private bool _ready;

    private static readonly (string Id, string Label)[] Themes =
    {
        ("Dark", "深色"), ("Light", "浅色"), ("Auto", "跟随系统+壁纸"), ("OneDark", "One Dark Pro"),
        ("GitHubDark", "GitHub Dark"), ("Dracula", "Dracula"), ("NightOwl", "Night Owl"),
        ("Island", "海岛假日"), ("Cyberpunk", "赛博朋克"), ("Pixel", "像素方块"),
    };
    private static readonly (string Id, string Label)[] Edges =
        { ("top", "顶部"), ("bottom", "底部"), ("left", "左侧"), ("right", "右侧") };
    private static readonly (string Id, string Label)[] ExpandModes =
        { ("hover", "悬停展开"), ("always", "常驻展开"), ("never", "长关") };
    private static readonly (string Id, string Label)[] Views =
    {
        ("dash", "仪表盘"), ("clock", "时间+天气"), ("combo", "信息胶囊"), ("monitor", "系统读数"),
        ("devices", "设备电量"), ("clipboard", "剪贴板"), ("pet", "小形象"),
    };
    private static readonly (string Id, string Label)[] PetStyles =
        { ("classic", "经典双眼"), ("mono", "独眼"), ("cat", "猫咪"), ("robot", "机器人"), ("pixel", "像素") };
    private static readonly string[] Cities =
        { "北京", "上海", "广州", "深圳", "杭州", "成都", "重庆", "西安", "武汉", "南京", "苏州", "厦门", "长沙", "青岛" };

    public SettingsWindow(Settings settings, Palette palette, Host host)
    {
        _settings = settings;
        _palette = palette;
        _host = host;

        Title = "MyIsland 设置";
        Width = 520; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = new SolidColorBrush(palette.Surface);
        Foreground = new SolidColorBrush(palette.Text);
        FontSize = 13;

        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };

        root.Children.Add(Title2("外观"));
        root.Children.Add(Row("面板主题", Combo(Themes, settings.Theme, v => _host.SetTheme(v))));
        root.Children.Add(Row("停靠边缘", Combo(Edges, settings.Edge, v => _host.SetEdge(v))));
        root.Children.Add(Row("收起视图", Combo(Views, settings.CompactView, v => _host.SetCompactView(v))));
        root.Children.Add(Row("小形象款式", Combo(PetStyles, settings.PetStyle, v => _host.SetPetStyle(v))));

        root.Children.Add(Title2("行为"));
        root.Children.Add(Row("展开方式", Combo(ExpandModes, settings.ExpandMode, v => _host.SetExpandMode(v))));
        root.Children.Add(Check("最大化时缩成指示条", settings.IndicatorOnMaximize,
            v => { settings.IndicatorOnMaximize = v; settings.Save(); }));
        root.Children.Add(Check("全屏游戏/视频时自动隐藏", settings.HideFullscreen,
            v => { settings.HideFullscreen = v; settings.Save(); }));
        root.Children.Add(Check("小形象 ⇄ 信息胶囊 自动轮换", settings.PetAutoCycle,
            v => { settings.PetAutoCycle = v; settings.Save(); }));
        root.Children.Add(Check("鼠标穿透（只显示不拦截，游戏适用）", settings.ClickThrough,
            v => _host.SetClickThrough(v)));

        root.Children.Add(Title2("内容"));
        root.Children.Add(Row("天气城市", CityCombo()));
        root.Children.Add(Row("仪表盘显示项", Link("自定义…", () => _host.ShowDashSettings())));
        root.Children.Add(Row("信息胶囊显示项", Link("自定义…", () => _host.ShowComboSettings())));
        root.Children.Add(Row("监控页显示项", Link("自定义…", () => _host.ShowMonitorSettings())));

        root.Children.Add(Title2("系统"));
        root.Children.Add(AutostartCheck());
        if (!_host.IsElevated)
        {
            var hint = new TextBlock
            {
                Text = "温度读数（CPU/GPU）需要管理员权限与 PawnIO 驱动。",
                Foreground = new SolidColorBrush(palette.TextDim),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 6),
            };
            root.Children.Add(hint);
            var admin = DarkButton("以管理员重启（获取温度读数）", 32);
            admin.HorizontalAlignment = HorizontalAlignment.Left;
            admin.Margin = new Thickness(0, 0, 0, 4);
            admin.Click += (_, _) => { _host.RestartAsAdmin(); Close(); };
            root.Children.Add(admin);
        }

        var close = DarkButton("完成", 32);
        close.Width = 84;
        close.IsDefault = true;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 14, 0, 0);
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _ready = true;
    }

    private TextBlock Title2(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(_palette.Accent),
        Margin = new Thickness(0, 12, 0, 6),
    };

    private FrameworkElement Row(string label, FrameworkElement control)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var tb = new TextBlock
        {
            Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(_palette.TextDim),
        };
        DockPanel.SetDock(tb, Dock.Left);
        dock.Children.Add(tb);
        dock.Children.Add(control);
        return dock;
    }

    private ComboBox Combo((string Id, string Label)[] items, string current, Action<string> apply)
    {
        var cb = DarkCombo();
        int selected = 0;
        for (int i = 0; i < items.Length; i++)
        {
            cb.Items.Add(new ComboBoxItem { Content = items[i].Label, Tag = items[i].Id });
            if (items[i].Id == current) selected = i;
        }
        cb.SelectedIndex = selected;
        cb.SelectionChanged += (_, _) =>
        {
            if (_ready && cb.SelectedItem is ComboBoxItem { Tag: string id }) apply(id);
        };
        return cb;
    }

    /// <summary>深色下拉框：折叠态文字与弹出项强制用主题色（默认黑字在深底上不可读）。</summary>
    private ComboBox DarkCombo()
    {
        var cb = new ComboBox
        {
            MinWidth = 180,
            Background = new SolidColorBrush(_palette.SurfaceAlt),
            Foreground = new SolidColorBrush(_palette.Text),
            Padding = new Thickness(8, 3, 8, 3),
        };
        // 弹出项容器：深色底 + 主题文字色；悬停高亮用主题 Accent
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(_palette.Text)));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(_palette.SurfaceAlt)));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 5, 10, 5)));
        var hoverTrigger = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(_palette.Accent) { Opacity = 0.25 }));
        hoverTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(_palette.Text)));
        itemStyle.Triggers.Add(hoverTrigger);
        cb.ItemContainerStyle = itemStyle;
        return cb;
    }

    /// <summary>开机自启开关：普通权限走 Run 键；管理员权限走计划任务（保住温度链路）。</summary>
    private FrameworkElement AutostartCheck()
    {
        var panel = new StackPanel();
        var cb = new CheckBox
        {
            Content = _host.IsElevated ? "开机自启（计划任务 · 管理员权限）" : "开机自启",
            IsChecked = StartupService.IsEnabled(_host.IsElevated),
            Foreground = new SolidColorBrush(_palette.Text),
            Margin = new Thickness(0, 4, 0, 2),
        };
        var status = new TextBlock
        {
            Foreground = new SolidColorBrush(_palette.TextDim),
            FontSize = 11,
            Margin = new Thickness(22, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        };
        cb.Checked += (_, _) => ApplyAutostart(true, cb, status);
        cb.Unchecked += (_, _) => ApplyAutostart(false, cb, status);
        panel.Children.Add(cb);
        panel.Children.Add(status);
        return panel;
    }

    private void ApplyAutostart(bool enable, CheckBox cb, TextBlock status)
    {
        string? error = StartupService.SetEnabled(enable, _host.IsElevated);
        if (error is null)
        {
            status.Text = "";
        }
        else
        {
            status.Text = $"设置失败：{error}";
            cb.IsChecked = !enable; // 回滚勾选态
        }
    }

    private CheckBox Check(string label, bool current, Action<bool> apply)
    {
        var cb = new CheckBox
        {
            Content = label, IsChecked = current,
            Foreground = new SolidColorBrush(_palette.Text),
            Margin = new Thickness(0, 4, 0, 4),
        };
        cb.Checked += (_, _) => apply(true);
        cb.Unchecked += (_, _) => apply(false);
        return cb;
    }

    private Button Link(string text, Action open)
    {
        var b = DarkButton(text, 28);
        b.Click += (_, _) => { open(); };
        return b;
    }

    /// <summary>深色按钮：SurfaceAlt 底 + 主题文字色（默认灰按钮在深色窗口里突兀）。</summary>
    private Button DarkButton(string text, double height)
    {
        var b = new Button
        {
            Content = text,
            Height = height,
            Padding = new Thickness(12, 0, 12, 0),
            Background = new SolidColorBrush(_palette.SurfaceAlt),
            Foreground = new SolidColorBrush(_palette.Text),
            BorderBrush = new SolidColorBrush(_palette.Track),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        return b;
    }

    private ComboBox CityCombo()
    {
        var cb = DarkCombo();
        cb.Items.Add(new ComboBoxItem { Content = "自动定位（IP）", Tag = "" });
        foreach (var c in Cities)
            cb.Items.Add(new ComboBoxItem { Content = c, Tag = c });
        cb.Items.Add(new ComboBoxItem { Content = "自定义…", Tag = "__custom" });

        int selected = 0;
        for (int i = 0; i < cb.Items.Count; i++)
            if (cb.Items[i] is ComboBoxItem { Tag: string t } && t == _settings.City) selected = i;
        cb.SelectedIndex = selected;

        cb.SelectionChanged += (_, _) =>
        {
            if (!_ready || cb.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            if (tag == "__custom")
            {
                string? input = _host.PromptCity();
                if (input is not null) _host.SetWeatherCity(input);
                // 恢复显示当前城市（自定义已生效但不改下拉）
            }
            else _host.SetWeatherCity(tag);
        };
        return cb;
    }
}
