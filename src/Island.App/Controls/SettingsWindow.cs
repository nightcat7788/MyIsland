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
using Island.App.Services;
using Island.App.Themes;

namespace Island.App.Controls;

/// <summary>
/// 设置中心（2026-11-23 v2，参考 LiteMonitor 设置面板）：
/// 左侧图标导航 + 右侧卡片分页 + 底部按钮栏；无边框圆角深色窗口，
/// 胶囊开关 + 重模板深色下拉 + 主题色主按钮。所有改动即时生效并持久化。
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
        public Action<bool> SetPetAutoCycle = _ => { };
        public Action<bool> SetDecorNotch = _ => { };
        public Action<bool> SetDecorLetters = _ => { };
        public Action<bool> SetComboNumericHero = _ => { };
        public Action<string> SetClipboardRetention = _ => { };
        public Action<string> SetWeatherCity = _ => { };
        public Action ShowDashSettings = () => { };
        public Action ShowComboSettings = () => { };
        public Action ShowMonitorSettings = () => { };
        public Action ApplyItemFilters = () => { };
        public Action RestartAsAdmin = () => { };
        public Action<bool> SetClickThrough = _ => { };
        public Action<int> SetMonitorInterval = _ => { };
        public Action<string> SetPreferredNic = _ => { };
        public Func<string> GetHardwareSummary = () => "";
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
        ("IslandAuto", "海岛假日 · 自动日夜"), ("IslandDay", "海岛假日 · 白天"), ("IslandNight", "海岛假日 · 夜晚"),
        ("Cyberpunk", "赛博朋克"), ("Pixel", "像素方块"),
    };
    private static readonly (string Id, string Label)[] Edges =
        { ("top", "顶部"), ("bottom", "底部"), ("left", "左侧"), ("right", "右侧") };
    private static readonly (string Id, string Label)[] ExpandModes =
        { ("hover", "悬停展开"), ("always", "常驻展开"), ("never", "长关") };
    private static readonly (string Id, string Label)[] Views =
    {
        ("clock", "时间胶囊"), ("combo", "信息胶囊"), ("dash", "仪表盘"), ("media", "媒体律动"), ("pet", "小形象"),
        ("monitor", "系统读数"), ("devices", "设备电量"), ("clipboard", "剪贴板"), ("window", "活动窗口"),
    };
    private static readonly (string Id, string Label)[] PetStyles =
    {
        ("classic", "经典双眼"), ("mono", "独眼"), ("cat", "猫咪"), ("robot", "机器人"),
        ("pixel", "像素"), ("alien", "外星人"), ("ghost", "小幽灵"), ("frog", "小青蛙"),
    };
    private static readonly string[] Cities =
        { "北京", "上海", "广州", "深圳", "杭州", "成都", "重庆", "西安", "武汉", "南京", "苏州", "厦门", "长沙", "青岛" };

    private static readonly (string Id, string Glyph, string Label)[] Nav =
        { ("appearance", "◐", "外观"), ("behavior", "⚡", "行为"), ("content", "▦", "内容"), ("system", "⚙", "系统") };

    public SettingsWindow(Settings settings, Palette palette, Host host)
    {
        _settings = settings;
        _p = palette;
        _host = host;

        // ---------- 无边框圆角深色窗口 ----------
        Title = "MyIsland 设置";
        Width = 760; Height = 620;
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
        shellGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });              // 标题栏
        shellGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });              // 提示条
        shellGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 主区
        shellGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });              // 底部按钮栏
        shell.Child = shellGrid;

        // 保存失败提示条：默认隐藏，写盘失败时才出现
        _statusText = new TextBlock
        {
            Text = "", FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Foreground = BrushOf(_p.Warning),
        };
        _statusBar = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = WithOpacity(_p.Warning, 0.12),
            BorderBrush = WithOpacity(_p.Warning, 0.45),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(18, 0, 18, 4),
            Child = _statusText,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(_statusBar, 1);
        shellGrid.Children.Add(_statusBar);

        shellGrid.Children.Add(BuildTitleBar());

        // ---------- 主区：左导航 + 右分页 ----------
        var main = new Grid { Margin = new Thickness(6, 0, 6, 0) };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var navPanel = new StackPanel { Margin = new Thickness(6, 2, 6, 6) };
        foreach (var (id, glyph, label) in Nav)
            navPanel.Children.Add(BuildNavItem(id, glyph, label));
        main.Children.Add(navPanel);

        // 分页容器
        var pageHost = new Grid { Margin = new Thickness(2, 0, 10, 4) };
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

        // ---------- 底部按钮栏（LiteMonitor 同款：左重置 + 右确定） ----------
        shellGrid.Children.Add(BuildFooter());

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

    // ---------- 标题栏（可拖动 + 关闭） ----------

    private UIElement BuildTitleBar()
    {
        var bar = new Grid { Margin = new Thickness(18, 12, 14, 8) };
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
        title.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = WithOpacity(_p.Accent, 0.16),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"v{Settings.AppVersion}", FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                Foreground = BrushOf(_p.Accent),
            },
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
        close.MouseLeftButtonUp += (_, _) =>
        {
            _settings.Save();
            if (!CheckSaveError())
            {
                // 写盘失败：问一句要不要仍然关闭（既不让用户以为存上了，也不把他困在窗口里）
                var ans = System.Windows.MessageBox.Show(
                    _statusText.Text + "\n\n仍然要关闭设置窗口吗？",
                    "MyIsland", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                    System.Windows.MessageBoxResult.Yes, System.Windows.MessageBoxOptions.DefaultDesktopOnly);
                if (ans != System.Windows.MessageBoxResult.Yes) return;
            }
            Close();
        };
        Grid.SetColumn(close, 1);
        bar.Children.Add(close);

        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        return bar;
    }

    // ---------- 左侧导航 ----------

    private UIElement BuildNavItem(string id, string glyph, string label)
    {
        var glyphTb = new TextBlock
        {
            Text = glyph, FontSize = 13, Width = 24,
            Foreground = BrushOf(_p.TextDim),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var labelTb = new TextBlock
        {
            Text = label, FontSize = 13,
            Foreground = BrushOf(_p.TextDim),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var dot = new Border
        {
            Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5),
            Background = BrushOf(_p.Accent), Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(glyphTb);
        row.Children.Add(labelTb);

        var item = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = row,
            Tag = (glyphTb, labelTb, dot),
        };
        item.MouseLeftButtonUp += (_, _) => ShowPage(id);
        item.MouseEnter += (_, _) =>
        {
            if (_currentPage != id) item.Background = WithOpacity(_p.SurfaceAlt, 0.6);
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
            item.Background = on ? BrushOf(_p.SurfaceAlt) : Brushes.Transparent;
            if (item.Tag is (TextBlock g, TextBlock l, Border dot))
            {
                g.Foreground = BrushOf(on ? _p.Accent : _p.TextDim);
                l.Foreground = BrushOf(on ? _p.Text : _p.TextDim);
                l.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                dot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // ---------- 底部按钮栏 ----------

    private UIElement BuildFooter()
    {
        var bar = new Border
        {
            BorderBrush = BrushOf(_p.Track),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 10, 16, 12),
        };
        var dock = new DockPanel();

        // 左：全部重置（两段确认，防误触）
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
        DockPanel.SetDock(reset, Dock.Left);
        dock.Children.Add(reset);

        // 右：完成（主题色主按钮）
        var done = PrimaryButton("完成");
        done.Width = 96;
        done.IsDefault = true;
        done.Click += (_, _) =>
        {
            _settings.Save();
            // 保存失败：提示原因并留在窗口里，别让用户以为设置存上了
            if (!CheckSaveError()) return;
            Close();
        };
        DockPanel.SetDock(done, Dock.Right);
        dock.Children.Add(done);

        bar.Child = dock;
        Grid.SetRow(bar, 3);
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
        _settings.Theme = "Dark";
        _settings.Edge = "top";
        _settings.PosAlongEdge = 0.5;
        _settings.AlwaysOnTop = true;
        _settings.ExpandMode = "hover";
        _settings.CompactView = "combo";
        _settings.PetStyle = "classic";
        _settings.City = "";
        _settings.ClickThrough = false;
        _settings.IndicatorOnMaximize = true;
        _settings.HideFullscreen = true;
        _settings.PetAutoCycle = true;
        _settings.DecorNotch = false;
        _settings.DecorLetters = true;
        _settings.ComboNumericHero = true;
        _settings.AlwaysVisible = false;
        _settings.DashItems = "";
        _settings.ComboItems = "clock,dials,disk,cputemp,gputemp,ssdtemp,battery,net";
        _settings.MonitorItems = "";
        _settings.TempAlertEnabled = true;
        _settings.TempAlertThreshold = 90;
        _settings.BatteryAlertEnabled = true;
        _settings.BatteryAlertThreshold = 20;
        _settings.MonitorIntervalMs = 1000;
        _settings.PreferredNetwork = "";
        _settings.ClipboardExcludedProcesses = "";
        ClipboardService.SetExcludedProcesses("");
        _settings.Save();

        _host.SetTheme("Dark");
        _host.SetEdge("top");
        _host.SetExpandMode("hover");
        _host.SetCompactView("clock");
        _host.SetPetStyle("classic");
        _host.SetPetAutoCycle(true);
        _host.SetDecorNotch(false);
        _host.SetDecorLetters(true);
        _host.SetComboNumericHero(true);
        _host.ApplyItemFilters();
        _host.SetMonitorInterval(1000);
        _host.SetPreferredNic("");
        _host.SetWeatherCity("");
        _host.SetClickThrough(false);
    }

    // ---------- 四个分页 ----------

    private UIElement WrapScroll(UIElement content) => new ScrollViewer
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private UIElement BuildAppearancePage()
    {
        var panel = new StackPanel { Margin = new Thickness(4, 4, 6, 4) };
        // LiteMonitor 式两列网格：标签在上、控件在下
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Cell(int row, int col, string label, UIElement control)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 4, 14, 8) };
            cell.Children.Add(new TextBlock
            {
                Text = label, FontSize = 11,
                Foreground = BrushOf(_p.TextDim), Margin = new Thickness(2, 0, 0, 4),
            });
            if (control is FrameworkElement fe)
            {
                fe.HorizontalAlignment = HorizontalAlignment.Left;
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            cell.Children.Add(control);
            Grid.SetRow(cell, row); Grid.SetColumn(cell, col);
            grid.Children.Add(cell);
        }

        // 每个回调都"先写设置再通知宿主"：宿主回调在岛体窗口缺席时是空转的，
        // 只靠它落盘会让用户在设置中心改的东西既不生效也不写盘，重启后全丢。
        Cell(0, 0, "面板主题", Combo(Themes, _settings.Theme,
            v => { _settings.Theme = v; _settings.RequestSave(); _host.SetTheme(v); }));
        Cell(0, 1, "停靠边缘", Combo(Edges, _settings.Edge,
            v => { _settings.Edge = v; _settings.RequestSave(); _host.SetEdge(v); }));
        Cell(1, 0, "收起视图", Combo(Views, _settings.CompactView,
            v => { _settings.CompactView = v; _settings.RequestSave(); _host.SetCompactView(v); }));
        Cell(1, 1, "小形象款式", Combo(PetStyles, _settings.PetStyle,
            v => { _settings.PetStyle = v; _settings.RequestSave(); _host.SetPetStyle(v); }));

        var card = Card("外观设置");
        card.Add(grid);
        panel.Children.Add(card);

        var decor = Card("胶囊造型与装饰（Mac 同款）");
        decor.Add(ToggleRow("贴顶刘海造型（关闭为默认悬浮纯圆角时间胶囊）", _settings.DecorNotch,
            v => { _settings.DecorNotch = v; _settings.RequestSave(); _host.SetDecorNotch(v); }));
        decor.Add(ToggleRow("字母标识环（圆环内显示 C / M / G / D，关闭显示矢量图标）", _settings.DecorLetters,
            v => { _settings.DecorLetters = v; _settings.RequestSave(); _host.SetDecorLetters(v); }));
        decor.Add(ToggleRow("信息胶囊数字主角样式（大号粗体百分比，关闭为时间同级经典版）", _settings.ComboNumericHero,
            v => { _settings.ComboNumericHero = v; _settings.RequestSave(); _host.SetComboNumericHero(v); }, last: true));
        panel.Children.Add(decor);
        return panel;
    }

    private UIElement BuildBehaviorPage()
    {
        var panel = new StackPanel { Margin = new Thickness(4, 4, 6, 4) };

        var expand = Card("展开方式");
        expand.Add(Row("悬停/常驻/长关", Combo(ExpandModes, _settings.ExpandMode,
            v => { _settings.ExpandMode = v; _settings.RequestSave(); _host.SetExpandMode(v); }), last: true));
        panel.Children.Add(expand);

        var behavior = Card("行为开关");
        behavior.Add(ToggleRow("最大化时缩成指示条", _settings.IndicatorOnMaximize,
            v => { _settings.IndicatorOnMaximize = v; _settings.RequestSave(); }));
        behavior.Add(ToggleRow("全屏游戏/视频时自动隐藏", _settings.HideFullscreen,
            v => { _settings.HideFullscreen = v; _settings.RequestSave(); }));
        behavior.Add(ToggleRow("小形象 ⇄ 信息胶囊 自动轮换", _settings.PetAutoCycle,
            v => { _settings.PetAutoCycle = v; _settings.RequestSave(); _host.SetPetAutoCycle(v); }));
        behavior.Add(ToggleRow("鼠标穿透（只显示不拦截，游戏适用）", _settings.ClickThrough,
            v => { _settings.ClickThrough = v; _settings.RequestSave(); _host.SetClickThrough(v); }));
        behavior.Add(ToggleRow("长期显示灵动岛（全屏/最大化也不隐藏）", _settings.AlwaysVisible,
            v => { _settings.AlwaysVisible = v; _settings.RequestSave(); }, last: true));
        panel.Children.Add(behavior);

        // 监控：刷新率 + 高温告警（阈值可调）
        var monitor = Card("监控");
        monitor.Add(Row("数据刷新率", Combo(
            new[] { ("500", "0.5 秒（流畅）"), ("1000", "1 秒（推荐）"), ("2000", "2 秒（省电）"), ("5000", "5 秒（最省）") },
            _settings.MonitorIntervalMs.ToString(),
            v =>
            {
                int ms = int.Parse(v);
                _settings.MonitorIntervalMs = ms;
                _settings.RequestSave();
                _host.SetMonitorInterval(ms);
            })));
        monitor.Add(ToggleRow("高温告警（超阈值弹浮条提醒）", _settings.TempAlertEnabled,
            v => { _settings.TempAlertEnabled = v; _settings.RequestSave(); }));
        monitor.Add(Row("告警阈值", Combo(
            new[] { ("80", "80°（敏感）"), ("85", "85°"), ("90", "90°（默认 · 红温档）"), ("95", "95°（宽松）") },
            _settings.TempAlertThreshold.ToString(),
            v => { _settings.TempAlertThreshold = int.Parse(v); _settings.RequestSave(); })));
        monitor.Add(ToggleRow("低电量告警（拔电且低于阈值时弹浮条提醒）", _settings.BatteryAlertEnabled,
            v => { _settings.BatteryAlertEnabled = v; _settings.RequestSave(); }));
        monitor.Add(Row("低电告警阈值", Combo(
            new[] { ("15", "15%"), ("20", "20%（默认）"), ("30", "30%"), ("40", "40%") },
            _settings.BatteryAlertThreshold.ToString(),
            v => { _settings.BatteryAlertThreshold = int.Parse(v); _settings.RequestSave(); }), last: true));
        panel.Children.Add(monitor);

        var hotkeys = Card("全局快捷键与手势速查");
        hotkeys.Add(Hint("Ctrl + Alt + Space：展开 / 收起灵动岛\n"
            + "Ctrl + Alt + D：循环切换收起胶囊样式（信息胶囊 / 时间胶囊 / 媒体 / 仪表盘）\n"
            + "Ctrl + Alt + S：循环切换展开面板页签\n"
            + "收起胶囊上滚动滚轮 / 单击鼠标中键：快速切换胶囊视图\n"
            + "连续左键三击胶囊：一键进入半透明鼠标穿透专注模式"));
        panel.Children.Add(hotkeys);
        return panel;
    }

    private UIElement BuildContentPage()
    {
        var panel = new StackPanel { Margin = new Thickness(4, 4, 6, 4) };

        var weather = Card("天气");
        weather.Add(Row("天气城市", CityCombo(), last: true));
        panel.Children.Add(weather);

        var items = Card("显示项自定义");
        items.Add(Row("仪表盘显示项", GhostLink("自定义", () => _host.ShowDashSettings())));
        items.Add(Row("信息胶囊显示项", GhostLink("自定义", () => _host.ShowComboSettings())));
        items.Add(Row("监控页显示项", GhostLink("自定义", () => _host.ShowMonitorSettings()), last: true));
        panel.Children.Add(items);

        var privacy = Card("剪贴板保留与隐私保护");
        privacy.Add(Row("历史保留策略", Combo(
            new[] { ("session", "退出时清除（纯内存）"), ("1d", "保留 1 天"), ("7d", "保留 7 天"), ("30d", "保留 30 天") },
            _settings.ClipboardRetention,
            v => { _settings.ClipboardRetention = v; _settings.RequestSave(); _host.SetClipboardRetention(v); })));
        privacy.Add(Hint("已默认过滤常见密码管理器（1Password / Bitwarden / KeePassXC 等）及系统剪贴板隐私标记；可在此追加额外排除进程名（逗号分隔）："));
        var clipTb = new TextBox
        {
            Width = 260, Height = 30, FontSize = 12,
            Text = _settings.ClipboardExcludedProcesses,
            Background = BrushOf(_p.SurfaceAlt),
            Foreground = BrushOf(_p.Text),
            BorderBrush = BrushOf(_p.Track),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            ToolTip = "如：wechat,dingtalk",
        };
        clipTb.TextChanged += (_, _) =>
        {
            if (!_ready) return;
            _settings.ClipboardExcludedProcesses = clipTb.Text.Trim();
            ClipboardService.SetExcludedProcesses(_settings.ClipboardExcludedProcesses);
            _settings.RequestSave();
        };
        privacy.Add(Row("追加排除进程", clipTb, last: true));
        panel.Children.Add(privacy);
        return panel;
    }

    private UIElement BuildSystemPage()
    {
        var panel = new StackPanel { Margin = new Thickness(4, 4, 6, 4) };

        var sys = Card("系统");
        sys.Add(AutostartRow());
        sys.Add(Row("监控网卡", NicCombo(), last: true));
        panel.Children.Add(sys);

        if (!_host.IsElevated)
        {
            var perm = Card("权限与免弹窗自启");
            perm.Add(Hint("读取 Intel / AMD CPU 温度需要管理员权限与 PawnIO 驱动。推荐先点下方「以管理员重启」，再开启上方「开机自启（计划任务）」——以后每次开机即可免 UAC 弹窗自动显示温度环。"));
            var admin = OutlineButton("以管理员重启（获取温度读数）", _p.Warning);
            admin.HorizontalAlignment = HorizontalAlignment.Left;
            admin.Margin = new Thickness(0, 2, 0, 2);
            admin.Click += (_, _) => { _host.RestartAsAdmin(); Close(); };
            perm.Add(admin);
            panel.Children.Add(perm);
        }

        // 硬件传感器驱动（PawnIO）：LHM 0.9.6 通过微软签名驱动统一支持 Intel MSR 与 AMD Ryzen SMN，无需手动高风险开关
        var drv = Card("硬件传感器驱动（PawnIO）");
        drv.Add(Hint("已通过微软签名的 PawnIO 驱动统一读取 Intel 与 AMD Ryzen CPU 温度/功耗（兼容 Win11 内存完整性，无需手动开关）。"));

        var statusDot = new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var statusLabel = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var statusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        statusRow.Children.Add(statusDot);
        statusRow.Children.Add(statusLabel);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var refreshBtn = OutlineButton("重新检测", _p.TextDim);
        refreshBtn.Height = 28;
        refreshBtn.Padding = new Thickness(10, 0, 10, 0);
        refreshBtn.Margin = new Thickness(0, 0, 8, 0);

        var downloadBtn = OutlineButton("下载 PawnIO", _p.Accent);
        downloadBtn.Height = 28;
        downloadBtn.Padding = new Thickness(10, 0, 10, 0);
        downloadBtn.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PawnIoDetector.DownloadUrl)
                {
                    UseShellExecute = true,
                });
            }
            catch { }
        };

        var hwSummaryText = new TextBlock
        {
            FontSize = 11,
            Foreground = BrushOf(_p.TextDim),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        void RefreshPawnIo()
        {
            var st = PawnIoDetector.Probe();
            bool ok = st.IsInstalled && !st.IsDisabled;
            statusDot.Background = BrushOf(ok ? (_host.IsElevated ? _p.Accent : _p.Warning) : _p.Warning);
            statusLabel.Foreground = BrushOf(ok && _host.IsElevated ? _p.Text : _p.Warning);
            statusLabel.Text = ok
                ? (_host.IsElevated ? $"{st.Summary} · 管理员权限正常" : $"{st.Summary}（需以管理员重启后读取温度）")
                : st.Summary;
            downloadBtn.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            string hw = _host.GetHardwareSummary();
            hwSummaryText.Text = hw;
            hwSummaryText.Visibility = string.IsNullOrWhiteSpace(hw) ? Visibility.Collapsed : Visibility.Visible;
        }

        refreshBtn.Click += (_, _) => RefreshPawnIo();
        actions.Children.Add(refreshBtn);
        actions.Children.Add(downloadBtn);
        RefreshPawnIo();

        var drvDock = new DockPanel { Margin = new Thickness(0, 4, 0, 0), MinHeight = 32 };
        DockPanel.SetDock(actions, Dock.Right);
        drvDock.Children.Add(actions);
        drvDock.Children.Add(statusRow);
        drv.Add(drvDock);
        drv.Add(hwSummaryText);
        panel.Children.Add(drv);
        return panel;
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
        // label 传给开关做无障碍名称：读屏能读到"最大化时缩成指示条：开"这类信息
        var toggle = new ToggleSwitch(_p, current, label);
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
            MinWidth = 180,
            Height = 32,
            Foreground = BrushOf(_p.Text),
            Template = _comboTemplate,
        };
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(ForegroundProperty, BrushOf(_p.Text)));
        itemStyle.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 7, 12, 7)));
        itemStyle.Setters.Add(new Setter(MarginProperty, new Thickness(4, 1, 4, 1)));
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

    /// <summary>描边按钮（重置 / 管理员重启等次级动作）。</summary>
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
        var toggle = new ToggleSwitch(_p, StartupService.IsEnabled(_host.IsElevated), "开机自启");
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
                if (input is null)
                {
                    SelectCityItem(cb, _settings.City);
                    return;
                }
                _settings.City = input;
                _settings.RequestSave();
                _host.SetWeatherCity(input);
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

    private SolidColorBrush BrushOf(Color c) => RenderKit.BrushOf(c);

    private static SolidColorBrush WithOpacity(Color c, double opacity) => RenderKit.Faint(c, opacity);

    // ---------- 胶囊开关 ----------

    /// <summary>
    /// iOS 式胶囊开关：40×22，主题色点亮，140ms 滑动动画。
    /// 键盘可达（Tab 可停靠、空格/回车切换）+ AutomationPeer（读屏能报出开关状态）。
    /// </summary>
    private sealed class ToggleSwitch : Grid
    {
        public event Action<bool>? Changed;
        private bool _isChecked;
        private readonly Palette _p;
        private readonly Border _pill;
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
            Width = 40; Height = 22;
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
                CornerRadius = new CornerRadius(13),
                BorderBrush = new SolidColorBrush(_p.Accent),
                BorderThickness = new Thickness(1.5),
                Margin = new Thickness(-2),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
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
            Children.Add(_focusRing);
            Children.Add(_pill);
            Children.Add(knob);

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
