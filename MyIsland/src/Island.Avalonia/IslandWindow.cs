using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Island.App.Animation;
using Island.App.Themes;
using Island.Core.Monitoring;
using Island.Core.Quota;

namespace Island.Avalonia;

/// <summary>
/// 灵动岛主窗口（Avalonia 版）：透明置顶无框窗口，贴屏幕顶部中央。
/// 悬停展开监控页（150ms 开 / 350ms 关防抖，与 Windows 版一致），
/// 滚轮切换 dash/clock 收起视图；弹簧驱动形变。
/// 窗口铺满比本体大的区域，本体（胶囊）锚定顶边——与 Windows 版同一坐标约定。
/// </summary>
public sealed class IslandWindow : Window
{
    private const double WinW = 640, WinH = 500;
    private const double ExpandW = 560, ExpandH = 420;
    private const int HoverOpenMs = 150, HoverCloseMs = 350;

    private readonly IslandCanvas _canvas = new();
    private readonly ISampler _sampler;
    private readonly QuotaService _quota = new();
    private readonly DispatcherTimer _frame;
    private readonly DispatcherTimer _quotaTimer;
    private TrayIcon? _tray;
    private readonly Spring _bodyW = new(550, 260, 0.82);
    private readonly Spring _bodyH = new(69, 300, 0.8);
    private readonly double[] _netHist = new double[36];
    private int _histIdx;
    private double _netMax = 1;

    private bool _expanded;
    private DispatcherTimer? _hoverTimer;
    private readonly DispatcherTimer _cycleTimer;
    private readonly DispatcherTimer _weatherTimer;
    private readonly DispatcherTimer _clipsTimer;
    private readonly Island.App.Services.WeatherService _weather = new();
    private readonly ClipboardHistory _clips = new();
    private const int PetPhaseSec = 10, ComboPhaseSec = 30;

    public IslandWindow()
    {
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = true;
        CanResize = false;
        ShowInTaskbar = false;
        Width = WinW; Height = WinH;
        Content = _canvas;

        // 弹簧初值按初始视图尺寸直接到位（否则从 550 宽慢慢收缩）
        var s0 = ViewSize(_canvas.View);
        _bodyW.Snap(s0.W); _bodyH.Snap(s0.H);

        Opened += (_, _) => PositionTopCenter();

        _sampler = Samplers.Create(1000);
        _sampler.Snapshot += OnSnapshot;
        _sampler.Start();

        _frame = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _frame.Tick += Frame_Tick;
        _frame.Start();

        // pet 10s → 信息胶囊 30s 轮换（与 Windows 版一致）
        // 注意：必须显式 Normal 优先级——默认 Background 会被 60fps 渲染循环饿死
        _cycleTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(PetPhaseSec) };
        _cycleTimer.Tick += (_, _) => CycleCompact();
        StartPetCycle();

        _canvas.PointerWheelChanged += OnWheel;
        // 热区 = 本体矩形外扩（窗口本体是 640×500 的透明大区域，不能直接以进入窗口为准）
        _canvas.PointerMoved += (_, e) =>
        {
            var pos = e.GetPosition(_canvas);
            bool over = _canvas.BodyRect.Inflate(new Thickness(24, 18)).Contains(pos);
            ArmHover(over);
            // pet 视线/互动
            var bc = _canvas.BodyRect.Center;
            _canvas.LookPoint = new Point(pos.X - bc.X, pos.Y - bc.Y);
            _canvas.PointerNear = !_expanded && _canvas.BodyRect.Inflate(new Thickness(60, 60)).Contains(pos);
        };
        _canvas.PointerExited += (_, _) => { ArmHover(open: false); _canvas.PointerNear = false; };
        _canvas.PointerPressed += (_, e) =>
        {
            if (_canvas.HandleTabClick(e.GetPosition(_canvas))) { e.Handled = true; return; }
            if (!_expanded && _canvas.View == "pet") _canvas.Pet.React("surprise");
        };

        // AI 用量：本地 JSONL 扫描，10 分钟一刷（后台线程，不卡 UI）
        _quotaTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMinutes(10) };
        _quotaTimer.Tick += (_, _) => RefreshQuota();
        _quotaTimer.Start();
        RefreshQuota();

        // 天气：Open-Meteo（30 分钟刷新，内部缓存节流；城市随 settings.json 热更新）
        _weather.Settings = Island.App.Services.Settings.Load();
        _canvas.Weather = _weather.Get();   // 立即拉一次（内部后台线程）
        _weatherTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMinutes(5) };
        _weatherTimer.Tick += (_, _) =>
        {
            var s = Island.App.Services.Settings.Load();
            if (s.City != _weather.Settings.City) { _weather.Settings = s; _weather.Invalidate(); }
            _canvas.Weather = _weather.Get();
        };
        _weatherTimer.Start();

        // 剪贴板历史（跨平台轮询，1.5s；内容变化时刷新面板）
        _clips.Start(this);
        _clipsTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(2) };
        _clipsTimer.Tick += (_, _) =>
        {
            if (_expanded && _canvas.Page == "clips")
            {
                _canvas.Clips = _clips.Items;
                _canvas.InvalidateVisual();
            }
        };
        _clipsTimer.Start();

        SetupTray();
    }

    /// <summary>主屏顶部中央（Position 是物理像素，需乘缩放）。</summary>
    private void PositionTopCenter()
    {
        var scr = Screens.Primary;
        if (scr is null) return;
        double scaling = scr.Scaling;
        int pw = (int)(WinW * scaling);
        var pos = new PixelPoint(
            scr.Bounds.X + (scr.Bounds.Width - pw) / 2,
            scr.Bounds.Y);
        Position = pos;
    }

    // ---------- 悬停展开（开 150ms / 关 350ms 防抖） ----------
    private void ArmHover(bool open)
    {
        _hoverTimer?.Stop();
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(open ? HoverOpenMs : HoverCloseMs) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer?.Stop();
            SetExpanded(open);
        };
        _hoverTimer.Start();
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        _canvas.IsExpanded = expanded;
        if (expanded) { _bodyW.To(ExpandW); _bodyH.To(ExpandH); }
        else
        {
            var s = ViewSize(_canvas.View);
            _bodyW.To(s.W); _bodyH.To(s.H);
            StartPetCycle();   // 收起后恢复轮换计时
        }
    }

    // ---------- 小形象 ⇄ 信息胶囊轮换 ----------
    private void StartPetCycle()
    {
        _cycleTimer.Stop();
        if (_canvas.View is not ("pet" or "combo")) return;
        _cycleTimer.Interval = TimeSpan.FromSeconds(_canvas.View == "pet" ? PetPhaseSec : ComboPhaseSec);
        _cycleTimer.Start();
    }

    private void CycleCompact()
    {
        if (_expanded) { StartPetCycle(); return; }
        if (_canvas.View is not ("pet" or "combo")) { _cycleTimer.Stop(); return; }
        SetView(_canvas.View == "pet" ? "combo" : "pet");
    }

    private void OnSnapshot(MonitorSnapshot s)
        => Dispatcher.UIThread.Post(() =>
        {
            _canvas.Snapshot = s;
            double net = Math.Max(s.DownloadSpeed, s.UploadSpeed);
            _netMax = Math.Max(_netMax * 0.97, Math.Max(net, 1024));
            _netHist[_histIdx % _netHist.Length] = Easing.Clamp(net / _netMax, 0, 1);
            _histIdx++;
            var seq = new double[_netHist.Length];
            for (int i = 0; i < _netHist.Length; i++)
                seq[i] = _netHist[(_histIdx + i) % _netHist.Length];
            _canvas.NetHistory = seq;
        });

    private void Frame_Tick(object? sender, EventArgs e)
    {
        double dt = 1.0 / 60.0;
        _bodyW.Step(dt); _bodyH.Step(dt);
        // 动态帧率（与 WPF 版一致）：忙时 60fps，静止时 10fps——也避免渲染优先级饿死低优定时器
        bool busy = !_bodyW.AtRest || !_bodyH.AtRest || _expanded || _canvas.View == "pet";
        _frame.Interval = busy ? TimeSpan.FromMilliseconds(16) : TimeSpan.FromMilliseconds(100);
        _canvas.BodyRect = new Rect((WinW - _bodyW.Value) / 2, 0, _bodyW.Value, _bodyH.Value);
        _canvas.InvalidateVisual();
    }

    private static (double W, double H) ViewSize(string view) => view switch
    {
        "clock" => (230, 44),
        "pet" => (110, 44),
        "combo" => (440, 44),   // 信息胶囊：配额环 + 时间 + 表盘 + 网速
        _ => (660, 69),   // 核心柱 + CPU/GPU/SSD 温度 + MEM + GPU% + 盘 + 电 + ↑↓
    };

    private static readonly string[] Views = { "dash", "clock", "pet", "combo" };

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_expanded) return;
        int i = Array.IndexOf(Views, _canvas.View);
        i = (i + (e.Delta.Y > 0 ? -1 : 1) + Views.Length) % Views.Length;   // Avalonia: Delta 是 Vector
        SetView(Views[i]);
    }

    public void SetView(string view)
    {
        _canvas.View = view;
        if (!_expanded)
        {
            var s = ViewSize(view);
            _bodyW.To(s.W); _bodyH.To(s.H);
        }
        _canvas.InvalidateVisual();
        StartPetCycle();
    }

    /// <summary>按名称切主题（托盘菜单）。</summary>
    public void SetTheme(string name)
    {
        _canvas.Palette = Palette.ByName(name) ?? Palette.Dark;
        _canvas.InvalidateVisual();
    }

    // ---------- AI 用量（后台线程扫描，结果回 UI 线程） ----------
    private bool _quotaBusy;

    private void RefreshQuota()
    {
        if (_quotaBusy) return;
        _quotaBusy = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var snap = _quota.Refresh();
                Dispatcher.UIThread.Post(() => _canvas.Quota = snap);
            }
            catch { /* 本轮失败，下周期重试 */ }
            finally { _quotaBusy = false; }
        });
    }

    // ---------- 菜单栏托盘（macOS 菜单栏 / Windows 通知区） ----------
    private void SetupTray()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Island.Avalonia/island-logo-32.png"));
            _tray = new TrayIcon { Icon = new WindowIcon(stream), ToolTipText = "MyIsland" };
            NativeMenuItem Item(string header, Action act)
            {
                var mi = new NativeMenuItem(header);
                mi.Click += (_, _) => act();
                return mi;
            }
            var menu = new NativeMenu();
            menu.Items.Add(Item("仪表盘", () => SetView("dash")));
            menu.Items.Add(Item("时钟", () => SetView("clock")));
            menu.Items.Add(Item("小形象", () => SetView("pet")));
            menu.Items.Add(Item("信息胶囊", () => SetView("combo")));
            // 主题子菜单（9 套，与 Windows 版同名）
            var themeMenu = new NativeMenu();
            foreach (var t in new[] { ("Dark", "深色"), ("Light", "浅色"), ("OneDark", "One Dark Pro"),
                     ("GitHubDark", "GitHub Dark"), ("Dracula", "Dracula"), ("NightOwl", "Night Owl"),
                     ("Island", "海岛假日"), ("Cyberpunk", "赛博朋克"), ("Pixel", "像素方块") })
            {
                string n = t.Item1;
                themeMenu.Items.Add(Item(t.Item2, () => SetTheme(n)));
            }
            menu.Items.Add(new NativeMenuItem("面板主题") { Menu = themeMenu });
            menu.Items.Add(Item("退出", Close));
            _tray.Menu = menu;
            _tray.IsVisible = true;
        }
        catch { /* 托盘不可用的环境：静默降级 */ }
    }

    protected override void OnClosed(EventArgs e)
    {
        _frame.Stop();
        _hoverTimer?.Stop();
        _quotaTimer.Stop();
        _weatherTimer.Stop();
        _clipsTimer.Stop();
        _clips.Dispose();
        _weather.Dispose();
        if (_tray is not null) _tray.IsVisible = false;
        _sampler.Dispose();
        base.OnClosed(e);
    }
}
