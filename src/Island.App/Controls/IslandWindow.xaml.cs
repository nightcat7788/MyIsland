using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Island.App.Animation;
using Island.App.Services;
using Island.App.Themes;
using Island.Core.Monitoring;
using Island.Native;
using static Island.Native.NativeMethods;

namespace Island.App.Controls;

/// <summary>
/// 灵动岛主窗口：透明置顶分层窗口，驱动弹簧动画、悬停展开、边缘停靠、全屏隐藏。
/// 参考 BrimDeck/MainWindow（分层窗口/主动轮询光标）与 wisland/island.py（弹簧+液桥）。
///
/// 坐标约定（重要）：
/// - 窗口铺满一块矩形区域（比本体大一圈，容纳液桥/阴影/展开面板），Left/Top 是屏幕 DIP；
/// - 本体（胶囊）贴着屏幕边缘锚定（edge 侧 v=0），液桥从边缘"长"出来；
/// - 所有屏幕坐标统一用 DIP（WPF 逻辑像素）；光标物理像素按 DPI 换算成 DIP。
/// </summary>
public partial class IslandWindow : Window
{
    private readonly Settings _settings;
    private readonly SystemMonitor _monitor;
    private readonly Services.WeatherService _weather = new();
    private readonly Services.MediaService _media = new();
    private readonly double[] _audioBars = new double[16];
    public event Action? OpenSettingsRequested;
    private readonly DispatcherTimer _frame;
    // 帧计时：用真实时间差推进弹簧（固定 dt 会让空闲档动画慢 6 倍）
    private readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastFrameSeconds;
    // 指针跟踪是「用户正在看/正在等」性质的计时：DispatcherTimer 骑在 WM_TIMER 上，
    // 鼠标持续移动时 tick 最迟到 3.5 秒才来，悬停展开/收起会明显迟滞 → 改用 PromptTimer
    private readonly PromptTimer _pointerTrack;
    private readonly DispatcherTimer _fullscreenCheck;
    private readonly DispatcherTimer _cycleTimer;
    private readonly DispatcherTimer _weatherTimer;
    private readonly DispatcherTimer _islandSwitchTimer;
    private readonly DispatcherTimer _displayFollowTimer;
    private readonly DispatcherTimer _selfTrimTimer;

    // 弹簧：折叠宽度/高度、展开进度、液桥强度/拉伸、水滴、挤压
    private readonly Spring _bodyW = new(180, 260, 0.82);
    private readonly Spring _bodyH = new(44, 300, 0.8);
    private readonly Spring _open = new(0, 220, 0.85, 0.001);
    private readonly Spring _neckStrength = new(1, 260, 1.0, 0.01);
    private readonly Spring _neckStretch = new(0, 200, 0.9, 0.01);
    private readonly Spring _drip = new(0, 180, 0.75, 0.05);
    private readonly Spring _squash = new(1, 240, 0.6, 0.001);

    private const double ExpandW = 560, ExpandH = 420;
    private const int HoverOpenMs = 150;
    private const int HoverCloseMs = 350;

    private bool _expanded;
    private bool _dragging;
    private bool _hoverGrow;
    private int _frameTick;
    private int _lastSecond = -1;
    private const double HoverGrowW = 12, HoverGrowH = 3, SpringHoverRatio = 0.68;
    private Point _dragStart;
    private DateTime _pointerEnterSince = DateTime.MinValue;
    private DateTime _pointerLeftSince = DateTime.MinValue;
    private bool _lockedClosed;  // 双击"长关"：不再自动展开
    // 二次启动唤起后保持展开，直到鼠标真的移进岛体（否则悬停收起逻辑会把它马上关掉）
    private bool _keepOpenUntilHover;
    private Palette _palette = Palette.Dark;
    private MonitorSnapshot? _snapshot;
    // 用户显式选择的收起视图（与 PetAutoCycle 轮播临时态分离，避免关窗时把用户选的 pet 覆写成 combo）
    private string _userCompactView = "dash";

    // 本体的屏幕矩形（DIP），每帧由弹簧值计算
    private Rect _bodyRect;

    public IslandWindow(Settings settings)
    {
        _settings = settings;
        _monitor = new SystemMonitor(settings.MonitorIntervalMs);
        _monitor.PreferredNicId = settings.PreferredNetwork;   // 恢复手动网卡选择

        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        // 滚轮只挂一处：XAML 里 IslandCanvas.MouseWheel="OnMouseWheel"（冒泡阶段）已经挂了。
        // 这里再挂 PreviewMouseWheel（隧道阶段）的话，同一次滚轮先隧道再冒泡执行两遍，
        // 视图索引一次跳两格 —— 所以这里不能再挂。
        // 拖拽捕获丢失（Esc / Alt-Tab / 右键菜单）要复位拖拽态，否则岛体永久压扁
        LostMouseCapture += OnLostMouseCapture;

        expandedPanel.CloseRequested += () => SetExpanded(false);   // 顶栏 ✕
        expandedPanel.SettingsRequested += () => OpenSettingsRequested?.Invoke(); // 顶栏 ⚙
        expandedPanel.PageSelected += idx =>
        {
            _settings.LastPage = PageNameFromIndex(idx);
            _settings.RequestSave();
        };
        expandedPanel.LoadDisplayPreferences(_settings.DisplayPreferences);
        expandedPanel.DisplayPreferencesChanged += json =>
        {
            _settings.DisplayPreferences = json;
            _settings.RequestSave();
            UpdateDisplayFollowTimer();
        };
        expandedPanel.MediaView.OnPrev = () => _media.PreviousTrack();
        expandedPanel.MediaView.OnPlayPause = () => _media.TogglePlayPause();
        expandedPanel.MediaView.OnNext = () => _media.NextTrack();

        // 果冻物理：悬停微拉伸，按压缩扁回弹（仅收起态；展开面板不动）
        MouseEnter += (_, _) => { if (!_expanded) _squash.To(1.07, 240, 0.55); };
        MouseLeave += (_, _) => { if (!_expanded) _squash.To(1, 240, 0.6); };
        PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (_expanded) return;
            _squash.Snap(0.88); _squash.To(1, 300, 0.5);
            TrackTripleClick();   // 三击 → 专注模式（半透明+穿透）
        };

        // 监控刷新率（设置中心可调，立即生效并持久化）
        // 见 SetMonitorInterval()

        _frame = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _frame.Tick += Frame_Tick;

        _pointerTrack = new PromptTimer(Dispatcher, TimeSpan.FromMilliseconds(30));
        _pointerTrack.Tick += PointerTrack_Tick;

        _fullscreenCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fullscreenCheck.Tick += (_, _) => UpdateFullscreenHidden();

        // 天气：首次拉取 + 每 30 分钟刷新（内部有缓存节流）
        _weather.Settings = settings;
        _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _weatherTimer.Tick += (_, _) => islandCanvas.Weather = _weather.Get();
        _weatherTimer.Start();
        islandCanvas.Weather = _weather.Get();   // 立即拉一次（内部后台线程）

        _cycleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _cycleTimer.Tick += (_, _) => CycleCompact();

        _islandSwitchTimer = new DispatcherTimer();
        _islandSwitchTimer.Tick += (_, _) => { _islandSwitchTimer.Stop(); ApplyTheme(); };

        _displayFollowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _displayFollowTimer.Tick += (_, _) =>
        {
            var prefs = expandedPanel.CurrentDisplayPreferences;
            if (prefs.Values.Any(p => p.FollowBuiltin))
                Task.Run(() => Services.ControlService.SyncExternalFollowBrightness(prefs));
        };
        UpdateDisplayFollowTimer();

        // 轻量自瘦身定时：启动 35s 后清理冷启动分配峰值，此后收起待机每 15 分钟温和修剪一次工作集
        _selfTrimTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(35) };
        _selfTrimTimer.Tick += (_, _) =>
        {
            _selfTrimTimer.Interval = TimeSpan.FromMinutes(15);
            if (!_expanded) Task.Run(Services.ControlService.TrimSelfWorkingSet);
        };
        _selfTrimTimer.Start();

        ApplyTheme();
    }

    private void UpdateDisplayFollowTimer()
    {
        bool anyFollow = expandedPanel.CurrentDisplayPreferences.Values.Any(p => p.FollowBuiltin);
        if (anyFollow) _displayFollowTimer.Start();
        else _displayFollowTimer.Stop();
    }

    private const int HotkeyToggleId = 0xB001;
    private const int HotkeyViewId = 0xB002;
    private const int HotkeyPageId = 0xB003;

    private static string PageNameFromIndex(int idx) => idx switch
    {
        ExpandedPanel.MediaPageIndex => "media",
        ExpandedPanel.ShelfPageIndex => "clips",
        ExpandedPanel.DevicesPageIndex => "devices",
        ExpandedPanel.ControlPageIndex => "control",
        _ => "monitor",
    };

    private static int PageIndexFromName(string? name) => name switch
    {
        "media" => ExpandedPanel.MediaPageIndex,
        "clips" => ExpandedPanel.ShelfPageIndex,
        "devices" => ExpandedPanel.DevicesPageIndex,
        "control" => ExpandedPanel.ControlPageIndex,
        _ => ExpandedPanel.MonitorPageIndex,
    };

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        IntPtr ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long style = ex.ToInt64() | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
        Topmost = _settings.AlwaysOnTop;
        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(OnWndProc);
        RegisterHotKey(hwnd, HotkeyToggleId, MOD_CTRL | MOD_ALT, 0x20 /* VK_SPACE：展开/收起 */);
        RegisterHotKey(hwnd, HotkeyViewId, MOD_CTRL | MOD_ALT, 0x44 /* VK_D：切换收起样式 */);
        RegisterHotKey(hwnd, HotkeyPageId, MOD_CTRL | MOD_ALT, 0x53 /* VK_S：切换展开页签 */);
        PositionOnEdge();
    }

    private IntPtr OnWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_DPICHANGED / WM_DISPLAYCHANGE(0x007E) 会让显示器矩形与缩放失效 → 清缓存再定位
        if (msg == WM_DPICHANGED) { _dpiScale = 0; _monitorDip = null; PositionOnEdge(); }
        else if (msg == 0x007E) { _monitorDip = null; PositionOnEdge(); }   // DPI 变化后重定位（防级联位置残留）
        else if (msg == WM_SETTINGCHANGE && _settings.Theme == "Auto") ApplyTheme();   // 壁纸/深浅切换跟随
        else if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyToggleId)
            {
                if (_focusFade) { _focusFade = false; Opacity = 1.0; }
                ToggleExpand();
                handled = true;
            }
            else if (id == HotkeyViewId)
            {
                if (_focusFade) { _focusFade = false; Opacity = 1.0; }
                string[] coreViews = { "clock", "combo", "dash", "media", "pet" };
                int cur = Array.IndexOf(coreViews, _userCompactView);
                SetCompactView(coreViews[(cur + 1) % coreViews.Length]);
                handled = true;
            }
            else if (id == HotkeyPageId)
            {
                if (_focusFade) { _focusFade = false; Opacity = 1.0; }
                if (!_expanded) { _lockedClosed = false; SetExpanded(true); _keepOpenUntilHover = true; }
                else expandedPanel.CycleNextPage();
                _settings.LastPage = PageNameFromIndex(expandedPanel.CurrentPageIndex);
                _settings.RequestSave();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 心跳日志：证明窗口真的创建并跑到了这里（区分"僵尸进程"）
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "MyIsland");
            Directory.CreateDirectory(dir);
            // 走 SystemMonitor.AppendRunLog：与采样线程写 run.log 共用同一把进程内锁，
            // 否则两个写入者交错会把日志写花
            SystemMonitor.AppendRunLog(
                $"[{DateTime.Now:HH:mm:ss}] island-up pid={Environment.ProcessId} edge={_settings.Edge}\n");
        }
        catch { }

        _monitor.Snapshot += OnMonitorSnapshot;
        _monitor.Start();
        // PMv2 建窗时的 WM_DPICHANGED 会把窗口挪到级联位置，盖掉 SourceInitialized 里的定位；
        // Loaded 后窗口矩形已定，重新定位一次（实测本机 200% 缩放必现）
        PositionOnEdge();

        // 设备电量（本机电池 + 蓝牙/BLE 外设）：启动立即后台预采一次，变化即时回调 UI 线程重绘
        Services.DeviceService.Updated += OnDevicesUpdated;
        Services.DeviceService.PrimeOnce();

        // 剪贴板历史：挂监听并按保留策略恢复历史
        Services.ClipboardService.Attach(
            new System.Windows.Interop.WindowInteropHelper(this).Handle, _settings.ClipboardRetention);
        Services.ClipboardService.Changed += OnClipboardChanged;

        // 文件中转：岛体接受拖放
        AllowDrop = true;
        Drop += OnFilesDrop;

        // 系统媒体会话（SMTC + 按需音频律动；已移除不透明底不再使用的后台 GDI 抓屏毛玻璃线程）
        _media.Updated += OnMediaUpdated;
        _media.Start();

        // 音量：系统音量变化 -> 岛内浮条
        _volume.Changed += OnVolumeChanged;
        _volume.Start();

        // 温度/风扇：展开或收起视图需要时才采样；统一经 LHM 0.9.6 + PawnIO 签名驱动读取 Intel/AMD 传感器
        // 具名方法订阅：OnClosed 里要用同一个目标 -= 解绑
        //（lambda 订阅解不掉，ThermalService 是长驻的，会一直持有这个已关闭的窗口）
        _thermal.Updated += OnThermalUpdated;
        try { Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged; } catch { }
        // 启动 5s 后采一次温度做诊断（一次性；此前写法每 5s 空跑一次，违背按需采样设计）
        var prime = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        prime.Tick += (_, _) => { prime.Stop(); _thermal.PrimeOnce(); };
        prime.Start();

        _frame.Start();
        _pointerTrack.Start();
        _fullscreenCheck.Start();
        if (_settings.ClickThrough) SetClickThrough(true);   // 恢复上次的穿透状态
        islandCanvas.Pet.Style = _settings.PetStyle;
        islandCanvas.DecorNotch = _settings.DecorNotch;
        islandCanvas.DecorLetters = _settings.DecorLetters;
        islandCanvas.ComboNumericHero = _settings.ComboNumericHero;
        ApplyPalette(_palette);                      // 同步画布/监控页/设备页配色及面板资源（构造期 IsLoaded=false 曾跳过）
        expandedPanel.SelectPage(PageIndexFromName(_settings.LastPage));
        ApplyItemFilters();                          // dash/combo/监控页 显示项注入
        _userCompactView = _settings.CompactView;
        islandCanvas.CompactView = _userCompactView;
        if (_settings.MigratedOnLoad)
            _settings.RequestSave();
        StartPetCycle();   // 必须在恢复视图之后：仅 pet/combo 视图才会启动轮换
        ApplyExpandMode();
        // 预热面板数据（后台构建）：第一次展开时面板不会是空的
        RequestPanelData();
        var s = CurrentCollapsedSize();
        _lastAutoW = s.Width;
        _bodyW.Snap(s.Width); _bodyH.Snap(s.Height);
        // 首帧立即注入非零 BodyRect 并触发重绘，避免 Snap 静止态下等到首个监控快照或 3s 节拍才出画面
        _bodyRect = ComputeBodyRect(_bodyW.Value, _bodyH.Value);
        islandCanvas.Edge = _settings.Edge;
        islandCanvas.BodyRect = RenderKit.SafeRect(
            _bodyRect.X - Left, _bodyRect.Y - Top, _bodyRect.Width, _bodyRect.Height);
        islandCanvas.InvalidateVisual();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(hwnd, HotkeyToggleId);
            UnregisterHotKey(hwnd, HotkeyViewId);
            UnregisterHotKey(hwnd, HotkeyPageId);
        }
        _frame.Stop(); _fullscreenCheck.Stop();
        _cycleTimer.Stop(); _weatherTimer.Stop();
        _islandSwitchTimer.Stop(); _displayFollowTimer.Stop(); _selfTrimTimer.Stop();
        expandedPanel.StopTimers();
        _pointerTrack.Stop(); _pointerTrack.Dispose();
        // 服务是长驻的：不解绑的话，它们会一直持有这个已关闭的窗口
        _monitor.Snapshot -= OnMonitorSnapshot;
        _media.Updated -= OnMediaUpdated;
        Services.ClipboardService.Changed -= OnClipboardChanged;
        Services.DeviceService.Updated -= OnDevicesUpdated;
        _volume.Changed -= OnVolumeChanged;
        _thermal.Updated -= OnThermalUpdated;
        try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }   // 解绑温度回调（服务长驻，不解绑会一直持有已关闭的窗口）
        LostMouseCapture -= OnLostMouseCapture;
        Services.ClipboardService.Detach();   // 摘掉剪贴板监听（窗口关了就不该再记录）
        Services.DeviceService.Stop();        // 停掉设备电量轮询（不随窗口常驻）
        StopMonitor();
        _volume.Dispose();
        _thermal.Dispose();
        _media.Dispose();
        _weather.Dispose();
        _settings.CompactView = _userCompactView;
        _settings.Save();
    }

    /// <summary>
    /// 停掉监控采样：StopAndWait = 停表 + 等在途采样结束。
    /// 直接 Dispose 不会等在途 tick，那一轮采样会在已释放的计数器上抛
    /// ObjectDisposedException，被采样线程记进 run.log —— 写出误导性的 "sample-failed"。
    /// </summary>
    private void StopMonitor()
    {
        _monitor.StopAndWait(500);
        // Dispose 走同一条路径且幂等，负责释放 PDH 句柄等资源
        _monitor.Dispose();
    }

    // ---------- 主循环 ----------
    private readonly Services.VolumeService _volume = new();
    private readonly Services.ThermalService _thermal = new();

    // 历史环形缓冲（走势图）：36 点
    private readonly double[] _cpuHist = new double[36];
    private readonly double[] _netHist = new double[36];
    private readonly double[] _downHist = new double[36];
    private readonly double[] _upHist = new double[36];
    private int _histIdx;
    private double _netMax = 1; // 网速自归一化的滑动峰值

    private void Frame_Tick(object? sender, EventArgs e)
    {
        // 真实帧间隔：原来固定 1/60，而空闲档的 tick 是 100ms → 弹簧每拍只推进 1/6 秒，
        // 动画尾段看起来"顿一下再跳到终点"。改用真实 dt 并钳到 50ms
        //（Spring 内部按 1/240 子步积分，大 dt 也不会发散）。
        double now = _frameClock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastFrameSeconds, 0.0, 0.05);
        _lastFrameSeconds = now;
        _bodyW.Step(dt); _bodyH.Step(dt); _open.Step(dt);
        _neckStrength.Step(dt); _neckStretch.Step(dt); _drip.Step(dt); _squash.Step(dt);

        // 动态帧率（参考 BrimDeck/wisland 的省电做法），三档：
        //   弹簧在动 / 有临时活动 → 60fps
        //   小形象视图（连续帧动画）→ 30fps
        //   其它静止态 → 10fps
        // ⚠️ 之前把 "dash" 也算进 busy：仪表盘的数据全是 1Hz 快照、���面里没有时钟，
        // 却常年 60fps 重建 640×500 的分层表面（实测单核 28%）。
        bool moving = !_bodyW.AtRest || !_bodyH.AtRest || !_open.AtRest || !_neckStrength.AtRest ||
                      !_squash.AtRest || !_drip.AtRest;
        bool activity = islandCanvas.Activity is { } a && DateTime.Now < a.Until;
        bool petFrames = !_expanded && islandCanvas.CompactView == "pet";
        bool wantSpectrum = (_expanded && expandedPanel.CurrentPageIndex == ExpandedPanel.MediaPageIndex)
                         || (!_expanded && islandCanvas.CompactView == "media");
        _media.SetSpectrumWanted(wantSpectrum);
        bool mediaFrames = wantSpectrum && _media.Current.IsPlaying;
        if (mediaFrames)
        {
            _media.CopyBars(_audioBars);
            islandCanvas.AudioBars = _audioBars;
            expandedPanel.MediaView.SetAudioBars(_audioBars);
            if (_expanded) expandedPanel.MediaView.InvalidateVisual();
        }
        bool busy = moving || activity;
        _frame.Interval = busy ? TimeSpan.FromMilliseconds(16)
                         : (petFrames || mediaFrames) ? TimeSpan.FromMilliseconds(33)
                         : TimeSpan.FromMilliseconds(100);

        _bodyRect = ComputeBodyRect(_bodyW.Value, _bodyH.Value);

        // 本体矩形（窗口相对 DIP）注入自绘控件；位置或尺寸跳变（如重定位/Snap）时也强制重绘
        var nextBody = RenderKit.SafeRect(
            _bodyRect.X - Left, _bodyRect.Y - Top, _bodyRect.Width, _bodyRect.Height);
        bool bodyChanged = nextBody != islandCanvas.BodyRect;
        islandCanvas.BodyRect = nextBody;

        islandCanvas.NeckStrength = _neckStrength.Value;
        islandCanvas.NeckStretch = _neckStretch.Value;
        islandCanvas.DripRadius = _drip.Value;
        islandCanvas.Squash = _squash.Value;
        islandCanvas.IsExpanded = _open.Value > 0.5;
        islandCanvas.Edge = _settings.Edge;
        var cursor = CursorDip();
        var bc = BodyCenter;
        islandCanvas.LookPoint = new Point(cursor.X - bc.X, cursor.Y - bc.Y);
        bool near = !_expanded && System.Windows.Rect.Inflate(_bodyRect, 24, 18).Contains(cursor);
        islandCanvas.PointerNear = !_expanded && System.Windows.Rect.Inflate(_bodyRect, 60, 60).Contains(cursor);
        ApplyHoverGrow(near);
        // 小形象情境（拓麻歌子式：温度/磁盘状态直接读现有快照，零新增采集）
        islandCanvas.Pet.CpuTemp = _thermal.Current?.CpuTemp;
        islandCanvas.Pet.DiskActive = _snapshot?.DiskUsagePercent ?? 0;
        // 天气：每当 WeatherService 产出新实例（首次异步定位拉取 / 换城市 / 30 分钟刷新）时同步给画布并重测宽度
        var latestWeather = _weather.Current;
        if (!ReferenceEquals(islandCanvas.Weather, latestWeather))
        {
            islandCanvas.Weather = latestWeather;
            ApplyAutoCollapsedWidth();
            islandCanvas.InvalidateVisual();
        }
        // 秒级心跳：时间胶囊（HH:mm ss）每秒实时跳秒，跨分钟时同步更新 combo 时间
        int sec = DateTime.Now.Second;
        bool secondTick = sec != _lastSecond;
        if (secondTick)
        {
            _lastSecond = sec;
            if (!_expanded && islandCanvas.CompactView is "clock" or "combo")
            {
                ApplyAutoCollapsedWidth();
                islandCanvas.InvalidateVisual();
            }
        }
        _frameTick++;
        if (busy || bodyChanged || petFrames || mediaFrames
            || (!_expanded && islandCanvas.CompactView is "devices" or "clipboard")
            || _frameTick % 30 == 0)
            islandCanvas.InvalidateVisual();

        expandedPanel.Opacity = Easing.Clamp(_open.Value, 0, 1);
        expandedPanel.IsHitTestVisible = _open.Value > 0.9;
        if (_open.Value > 0.05 && expandedPanel.DataContext is null)
            RequestPanelData();

        // 按需采样配额：展开监控页才扫进程榜与 Ping；收起态按当前胶囊视图裁剪
        var demand = (_open.Value > 0.3 && expandedPanel.CurrentPageIndex == ExpandedPanel.MonitorPageIndex)
            ? SamplerDemand.MonitorPage
            : islandCanvas.CompactView is "dash" or "combo" or "monitor" ? SamplerDemand.Dash
            : SamplerDemand.Collapsed;
        _monitor.SetDemand(demand);

        bool thermalWanted = (_open.Value > 0.3 && expandedPanel.CurrentPageIndex == ExpandedPanel.MonitorPageIndex)
            || (!_expanded && islandCanvas.CompactView is "dash" or "combo");
        _thermal.SetWanted(thermalWanted);
        _thermal.SetHighFrequency(_expanded);
        if (thermalWanted)
            islandCanvas.Thermal = _thermal.Current;

        // 设备电量：展开监控页/设备页，或收起态 clock/combo/dash/devices 视图下保持 15s 轻量原生采样
        bool wantDevices = (_expanded && expandedPanel.CurrentPageIndex is ExpandedPanel.DevicesPageIndex or ExpandedPanel.MonitorPageIndex)
            || (!_expanded && islandCanvas.CompactView is "clock" or "combo" or "dash" or "devices");
        Services.DeviceService.SetHighFrequency(_expanded && expandedPanel.CurrentPageIndex == ExpandedPanel.DevicesPageIndex);
        Services.DeviceService.SetWanted(wantDevices);
    }

    /// <summary>本体的屏幕矩形（DIP）。默认圆角时间胶囊与展开面板悬浮留 6px 呼吸间距；仅贴顶刘海收起态贴住顶边（y=0）。</summary>
    private Rect ComputeBodyRect(double bodyW, double bodyH)
    {
        double t = Easing.Clamp(_settings.PosAlongEdge, 0.02, 0.98);
        // 屏宽用监视器物理像素 ÷ DpiScale：SystemParameters 在本机（200% 缩放）实测
        // 返回物理像素 3072，导致岛心被放到可见区右缘（2 倍未换算）
        GetScreenSizeDip(out double sw, out double sh, out double ox, out double oy);
        double topPad = !_indicatorMode && (!_settings.DecorNotch || _expanded) ? 6 : 0;
        // ox/oy：副屏在主屏左侧/上方时，虚拟桌面原点不是 (0,0)；不带上就会贴到主屏那一侧
        return _settings.Edge switch
        {
            "bottom" => RenderKit.SafeRect(ox + sw * t - bodyW / 2, oy + sh - bodyH - topPad, bodyW, bodyH),
            "left"   => RenderKit.SafeRect(ox + topPad, oy + sh * t - bodyH / 2, bodyW, bodyH),
            "right"  => RenderKit.SafeRect(ox + sw - bodyW - topPad, oy + sh * t - bodyH / 2, bodyW, bodyH),
            _        => RenderKit.SafeRect(ox + sw * t - bodyW / 2, oy + topPad, bodyW, bodyH),
        };
    }

    /// <summary>
    /// 屏幕 DIP 尺寸与原点：监视器物理矩形 ÷ 窗口 DPI 缩放（任何感知模式下都正确）。
    /// 显示器信息做缓存：原来每帧都要 MonitorFromWindow + GetMonitorInfo + 结构体编组。
    /// 缓存只在 DPI 变化（WM_DPICHANGED）或显示器拓扑变化（WM_DISPLAYCHANGE）时失效。
    /// </summary>
    private void GetScreenSizeDip(out double sw, out double sh, out double ox, out double oy)
    {
        try
        {
            Rect cached = _monitorDip ?? ReadMonitorDip();
            sw = cached.Width; sh = cached.Height;
            ox = cached.X; oy = cached.Y;
            return;
        }
        catch { }
        sw = SystemParameters.PrimaryScreenWidth;
        sh = SystemParameters.PrimaryScreenHeight;
        ox = 0; oy = 0;
    }

    /// <summary>读当前显示器的 DIP 矩形（含原点）并缓存。</summary>
    private Rect ReadMonitorDip()
    {
        try
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            double scale = DpiScale;
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi) && scale > 0.1)
            {
                var rect = RenderKit.SafeRect(
                    mi.rcMonitor.Left / scale,
                    mi.rcMonitor.Top / scale,
                    (mi.rcMonitor.Right - mi.rcMonitor.Left) / scale,
                    (mi.rcMonitor.Bottom - mi.rcMonitor.Top) / scale);
                _monitorDip = rect;
                return rect;
            }
        }
        catch { }
        var fallback = RenderKit.SafeRect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        _monitorDip = fallback;
        return fallback;
    }

    // 显示器 DIP 矩形缓存（DPI / 显示器拓扑变化时失效）
    private Rect? _monitorDip;

    private Point BodyCenter => new(_bodyRect.X + _bodyRect.Width / 2, _bodyRect.Y + _bodyRect.Height / 2);

    /// <summary>光标位置（屏幕 DIP）。GetCursorPos 是物理像素，按监视器 DPI 换算。</summary>
    private Point CursorDip()
    {
        if (!GetCursorPos(out var p)) return new Point(0, 0);
        double scale = DpiScale;
        return new Point(p.X / scale, p.Y / scale);
    }

    private double _dpiScale;
    private double DpiScale
    {
        get
        {
            if (_dpiScale > 0) return _dpiScale;
            try
            {
                var src = System.Windows.Interop.HwndSource.FromVisual(this);
                if (src?.CompositionTarget is not null)
                {
                    _dpiScale = src.CompositionTarget.TransformToDevice.M11;
                    return _dpiScale;
                }
            }
            catch { }
            _dpiScale = 1.0;
            return 1.0;
        }
    }

    // ---------- 悬停展开 / 收起 ----------
    /// <summary>监控刷新率（设置中心调用）：立即生效并持久化。</summary>
    public void SetMonitorInterval(int ms)
    {
        _settings.MonitorIntervalMs = ms;
        _settings.Save();
        _monitor.SetInterval(ms);
    }

    /// <summary>手动选择网卡（设置中心调用）：立即生效并持久化。</summary>
    public void SetPreferredNic(string nicId)
    {
        _settings.PreferredNetwork = nicId;
        _settings.Save();
        _monitor.PreferredNicId = nicId;
    }

    /// <summary>非管理员运行时为 true：监控页温度位显示"需管理员"提示。</summary>
    private bool _tempNeedAdmin;
    public bool MonitorTempNeedAdmin
    {
        get => expandedPanel.Monitor.TempNeedAdmin;
        set { _tempNeedAdmin = value; expandedPanel.Monitor.TempNeedAdmin = value; }
    }

    /// <summary>展开方式：hover=悬停展开 always=常驻展开 never=长关（不自动展开）。</summary>
    public string ExpandMode => _settings.ExpandMode;

    private void ApplyExpandMode()
    {
        switch (_settings.ExpandMode)
        {
            case "always":
                _lockedClosed = false;
                SetExpanded(true);
                break;
            case "never":
                _lockedClosed = true;
                SetExpanded(false);
                break;
            default: // hover
                _lockedClosed = false;
                break;
        }
    }

    public void SetExpandMode(string mode)
    {
        _settings.ExpandMode = mode;
        _settings.Save();
        ApplyExpandMode();
    }

    /// <summary>
    /// 二次启动唤起用：把岛体叫出来并展开到展开态。
    /// 与 ToggleExpand 的区别是"展开到"而不是"切换"——已经是展开态时不会被收起
    /// （BrimDeck 的二次启动也是"唤起到展开"，不是切换）。
    /// </summary>
    public void ShowAndExpand()
    {
        if (!IsVisible) Show();
        if (_expanded) return;
        // never 是用户明确要求"不自动展开"，这里只把岛体叫出来，不替他展开
        if (_settings.ExpandMode == "never") return;
        SetExpanded(true);
        // 唤起时鼠标多半不在岛体上：不锁住的话，悬停收起逻辑 350ms 后又把它关回去，等于白唤起。
        // 锁到"鼠标真的移进岛体"为止——那时用户已经在用面板了，再按正常悬停规则走。
        _keepOpenUntilHover = true;
    }

    private void PointerTrack_Tick(object? sender, EventArgs e)
    {
        if (_settings.ClickThrough) return;   // 穿透模式：悬停不展开（BrimDeck 兼容模式）
        if (_settings.ExpandMode == "always")
        {
            if (!_expanded) SetExpanded(true);
            return; // 常驻展开：不跟踪悬停
        }
        if (_settings.ExpandMode == "never") return; // 长关：不自动展开

        var cursor = CursorDip();
        // 热区随视图宽度自适应（宽视图不要求鼠标精确怼到中心；指示条模式下用指示条尺寸）
        double hotX = Math.Max(24, (CurrentCollapsedSize().Width - _bodyRect.Width) / 2 + 24);
        bool over = System.Windows.Rect.Inflate(_bodyRect, hotX, 18).Contains(cursor);

        if (over)
        {
            _pointerLeftSince = DateTime.MinValue;
            _keepOpenUntilHover = false;   // 鼠标已移进岛体：恢复正常的悬停规则
            if (!_expanded && !_lockedClosed)
            {
                if (_pointerEnterSince == DateTime.MinValue)
                    _pointerEnterSince = DateTime.Now;   // 先记录进入时刻，下一轮再比较（修复永不触发 bug）
                else if ((DateTime.Now - _pointerEnterSince).TotalMilliseconds >= HoverOpenMs)
                    SetExpanded(true);
            }
        }
        else
        {
            _pointerEnterSince = DateTime.MinValue;
            if (_expanded)
            {
                // 二次启动唤起后：鼠标还没移进来，先保持展开，别让它 350ms 后自己关掉
                if (_keepOpenUntilHover) return;
                if (_pointerLeftSince == DateTime.MinValue)
                    _pointerLeftSince = DateTime.Now;
                else if ((DateTime.Now - _pointerLeftSince).TotalMilliseconds >= HoverCloseMs)
                    SetExpanded(false);
            }
        }
    }

    /// <summary>悬停时胶囊轻微长大（对齐 macOS 版 NotchBar 手感：宽 +12 / 高 +3，阻尼 0.68）。</summary>
    private void ApplyHoverGrow(bool hovering)
    {
        if (_expanded || _indicatorMode) { _hoverGrow = false; return; }
        if (_hoverGrow == hovering) return;
        _hoverGrow = hovering;
        if (islandCanvas.CompactView != "pet")
        {
            var s = CurrentCollapsedSize();
            _lastAutoW = s.Width;
            _bodyW.To(s.Width + (hovering ? HoverGrowW : 0), null, SpringHoverRatio);
            _bodyH.To(s.Height + (hovering ? HoverGrowH : 0), null, SpringHoverRatio);
        }
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        _pointerEnterSince = DateTime.MinValue;
        _pointerLeftSince = DateTime.MinValue;

        if (expanded)
        {
            _hoverGrow = false;
            _bodyW.To(ExpandW); _bodyH.To(ExpandH); _open.To(1);
            _neckStrength.To(0);
            // 每次展开都重新拉一遍面板数据（后台构建）：读数不会停在上一轮的旧值
            RequestPanelData();
        }
        else
        {
            var s = CurrentCollapsedSize();
            _lastAutoW = s.Width;
            _bodyW.To(s.Width); _bodyH.To(s.Height); _open.To(0);
            _neckStrength.To(1);
            StartPetCycle();   // 收起后恢复小形象轮换计时
        }
    }

    // ---------- 数据 ----------
    private void OnMediaUpdated(Services.MediaSnapshot m)
    {
        Dispatcher.BeginInvoke(() =>
        {
            islandCanvas.Media = m;
            expandedPanel.MediaView.Media = m;
            expandedPanel.MediaView.InvalidateVisual();
            if (!_expanded && islandCanvas.CompactView == "media")
            {
                ApplyAutoCollapsedWidth();
                islandCanvas.InvalidateVisual();
            }
        });
    }

    private void OnDevicesUpdated()
    {
        Dispatcher.BeginInvoke(() =>
        {
            expandedPanel.DevicesView.InvalidateVisual();
            expandedPanel.Monitor.InvalidateVisual();
            if (!_expanded)
            {
                ApplyAutoCollapsedWidth();
                islandCanvas.InvalidateVisual();
            }
        });
    }

    private void OnClipboardChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (expandedPanel.DataContext is PanelData pd)
                pd.UpdateClipboard(force: true);
            if (!_expanded && islandCanvas.CompactView is "dash" or "clipboard")
            {
                ApplyAutoCollapsedWidth();
                islandCanvas.InvalidateVisual();
            }
        });
    }

    private void OnMonitorSnapshot(MonitorSnapshot s)
    {
        // 采样在 System.Timers.Timer 的线程池线程触发：DependencyProperty.SetValue 有
        // 线程亲和检查，直接写 islandCanvas.Snapshot 会跨线程抛异常并被采样线程吞掉
        // （表现为监控永远只显示启动时一帧）。一切 UI 访问必须回到 UI 线程。
        Dispatcher.BeginInvoke(() =>
        {
            _snapshot = s;
            islandCanvas.Snapshot = s;

            // 走势历史（时间正序展开给画刷）
            double net = Math.Max(s.DownloadSpeed, s.UploadSpeed);
            _netMax = Math.Max(_netMax * 0.97, Math.Max(net, 1024)); // 滑动峰值，最小 1KB
            _cpuHist[_histIdx % _cpuHist.Length] = Easing.Clamp(s.CpuUsage / 100, 0, 1);
            _netHist[_histIdx % _netHist.Length] = Easing.Clamp(net / _netMax, 0, 1);
            _downHist[_histIdx % _downHist.Length] = Easing.Clamp(s.DownloadSpeed / _netMax, 0, 1);
            _upHist[_histIdx % _upHist.Length] = Easing.Clamp(s.UploadSpeed / _netMax, 0, 1);
            _histIdx++;
            var cpuSeq = new double[_cpuHist.Length];
            var netSeq = new double[_netHist.Length];
            var downSeq = new double[_downHist.Length];
            var upSeq = new double[_upHist.Length];
            for (int i = 0; i < _cpuHist.Length; i++)
            {
                cpuSeq[i] = _cpuHist[(_histIdx + i) % _cpuHist.Length];
                netSeq[i] = _netHist[(_histIdx + i) % _netHist.Length];
                downSeq[i] = _downHist[(_histIdx + i) % _downHist.Length];
                upSeq[i] = _upHist[(_histIdx + i) % _upHist.Length];
            }

            // 收起态 dash 频谱柱同步 CPU 历史
            islandCanvas.CpuHistory = cpuSeq;
            islandCanvas.CpuHistoryCount = Math.Min(_histIdx, _cpuHist.Length);

            // 展开页监控页（自绘，不走绑定）
            var mon = expandedPanel.Monitor;
            mon.Snapshot = s;
            mon.CpuHistory = cpuSeq;
            mon.NetHistory = netSeq;
            mon.DownHistory = downSeq;
            mon.UpHistory = upSeq;
            mon.Thermal = _thermal.Current;
            mon.InvalidateVisual();
            UpdateTempTooltip(_thermal.Current);
            ApplyAutoCollapsedWidth();   // 内容增减（显示项/电池/天气）→ 岛体宽度自动跟随
            islandCanvas.MicActive = _volume.MicInUse;   // 隐私点：麦克风使用中亮绿点

            CheckAlerts();

            if (expandedPanel.DataContext is PanelData pd)
            {
                pd.UpdateClipboard();   // 剪贴板（内部 2s 节流）
                pd.UpdateFiles();       // 文件中转
            }
            expandedPanel.DevicesView.InvalidateVisual();   // 自绘设备页：随帧重绘
        });
    }

    // ---------- 温度 tooltip（悬停收起岛体 / 监控页：分级配色 + 状态词 + 权限提示） ----------
    private string? _tempTipKey;

    private void UpdateTempTooltip(Services.ThermalSnapshot? t)
    {
        string F(double? v) => v is { } x ? $"{Math.Round(x)}" : "--";
        string key = $"{F(t?.CpuTemp)}|{F(t?.GpuTemp)}|{F(t?.SsdTemp)}|{F(t?.MotherboardTemp)}|{_tempNeedAdmin}";
        if (key == _tempTipKey) return;   // 读数没变不重建（1s 一跳，避免抖动）
        _tempTipKey = key;

        var tip = BuildTempTip(t);
        islandCanvas.ToolTip = tip;
        expandedPanel.Monitor.ToolTip = tip;
    }

    private UIElement BuildTempTip(Services.ThermalSnapshot? t)
    {
        var pal = _palette;
        (Color C, string Word) Grade(double? v) => v switch
        {
            null => (pal.TextTertiary, _tempNeedAdmin ? "需管理员" : "读不到"),
            <= 70 => (Color.FromRgb(0x60, 0xC0, 0x78), "正常"),
            <= 85 => (pal.Warning, "偏高"),
            _ => (pal.Up, "过热"),
        };

        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "温度监控", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(pal.Accent), Margin = new Thickness(0, 0, 0, 6),
        });

        void Row(string label, double? v, string? note = null)
        {
            var g = Grade(v);
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = label, Width = 52, FontSize = 11,
                Foreground = new SolidColorBrush(pal.TextDim),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = v is { } x ? $"{Math.Round(x)}°  {g.Word}" : note ?? g.Word,
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(v is null && _tempNeedAdmin ? pal.Warning : g.C),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
            panel.Children.Add(row);
        }

        Row("CPU", t?.CpuTemp);
        Row("GPU", t?.GpuTemp, t?.GpuTemp is null && !_tempNeedAdmin ? "核显无接口" : null);
        Row("SSD", t?.SsdTemp, "传感器已禁用");
        Row("主板", t?.MotherboardTemp);

        if (_tempNeedAdmin)
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "托盘 → 以管理员重启 可读取 CPU/GPU 温度",
                FontSize = 10, Foreground = new SolidColorBrush(pal.TextTertiary),
                Margin = new Thickness(0, 6, 0, 0),
            });

        return new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(pal.Surface),
            BorderBrush = new SolidColorBrush(pal.Track),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Child = panel,
        };
    }

    // ---------- 高温与低电量告警（BrimDeck compact-alert 同款思路：超阈值在收起态弹 4 秒浮条） ----------
    private DateTime _lastTempAlert = DateTime.MinValue;
    private DateTime _lastBatteryAlert = DateTime.MinValue;

    private void CheckAlerts()
    {
        if (_expanded) return;

        // 1. 低电量告警（未接电源放电且 ≤ 阈值时每 5 分钟提醒一次）
        if (_settings.BatteryAlertEnabled)
        {
            var bat = Services.DeviceService.Snapshot.FirstOrDefault(d => d.Kind == "battery");
            double? bp = bat?.Percent ?? _snapshot?.BatteryPercent;
            bool charging = (bat?.Charging ?? false) || (bat?.PluggedIn ?? false) || (_snapshot?.BatteryCharging ?? false);
            if (bp is { } bpVal && !charging && bpVal <= _settings.BatteryAlertThreshold
                && (DateTime.Now - _lastBatteryAlert).TotalMinutes >= 5)
            {
                _lastBatteryAlert = DateTime.Now;
                islandCanvas.Activity = new ActivityView
                {
                    Kind = "temp",
                    Label = "低电",
                    Value = Math.Clamp(bpVal / 100.0, 0.04, 1.0),
                    RightText = $"{Math.Round(bpVal)}%",
                    Until = DateTime.Now.AddSeconds(4),
                };
                islandCanvas.Pet.React("surprise");
                islandCanvas.InvalidateVisual();
                return;
            }
        }

        if (!_settings.TempAlertEnabled) return;
        var t = _thermal.Current;
        if (t is null) return;
        double th = _settings.TempAlertThreshold;
        double? hot = t.CpuTemp is { } c && c >= th ? c
                    : t.GpuTemp is { } g && g >= th ? g : null;
        if (hot is null) return;
        if ((DateTime.Now - _lastTempAlert).TotalSeconds < 60) return;   // 每分钟最多提醒一次
        _lastTempAlert = DateTime.Now;
        islandCanvas.Activity = new ActivityView
        {
            Kind = "temp",
            Label = "高温",
            Value = Math.Min(1, hot.Value / 110),
            RightText = $"{Math.Round(hot.Value)}°",
            Until = DateTime.Now.AddSeconds(4),
        };
        islandCanvas.Pet.React("surprise");
        islandCanvas.InvalidateVisual();
    }

    /// <summary>温度/风扇更新（服务在后台线程触发）：回 UI 线程刷新自绘控件。</summary>
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        _monitor.NotifySystemResume();
        _thermal.NotifySystemResume();
        Dispatcher.BeginInvoke(() =>
        {
            _monitorDip = null;
            PositionOnEdge();
        });
    }

    private void OnThermalUpdated(Services.ThermalSnapshot t)
    {
        Dispatcher.BeginInvoke(() =>
        {
            islandCanvas.Thermal = t;
            expandedPanel.Monitor.Thermal = t;
            expandedPanel.Monitor.InvalidateVisual();
            if (!_expanded && islandCanvas.CompactView is "combo" or "dash")
            {
                ApplyAutoCollapsedWidth();
                islandCanvas.InvalidateVisual();
            }
        });
    }

    private void OnVolumeChanged(float vol, bool muted)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // 已有音量活动则原地续期（连续调音量不重新入场，参考 wisland）
            if (islandCanvas.Activity is { } act && act.Kind == "volume" && DateTime.Now < act.Until)
            {
                act.Value = vol; act.Muted = muted;
                act.Until = DateTime.Now.AddSeconds(1.8);
            }
            else
            {
                islandCanvas.Activity = new ActivityView { Value = vol, Muted = muted };
            }
            islandCanvas.InvalidateVisual();
        });
    }

    // ---------- 展开面板数据（后台构建，UI 线程赋值） ----------
    private PanelData? _panelData;
    private bool _panelBuilding;
    private bool _panelFailed;
    private DateTime _panelLastTry = DateTime.MinValue;
    private int _panelGen;

    /// <summary>
    /// 构建/刷新展开面板数据。
    /// 构建放后台线程：PanelData.UpdateControls 会同步起 netsh 子进程（本机实测 126–238ms），
    /// 在 UI 线程做会把展开动画首帧卡住。
    /// 完成后回 UI 线程赋值 DataContext（跨线程改 DataContext 会抛线程亲和异常）。
    /// 每次展开都走这里 → 面板读数不会停留在上一轮的旧值。
    /// </summary>
    private void RequestPanelData()
    {
        if (_panelBuilding) return;   // 已有构建在途：它就是要用的那份
        // 构建失败后别每帧重试（Frame_Tick 在 DataContext 为空时会反复调这里）
        if (_panelFailed && (DateTime.Now - _panelLastTry).TotalSeconds < 5) return;
        _panelLastTry = DateTime.Now;
        _panelBuilding = true;
        int gen = ++_panelGen;
        Task.Run(() =>
        {
            var pd = BuildPanelData();
            // 剪贴板/文件：实例刚建好、还没绑到界面上，这时在后台填好，展开时不会是空列表
            pd.UpdateClipboard();
            pd.UpdateFiles();
            return pd;
        }).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            _panelBuilding = false;
            if (gen != _panelGen) return;
            if (t.Status != TaskStatus.RanToCompletion)
            {
                _panelFailed = true;   // 失败：按上面的节流重试
                return;
            }
            _panelFailed = false;
            var pd = t.Result;
            // 只有新实例**没拿到数据**时才沿用上一轮的集合（防止换 DataContext 时列表闪空）。
            // ⚠️ 不能无条件覆盖：pd 的集合是后台刚取好的新数据，
            //    无条件换成 old 的集合会把新数据整个丢掉 —— 剪贴板/文件中转在首次展开后再也不更新。
            if (_panelData is { } old)
            {
                if (pd.Clipboard.Count == 0 && old.Clipboard.Count > 0)
                {
                    pd.Clipboard = old.Clipboard;
                    pd.HasClipboard = old.HasClipboard;
                }
                if (pd.Files.Count == 0 && old.Files.Count > 0)
                {
                    pd.Files = old.Files;
                    pd.HasFiles = old.HasFiles;
                }
            }
            _panelData = pd;
            expandedPanel.DataContext = pd;
        }));
    }

    private PanelData BuildPanelData()
    {
        var pd = new PanelData();
        // 控制页命令绑定（亮度步进 10%；执行后立即回读刷新显示）
        pd.BindControls(
            new RelayCommand(_ => Task.Run(() =>
            {
                Services.ControlService.SetBrightness(Math.Min(100, (Services.ControlService.GetBrightness() ?? 50) + 10));
                pd.UpdateControls();
            })),
            new RelayCommand(_ => Task.Run(() =>
            {
                Services.ControlService.SetBrightness(Math.Max(0, (Services.ControlService.GetBrightness() ?? 50) - 10));
                pd.UpdateControls();
            })),
            new RelayCommand(_ => Task.Run(() =>
            {
                var enabled = Services.ControlService.WlanAdapterEnabled;
                if (enabled is null)
                {
                    Dispatcher.BeginInvoke(() => System.Windows.MessageBox.Show("Wi-Fi 状态读取失败，未做改动。", "MyIsland"));
                    return;
                }
                var (wlanOk, wlanMsg) = Services.ControlService.SetWlan(!enabled.Value);
                if (!wlanOk) Dispatcher.BeginInvoke(() => System.Windows.MessageBox.Show(wlanMsg, "MyIsland"));
                pd.UpdateControls();
            })),
            new RelayCommand(_ => Task.Run(() =>
            {
                var (btOk, btMsg) = Services.ControlService.SetBluetooth(!Services.ControlService.BluetoothEnabled);
                if (!btOk) Dispatcher.BeginInvoke(() => System.Windows.MessageBox.Show(btMsg, "MyIsland"));
                pd.UpdateControls();
            })),
            new RelayCommand(_ => Services.ControlService.OpenBluetoothSettings()),
            new RelayCommand(_ => Services.ControlService.Lock()),
            new RelayCommand(_ => Services.ControlService.Sleep()),
            new RelayCommand(p => Services.ControlService.OpenSettings(p as string ?? "")));
        // 音量 / 深色模式回调与初值
        pd.OnSetVolume = v => _volume.SetVolume((float)v);
        pd.OnToggleMute = () => _volume.SetMute(!_volume.IsMutedNow);
        pd.OnToggleMicMute = () =>
        {
            _volume.ToggleMicMute();
            pd.MicMuted = _volume.MicMuted ?? false;
        };
        pd.MicMuted = _volume.MicMuted ?? false;
        pd.OnSetLightTheme = light => Services.ControlService.SetSystemLightTheme(light);
        pd.OnSetNightLight = on =>
        {
            // 本机从未配置过夜灯（无 Data 值）：带去系统设置先开启一次
            if (!Services.ControlService.SetNightLight(on))
                Services.ControlService.OpenSettings("nightlight");
        };
        // 面板里的开关触发的穿透设置：面板自己会在用户点开关后收起（见 ExpandedPanel.OnClickThroughToggle），
        // 这里不能重复收（那就是"一开就自我收起"）。用标志区分来源。
        pd.OnSetClickThrough = on =>
        {
            _clickThroughFromPanel = true;
            try { SetClickThrough(on); }
            finally { _clickThroughFromPanel = false; }
        };
        if (_volume.Current is { } vc) { pd.VolumeValue = vc.vol; pd.IsMuted = vc.mute; }
        pd.IsLightTheme = Services.ControlService.IsSystemLightTheme();
        pd.ClickThrough = _settings.ClickThrough;
        if (Services.ControlService.GetNightLightOn() is { } nl)
        {
            pd.HasNightLight = true;
            pd.NightLightOn = nl;
        }
        else
        {
            // 从未配置过夜灯也显示卡片（点了会带去系统设置开启）
            pd.HasNightLight = true;
        }
        pd.UpdateControls();
        return pd;
    }

    /// <summary>极简命令。</summary>
    private sealed class RelayCommand(Action<object?> execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }   // ICommand 要求，本实现状态不变
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
    }

    // ---------- 小形象 ⇄ 信息胶囊 自动轮换（参考 BrimDeck 刘海轮播） ----------
    // pet 10s → combo（配额环+时间+系统读数）30s → pet，循环；切到其他视图即停。
    private const int PetPhaseSec = 10, ComboPhaseSec = 30;

    private void StartPetCycle()
    {
        _cycleTimer.Stop();
        if (!_settings.PetAutoCycle) return;
        if (islandCanvas.CompactView is not ("pet" or "combo")) return;
        _cycleTimer.Interval = TimeSpan.FromSeconds(
            islandCanvas.CompactView == "pet" ? PetPhaseSec : ComboPhaseSec);
        _cycleTimer.Start();
    }

    private void CycleCompact()
    {
        if (_expanded) { StartPetCycle(); return; }   // 展开中不切，收起后重新计时
        if (islandCanvas.CompactView is not ("pet" or "combo")) { _cycleTimer.Stop(); return; }
        islandCanvas.CompactView = islandCanvas.CompactView == "pet" ? "combo" : "pet";
        var s = CurrentCollapsedSize();
        _bodyW.To(s.Width); _bodyH.To(s.Height);
        StartPetCycle();
    }

    private (double Width, double Height) CollapsedSize(string view) => view switch
    {
        "clock" => (600, 46),   // 时间胶囊：大号时间+秒 │ 日期·星期 │ 矢量天气+温度+城市（对齐 macOS 版）
        "combo" => (1080, _settings.DecorNotch && _settings.Edge == "top" ? 42 : 36),   // 信息胶囊：对齐 Mac 实机 36px 悬浮圆角药丸胶囊
        "media" => (420, 44),   // 媒体律动胶囊：圆角专辑封面 + 歌名歌手 + 封面主色实时音频律动柱
        "monitor" => (520, 44), // 单排高密度读数带
        "dash" => (1020, 46),   // 单排仪表盘：频谱柱 │ 剪贴板 │ 温度 │ 占用环 │ 电池 │ 网速（对齐 macOS 版）
        "window" => (380, 44),  // 活动窗口
        "devices" => (360, 46),
        "clipboard" => (360, 44),
        _ => (110, 44),
    };

    private void UpdateFullscreenHidden()
    {
        // 长期显示：全屏/最大化都不隐藏不缩条，优先级最高
        if (_settings.AlwaysVisible)
        {
            Visibility = Visibility.Visible;
            SetIndicator(false);
            InjectActiveWindow();
            return;
        }
        var ctx = ForegroundDetector.Detect();
        if (_settings.HideFullscreen && ctx.IsDistracting)
        {
            Visibility = Visibility.Hidden;
            SetIndicator(false);
            return;
        }
        Visibility = Visibility.Visible;
        // 前台窗口最大化时缩成指示条（BrimDeck 同款）；展开中不缩
        SetIndicator(_settings.IndicatorOnMaximize && !_expanded && ctx.Kind == ForegroundKind.Maximized);
        islandCanvas.ActiveWindow = (ctx.ProcessName, ctx.Title);
        // ActiveWindow 是普通 CLR 属性（不是 DP），不会自动触发重绘 → 这里显式重画一次，
        // 否则活动窗口视图的标题最多滞后 3 秒（重绘只发生在忙时或每 3s 一次）
        islandCanvas.InvalidateVisual();
    }

    /// <summary>长期显示模式下也要注入前台窗口（UpdateFullscreenHidden 提前 return 的分支）。</summary>
    private void InjectActiveWindow()
    {
        var ctx = ForegroundDetector.Detect();
        islandCanvas.ActiveWindow = (ctx.ProcessName, ctx.Title);
        islandCanvas.InvalidateVisual();
    }

    // ---------- 指示条模式（最大化时：本体缩成 44×6 小条，只留呼吸底） ----------
    private bool _indicatorMode;

    private void SetIndicator(bool on)
    {
        if (_indicatorMode == on) return;
        _indicatorMode = on;
        islandCanvas.IsIndicator = on;
        var s = CurrentCollapsedSize();
        _bodyW.To(s.Width); _bodyH.To(s.Height);
        islandCanvas.InvalidateVisual();
    }

    /// <summary>当前收起态尺寸（指示条模式下固定 44×6；clock/combo/dash 直接由版式精确测宽，其余视图按渲染回报调节）。</summary>
    private (double Width, double Height) CurrentCollapsedSize()
    {
        if (_indicatorMode) return (44, 6);
        var view = islandCanvas.CompactView;
        var fix = CollapsedSize(view);
        double direct = islandCanvas.MeasureCompactWidth(view);
        double w = direct > 0
            ? Math.Clamp(direct, 110, fix.Width)
            : islandCanvas.HasMeasuredCompact
                ? Math.Clamp(islandCanvas.CompactContentWidth, 110, fix.Width)
                : fix.Width;
        return (w, fix.Height);
    }

    /// <summary>内容宽度变化时重新设定岛体目标宽度（容差 1.5px 防微抖，弹簧动画平滑过渡）。</summary>
    private double _lastAutoW = -1;
    private void ApplyAutoCollapsedWidth()
    {
        if (_expanded || _indicatorMode) return;
        var s = CurrentCollapsedSize();
        if (Math.Abs(s.Width - _lastAutoW) > 1.5)
        {
            _lastAutoW = s.Width;
            double growW = _hoverGrow && islandCanvas.CompactView != "pet" ? HoverGrowW : 0;
            double growH = _hoverGrow && islandCanvas.CompactView != "pet" ? HoverGrowH : 0;
            _bodyW.To(s.Width + growW);
            _bodyH.To(s.Height + growH);
        }
    }

    private void ApplyTheme()
    {
        _islandSwitchTimer.Stop();
        if (_settings.Theme == "Auto")
        {
            // Auto 主题要读注册表（系统深浅）+ 解码壁纸位图（实测 30–150ms）——
            // 放后台线程算，UI 线程只负责应用结果（原来在 UI 线程同步做，启动和每次
            // WM_SETTINGCHANGE 都会卡一下）。
            int gen = ++_autoThemeGen;
            Task.Run(AutoPalette).ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (gen != _autoThemeGen) return;   // 已有更新的请求：丢弃过期结果
                    ApplyPalette(t.Result);
                });
            });
            return;
        }
        if (_settings.Theme is "IslandAuto" or "Island")
        {
            ApplyPalette(Palette.IslandAt(DateTime.Now));
            var next = Palette.NextIslandSwitch(DateTime.Now);
            double sec = Math.Clamp((next - DateTime.Now).TotalSeconds, 1, 600);
            _islandSwitchTimer.Interval = TimeSpan.FromSeconds(sec);
            _islandSwitchTimer.Start();
            return;
        }
        ApplyPalette(_settings.Theme switch
        {
            "Light" => Palette.Light,
            "IslandDay" => Palette.IslandDay,
            "IslandNight" => Palette.IslandNight,
            _ => Themes.Palette.ByName(_settings.Theme) ?? Palette.Dark,
        });
    }

    /// <summary>Auto 主题的异步代次：只有最后一次请求的结果会被应用。</summary>
    private int _autoThemeGen;

    /// <summary>应用配色（必须在 UI 线程调用）。</summary>
    private void ApplyPalette(Palette pal)
    {
        _palette = pal;
        if (IsLoaded)
        {
            islandCanvas.Palette = _palette;
            expandedPanel.Monitor.Palette = _palette;
            expandedPanel.MediaView.Palette = _palette;
            expandedPanel.DevicesView.Palette = _palette;
            islandCanvas.InvalidateVisual();
            expandedPanel.Monitor.InvalidateVisual();
            expandedPanel.MediaView.InvalidateVisual();
            expandedPanel.DevicesView.InvalidateVisual();
        }
        // 展开面板静态资源同步（CardBg/FgDim 等随主题变）
        UpdatePanelResources();
    }

    /// <summary>把当前主题刷进展开面板的 StaticResource（XAML 用 StaticResource 故需手动同步）。</summary>
    private void UpdatePanelResources()
    {
        if (!IsLoaded || _palette is null) return;
        // 注意：面板 XAML 的引用已改为 DynamicResource，写面板自己的资源字典才会实时生效
        var res = expandedPanel.Resources;
        res["PanelBg"] = new SolidColorBrush(_palette.Surface) { Opacity = 0.96 };
        res["CardBg"] = new SolidColorBrush(_palette.SurfaceAlt);
        res["Fg"] = new SolidColorBrush(_palette.Text);
        res["FgDim"] = new SolidColorBrush(_palette.TextDim);
        res["FgTertiary"] = new SolidColorBrush(_palette.TextTertiary);
        res["Accent"] = new SolidColorBrush(_palette.Accent);
        res["Track"] = new SolidColorBrush(_palette.Track);
        res["Down"] = new SolidColorBrush(_palette.Down);
        expandedPanel.SelectPage(expandedPanel.CurrentPageIndex);
    }

    /// <summary>Auto 主题：系统深浅 + 壁纸主色作为 Accent（macOS 壁纸取色）。</summary>
    private static Palette AutoPalette()
    {
        var basePal = IsSystemLight() ? Palette.Light : Palette.Dark;
        var c = RenderKit.WallpaperAccent(basePal.IsDark);
        return c is { } ac ? basePal with { Accent = ac } : basePal;
    }

    private static bool IsSystemLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    // ---------- 定位 ----------
    private const double MaxCollapsedW = 1040;

    /// <summary>窗口矩形覆盖最大胶囊宽（880 DIP）+ 展开面板（560×420）+ 阴影边距，四边停靠均不裁切圆角。</summary>
    private void PositionOnEdge()
    {
        const double margin = 40;
        double maxW = Math.Max(ExpandW, MaxCollapsedW) + margin * 2;
        double maxH = ExpandH + margin * 2;
        Width = maxW; Height = maxH;

        var expandRect = ComputeBodyRect(ExpandW, ExpandH);
        GetScreenSizeDip(out double sw, out _, out double ox, out _);
        double cy = expandRect.Y + expandRect.Height / 2;
        Top = cy - maxH / 2;
        Left = _settings.Edge switch
        {
            "left" => ox - margin,
            "right" => ox + sw + margin - maxW,
            _ => expandRect.X + expandRect.Width / 2 - maxW / 2,
        };

        expandedPanel.HorizontalAlignment = HorizontalAlignment.Left;
        expandedPanel.VerticalAlignment = VerticalAlignment.Top;
        expandedPanel.Margin = new Thickness(
            expandRect.X - Left + (ExpandW - expandedPanel.Width) / 2,
            expandRect.Y - Top + (ExpandH - expandedPanel.Height) / 2,
            0, 0);
    }

    // ---------- 鼠标 ----------
    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _lockedClosed = !_lockedClosed;
            if (_lockedClosed) SetExpanded(false);
            return;
        }
        _dragStart = CursorDip();
        _squash.To(0.82, 300, 0.6);
        // 捕获失败就别进拖拽态：否则永远等不到 MouseLeftButtonUp，岛体会一直压着不回弹
        if (!CaptureMouse()) return;
        _dragging = true;
    }

    /// <summary>
    /// 鼠标捕获被系统夺走（Esc / Alt-Tab / 弹右键菜单）：此时不会补发 MouseLeftButtonUp，
    /// _dragging 复位不了 → 岛体永久保持压扁 18%。这里补复位。
    /// </summary>
    private void OnLostMouseCapture(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _squash.To(1, 240, 0.6);
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            _squash.To(1, 240, 0.6);
            ReleaseMouseCapture();
        }
        if (!_expanded) islandCanvas.Pet.React("surprise");
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        // 拖拽距离 -> 液桥拉伸 -> 超阈值断开残留水滴（简化版液态交互）
        var pos = CursorDip();
        double dist = Math.Abs(pos.X - _dragStart.X) + Math.Abs(pos.Y - _dragStart.Y);
        _neckStretch.To(Easing.Clamp(dist / 80.0, 0, 1), 200, 0.9);
        if (_neckStretch.Value > 0.95 && _drip.Value <= 0.01)
        {
            _drip.Snap(9);
            _drip.To(0, 180, 0.7);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _expanded)
        {
            SetExpanded(false);
            e.Handled = true;
        }
    }

    private void OnFilesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            Services.FileTrayService.Add(files);
            if (expandedPanel.DataContext is PanelData pd) pd.ForceRefreshShelf();
            _squash.Snap(0.88); _squash.To(1, 260, 0.7);
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_expanded) return;
        StepCompactView(e.Delta > 0 ? -1 : 1);
    }

    private void OnMouseMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _expanded) return;
        StepCompactView(1);
        e.Handled = true;
    }

    private void StepCompactView(int delta)
    {
        string[] views = { "combo", "clock", "media", "dash", "pet", "monitor", "devices", "clipboard", "window" };
        int i = Array.IndexOf(views, islandCanvas.CompactView);
        if (i < 0) i = 0;
        i = (i + delta + views.Length) % views.Length;
        _userCompactView = views[i];
        _settings.CompactView = _userCompactView;
        _settings.CompactViewPinned = !_settings.AutoCycleViews;
        _settings.RequestSave();
        islandCanvas.ResetCompactMeasure();
        islandCanvas.CompactView = views[i];
        var s = CurrentCollapsedSize();
        _lastAutoW = s.Width;
        _bodyW.To(s.Width); _bodyH.To(s.Height);
        _squash.Snap(0.9); _squash.To(1, 260, 0.7);
        StartPetCycle();
    }

    private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        if (ContextMenu is not null) ContextMenu.IsOpen = true;
    }

    // ---------- 供 App/托盘调用 ----------
    public void ToggleExpand()
    {
        bool willExpand = !_expanded;
        if (willExpand)
        {
            _lockedClosed = false;
            SetExpanded(true);
            _keepOpenUntilHover = true;
        }
        else
        {
            _keepOpenUntilHover = false;
            SetExpanded(false);
        }
    }
    public void ApplyThemeExternal() => ApplyTheme();
    public void RepositionExternal() => PositionOnEdge();
    public void ApplyItemFiltersExternal() => ApplyItemFilters();
    public string GetHardwareSummary() => _thermal.GetHardwareSummary();
    public void RefreshShelfExternal()
    {
        if (expandedPanel.DataContext is PanelData pd) pd.ForceRefreshShelf();
        islandCanvas.InvalidateVisual();
    }
    public void SetPetAutoCycle(bool on)
    {
        _settings.PetAutoCycle = on;
        if (!on && islandCanvas.CompactView != _userCompactView)
        {
            islandCanvas.ResetCompactMeasure();
            islandCanvas.CompactView = _userCompactView;
            if (!_expanded)
            {
                var s = CurrentCollapsedSize();
                _bodyW.To(s.Width); _bodyH.To(s.Height);
            }
        }
        StartPetCycle();
    }

    /// <summary>切换天气城市（菜单入口）：作废缓存立即重拉。</summary>
    public void SetWeatherCity(string city)
    {
        _settings.City = city;
        _settings.Save();
        _weather.Settings = _settings;
        _weather.Invalidate();
        islandCanvas.Weather = _weather.Get();
    }

    // combo 信息胶囊的默认显示项：时间 + 读数表盘(C/M/G 环) + CPU/GPU 温度 + 硬盘占用 + 网速。
    // 环组已经画了 CPU/内存/GPU 的占用率，所以 cpu/gpu/mem 三个纯数字项默认不勾——需要"数字不要环"时再单独开。
    internal static readonly string[] ComboDefaultItems =
        ["clock", "dials", "disk", "cputemp", "gputemp", "ssdtemp", "battery", "net"];

    /// <summary>
    /// 解析 combo 显示项。
    /// 空 = 默认组合；旧版本遗留值里带 "rings"（从来不是有效键的老写法）也按默认组合处理，
    /// 这样升级后用户立刻能看到新增的温度/硬盘读数，而不是"设置没变所以什么都没加"。
    /// </summary>
    private static HashSet<string> ParseComboItems(string? items)
    {
        if (string.IsNullOrWhiteSpace(items)) return new(ComboDefaultItems);
        // "none" = 一个都不显示（哨兵，与 ParseItems 一致）
        if (items.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return new HashSet<string>();
        var set = new HashSet<string>(items.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0));
        if (set.Count == 0) return new(ComboDefaultItems);
        if (set.Contains("rings")) return new(ComboDefaultItems);
        return set;
    }

    /// <summary>把 Settings.DashItems 注入自绘控件（空 = 全部显示）。</summary>
    private void ApplyDashItems()
    {
        islandCanvas.DashItems = ParseItems(_settings.DashItems);
        islandCanvas.InvalidateVisual();
    }

    /// <summary>解析显示项集合：空 = 全部；"none" = 一个都不显示。</summary>
    /// <remarks>
    /// "none" 是哨兵：用户把显示项**全部取消勾选**时必须能表达"什么都不显示"。
    /// 以前"取消最后一个"会存成空串，而空串被约定为"全部" ⇒ 取消全部反而变成全部显示（已修）。
    /// </remarks>
    private static HashSet<string>? ParseItems(string? items)
    {
        if (string.IsNullOrWhiteSpace(items)) return null;          // 空 = 全部
        if (items.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            return new HashSet<string>();                          // 一个都不显示
        return new HashSet<string>(items.Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>三处显示项过滤统一注入（dash / combo / 监控页）。</summary>
    private void ApplyItemFilters()
    {
        ApplyDashItems();
        // 旧值里带 "rings"（从来不是有效键）→ 升到新默认组合，并把这个结果落盘（只做一次）
        if (_settings.ComboItems.Contains("rings"))
        {
            _settings.ComboItems = string.Join(',', ComboDefaultItems);
            _settings.Save();
        }
        islandCanvas.ComboItems = ParseComboItems(_settings.ComboItems);
        expandedPanel.Monitor.Items = ParseItems(_settings.MonitorItems);
        islandCanvas.ResetCompactMeasure();
        _lastAutoW = -1;
        islandCanvas.InvalidateVisual();
        expandedPanel.Monitor.InvalidateVisual();
    }

    /// <summary>打开 dash 显示项设置（勾选 + 实时预览）。</summary>
    public void ShowDashSettings()
    {
        var preview = new IslandCanvas
        {
            CompactView = "dash", Palette = _palette, IsHitTestVisible = false,
            Width = 660, Height = 69,
            BodyRect = new Rect(0, 0, 660, 69),
            NeckStrength = 0,
        };
        preview.LayoutTransform = new ScaleTransform(0.78, 0.78);
        OpenModuleSettings(
            "仪表盘显示项",
            new ModuleSettingsWindow.Module[]
            {
                new("cores", "核心柱"), new("cputemp", "CPU 温度"), new("gputemp", "GPU 温度"),
                new("mem", "内存"), new("gpu", "GPU 使用率"), new("disk", "硬盘活动"),
                new("ssd", "SSD 温度"), new("battery", "电池"), new("net", "网速 ↓↑"),
            },
            preview,
            () => _settings.DashItems,
            v => { _settings.DashItems = v; _settings.Save(); },
            h => preview.DashItems = h);
    }

    /// <summary>打开 combo 信息胶囊显示项设置。</summary>
    public void ShowComboSettings()
    {
        var preview = new IslandCanvas
        {
            CompactView = "combo", Palette = _palette, IsHitTestVisible = false,
            Width = 820, Height = 44,
            BodyRect = new Rect(0, 0, 820, 44),
            NeckStrength = 0,
        };
        // 显示项变多，预览按比例缩一点才塞得进设置窗口
        preview.LayoutTransform = new ScaleTransform(0.62, 0.62);
        OpenModuleSettings(
            "信息胶囊显示项",
            new ModuleSettingsWindow.Module[]
            {
                new("clock", "时间"), new("dials", "资源占用(C/M/G)"), new("disk", "硬盘占用"),
                new("cputemp", "CPU 温度"), new("gputemp", "GPU 温度"), new("ssdtemp", "硬盘温度"),
                new("battery", "电池状态"), new("net", "网速"),
            },
            preview,
            () => _settings.ComboItems,
            v => { _settings.ComboItems = v; _settings.Save(); },
            h => preview.ComboItems = h);
    }

    /// <summary>打开监控页显示项设置。</summary>
    public void ShowMonitorSettings()
    {
        var preview = new MonitorPage { Palette = _palette, Width = 500, Height = 320 };
        preview.LayoutTransform = new ScaleTransform(0.9, 0.9);
        OpenModuleSettings(
            "监控页显示项",
            new ModuleSettingsWindow.Module[]
            {
                new("cpu", "CPU 行"), new("cores", "核心柱"), new("mem", "内存行"),
                new("gpu", "GPU 行"), new("disk", "硬盘行"), new("net", "网速行"),
                new("power", "功耗/主板行"), new("topproc", "进程榜"),
            },
            preview,
            () => _settings.MonitorItems,
            v => { _settings.MonitorItems = v; _settings.Save(); },
            h => preview.Items = h);
    }

    private void OpenModuleSettings(
        string title, ModuleSettingsWindow.Module[] modules, FrameworkElement preview,
        Func<string?> getItems, Action<string> saveItems, Action<HashSet<string>?> applyFilter)
    {
        var win = new ModuleSettingsWindow(
            title, modules, _palette, preview,
            () => (_snapshot, _thermal.Current),
            getItems, saveItems, applyFilter, ApplyItemFilters);
        win.ShowDialog();
    }

    public string CurrentCompactView => _userCompactView;

    /// <summary>切换收起态刘海融合造型（对齐 macOS 版收起态装饰）。</summary>
    public void SetDecorNotch(bool on)
    {
        _settings.DecorNotch = on;
        _settings.RequestSave();
        islandCanvas.DecorNotch = on;
        if (!_expanded)
        {
            var s = CurrentCollapsedSize();
            _lastAutoW = s.Width;
            _bodyW.To(s.Width); _bodyH.To(s.Height);
        }
        islandCanvas.InvalidateVisual();
    }

    /// <summary>切换环内字母标识 C/M/G/D 与矢量图标（对齐 macOS 版收起态装饰）。</summary>
    public void SetDecorLetters(bool on)
    {
        _settings.DecorLetters = on;
        _settings.RequestSave();
        islandCanvas.DecorLetters = on;
        islandCanvas.InvalidateVisual();
    }

    /// <summary>切换信息胶囊数字主角样式与经典同级样式（对齐 macOS 版胶囊样式）。</summary>
    public void SetComboNumericHero(bool on)
    {
        _settings.ComboNumericHero = on;
        _settings.RequestSave();
        islandCanvas.ComboNumericHero = on;
        if (!_expanded)
        {
            var s = CurrentCollapsedSize();
            _lastAutoW = s.Width;
            _bodyW.To(s.Width); _bodyH.To(s.Height);
        }
        islandCanvas.InvalidateVisual();
    }

    /// <summary>切收起视图（菜单入口），带挤压回弹反馈。</summary>
    public void SetCompactView(string view)
    {
        _userCompactView = view;
        islandCanvas.ResetCompactMeasure();
        islandCanvas.CompactView = view;
        _settings.CompactView = view;
        _settings.CompactViewPinned = !_settings.PetAutoCycle;
        _settings.RequestSave();
        if (!_expanded)
        {
            var s = CurrentCollapsedSize();
            _lastAutoW = s.Width;
            _bodyW.To(s.Width); _bodyH.To(s.Height);
            _squash.Snap(0.9); _squash.To(1, 260, 0.7);
        }
        StartPetCycle();
    }

    /// <summary>切小形象款式（菜单入口）：classic / mono / cat / robot。</summary>
    public void SetPetStyle(string style)
    {
        _settings.PetStyle = style;
        _settings.Save();
        islandCanvas.Pet.Style = style;
        islandCanvas.Pet.React("surprise");   // 换款式给个小反应作反馈
        islandCanvas.InvalidateVisual();
    }

    /// <summary>穿透设置来自面板内开关（那种情况由面板自己负责收起）。</summary>
    private bool _clickThroughFromPanel;

    /// <summary>
    /// 鼠标穿透模式：WS_EX_TRANSPARENT 动态切换。
    /// 面板里的开关回写也会走到这里，所以这里不能一开就收起面板 ——
    /// 那会让"穿透模式"开出来就没法用（鼠标已穿透，用户也点不到开关）。
    /// 面板内的收起由用户点开关触发（见 ExpandedPanel.OnClickThroughToggle）；
    /// 设置中心等外部入口开穿透时，这里代收一次（鼠标已穿透，面板留着也没法交互）。
    /// </summary>
    public void SetClickThrough(bool on)
    {
        _settings.ClickThrough = on;
        _settings.Save();
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            IntPtr ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            long style = ex.ToInt64();
            style = on ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
        }
        if (on && !_clickThroughFromPanel) SetExpanded(false);
    }

    // ---------- 专注模式（L4 三击同款）：三击岛体 → 半透明 + 穿透；再三击恢复 ----------

    private bool _focusFade;
    private readonly System.Collections.Generic.List<DateTime> _clicks = new();

    /// <summary>三击检测（700ms 内三击切换专注模式）。由 PreviewMouseLeftButtonDown 调用。</summary>
    private void TrackTripleClick()
    {
        if (_expanded) return;
        var now = DateTime.Now;
        _clicks.RemoveAll(t => (now - t).TotalMilliseconds > 700);
        _clicks.Add(now);
        if (_clicks.Count >= 3)
        {
            _clicks.Clear();
            // 专注模式只淡出半透明（不穿透明——否则无法再点回来；穿透用设置里的开关）
            _focusFade = !_focusFade;
            Opacity = _focusFade ? 0.35 : 1.0;
        }
    }

    /// <summary>当前本体屏幕矩形（DIP），供毛玻璃等模块定位截屏区域。</summary>
    public Rect BodyScreenRect => _bodyRect;
    public double MonitorDpiScale => DpiScale;
}
