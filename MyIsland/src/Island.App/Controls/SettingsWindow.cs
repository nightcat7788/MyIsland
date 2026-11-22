using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Island.App.Services;
using Island.App.Themes;

namespace Island.App.Controls;

/// <summary>
/// 设置中心（2026-11 重做）：BrimDeck 式深色卡片页 —— 无边框圆角窗口、
/// 分区卡片、胶囊开关、重模板化深色下拉框、主题色主按钮。所有改动即时生效并持久化。
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
    private readonly Palette _p;
    private readonly Host _host;
    private bool _ready;
    private ControlTemplate? _comboTemplate;

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
        _p = palette;
        _host = host;

        // ---------- 无边框圆角深色窗口 ----------
        Title = "MyIsland 设置";
        Width = 560; Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 13;

        var shell = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = BrushOf(_p.Surface),
            BorderBrush = BrushOf(_p.Track),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(10),   // 留出阴影空间
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, Opacity = 0.45, BlurRadius = 24,
                ShadowDepth = 4, Direction = 270,
            },
        };
        var shellGrid = new Grid();
        shellGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 标题栏
        shellGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 内容
        shell.Child = shellGrid;

        shellGrid.Children.Add(BuildTitleBar());

        // ---------- 内容 ----------
        var root = new StackPanel { Margin = new Thickness(16, 4, 16, 16) };

        // 外观
        var appearance = Card("外观");
        appearance.Add(Row("面板主题", Combo(Themes, settings.Theme, v => _host.SetTheme(v))));
        appearance.Add(Row("停靠边缘", Combo(Edges, settings.Edge, v => _host.SetEdge(v))));
        appearance.Add(Row("收起视图", Combo(Views, settings.CompactView, v => _host.SetCompactView(v))));
        appearance.Add(Row("小形象款式", Combo(PetStyles, settings.PetStyle, v => _host.SetPetStyle(v)), last: true));
        root.Children.Add(appearance);

        // 行为
        var behavior = Card("行为");
        behavior.Add(Row("展开方式", Combo(ExpandModes, settings.ExpandMode, v => _host.SetExpandMode(v))));
        behavior.Add(ToggleRow("最大化时缩成指示条", settings.IndicatorOnMaximize,
            v => { settings.IndicatorOnMaximize = v; settings.Save(); }));
        behavior.Add(ToggleRow("全屏游戏/视频时自动隐藏", settings.HideFullscreen,
            v => { settings.HideFullscreen = v; settings.Save(); }));
        behavior.Add(ToggleRow("小形象 ⇄ 信息胶囊 自动轮换", settings.PetAutoCycle,
            v => { settings.PetAutoCycle = v; settings.Save(); }));
        behavior.Add(ToggleRow("鼠标穿透（只显示不拦截，游戏适用）", settings.ClickThrough,
            v => _host.SetClickThrough(v)));
        behavior.Add(ToggleRow("长期显示灵动岛（全屏/最大化也不隐藏）", settings.AlwaysVisible,
            v => { settings.AlwaysVisible = v; settings.Save(); }, last: true));
        root.Children.Add(behavior);

        // 内容
        var content = Card("内容");
        content.Add(Row("天气城市", CityCombo()));
        content.Add(Row("仪表盘显示项", GhostLink("自定义", () => _host.ShowDashSettings())));
        content.Add(Row("信息胶囊显示项", GhostLink("自定义", () => _host.ShowComboSettings())));
        content.Add(Row("监控页显示项", GhostLink("自定义", () => _host.ShowMonitorSettings()), last: true));
        root.Children.Add(content);

        // 系统
        var system = Card("系统");
        system.Add(AutostartRow());
        if (!_host.IsElevated)
        {
            system.Add(Hint("温度读数（CPU/GPU）需要管理员权限与 PawnIO 驱动。"));
            var admin = OutlineButton("以管理员重启（获取温度读数）", _p.Warning);
            admin.HorizontalAlignment = HorizontalAlignment.Left;
            admin.Margin = new Thickness(0, 2, 0, 2);
            admin.Click += (_, _) => { _host.RestartAsAdmin(); Close(); };
            system.Add(admin);
        }
        root.Children.Add(system);

        // 底部主按钮
        var done = PrimaryButton("完成");
        done.Width = 96;
        done.IsDefault = true;
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Margin = new Thickness(0, 2, 2, 0);
        done.Click += (_, _) => Close();
        root.Children.Add(done);

        var scroll = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 1);
        shellGrid.Children.Add(scroll);

        Content = shell;
        _ready = true;
    }

    // ---------- 标题栏（可拖动 + 关闭） ----------

    private UIElement BuildTitleBar()
    {
        var bar = new Grid { Margin = new Thickness(16, 12, 12, 6) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
            Background = BrushOf(_p.Accent), Margin = new Thickness(0, 0, 8, 0),
        });
        title.Children.Add(new TextBlock
        {
            Text = "MyIsland 设置", FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = BrushOf(_p.Text), VerticalAlignment = VerticalAlignment.Center,
        });
        bar.Children.Add(title);

        var close = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "✕", FontSize = 12, Foreground = BrushOf(_p.TextDim),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        close.MouseEnter += (_, _) => close.Background = BrushOf(_p.SurfaceAlt);
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonUp += (_, _) => Close();
        Grid.SetColumn(close, 1);
        bar.Children.Add(close);

        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        return bar;
    }

    // ---------- 卡片与行 ----------

    private CardPanel Card(string title)
    {
        var inner = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(new Border
        {
            Width = 3, Height = 14, CornerRadius = new CornerRadius(1.5),
            Background = BrushOf(_p.Accent), Margin = new Thickness(0, 0, 7, 0),
        });
        head.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = BrushOf(_p.Accent), VerticalAlignment = VerticalAlignment.Center,
        });
        inner.Children.Add(head);

        var shell = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = WithOpacity(_p.SurfaceAlt, 0.55),
            Margin = new Thickness(0, 0, 0, 12),
            Child = inner,
        };
        return new CardPanel(inner, shell);
    }

    /// <summary>卡片面板：自身只装卡片边框，Add() 把行加进卡片内部。</summary>
    private sealed class CardPanel : StackPanel
    {
        private readonly StackPanel _inner;
        public CardPanel(StackPanel inner, Border shell)
        {
            _inner = inner;
            base.Children.Add(shell);
        }
        public void Add(UIElement element) => _inner.Children.Add(element);
        public int Count => _inner.Children.Count;
    }

    private FrameworkElement Row(string label, FrameworkElement control, bool last = false)
    {
        var dock = new DockPanel
        {
            Margin = new Thickness(0, 4, 0, last ? 0 : 4),
            MinHeight = 32,
        };
        var tb = new TextBlock
        {
            Text = label, VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushOf(_p.Text),
        };
        DockPanel.SetDock(tb, Dock.Left);
        control.HorizontalAlignment = HorizontalAlignment.Right;
        control.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(tb);
        dock.Children.Add(control);
        return dock;
    }

    private FrameworkElement ToggleRow(string label, bool current, Action<bool> apply, bool last = false)
    {
        var toggle = new ToggleSwitch(_p, current);
        toggle.Changed += apply;
        return Row(label, toggle, last);
    }

    private TextBlock Hint(string text) => new()
    {
        Text = text,
        Foreground = BrushOf(_p.TextDim),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 6),
    };

    // ---------- 控件 ----------

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

    /// <summary>深色下拉框：圆角重模板（折叠态 + 弹出层全部主题色）。</summary>
    private ComboBox DarkCombo()
    {
        _comboTemplate ??= BuildComboTemplate();
        var cb = new ComboBox
        {
            MinWidth = 190,
            Height = 32,
            Foreground = BrushOf(_p.Text),
            Template = _comboTemplate,
        };
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(ForegroundProperty, BrushOf(_p.Text)));
        itemStyle.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 7, 12, 7)));
        itemStyle.Setters.Add(new Setter(MarginProperty, new Thickness(4, 1, 4, 1)));
        var templateFactory = new FrameworkElementFactory(typeof(Border));
        // 圆角项容器
        var itemTpl = new ControlTemplate(typeof(ComboBoxItem));
        var b = new FrameworkElementFactory(typeof(Border));
        b.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        b.SetBinding(Border.BackgroundProperty, new Binding("Background")
            { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.MarginProperty, new Thickness(12, 7, 12, 7));
        b.AppendChild(cp);
        itemTpl.VisualTree = b;
        itemStyle.Setters.Add(new Setter(TemplateProperty, itemTpl));
        var hover = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        hover.Setters.Add(new Setter(BackgroundProperty, WithOpacity(_p.Accent, 0.28)));
        itemStyle.Triggers.Add(hover);
        cb.ItemContainerStyle = itemStyle;
        return cb;
    }

    private ControlTemplate BuildComboTemplate()
    {
        var tpl = new ControlTemplate(typeof(ComboBox));
        var root = new FrameworkElementFactory(typeof(Grid));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, BrushOf(_p.SurfaceAlt));
        border.SetValue(Border.BorderBrushProperty, BrushOf(_p.Track));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        root.AppendChild(border);

        // 全区域可点的 ToggleButton（透明底 + 右侧箭头）
        var toggle = new FrameworkElementFactory(typeof(ToggleButton));
        toggle.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
        toggle.SetValue(ToggleButton.FocusableProperty, false);
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen")
        { Mode = BindingMode.TwoWay, RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });

        var toggleTpl = new ControlTemplate(typeof(ToggleButton));
        var tGrid = new FrameworkElementFactory(typeof(Grid));
        tGrid.SetValue(Panel.BackgroundProperty, Brushes.Transparent); // 全区域命中
        var arrow = new FrameworkElementFactory(typeof(Path));
        arrow.SetValue(Path.DataProperty, Geometry.Parse("M0,0 L4,4 L8,0 Z"));
        arrow.SetValue(Path.FillProperty, BrushOf(_p.TextDim));
        arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0));
        tGrid.AppendChild(arrow);
        toggleTpl.VisualTree = tGrid;
        toggle.SetValue(ToggleButton.TemplateProperty, toggleTpl);
        root.AppendChild(toggle);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.MarginProperty, new Thickness(12, 0, 30, 0));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.IsHitTestVisibleProperty, false);
        content.SetBinding(ContentPresenter.ContentProperty, new Binding("SelectionBoxItem")
            { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        content.SetBinding(ContentPresenter.ContentTemplateProperty, new Binding("SelectionBoxItemTemplate")
            { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        root.AppendChild(content);

        var popup = new FrameworkElementFactory(typeof(Popup));
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
        popup.SetBinding(Popup.IsOpenProperty, new Binding("IsDropDownOpen")
            { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        var pBorder = new FrameworkElementFactory(typeof(Border));
        pBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        pBorder.SetValue(Border.BackgroundProperty, BrushOf(_p.SurfaceAlt));
        pBorder.SetValue(Border.BorderBrushProperty, BrushOf(_p.Track));
        pBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        pBorder.SetValue(Border.PaddingProperty, new Thickness(3, 4, 3, 4));
        pBorder.SetValue(Border.MarginProperty, new Thickness(0, 4, 0, 0));
        pBorder.SetValue(Border.EffectProperty, new DropShadowEffect
        { Color = Colors.Black, Opacity = 0.4, BlurRadius = 16, ShadowDepth = 3 });
        pBorder.SetBinding(FrameworkElement.MinWidthProperty, new Binding("ActualWidth")
            { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        var sv = new FrameworkElementFactory(typeof(ScrollViewer));
        sv.SetValue(ScrollViewer.MaxHeightProperty, 280.0);
        sv.SetValue(ScrollViewer.CanContentScrollProperty, true);
        sv.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        pBorder.AppendChild(sv);
        popup.AppendChild(pBorder);
        root.AppendChild(popup);

        tpl.VisualTree = root;
        return tpl;
    }

    /// <summary>幽灵按钮：主题色文字 + 圆角，悬停浅底。</summary>
    private Border GhostLink(string text, Action open)
    {
        var label = new TextBlock
        {
            Text = text + " ›", FontSize = 12,
            Foreground = BrushOf(_p.Accent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var b = new Border
        {
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = label,
        };
        b.MouseEnter += (_, _) => b.Background = WithOpacity(_p.Accent, 0.15);
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => open();
        return b;
    }

    /// <summary>描边按钮（管理员重启等次级动作）。</summary>
    private Button OutlineButton(string text, Color color)
    {
        var b = new Button
        {
            Content = text,
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            Background = Brushes.Transparent,
            Foreground = BrushOf(color),
            BorderBrush = BrushOf(color),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        b.Template = RoundButtonTemplate(ghost: true);
        return b;
    }

    /// <summary>主题色主按钮（自动选黑/白字保证对比度）。</summary>
    private Button PrimaryButton(string text)
    {
        double lum = 0.299 * _p.Accent.R + 0.587 * _p.Accent.G + 0.114 * _p.Accent.B;
        var b = new Button
        {
            Content = text,
            Height = 34,
            Padding = new Thickness(16, 0, 16, 0),
            Background = BrushOf(_p.Accent),
            Foreground = BrushOf(lum > 150 ? Color.FromRgb(0x14, 0x18, 0x20) : Colors.White),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
        };
        b.Template = RoundButtonTemplate(ghost: false);
        return b;
    }

    /// <summary>圆角按钮模板：悬停提亮、按下压暗（ghost=描边钮只加浅底）。</summary>
    private ControlTemplate RoundButtonTemplate(bool ghost)
    {
        var tpl = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        bd.SetValue(Border.SnapsToDevicePixelsProperty, true);
        bd.SetBinding(Border.BackgroundProperty, new Binding("Background")
            { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        bd.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush")
            { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        bd.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness")
            { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        bd.SetBinding(Border.PaddingProperty, new Binding("Padding")
            { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        bd.Name = "bd";
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        cp.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        bd.AppendChild(cp);
        tpl.VisualTree = bd;

        var over = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty,
            ghost ? BrushOf(_p.SurfaceAlt) : BrushOf(Lighten(_p.Accent, 0.12)), "bd"));
        tpl.Triggers.Add(over);
        var press = new Trigger { Property = Button.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(Border.BackgroundProperty,
            ghost ? BrushOf(_p.Track) : BrushOf(Lighten(_p.Accent, -0.10)), "bd"));
        tpl.Triggers.Add(press);
        return tpl;
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)Math.Clamp(c.R + 255 * amount, 0, 255),
        (byte)Math.Clamp(c.G + 255 * amount, 0, 255),
        (byte)Math.Clamp(c.B + 255 * amount, 0, 255));

    // ---------- 开机自启 ----------

    private FrameworkElement AutostartRow()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var status = new TextBlock
        {
            Foreground = BrushOf(_p.Warning),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        var toggle = new ToggleSwitch(_p, StartupService.IsEnabled(_host.IsElevated));
        toggle.Changed += v =>
        {
            string? error = StartupService.SetEnabled(v, _host.IsElevated);
            if (error is null) { status.Text = ""; }
            else { status.Text = $"设置失败：{error}"; toggle.IsChecked = !v; }
        };
        panel.Children.Add(Row(
            _host.IsElevated ? "开机自启（计划任务 · 管理员权限）" : "开机自启", toggle, last: true));
        panel.Children.Add(status);
        return panel;
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
            }
            else _host.SetWeatherCity(tag);
        };
        return cb;
    }

    private SolidColorBrush BrushOf(Color c) => new(c);

    private static SolidColorBrush WithOpacity(Color c, double opacity)
        => new(c) { Opacity = opacity };

    // ---------- 胶囊开关 ----------

    /// <summary>iOS 式胶囊开关：40×22，主题色点亮，140ms 滑动动画。</summary>
    private sealed class ToggleSwitch : Grid
    {
        public event Action<bool>? Changed;
        private bool _isChecked;
        private readonly Palette _p;
        private readonly Border _pill;
        private readonly TranslateTransform _knobX;

        public bool IsChecked
        {
            get => _isChecked;
            set => Set(value, animate: false, notify: false);
        }

        public ToggleSwitch(Palette p, bool initial)
        {
            _p = p;
            Width = 40; Height = 22;
            Cursor = Cursors.Hand;

            _pill = new Border { CornerRadius = new CornerRadius(11) };
            var knob = new Border
            {
                Width = 16, Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0),
                Effect = new DropShadowEffect
                { Color = Colors.Black, Opacity = 0.3, BlurRadius = 4, ShadowDepth = 1 },
            };
            _knobX = new TranslateTransform();
            knob.RenderTransform = _knobX;
            Children.Add(_pill);
            Children.Add(knob);

            MouseLeftButtonUp += (_, e) => { Set(!_isChecked, animate: true, notify: true); e.Handled = true; };
            Set(initial, animate: false, notify: false);
        }

        private void Set(bool value, bool animate, bool notify)
        {
            _isChecked = value;
            _pill.Background = new SolidColorBrush(value ? _p.Accent : _p.Track);
            double target = value ? 18 : 0;
            if (animate)
            {
                _knobX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(target,
                    TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase() });
            }
            else _knobX.X = target;
            if (notify) Changed?.Invoke(value);
        }
    }
}
