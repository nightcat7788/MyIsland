using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Documents;
using Island.App.Services;
using Island.App.Themes;

namespace Island.App.Controls;

/// <summary>
/// 设置中心（2026-11-23 v2，参考 LiteMonitor 设置面板；2026-11-25 按面板的同一套设计系统重做了外观）：
/// 左侧图标导航 + 右侧两栏卡片 + 底部按钮栏；无边框圆角窗口。所有改动即时生效并持久化。
///
/// 版式（2026-11-25 重做，规则见 SOURCE-README 的"界面设计系统"）：
///   · 窗口比原来矮（620 → <see cref="WindowH"/>）：原来四页里有三页下半截是空的；
///   · 每页两栏卡片，一屏读完。仍然留着细滚动条兜底（字号 / DPI 更大时，后面的设置不至于看不见）；
///   · 控件与面板共用 PanelStyles（细滚动条、按钮模板、卡片）和主题令牌（ExpandedPanel.ApplyTheme）；
///   · 强调色只给"选中的"：选中的导航项、主按钮、打开的开关、选中的主题。标题和卡片名用文字色 ——
///     以前每张卡片的标题和它前面的小竖条都是强调色，一页里十来处，等于没有重点。
/// </summary>
public sealed class SettingsWindow : Window
{
    /// <summary>窗口高度：最满的一页（行为）刚好一屏，见类说明。</summary>
    private const double WindowH = 444;
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
        public Action<bool> SetSmnDirectRead = _ => { };
        public Action<int> SetMonitorInterval = _ => { };
        public Action<string> SetPreferredNic = _ => { };
        public Func<string?> PromptCity = () => null;
        public bool IsElevated = true;
    }

    private readonly Settings _settings;
    private readonly Palette _p;
    private readonly Host _host;
    private bool _ready;
    private ControlTemplate? _comboTemplate;

    // 保存失败提示条（标题栏下方）：写盘失败时把原因摆到用户眼前，不再"照常关窗"
    private readonly Border _statusBar;
    private readonly TextBlock _statusText;
    // 「全部重置」的两段式确认计时器：提成字段，窗口关闭时能停掉
    private readonly System.Windows.Threading.DispatcherTimer _resetTimer;
    private Button? _resetButton;
    private bool _resetArmed;
    // 天气下拉内部改选中项时的重入保护
    private bool _cityUpdating;

    // 导航状态
    private readonly Dictionary<string, UIElement> _pages = new();
    private readonly Dictionary<string, Border> _navItems = new();
    private string _currentPage = "";

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
        ("devices", "设备电量"), ("clipboard", "剪贴板"), ("pet", "小形象"), ("window", "活动窗口"),
    };
    private static readonly (string Id, string Label)[] PetStyles =
        { ("classic", "经典双眼"), ("mono", "独眼"), ("cat", "猫咪"), ("robot", "机器人"), ("pixel", "像素") };
    private static readonly string[] Cities =
        { "北京", "上海", "广州", "深圳", "杭州", "成都", "重庆", "西安", "武汉", "南京", "苏州", "厦门", "长沙", "青岛" };

    // 导航：(页 id, 图标, 文字)。图标是 Glyphs 的矢量图标（2026-11-25 设置窗口重做：以前是 emoji ◐ ⚡ ▦ ⚙，
    // 四个 emoji 的线粗、基线、配色各不相同 —— 和面板页签原来的毛病一样）。
    private static readonly (string Id, GlyphKind Glyph, string Label)[] Nav =
    {
        ("appearance", GlyphKind.Swatch, "外观"), ("behavior", GlyphKind.Bolt, "行为"),
        ("content", GlyphKind.Grid, "内容"), ("system", GlyphKind.Gear, "系统"),
    };

    public SettingsWindow(Settings settings, Palette palette, Host host)
    {
        _settings = settings;
        _p = palette;
        _host = host;

        // ---------- 无边框圆角窗口 ----------
        Title = "MyIsland 设置";
        Width = 760; Height = WindowH;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 13;

        // ---------- 与面板同一套控件样式和主题令牌 ----------
        // 细滚动条（PanelStyles 里的隐式样式）、按钮 / 卡片的模板都在这个资源字典里；PanelStyles 里的
        // DynamicResource 全靠 ApplyTheme 写进来的令牌取色。设置窗口只开一小会儿，每次构造时现写，不做缓存。
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/Island.App;component/Controls/PanelStyles.xaml", UriKind.Relative) });
        ExpandedPanel.ApplyTheme(Resources, _p);

        var shell = new Border
        {
            CornerRadius = Res<CornerRadius>("RadiusCard"),   // 跟主题走：像素主题是近直角
            Background = BrushOf(_p.Surface),
            BorderBrush = Res<Brush>("Hairline"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(10),   // 留出阴影空间
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, Opacity = 0.45, BlurRadius = 24,
                ShadowDepth = 4, Direction = 270,
            },
        };
        var shellGrid = new Grid();
        shellGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });              // 标题栏
        shellGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });              // 提示条
        shellGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 主区
        shellGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });              // 底部按钮栏
        shell.Child = shellGrid;

        // 保存失败提示条：默认隐藏，写盘失败时才出现
        _statusText = new TextBlock
        {
            Text = "", FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Foreground = BrushOf(_p.Warning),
        };
        _statusBar = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = WithOpacity(_p.Warning, 0.12),
            BorderBrush = WithOpacity(_p.Warning, 0.45),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(16, 0, 16, 4),
            Child = _statusText,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(_statusBar, 1);
        shellGrid.Children.Add(_statusBar);

        shellGrid.Children.Add(BuildTitleBar());

        // ---------- 主区：左导航 + 右分页 ----------
        var main = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var navPanel = new StackPanel { Margin = new Thickness(8, 2, 4, 8) };
        foreach (var (id, glyph, label) in Nav)
            navPanel.Children.Add(BuildNavItem(id, glyph, label));
        main.Children.Add(navPanel);

        // 分页容器
        var pageHost = new Grid { Margin = new Thickness(0, 0, 6, 0) };
        _pages["appearance"] = WrapScroll(BuildAppearancePage());
        _pages["behavior"] = WrapScroll(BuildBehaviorPage());
        _pages["content"] = WrapScroll(BuildContentPage());
        _pages["system"] = WrapScroll(BuildSystemPage());
        foreach (var pg in _pages.Values)
        {
            pg.Visibility = Visibility.Collapsed;
            pageHost.Children.Add(pg);
        }
        Grid.SetColumn(pageHost, 1);
        main.Children.Add(pageHost);

        Grid.SetRow(main, 2);
        shellGrid.Children.Add(main);

        // ---------- 底部按钮栏（左重置 + 右完成） ----------
        var footer = BuildFooter();
        Grid.SetRow(footer, 3);
        shellGrid.Children.Add(footer);

        // 「全部重置」两段确认计时器：提成字段，窗口关闭时必须停掉，
        // 否则关窗后计时器还会持有闭包跑满 3 秒（DispatcherTimer 是强引用，不会随窗口释放）。
        _resetTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _resetTimer.Tick += (_, _) => DisarmReset();
        Closed += (_, _) => DisarmReset();
        // 打开窗口时把"上一次写盘失败"摆出来（可能是本次会话早先的失败）
        Loaded += (_, _) => ShowSaveError();

        Content = shell;
        ShowPage("appearance");
        _ready = true;
    }

    /// <summary>主题令牌（ExpandedPanel.ApplyTheme 写进本窗口资源的那一套：Fg / Accent / Hairline / Hover …）。</summary>
    private T Res<T>(string key) => (T)Resources[key];

    // ---------- 标题栏（可拖动 + 关闭） ----------

    private UIElement BuildTitleBar()
    {
        var bar = new Grid { Margin = new Thickness(16, 10, 10, 6) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = new GlyphIcon
        {
            Kind = GlyphKind.Sliders, Width = 15, Height = 15,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
        };
        icon.SetValue(TextElement.ForegroundProperty, Res<Brush>("FgDim"));
        title.Children.Add(icon);
        title.Children.Add(new TextBlock
        {
            Text = "MyIsland 设置", FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("Fg"), VerticalAlignment = VerticalAlignment.Center,
        });
        bar.Children.Add(title);

        var close = IconButton(GlyphKind.Close, TryClose);
        close.Foreground = Res<Brush>("FgDim");   // 图标颜色跟随按钮的文字色
        Grid.SetColumn(close, 1);
        bar.Children.Add(close);

        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        return bar;
    }

    /// <summary>
    /// 关闭：先落盘。写盘失败时问一句要不要仍然关闭（既不让用户以为存上了，也不把他困在窗口里）。
    /// </summary>
    private void TryClose()
    {
        _settings.Save();
        if (!CheckSaveError())
        {
            var ans = System.Windows.MessageBox.Show(
                _statusText.Text + "\n\n仍然要关闭设置窗口吗？",
                "MyIsland", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.Yes, System.Windows.MessageBoxOptions.DefaultDesktopOnly);
            if (ans != System.Windows.MessageBoxResult.Yes) return;
        }
        Close();
    }

    // ---------- 左侧导航 ----------

    private UIElement BuildNavItem(string id, GlyphKind glyph, string label)
    {
        var icon = new GlyphIcon
        {
            Kind = glyph, Width = 15, Height = 15,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
        };
        var labelTb = new TextBlock
        {
            Text = label, FontSize = 13,
            Foreground = Res<Brush>("FgDim"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(labelTb);

        var item = new Border
        {
            CornerRadius = Res<CornerRadius>("RadiusBtn"),
            Padding = new Thickness(10, 0, 10, 0),
            Height = 32,
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = row,
            Tag = (icon, labelTb),
        };
        item.MouseLeftButtonUp += (_, _) => ShowPage(id);
        item.MouseEnter += (_, _) =>
        {
            if (_currentPage != id) item.Background = Res<Brush>("CardFill");
        };
        item.MouseLeave += (_, _) =>
        {
            if (_currentPage != id) item.Background = Brushes.Transparent;
        };
        _navItems[id] = item;
        return item;
    }

    private void ShowPage(string id)
    {
        _currentPage = id;
        foreach (var (pid, page) in _pages)
            page.Visibility = pid == id ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (pid, item) in _navItems)
        {
            bool on = pid == id;
            item.Background = on ? Res<Brush>("Hover") : Brushes.Transparent;
            if (item.Tag is (GlyphIcon g, TextBlock l))
            {
                // 选中的那一项：图标换成强调色、文字提亮。强调色在设置窗口里只给"选中的"——
                // 导航、主按钮、打开的开关、选中的主题，别的地方回到文字色。
                g.SetValue(TextElement.ForegroundProperty, Res<Brush>(on ? "Accent" : "FgTertiary"));
                g.InvalidateVisual();   // 图标颜色是渲染时现读的，改完要明确重画
                l.Foreground = Res<Brush>(on ? "Fg" : "FgDim");
                l.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }

    // ---------- 底部按钮栏 ----------

    private UIElement BuildFooter()
    {
        var bar = new Border
        {
            BorderBrush = Res<Brush>("Hairline"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10, 14, 12),
        };
        var dock = new DockPanel();

        // 左：全部重置（两段确认，防误触）—— 次要动作，描边按钮 + 警示色文字
        var reset = OutlineButton("全部重置", _p.Warning);
        _resetButton = reset;
        reset.Click += (_, _) =>
        {
            if (!_resetArmed)
            {
                _resetArmed = true;
                reset.Content = "再点一次确认重置";
                _resetTimer.Start();
                return;
            }
            _resetTimer.Stop();
            _resetArmed = false;
            ResetDefaults();
            // 保存失败：不关窗，先把原因摆出来（用户重试或按 ✕ 关闭）
            if (!CheckSaveError()) return;
            Close();
        };
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        DockPanel.SetDock(reset, Dock.Left);
        dock.Children.Add(reset);

        // 右：完成（强调色主按钮，全窗口唯一带辉光的东西）
        var done = PrimaryButton("完成");
        done.MinWidth = 88;
        done.IsDefault = true;
        done.Click += (_, _) =>
        {
            _settings.Save();
            // 保存失败：提示原因并留在窗口里，别让用户以为设置存上了
            if (!CheckSaveError()) return;
            Close();
        };
        done.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(done, Dock.Right);
        dock.Children.Add(done);

        bar.Child = dock;
        return bar;
    }

    /// <summary>取消「全部重置」的待确认状态（计时器到点或窗口关闭时调用）。</summary>
    private void DisarmReset()
    {
        _resetTimer.Stop();
        _resetArmed = false;
        if (_resetButton is not null) _resetButton.Content = "全部重置";
    }

    // ---------- 保存失败提示（S5） ----------

    /// <summary>把提示条内容设成消息；null/空则隐藏提示条。</summary>
    private void ShowStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _statusText.Text = "";
            _statusBar.Visibility = Visibility.Collapsed;
            return;
        }
        _statusText.Text = message;
        _statusBar.Visibility = Visibility.Visible;
    }

    /// <summary>显示最近一次保存失败的原因（没有失败就不显示）。</summary>
    private void ShowSaveError()
    {
        string? err = Settings.LastSaveError;
        if (string.IsNullOrWhiteSpace(err)) { ShowStatus(null); return; }
        // 生产者已经带了"保存…"前缀，就别再叠一层
        ShowStatus(err.StartsWith("保存", StringComparison.Ordinal) ? err : "保存失败：" + err);
    }

    /// <summary>检查最近一次写盘是否失败：失败就把原因摆到提示条上。返回 true = 写盘正常。</summary>
    private bool CheckSaveError()
    {
        if (string.IsNullOrWhiteSpace(Settings.LastSaveError)) { ShowStatus(null); return true; }
        ShowSaveError();
        return false;
    }

    /// <summary>
    /// 全部重置：所有设置回到出厂值。
    /// 顺序很重要：先关掉活通道，再把设置写回并落盘，最后走宿主回调让运行时状态跟上。
    /// </summary>
    private void ResetDefaults()
    {
        // ⚠️ 高风险开关必须在这里关掉：SMN 直读会加载未签名的 inpoutx64.sys 内核驱动并映射 PCIe 物理内存
        //（2026-11-23 本机三次 0x1EA 蓝屏的根因）。只把配置写回 false 不够 ——
        // 当前进程里的通道还开着，而且下次**以管理员身份**启动时配置里若还留着 true，
        // 首次温度采样就会经 SmnChannel.Ensure() 去装驱动。所以先走宿主入口把活通道关掉。
        _host.SetSmnDirectRead(false);

        _settings.Theme = "Dark";
        _settings.Edge = "top";
        _settings.ExpandMode = "hover";
        _settings.CompactView = "dash";
        _settings.PetStyle = "classic";
        _settings.City = "";
        _settings.ClickThrough = false;
        _settings.IndicatorOnMaximize = true;
        _settings.HideFullscreen = true;
        _settings.PetAutoCycle = true;
        _settings.AlwaysVisible = false;
        _settings.DashItems = "";
        _settings.ComboItems = "";
        _settings.MonitorItems = "";
        // ↓ 上一轮漏掉的同类项（高风险开关 + 阈值/刷新率/网卡等）
        _settings.EnableSmnDirectRead = false;   // 高风险：必须归零，否则下次提权启动仍会装驱动
        _settings.TempAlertEnabled = true;
        _settings.TempAlertThreshold = 88;
        _settings.MonitorIntervalMs = 1000;
        _settings.PreferredNetwork = "";
        _settings.ClipboardExcludedProcesses = "";
        _settings.Save();

        // 立即生效（宿主回调只负责运行时状态）
        _host.SetTheme("Dark");
        _host.SetEdge("top");
        _host.SetExpandMode("hover");
        _host.SetCompactView("dash");
        _host.SetPetStyle("classic");
        _host.SetWeatherCity("");
        _host.SetClickThrough(false);
    }

    // ---------- 四个分页（每页两栏卡片，见类开头"版式"的说明） ----------

    private UIElement WrapScroll(UIElement content) => new ScrollViewer
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 6, 2, 8),   // 右侧给细滚动条留出位置
    };

    /// <summary>
    /// 两栏版式：左右各一列卡片。原来每页是一列卡片配一个 760×620 的窗口 —— 四页里三页的下半截是空的，
    /// 而"行为"页反而要滚动。改成两栏之后，最满的一页（行为）也刚好一屏。
    /// </summary>
    private static Grid Columns(out StackPanel left, out StackPanel right)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        left = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        right = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        g.Children.Add(left);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    private UIElement BuildAppearancePage()
    {
        var g = Columns(out var left, out var right);

        // 每个回调都"先写设置再通知宿主"：宿主回调在岛体窗口缺席时是空转的，
        // 只靠它落盘会让用户在设置中心改的东西既不生效也不写盘，重启后全丢。
        left.Children.Add(Card("主题", ThemePicker(v => { _settings.Theme = v; _settings.RequestSave(); _host.SetTheme(v); })));
        right.Children.Add(Card("收起态",
            Field("停靠边缘", Combo(Edges, _settings.Edge,
                v => { _settings.Edge = v; _settings.RequestSave(); _host.SetEdge(v); })),
            Field("收起视图", Combo(Views, _settings.CompactView,
                v => { _settings.CompactView = v; _settings.RequestSave(); _host.SetCompactView(v); })),
            Field("小形象款式", Combo(PetStyles, _settings.PetStyle,
                v => { _settings.PetStyle = v; _settings.RequestSave(); _host.SetPetStyle(v); }), last: true)));
        return g;
    }

    private UIElement BuildBehaviorPage()
    {
        var g = Columns(out var left, out var right);

        left.Children.Add(Card("展开",
            Field("展开方式", Combo(ExpandModes, _settings.ExpandMode,
                v => { _settings.ExpandMode = v; _settings.RequestSave(); _host.SetExpandMode(v); }), last: true)));

        // 监控：刷新率 + 高温告警（阈值可调）
        left.Children.Add(Card("监控",
            Field("数据刷新率", Combo(
                new[] { ("500", "0.5 秒（流畅）"), ("1000", "1 秒（推荐）"), ("2000", "2 秒（省电）"), ("5000", "5 秒（最省）") },
                _settings.MonitorIntervalMs.ToString(),
                v =>
                {
                    int ms = int.Parse(v);
                    _settings.MonitorIntervalMs = ms;
                    _settings.RequestSave();
                    _host.SetMonitorInterval(ms);
                })),
            ToggleRow("高温告警（超阈值弹浮条提醒）", _settings.TempAlertEnabled,
                v => { _settings.TempAlertEnabled = v; _settings.RequestSave(); }),
            Field("告警阈值", Combo(
                new[] { ("80", "80°（敏感）"), ("85", "85°"), ("88", "88°（默认）"), ("92", "92°"), ("95", "95°（宽松）") },
                _settings.TempAlertThreshold.ToString(),
                v => { _settings.TempAlertThreshold = int.Parse(v); _settings.RequestSave(); }), last: true)));

        right.Children.Add(Card("行为开关",
            ToggleRow("最大化时缩成指示条", _settings.IndicatorOnMaximize,
                v => { _settings.IndicatorOnMaximize = v; _settings.RequestSave(); }),
            ToggleRow("全屏游戏/视频时自动隐藏", _settings.HideFullscreen,
                v => { _settings.HideFullscreen = v; _settings.RequestSave(); }),
            ToggleRow("小形象 ⇄ 信息胶囊 自动轮换", _settings.PetAutoCycle,
                v => { _settings.PetAutoCycle = v; _settings.RequestSave(); }),
            ToggleRow("鼠标穿透（只显示不拦截，游戏适用）", _settings.ClickThrough,
                v => { _settings.ClickThrough = v; _settings.RequestSave(); _host.SetClickThrough(v); }),
            ToggleRow("长期显示灵动岛（全屏/最大化也不隐藏）", _settings.AlwaysVisible,
                v => { _settings.AlwaysVisible = v; _settings.RequestSave(); }, last: true)));
        return g;
    }

    private UIElement BuildContentPage()
    {
        var g = Columns(out var left, out var right);

        left.Children.Add(Card("天气", Field("城市", CityCombo(), last: true)));

        right.Children.Add(Card("显示项",
            Row("仪表盘", LinkButton("自定义", () => _host.ShowDashSettings())),
            Row("信息胶囊", LinkButton("自定义", () => _host.ShowComboSettings())),
            Row("监控页", LinkButton("自定义", () => _host.ShowMonitorSettings()), last: true)));
        return g;
    }

    private UIElement BuildSystemPage()
    {
        var g = Columns(out var left, out var right);

        if (!_host.IsElevated)
        {
            var admin = OutlineButton("以管理员重启（获取温度读数）", _p.Warning);
            admin.HorizontalAlignment = HorizontalAlignment.Left;
            admin.Margin = new Thickness(0, 2, 0, 2);
            admin.Click += (_, _) => { _host.RestartAsAdmin(); Close(); };
            left.Children.Add(Card("权限",
                Hint("温度读数（CPU/GPU）需要管理员权限与 PawnIO 驱动。"), admin));
        }

        left.Children.Add(Card("系统", AutostartRow(),
            Field("监控网卡", NicCombo(), last: true)));

        // ⚠️ AMD SMN 直读：会加载未签名的 inpoutx64 内核驱动 + 映射 PCIe 物理内存，
        // 2026-11-23 在本机引发过三次 0x1EA 蓝屏（整机重启）。默认关闭，只给确实需要的 AMD 用户留口子。
        //
        // 这张卡片**不再**被 if(!IsElevated) 包住：以前提权后整张卡片不渲染，
        // 于是"普通态勾开关 → 提权实例照设置加载驱动 → 事后连开关都关不掉"（2026-11-23 审查 P1-1）。
        // 现在提权/非提权都能看到它、也都能关掉它。
        // 自己建开关控件（不用 ToggleRow）：取消确认时需要把开关拨回去
        var smnToggle = new ToggleSwitch(_p, _settings.EnableSmnDirectRead, "启用 AMD SMN 直读（高风险）");
        smnToggle.Changed += v =>
        {
            if (v)
            {
                // 危险动作二次确认：授权（勾开关）与危险动作（提权后装驱动）被拆成两次独立操作，
                // 中间必须有一次明确确认，否则等于让用户在不知情的情况下给系统装一个未签名驱动。
                var answer = System.Windows.MessageBox.Show(
                    "开启后，MyIsland 在以管理员身份运行时会加载 inpoutx64.dll —— 它会自动把**未签名**的内核驱动"
                    + " inpoutx64.sys 安装进系统，并映射 PCIe 物理内存。\n\n"
                    + "2026-11-23 本机（Intel）因此三次蓝屏（0x1EA THREAD_STUCK_IN_DEVICE_DRIVER）。\n"
                    + "只有 AMD 机器才可能有收益，但风险同样存在。\n\n确定要开启吗？",
                    "高风险功能确认", System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No,
                    System.Windows.MessageBoxOptions.DefaultDesktopOnly);
                if (answer != System.Windows.MessageBoxResult.Yes)
                {
                    // 用户点了"否"：开关拨回去，设置也不要留下"已开启"
                    smnToggle.IsChecked = false;   // 不触发 Changed（notify=false）
                    _settings.EnableSmnDirectRead = false;
                    _host.SetSmnDirectRead(false);
                    return;
                }
                if (!_host.IsElevated)
                    System.Windows.MessageBox.Show(
                        "已记录开关。它只会在**以管理员身份**运行时生效 —— 请用上方「以管理员重启」再启动一次。",
                        "MyIsland", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information,
                        System.Windows.MessageBoxResult.OK, System.Windows.MessageBoxOptions.DefaultDesktopOnly);
            }
            _settings.EnableSmnDirectRead = v;
            _settings.Save();
            _host.SetSmnDirectRead(v);
        };
        right.Children.Add(Card("高风险实验功能",
            Hint("AMD SMN 直读 CPU 温度：会安装未签名的 inpoutx64.sys 内核驱动，并映射 PCIe ECAM 物理内存。"
                + "2026-11-23 在本机（Intel）曾三次 0x1EA 蓝屏。AMD 机器上首次启动会自动开启（只此一次）；"
                + "在这里关掉之后不会再自动打开，而且只有温度走常规路径读不到时才会真正用它。"),
            Row("启用 AMD SMN 直读（高风险）", smnToggle, last: true)));
        return g;
    }

    // ---------- 卡片与行 ----------

    /// <summary>分组卡片：面板同款（PanelStyles 的 GroupCard —— 半透明底 + 1px 微光边），最上面一行是卡片名。</summary>
    private CardPanel Card(string title, params UIElement[] rows)
    {
        var inner = new StackPanel { Margin = new Thickness(14, 10, 14, 8) };
        inner.Children.Add(new TextBlock
        {
            Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("FgTertiary"), Margin = new Thickness(0, 0, 0, 4),
        });
        var card = new CardPanel(inner)
        {
            Style = (Style)FindResource("GroupCard"),
            Padding = new Thickness(0),   // 留白由 inner 的 Margin 给（GroupCard 默认的内边距是给面板里更紧凑的分组用的）
            Margin = new Thickness(0, 0, 0, 10),
        };
        foreach (var r in rows) card.Add(r);
        return card;
    }

    /// <summary>卡片：自身就是那块带边的面板，Add() 把行加进卡片内部。</summary>
    private sealed class CardPanel : Border
    {
        private readonly StackPanel _inner;
        public CardPanel(UIElement inner)
        {
            _inner = (StackPanel)inner;
            Child = _inner;
        }
        public void Add(UIElement element) => _inner.Children.Add(element);
    }

    /// <summary>
    /// "左标签、右控件"的一行。标签可以折行：两栏版式里一栏只有 260 px 左右，
    /// 长一点的说明（"鼠标穿透（只显示不拦截，游戏适用）"）要折成两行才排得下。
    /// </summary>
    private FrameworkElement Row(string label, FrameworkElement control, bool last = false)
    {
        var g = new Grid { MinHeight = 30, Margin = new Thickness(0, 3, 0, last ? 0 : 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock
        {
            Text = label, Foreground = Res<Brush>("Fg"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        });
        control.HorizontalAlignment = HorizontalAlignment.Right;
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    private FrameworkElement ToggleRow(string label, bool current, Action<bool> apply, bool last = false)
    {
        // label 传给开关做无障碍名称：读屏能读到"最大化时缩成指示条：开"这类信息
        var toggle = new ToggleSwitch(_p, current, label);
        toggle.Changed += apply;
        return Row(label, toggle, last);
    }

    /// <summary>
    /// "标签在上、控件在下"的字段（下拉框用）。两栏版式里一栏只有 260 px 左右，
    /// 下拉框放不进"左标签右控件"的一行，所以竖着排。
    /// </summary>
    private FrameworkElement Field(string caption, FrameworkElement control, bool last = false)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, last ? 0 : 10) };
        panel.Children.Add(new TextBlock
        {
            Text = caption, FontSize = 12, Foreground = Res<Brush>("FgDim"), Margin = new Thickness(0, 0, 0, 4),
        });
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(control);
        return panel;
    }

    private TextBlock Hint(string text) => new()
    {
        Text = text,
        Foreground = Res<Brush>("FgTertiary"),
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 6),
    };

    /// <summary>
    /// 主题选择：每套主题一块色板（那套主题真实的中性底色 + 一点强调色），下面是名字；选中的那块描强调色边。
    /// 以前是一个下拉框 —— 九套主题只能靠名字想象。色板直接读 Palette 的颜色，换主题时这里自动跟着变。
    /// "跟随系统+壁纸"没有固定的颜色：左半深色、右半浅色。
    /// </summary>
    private FrameworkElement ThemePicker(Action<string> apply)
    {
        var panel = new WrapPanel { ItemWidth = 52, ItemHeight = 58 };
        var accent = Res<Brush>("Accent");
        var glow = Resources.Contains("Glow") ? Resources["Glow"] as Effect : null;
        var tiles = new Dictionary<string, (Border Chip, TextBlock Label, Brush Idle)>();

        // 色板按钮只有内容，没有按钮自己的边框和底（选中态画在色板的描边上）
        var tpl = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        bd.Name = "bd";
        bd.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        tpl.VisualTree = bd;
        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(UIElement.OpacityProperty, 0.82));
        tpl.Triggers.Add(over);
        // 键盘焦点落在色板上时描一圈边（Tab 走到哪儿看得见）
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, accent, "bd"));
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "bd"));
        tpl.Triggers.Add(focus);

        foreach (var (id, label) in Themes)
        {
            var pal = Palette.ByName(id);
            var chip = new Border
            {
                Width = 44, Height = 26, CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1.5), SnapsToDevicePixels = true,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            if (pal is not null)
            {
                chip.Background = new SolidColorBrush(pal.Surface);
                // 一点强调色：一眼能分出"薄荷青的深色"和"珊瑚色的海岛"
                chip.Child = new System.Windows.Shapes.Ellipse
                {
                    Width = 6, Height = 6, Fill = new SolidColorBrush(pal.Accent),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 5, 5),
                };
            }
            else
            {
                chip.Background = new LinearGradientBrush(Palette.Dark.Surface, Palette.Light.Surface, 0.0);
            }
            Brush idle = pal is null ? Res<Brush>("Hairline") : new SolidColorBrush(pal.Hairline);
            chip.BorderBrush = idle;
            var name = new TextBlock
            {
                Text = id == "Auto" ? "跟随系统" : label, FontSize = 10.5,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 52,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Res<Brush>("FgTertiary"), Margin = new Thickness(0, 4, 0, 0),
            };
            var tile = new Button
            {
                Content = new StackPanel { Children = { chip, name } },
                Template = tpl, Cursor = Cursors.Hand,
            };
            System.Windows.Automation.AutomationProperties.SetName(tile, label);
            tile.Click += (_, _) => { Select(id); apply(id); };
            tiles[id] = (chip, name, idle);
            panel.Children.Add(tile);
        }
        Select(_settings.Theme);
        return panel;

        void Select(string id)
        {
            foreach (var (key, t) in tiles)
            {
                bool on = key == id;
                t.Chip.BorderBrush = on ? accent : t.Idle;
                t.Label.Foreground = Res<Brush>(on ? "Fg" : "FgTertiary");
                t.Label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                // 深色主题下选中项带一圈同色辉光（与面板里选中的电源计划同一个手法）；浅色主题没有这束光
                t.Chip.Effect = on ? glow : null;
            }
        }
    }
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

    /// <summary>
    /// 下拉框：圆角重模板（折叠态 + 弹出层全部主题色）。2026-11-25 重做样式：1px 发丝线、几乎透明的底、细折线箭头
    /// —— 以前是实心三角箭头压在一条较重的描边上，像系统默认控件。
    /// </summary>
    private ComboBox DarkCombo()
    {
        _comboTemplate ??= BuildComboTemplate();
        var cb = new ComboBox
        {
            Height = 30,
            FontSize = 12.5,
            Foreground = Res<Brush>("Fg"),
            Template = _comboTemplate,
        };
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(ForegroundProperty, Res<Brush>("Fg")));
        itemStyle.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        // 圆角项容器
        var itemTpl = new ControlTemplate(typeof(ComboBoxItem));
        var b = new FrameworkElementFactory(typeof(Border));
        b.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        b.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        b.SetBinding(Border.BackgroundProperty, new Binding("Background")
            { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 6, 10, 6));
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        b.AppendChild(cp);
        itemTpl.VisualTree = b;
        itemStyle.Setters.Add(new Setter(TemplateProperty, itemTpl));
        var hover = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        hover.Setters.Add(new Setter(BackgroundProperty, Res<Brush>("Hover")));
        itemStyle.Triggers.Add(hover);
        var picked = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        picked.Setters.Add(new Setter(BackgroundProperty, Res<Brush>("CardFill")));
        itemStyle.Triggers.Add(picked);
        cb.ItemContainerStyle = itemStyle;
        return cb;
    }

    private ControlTemplate BuildComboTemplate()
    {
        var tpl = new ControlTemplate(typeof(ComboBox));
        var root = new FrameworkElementFactory(typeof(Grid));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, Res<CornerRadius>("RadiusBtn"));
        border.SetValue(Border.BackgroundProperty, Res<Brush>("CardFill"));
        border.SetValue(Border.BorderBrushProperty, Res<Brush>("Hairline"));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
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
        // 细折线（圆头），不是实心三角：和面板里"关闭"图标是同一种线条
        arrow.SetValue(Path.DataProperty, Geometry.Parse("M0,0 L3.5,3.5 L7,0"));
        arrow.SetValue(Path.StrokeProperty, Res<Brush>("FgDim"));
        arrow.SetValue(Path.StrokeThicknessProperty, 1.4);
        arrow.SetValue(Path.StrokeStartLineCapProperty, PenLineCap.Round);
        arrow.SetValue(Path.StrokeEndLineCapProperty, PenLineCap.Round);
        arrow.SetValue(Path.StrokeLineJoinProperty, PenLineJoin.Round);
        arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 11, 0));
        tGrid.AppendChild(arrow);
        toggleTpl.VisualTree = tGrid;
        toggle.SetValue(ToggleButton.TemplateProperty, toggleTpl);
        root.AppendChild(toggle);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 30, 0));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(FrameworkElement.IsHitTestVisibleProperty, false);
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
        pBorder.SetValue(Border.BackgroundProperty, BrushOf(_p.Surface));   // 浮层要不透明：它会压在别的内容上
        pBorder.SetValue(Border.BorderBrushProperty, Res<Brush>("Hairline"));
        pBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        pBorder.SetValue(Border.PaddingProperty, new Thickness(4, 4, 4, 4));
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

    /// <summary>"自定义"这类行内动作：描边按钮，文字用普通的文字色（强调色只留给"选中的"）。</summary>
    private Button LinkButton(string text, Action open)
    {
        var b = OutlineButton(text, _p.Text);
        b.Click += (_, _) => open();
        return b;
    }

    /// <summary>描边按钮（全部重置 / 以管理员重启这类次要动作）：面板的 QuietBtn 模板，放大到窗口里合适的尺寸。</summary>
    private Button OutlineButton(string text, Color color)
        => new()
        {
            Content = text,
            Style = WindowButton("QuietBtn"),
            Foreground = new SolidColorBrush(color),
            Cursor = Cursors.Hand,
        };

    /// <summary>主题色主按钮（全窗口只有"完成"用它）：面板的 AccentBtn 模板；深色主题下带一圈同色辉光。</summary>
    private Button PrimaryButton(string text)
        => new()
        {
            Content = text,
            Style = WindowButton("AccentBtn"),
            Effect = Resources.Contains("Glow") ? Resources["Glow"] as Effect : null,
            Cursor = Cursors.Hand,
        };

    /// <summary>
    /// 把面板的按钮样式用到设置窗口。面板里的按钮是按小面板定的尺寸（26px 高、11.5 号字），窗口里放大一号；
    /// 面板的按钮不吃 Tab 焦点（那是给鼠标 / 触控用的岛体面板），设置窗口是键盘可达的。
    /// </summary>
    private Style WindowButton(string key)
    {
        var s = new Style(typeof(Button), (Style)FindResource(key));
        s.Setters.Add(new Setter(HeightProperty, 30.0));
        s.Setters.Add(new Setter(FontSizeProperty, 12.5));
        s.Setters.Add(new Setter(PaddingProperty, new Thickness(14, 0, 14, 0)));
        s.Setters.Add(new Setter(FocusableProperty, true));
        return s;
    }

    /// <summary>图标按钮：面板页签同款（PanelStyles 的 TabBtn —— 无边框，悬停一层底色），图标颜色跟随按钮的文字色。</summary>
    private Button IconButton(GlyphKind kind, Action onClick)
    {
        var b = new Button
        {
            Style = (Style)FindResource("TabBtn"), Cursor = Cursors.Hand,
            Content = new GlyphIcon { Kind = kind, Width = 14, Height = 14 },
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ---------- 开机自启 ----------

    private FrameworkElement AutostartRow()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 7) };
        // 出错说明默认收起：空的 TextBlock 也占一行高，开关和下一项之间会多出一段空白
        var status = new TextBlock
        {
            Foreground = BrushOf(_p.Warning),
            FontSize = 11.5,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var toggle = new ToggleSwitch(_p, StartupService.IsEnabled(_host.IsElevated), "开机自启");
        toggle.Changed += v =>
        {
            string? error = StartupService.SetEnabled(v, _host.IsElevated);
            status.Text = error is null ? "" : $"设置失败：{error}";
            status.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
            if (error is not null) toggle.IsChecked = !v;
        };
        panel.Children.Add(Row("开机自启", toggle));
        // 提权实例走计划任务（要管理员权限）：当成一句说明放在开关下面，不挤在标签里
        if (_host.IsElevated)
            panel.Children.Add(new TextBlock
            {
                Text = "以计划任务方式启动（管理员权限）", FontSize = 11.5,
                Foreground = Res<Brush>("FgTertiary"), Margin = new Thickness(0, 0, 0, 2),
            });
        panel.Children.Add(status);
        return panel;
    }

    /// <summary>网卡选择下拉（LiteMonitor PreferredNetwork 同款：自动 + 本机物理网卡）。</summary>
    private ComboBox NicCombo()
    {
        var cb = DarkCombo();
        var nics = ControlService.GetNetworkAdapters();
        int selected = 0;
        for (int i = 0; i < nics.Count; i++)
        {
            cb.Items.Add(new ComboBoxItem { Content = nics[i].Name, Tag = nics[i].Id });
            if (nics[i].Id == _settings.PreferredNetwork) selected = i;
        }
        cb.SelectedIndex = selected;
        cb.SelectionChanged += (_, _) =>
        {
            if (!_ready || cb.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            _settings.PreferredNetwork = id;
            _settings.RequestSave();
            _host.SetPreferredNic(id);
        };
        return cb;
    }

    /// <summary>
    /// 天气城市下拉。
    /// 自定义城市不在候选表里时，把它作为一项（「当前值：xxx」）插进列表并选中：
    /// 否则下拉会回落到「自动定位（IP）」，用户再点它时 SelectedIndex 本来就是 0 →
    /// SelectionChanged 不触发 → SetWeatherCity("") 永远不会被调用，就再也切不回自动定位了。
    /// </summary>
    private ComboBox CityCombo()
    {
        var cb = DarkCombo();
        cb.Items.Add(new ComboBoxItem { Content = "自动定位（IP）", Tag = "" });
        foreach (var c in Cities)
            cb.Items.Add(new ComboBoxItem { Content = c, Tag = c });

        string current = _settings.City;
        bool inList = Array.IndexOf(Cities, current) >= 0;
        if (!string.IsNullOrEmpty(current) && !inList)
            cb.Items.Add(new ComboBoxItem { Content = $"当前值：{current}", Tag = current });
        cb.Items.Add(new ComboBoxItem { Content = "自定义…", Tag = "__custom" });

        int selected = 0;
        for (int i = 0; i < cb.Items.Count; i++)
            if (cb.Items[i] is ComboBoxItem { Tag: string t } && t == current) selected = i;
        cb.SelectedIndex = selected;

        cb.SelectionChanged += (_, _) =>
        {
            if (!_ready || _cityUpdating) return;
            if (cb.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            if (tag == "__custom")
            {
                string? input = _host.PromptCity();
                if (input is null) return;
                _settings.City = input;
                _settings.RequestSave();
                _host.SetWeatherCity(input);
                // 刚设的城市回显到下拉里（否则一直显示"自定义…"）
                SelectCityItem(cb, input);
            }
            else
            {
                // 空串 = 自动定位（IP）：这里必须真的调用 SetWeatherCity("")，否则切不回去
                _settings.City = tag;
                _settings.RequestSave();
                _host.SetWeatherCity(tag);
            }
        };
        return cb;
    }

    /// <summary>把城市下拉选中项切到指定城市；列表里没有就先插一项「当前值：xxx」。</summary>
    private void SelectCityItem(ComboBox cb, string city)
    {
        for (int i = 0; i < cb.Items.Count; i++)
            if (cb.Items[i] is ComboBoxItem { Tag: string t } && t == city)
            {
                _cityUpdating = true;
                try { cb.SelectedIndex = i; } finally { _cityUpdating = false; }
                return;
            }
        int at = cb.Items.Count > 0 ? cb.Items.Count - 1 : 0;   // 插在「自定义…」前面
        cb.Items.Insert(at, new ComboBoxItem { Content = $"当前值：{city}", Tag = city });
        _cityUpdating = true;
        try { cb.SelectedIndex = at; } finally { _cityUpdating = false; }
    }

    private SolidColorBrush BrushOf(Color c) => new(c);

    private static SolidColorBrush WithOpacity(Color c, double opacity)
        => new(c) { Opacity = opacity };

    // ---------- 胶囊开关 ----------

    /// <summary>
    /// 胶囊开关：36×20，与面板里的开关是同一种（打开 = 强调色轨道 + 底色滑块，关闭 = 一层淡淡的底），140ms 滑动动画。
    /// 键盘可达（Tab 可停靠、空格/回车切换）+ AutomationPeer（读屏能报出开关状态）。
    /// </summary>
    private sealed class ToggleSwitch : Grid
    {
        public event Action<bool>? Changed;
        private bool _isChecked;
        private readonly Palette _p;
        private readonly Border _pill;
        private readonly Border _knob;
        private readonly TranslateTransform _knobX;
        private readonly Border _focusRing;

        public bool IsChecked
        {
            get => _isChecked;
            set => Set(value, animate: false, notify: false);
        }

        /// <param name="label">开关对应的文字标签（给读屏用；传 null 则不设无障碍名称）。</param>
        public ToggleSwitch(Palette p, bool initial, string? label = null)
        {
            _p = p;
            Width = 36; Height = 20;
            Cursor = Cursors.Hand;
            // 键盘可达：Grid 默认不可聚焦，Tab 会直接跳过所有开关
            // （IsTabStop 是 Control 的属性，Grid 没有；UIElement.Focusable=true 就够 Tab 停靠）
            Focusable = true;
            if (!string.IsNullOrWhiteSpace(label))
            {
                AutomationProperties.SetName(this, label);
                AutomationProperties.SetHelpText(this, label);
            }

            // 焦点环：键盘用户必须看得见焦点落在哪个开关上
            _focusRing = new Border
            {
                CornerRadius = new CornerRadius(11),
                BorderBrush = new SolidColorBrush(_p.Accent),
                BorderThickness = new Thickness(1.5),
                Margin = new Thickness(-2),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            _pill = new Border { CornerRadius = new CornerRadius(10) };
            // 滑块不带阴影：这个窗口是软件渲染的分层窗口，滤镜每画一遍都要重算；平面的小圆点已经够清楚
            _knob = new Border
            {
                Width = 14, Height = 14,
                CornerRadius = new CornerRadius(7),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0),
            };
            _knobX = new TranslateTransform();
            _knob.RenderTransform = _knobX;
            Children.Add(_focusRing);
            Children.Add(_pill);
            Children.Add(_knob);

            MouseLeftButtonUp += (_, e) => { Set(!_isChecked, animate: true, notify: true); e.Handled = true; };
            Set(initial, animate: false, notify: false);
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            _focusRing.Visibility = Visibility.Visible;
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            _focusRing.Visibility = Visibility.Collapsed;
        }

        /// <summary>空格/回车切换开关（键盘用户唯一能操作它的方式）。</summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key is Key.Space or Key.Enter)
            {
                Set(!_isChecked, animate: true, notify: true);
                e.Handled = true;
            }
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new ToggleSwitchPeer(this);

        /// <summary>无障碍 Toggle() 入口：等价于点一下（走动画 + 通知 Changed）。</summary>
        internal void ToggleFromPeer() => Set(!_isChecked, animate: true, notify: true);

        /// 关闭时的轨道色：与 ExpandedPanel.ApplyTheme 的 FillStrong 同一个公式。
        private Color FillStrong()
            => Color.FromArgb((byte)Math.Round((_p.IsDark ? 0.18 : 0.12) * 255), _p.Text.R, _p.Text.G, _p.Text.B);

        private void Set(bool value, bool animate, bool notify)
        {
            _isChecked = value;
            _pill.Background = new SolidColorBrush(value ? _p.Accent : FillStrong());
            // 打开时滑块换成底色（面板里开关的同一种做法）：亮色的强调色（赛博朋克的黄）上白滑块看不清
            bool mono = _p.Accent == _p.Text;   // 单色主题（像素）：滑块也跟着单色
            _knob.Background = new SolidColorBrush(value ? _p.Surface : mono ? _p.Text : Colors.White);
            double target = value ? 16 : 0;
            if (animate)
            {
                _knobX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(target,
                    TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase() });
            }
            else
            {
                // ⚠️ 必须先摘掉上一次动画：动画默认 FillBehavior=HoldEnd，播完后仍以最高优先级持有 X 属性，
                // 不摘掉的话下面的本地赋值会被动画遮蔽 —— 药丸底色变了（没走动画）滑块却停在原位，
                // 呈现"底色是关、滑块在开"的自相矛盾状态。
                _knobX.BeginAnimation(TranslateTransform.XProperty, null);
                _knobX.X = target;
            }
            if (notify) Changed?.Invoke(value);
        }

        /// <summary>最小可用的无障碍 peer：读出名称/状态，读屏也能切换开关。</summary>
        private sealed class ToggleSwitchPeer : FrameworkElementAutomationPeer, IToggleProvider
        {
            private readonly ToggleSwitch _owner;

            public ToggleSwitchPeer(ToggleSwitch owner) : base(owner) => _owner = owner;

            protected override string GetClassNameCore() => nameof(ToggleSwitch);
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.CheckBox;

            protected override string GetNameCore()
            {
                string name = AutomationProperties.GetName(_owner);
                return string.IsNullOrEmpty(name) ? base.GetNameCore() : name;
            }

            protected override string GetHelpTextCore()
            {
                string help = AutomationProperties.GetHelpText(_owner);
                return string.IsNullOrEmpty(help) ? base.GetHelpTextCore() : help;
            }

            // Toggle 模式：既实现 IToggleProvider，也显式把 PatternInterface.Toggle 指向自己
            public override object GetPattern(PatternInterface patternInterface)
                => patternInterface == PatternInterface.Toggle ? this : base.GetPattern(patternInterface);

            public bool IsTogglePatternAvailable => true;
            public ToggleState ToggleState => _owner.IsChecked ? ToggleState.On : ToggleState.Off;
            public void Toggle() => _owner.ToggleFromPeer();
        }
    }
}
