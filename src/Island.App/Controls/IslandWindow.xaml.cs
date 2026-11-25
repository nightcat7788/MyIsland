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
    private readonly GlassBackdrop _glass = new();
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
    private int _frameTick;
    // 上一拍临时活动浮条（音量/高温告警）是否还在显示：过期的那一拍要补画一帧擦掉它（见 Frame_Tick）
    private bool _activityShown;
    // 上一次画到画布上的分钟数（clock / combo 视图的时钟跨分钟时要补画一帧，见 Frame_Tick）
    private int _minuteShown = -1;
    private Point _dragStart;
    private DateTime _pointerEnterSince = DateTime.MinValue;
    private DateTime _pointerLeftSince = DateTime.MinValue;
    private bool _lockedClosed;  // 双击"长关"：不再自动展开
    // 二次启动唤起后保持展开，直到鼠标真的移进岛体（否则悬停收起逻辑会把它马上关掉）
    private bool _keepOpenUntilHover;
    private Palette _palette = Palette.Dark;
    private MonitorSnapshot? _snapshot;

    // 本体的屏幕矩形（DIP），每帧由弹簧值计算
    private Rect _bodyRect;

    // 展开面板（懒加载）：见 EnsurePanel / ReleasePanel 的说明
    private ExpandedPanel? _panel;

    // 收起动画结束后把分层窗口缩回胶囊大小（展开期间保持大窗口，避免逐帧 resize 闪帧）
    private bool _pendingWindowShrink;

    /// <summary>
    /// 取展开面板（不存在则返回 null，不触发创建）。
    /// </summary>
    private ExpandedPanel? Panel => _panel;

    /// <summary>岛体窗口的宿主事件（App 订阅）：面板顶栏的设置齿轮被点（2026-11-25 UI 对齐 BrimDeck）。</summary>
    public event Action? SettingsRequested;

    /// <summary>
    /// 确保展开面板存在（不存在就创建并挂上）。
    ///
    /// 为什么懒加载：面板在 XAML 里声明就会**启动即实例化** —— 540×380 的可视树、
    /// 主题资源字典、中文字体（雅黑 UI 约 20MB 级）全部常驻，而常驻态只有一个小胶囊。
    /// 创建时机放在展开动画**开始时**：面板还没显示（_open 从 0 淡入），
    /// XAML 解析 + 布局的耗时被动画覆盖，用户看不到卡顿。
    /// </summary>
    private ExpandedPanel EnsurePanel()
    {
        if (_panel is not null) return _panel;
        var p = new ExpandedPanel
        {
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 540,
            Height = 380,
            Margin = new Thickness(0)
        };
        p.CloseRequested += () => SetExpanded(false);
        p.SettingsRequested += () => SettingsRequested?.Invoke();
        _panel = p;
        Root.Children.Add(p);
        p.Monitor.Palette = _palette;
        p.DevicesView.Palette = _palette;
        // 面板是懒加载的：启动时 App 设下的两个温度状态词要在这里补交给监控页。
        // 只靠 MonitorTempNeedAdmin / MonitorTempNoDriver 的 setter 同步不到它 —— 设的那一刻面板还不存在
        //（2026-11-24 发现：此前监控页从来没显示过"需管理员"，一直是"—"）。
        p.Monitor.TempNeedAdmin = _tempNeedAdmin;
        p.Monitor.TempNoDriver = _tempNoDriver;
        UpdatePanelResources();
        RequestPanelData();
        return p;
    }

    /// <summary>
    /// 收起动画结束后释放展开面板，把可视树与控件还回内存。
    /// （中文字体是进程级的 DirectWrite 缓存，释放面板不会卸载它 —— 这部分是首启省下的，
    ///   展开过一次之后就回不来了，属于已知取舍。）
    /// </summary>
    private void ReleasePanel()
    {
        if (_panel is null) return;
        Root.Children.Remove(_panel);
        _panel = null;
        // 面板没了，只有它才用得上的明细采样（进程榜等）也停（2026-11-24 空闲降耗；展开时在 SetExpanded 里开）
        _monitor.SetDetailWanted(false);
        // 面板那棵可视树已经没有持有者了：申请一次"用完即还"（不阻塞；确认 UI 空闲后才回收，见 IsUiIdle）
        Services.MemoryReclaim.Request();
    }

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

        // 收起态切视图的变形帧：画布按"本体要去的地方"排内容（见 IslandCanvas.FinalBodyRect 与它 OnRender 的"变形中"）。
        // 每次渲染时现取弹簧的目标值，所以只挂这一次。展开动画期间给空矩形：那一段保持原来的画法。
        islandCanvas.FinalBodyRect = () =>
        {
            if (_expanded) return Rect.Empty;
            var f = ComputeBodyRect(_bodyW.Target, _bodyH.Target);
            return RenderKit.SafeRect(f.X - Left, f.Y - Top, f.Width, f.Height);
        };

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

        ApplyTheme();
    }

    private const int HotkeyToggleId = 0xB001;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        IntPtr ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long style = ex.ToInt64() | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
        // DPI 变化（拖到别的显示器/系统缩放调整）：让光标换算与毛玻璃重新取缩放
        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(OnWndProc);
        // 全局热键 Ctrl+Alt+Space：展开/收起（L4-NotchBar 同款触发）
        RegisterHotKey(hwnd, HotkeyToggleId, MOD_CTRL | MOD_ALT, 0x20 /* VK_SPACE */);
        PositionOnEdge();
    }

    private IntPtr OnWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_DPICHANGED / WM_DISPLAYCHANGE(0x007E) 会让显示器矩形与缩放失效 → 清缓存再定位
        if (msg == WM_DPICHANGED) { _dpiScale = 0; _monitorDip = null; PositionOnEdge(); }
        else if (msg == 0x007E) { _monitorDip = null; PositionOnEdge(); }   // DPI 变化后重定位（防级联位置残留）
        else if (msg == WM_SETTINGCHANGE && _settings.Theme == "Auto") ApplyTheme();   // 壁纸/深浅切换跟随
        else if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyToggleId)
        {
            if (_focusFade) { _focusFade = false; Opacity = 1.0; }   // 专注模式下热键先恢复可见
            ToggleExpand();
            handled = true;
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

        // 设备电量：30s 轮询（BLE/HID/WMI）——按需启停：
        // 只有收起态是 devices 视图、或面板展开(里面有设备电量页)时才跑，
        // 否则 30 秒两次 WMI 搜索会一直唤醒 WMI provider host（实测 13ms + 19ms 每次）
        Services.DeviceService.SetWanted(false);

        // 剪贴板历史：挂监听（纯本地内存，最多 50 条）
        Services.ClipboardService.Attach(new System.Windows.Interop.WindowInteropHelper(this).Handle);

        // 文件中转：岛体接受拖放
        AllowDrop = true;
        Drop += OnFilesDrop;

        // 毛玻璃：后台截图 -> 主线程构造画刷
        _glass.Updated += OnGlassUpdated;
        _glass.Start();

        // 音量：系统音量变化 -> 岛内浮条
        _volume.Changed += OnVolumeChanged;
        _volume.Start();

        // 温度/风扇：展开或收起视图需要时才采样；启动先采一次做诊断
        // SMN 直读默认关闭（会装未签名内核驱动，见 InpOutSmnService 的 0x1EA 教训）
        _thermal.AllowSmnDirectRead = _settings.EnableSmnDirectRead;
        // 具名方法订阅：OnClosed 里要用同一个目标 -= 解绑
        //（lambda 订阅解不掉，ThermalService 是长驻的，会一直持有这个已关闭的窗口）
        _thermal.Updated += OnThermalUpdated;
        // 启动 5s 后采一次温度做诊断（一次性；此前写法每 5s 空跑一次，违背按需采样设计）
        var prime = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        prime.Tick += (_, _) => { prime.Stop(); _thermal.PrimeOnce(); };
        prime.Start();

        _frame.Start();
        UpdatePointerTracking();   // 按需启停：穿透 / 长关模式下 PointerTrack_Tick 整个是空转
        _fullscreenCheck.Start();
        if (_settings.ClickThrough) SetClickThrough(true);   // 恢复上次的穿透状态
        islandCanvas.Pet.Style = _settings.PetStyle;
        ApplyItemFilters();                          // dash/combo/监控页 显示项注入
        islandCanvas.CompactView = _settings.CompactView;
        islandCanvas.ComboNumericHero = _settings.ComboNumericHero;
        StartPetCycle();   // 必须在恢复视图之后：仅 pet/combo 视图才会启动轮换
        ApplyExpandMode();
        // 预热面板数据（后台构建）：第一次展开时面板不会是空的
        RequestPanelData();
        var s = CollapsedSize(islandCanvas.CompactView);
        _bodyW.Snap(s.Width); _bodyH.Snap(s.Height);

        // 内存"用完即还"（2026-11-24 空闲降耗）：空闲与否由 IsUiIdle 判定；
        // 启动稳定后（45s）回收一次，把启动期的临时分配还回去。
        Services.MemoryReclaim.IsIdle = IsUiIdle;
        Services.MemoryReclaim.Request(TimeSpan.FromSeconds(45));
    }

    /// <summary>
    /// UI 是否空闲 —— MemoryReclaim 的回调（UI 线程上调用）。回收会短暂阻塞 UI 线程，
    /// 所以凡是"用户正盯着看"的状态都不算空闲：展开中、拖拽中、任意一根弹簧还在动
    /// （展开/收起、液桥、水滴、挤压）、临时活动浮条还在显示、还有一次缩窗没做。
    /// 展开面板还在可视树上也不算：面板那块内存正是这次要还的东西。
    /// </summary>
    private bool IsUiIdle() =>
        !_expanded && !_dragging
        && _bodyW.AtRest && _bodyH.AtRest && _open.AtRest && _neckStrength.AtRest
        && _neckStretch.AtRest && _drip.AtRest && _squash.AtRest
        && !(islandCanvas.Activity is { } a && DateTime.Now < a.Until)
        && !_pendingWindowShrink && _panel is null;

    private void OnClosed(object? sender, EventArgs e)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, HotkeyToggleId);
        _frame.Stop(); _fullscreenCheck.Stop();
        _cycleTimer.Stop(); _weatherTimer.Stop();
        _pointerTrack.Stop(); _pointerTrack.Dispose();   // PromptTimer：释放线程池计时器并解绑 Tick
        // 服务是长驻的：不解绑的话，它们会一直持有这个已关闭的窗口
        _monitor.Snapshot -= OnMonitorSnapshot;
        _glass.Updated -= OnGlassUpdated;
        _volume.Changed -= OnVolumeChanged;
        _thermal.Updated -= OnThermalUpdated;   // 解绑温度回调（服务长驻，不解绑会一直持有已关闭的窗口）
        LostMouseCapture -= OnLostMouseCapture;
        Services.ClipboardService.Detach();   // 摘掉剪贴板监听（窗口关了就不该再记录）
        Services.DeviceService.Stop();        // 停掉设备电量轮询（不随窗口常驻）
        Services.MemoryReclaim.IsIdle = null; // 这个静态回调持有本窗口：窗口关了就摘掉
        StopMonitor();
        _volume.Dispose();
        _thermal.Dispose();
        _glass.Dispose();
        _settings.CompactView = islandCanvas.CompactView;
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
        // 液桥拉伸（_neckStretch，只有拖拽时在动）也要算进 moving（2026-11-24 空闲降耗）：
        // 小形象视图不再整画布重绘（见下方的 InvalidatePet），这根弹簧不算进来的话，
        // 挤压弹簧静止之后再拖，液桥就不能逐帧跟着鼠标走了（只剩监控快照那一拍的重绘）。
        bool moving = !_bodyW.AtRest || !_bodyH.AtRest || !_open.AtRest || !_neckStrength.AtRest ||
                      !_neckStretch.AtRest || !_squash.AtRest || !_drip.AtRest;
        bool activity = islandCanvas.Activity is { } a && DateTime.Now < a.Until;
        bool petFrames = !_expanded && islandCanvas.CompactView == "pet";
        bool busy = moving || activity;
        // 小形象动画 30fps → 24fps：逐帧重绘的是整块分层窗口的画布，
        // 24fps 对这个尺度的连续动画已经看不出差别，但每帧省 20% 的重建开销
        _frame.Interval = busy ? TimeSpan.FromMilliseconds(16)
                         : petFrames ? TimeSpan.FromMilliseconds(42)
                         : TimeSpan.FromMilliseconds(100);

        // 收起动画彻底结束后再把分层窗口缩回胶囊尺寸（省 native 内存）
        if (_pendingWindowShrink && _open.AtRest)
        {
            _pendingWindowShrink = false;
            ApplyWindowSize(false);
            // 缩窗后本体在窗口内的相对位置变了，显式补一次整画布重画（2026-11-24 空闲降耗）：
            // 这一拍弹簧已静止、不算 busy，而以前兜着它的是小形象视图的每拍整画布重绘和每秒一次的无条件重绘，
            // 这两样现在都去掉了 —— 位置变了就明确画一帧，不去依赖"窗口改尺寸时 WPF 会自己重画自绘控件"。
            islandCanvas.InvalidateVisual();
        }

        _bodyRect = ComputeBodyRect(_bodyW.Value, _bodyH.Value);

        // 本体矩形（窗口相对 DIP）注入自绘控件
        islandCanvas.BodyRect = RenderKit.SafeRect(
            _bodyRect.X - Left, _bodyRect.Y - Top, _bodyRect.Width, _bodyRect.Height);

        islandCanvas.NeckStrength = _neckStrength.Value;
        islandCanvas.NeckStretch = _neckStretch.Value;
        islandCanvas.DripRadius = _drip.Value;
        islandCanvas.Squash = _squash.Value;
        islandCanvas.IsExpanded = _open.Value > 0.5;
        islandCanvas.Edge = _settings.Edge;
        var cursor = CursorDip();
        var bc = BodyCenter;
        islandCanvas.LookPoint = new Point(cursor.X - bc.X, cursor.Y - bc.Y);
        islandCanvas.PointerNear = !_expanded && System.Windows.Rect.Inflate(_bodyRect, 60, 60).Contains(cursor);
        // 小形象情境（拓麻歌子式：温度/磁盘状态直接读现有快照，零新增采集）
        islandCanvas.Pet.CpuTemp = _thermal.Current?.CpuTemp;
        islandCanvas.Pet.DiskActive = _snapshot?.DiskUsagePercent ?? 0;
        // 天气：后台拉到数据后自然出现在时钟视图右缘
        if (islandCanvas.Weather is null)
            islandCanvas.Weather = _weather.Current;
        // 按需重绘：忙时（弹簧/律动/仪表盘）每帧画；静止时 10fps 且每 3s 才画一帧。
        // 监控快照等数据经 DependencyProperty(AffectsRender) 变化时自动触发重绘，无需兜底。
        _frameTick++;
        // 浮条（音量/高温告警）过期的那一拍补画一帧把它擦掉，且只补这一拍（2026-11-24 空闲降耗）：
        // 它显示期间算 busy、每帧都画；过期后 busy 变 false。以前毛玻璃每秒两次的重绘会顺手把它擦掉，
        // 现在身后的桌面没变时毛玻璃不再触发重绘，不补这一帧浮条就得等到下一次别的重绘（下一拍监控快照）才消失。
        bool activityGone = _activityShown && !activity;
        _activityShown = activity;
        if (activityGone) ApplyAutoCollapsedWidth();
        if (busy || activityGone
            || (!_expanded && islandCanvas.CompactView is "devices" or "clipboard")
            || _frameTick % 30 == 0)
            islandCanvas.InvalidateVisual();
        // 时钟（clock / combo 视图里的"时:分"）跨分钟的那一拍补画一帧（2026-11-24 空闲降耗的配套）：
        // 以前靠每秒一次的无条件重绘顺带更新（最多滞后 1 秒）；那次重绘去掉后只剩监控快照和每 3 秒的兜底重绘，
        // 采样间隔设得长时分钟跳变最长会滞后 3 秒。现在帧循环（空闲 10Hz）发现分钟变了就画，滞后 ≤ 0.1 秒。
        int minute = DateTime.Now.Minute;
        if (minute != _minuteShown)
        {
            _minuteShown = minute;
            if (!_expanded && islandCanvas.CompactView is "clock" or "combo")
                islandCanvas.InvalidateVisual();
        }
        // 小形象视图只重画小形象自己的图层，不再为此重建整块分层窗口（2026-11-24 空闲降耗）；
        // 弹簧在动时（上面的整画布重绘之外）同样要调：小形象的落点跟着本体矩形走。
        if (petFrames)
            islandCanvas.InvalidatePet();

        if (_panel is not null)
        {
            _panel.Opacity = Easing.Clamp(_open.Value, 0, 1);
            _panel.IsHitTestVisible = _open.Value > 0.9;
            // 面板数据在后台线程构建（见 RequestPanelData）：这里只补一次"还没建好"的请求
            if (_open.Value > 0.05 && _panel.DataContext is null)
                RequestPanelData();
        }
        // 收起动画彻底结束后释放面板：常驻态不需要它
        if (!_expanded && _open.AtRest && _open.Value <= 0.001 && _panel is not null)
            ReleasePanel();

        // 温度数据：面板可见或 dash 视图需要时采（按需采样）
        bool thermalWanted = _settings.TempAlertEnabled || _open.Value > 0.3
            || (!_expanded && islandCanvas.CompactView is "dash" or "combo");
        _thermal.SetWanted(thermalWanted);
        if (thermalWanted)
            islandCanvas.Thermal = _thermal.Current;

        // 设备电量按需轮询（devices 视图或面板展开时才跑）；低电量提醒开着时退到 5 分钟慢轮询兜底
        Services.DeviceService.SetWanted(_expanded || (!_expanded &&
            (islandCanvas.CompactView == "devices" ||
             (islandCanvas.CompactView == "dash" &&
              (islandCanvas.DashItems is null || islandCanvas.DashItems.Contains("battery"))))));
        Services.DeviceService.BackgroundWatch = _settings.BatteryAlertEnabled;

        // 毛玻璃刷新：展开时 8fps，收起且静止时 2fps（省 CPU）
        if (Visibility == Visibility.Visible)
        {
            var cover = _bodyRect;
            cover.Inflate(10, 10);
            _glass.Request(cover, DpiScale, _expanded ? 0.125 : 0.5);
        }
    }

    /// <summary>本体的屏幕矩形（DIP）。edge 侧贴住屏幕边缘（v=0），液桥才有意义。</summary>
    private Rect ComputeBodyRect(double bodyW, double bodyH)
    {
        double t = Easing.Clamp(_settings.PosAlongEdge, 0.02, 0.98);
        // 屏宽用监视器物理像素 ÷ DpiScale：SystemParameters 在本机（200% 缩放）实测
        // 返回物理像素 3072，导致岛心被放到可见区右缘（2 倍未换算）
        GetScreenSizeDip(out double sw, out double sh, out double ox, out double oy);
        // ox/oy：副屏在主屏左侧/上方时，虚拟桌面原点不是 (0,0)；不带上就会贴到主屏那一侧
        return _settings.Edge switch
        {
            "bottom" => RenderKit.SafeRect(ox + sw * t - bodyW / 2, oy + sh - bodyH, bodyW, bodyH),
            "left"   => RenderKit.SafeRect(ox, oy + sh * t - bodyH / 2, bodyW, bodyH),
            "right"  => RenderKit.SafeRect(ox + sw - bodyW, oy + sh * t - bodyH / 2, bodyW, bodyH),
            _        => RenderKit.SafeRect(ox + sw * t - bodyW / 2, oy, bodyW, bodyH),
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
        get => _panel?.Monitor.TempNeedAdmin ?? _tempNeedAdmin;
        set { _tempNeedAdmin = value; if (_panel is not null) _panel.Monitor.TempNeedAdmin = value; }
    }

    /// <summary>
    /// 以管理员运行、但温度驱动（PawnIO）还没到位时为 true：温度位显示"缺驱动"而不是"—"（2026-11-24）。
    /// 驱动是启动之后在后台装的：装完（成功或失败）由 App.OnDriverInstallDone 调 <see cref="SyncTempHints"/> 再同步一次。
    /// </summary>
    private bool _tempNoDriver;
    public bool MonitorTempNoDriver
    {
        get => _tempNoDriver;
        set
        {
            if (_tempNoDriver == value) return;
            _tempNoDriver = value;
            if (_panel is null) return;
            _panel.Monitor.TempNoDriver = value;
            _panel.Monitor.InvalidateVisual();
        }
    }

    /// <summary>驱动安装结束时（成功或失败）同步一次"缺驱动"状态词。只在 UI 线程上调用。</summary>
    internal void SyncTempHints()
    {
        // 非管理员：问题不在驱动，状态词保持"需管理员"
        if (_tempNeedAdmin) return;
        MonitorTempNoDriver = !Services.ThermalService.DriverPresent;
        UpdateTempTooltip(_thermal.Current);
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
        UpdatePointerTracking();   // 展开方式变了：指针跟踪要不要跑也随之变
    }

    /// <summary>
    /// 开关 AMD SMN 直读温度通道（高风险：会安装未签名内核驱动，见 InpOutSmnService）。
    /// 运行中切换立即生效：关掉后温度服务不再使用该通道。
    /// </summary>
    public void SetSmnDirectRead(bool on) => _thermal.AllowSmnDirectRead = on;

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

    /// <summary>
    /// 指针跟踪按需启停（2026-11-24 空闲降耗）。_pointerTrack 是 30ms 一次的线程池计时器：
    /// 每拍都要唤醒一个线程池线程、再往 UI 线程投递一次，而 PointerTrack_Tick 在下面两种状态下整个是空转 ——
    /// 穿透模式（第一行就 return，任何展开方式下都一样）、长关模式（不自动展开）。
    /// 所以只有"没开穿透、且不是长关"时才让它跑：
    ///   hover           → 悬停展开 / 自动收起全靠它；
    ///   always          → tick 兼职"被收起就重新展开"的看门狗（穿透时这个看门狗本来也不生效，一并停掉）。
    /// 调用点：OnLoaded（替代原来无条件的 Start）+ 每个会改这两个设置的地方
    ///（SetClickThrough / ApplyExpandMode / ToggleExpand）。
    /// </summary>
    private void UpdatePointerTracking()
    {
        // 与 PointerTrack_Tick 的判定保持一致：穿透在最前面直接返回；只有 "never" 是长关
        bool wanted = !_settings.ClickThrough && _settings.ExpandMode != "never";
        if (!wanted) { _pointerTrack.Stop(); return; }
        if (!_pointerTrack.IsEnabled) _pointerTrack.Start();   // 已经在跑就别重启（Start = Stop + 新计时器）
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

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        _pointerEnterSince = DateTime.MinValue;
        _pointerLeftSince = DateTime.MinValue;

        if (expanded)
        {
            // 面板要显示进程榜等明细：现在起采样才带上它们（2026-11-24 空闲降耗；收起后在 ReleasePanel 里停）
            _monitor.SetDetailWanted(true);
            // 面板此时还没显示（_open 从 0 淡入），创建耗时被动画覆盖
            EnsurePanel();
            // 先把分层窗口撑到展开态尺寸，再播动画（动画期间不逐帧 resize）
            ApplyWindowSize(true);
            // ⚠️ 必须清掉"待缩窗"标记：收起动画还没播完就又被展开时，
            //    否则展开态稳定后（_open.AtRest）仍会触发缩窗，把展开面板裁掉。
            _pendingWindowShrink = false;
            _bodyW.To(ExpandW); _bodyH.To(ExpandH); _open.To(1);
            _neckStrength.To(0);
            // 每次展开都重新拉一遍面板数据（后台构建）：读数不会停在上一轮的旧值
            RequestPanelData();
        }
        else
        {
            var s = CurrentCollapsedSize();
            _bodyW.To(s.Width); _bodyH.To(s.Height); _open.To(0);
            _neckStrength.To(1);
            // 收起动画结束后再缩窗口（动画期间保持大窗口，避免分层窗逐帧 resize 闪帧）
            _pendingWindowShrink = true;
            StartPetCycle();   // 收起后恢复小形象轮换计时
        }
    }

    // ---------- 数据 ----------
    private void OnGlassUpdated()
    {
        // 后台线程触发；回 UI 线程构造画刷
        Dispatcher.BeginInvoke(() =>
        {
            // 传本体矩形（窗口 DIP）：Viewbox 相对被填充几何的包围盒，
            // 传整个窗口矩形会把纹理映射到错误区域（此前被高不透明压层掩盖未发现）
            islandCanvas.BackdropBrush = _glass.BuildBrush(islandCanvas.BodyRect, Left, Top);
            islandCanvas.InvalidateVisual();
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

            // 展开页监控页（自绘，不走绑定）
            var mon = _panel?.Monitor;
            if (mon is not null)
            {
                mon.Snapshot = s;
                mon.CpuHistory = cpuSeq;
                mon.NetHistory = netSeq;
                mon.DownHistory = downSeq;
                mon.UpHistory = upSeq;
                mon.Thermal = _thermal.Current;
                mon.InvalidateVisual();
            }
            UpdateTempTooltip(_thermal.Current);
            ApplyAutoCollapsedWidth();   // 内容增减（显示项/电池/天气）→ 岛体宽度自动跟随
            islandCanvas.MicActive = _volume.MicInUse;   // 隐私点：麦克风使用中亮绿点

            CheckAlerts();

            if (_panel?.DataContext is PanelData pd)
            {
                pd.UpdateClipboard();   // 剪贴板（内部 2s 节流）
                pd.UpdateFiles();       // 文件中转
            }
            _panel?.DevicesView.InvalidateVisual();   // 自绘设备页：随帧重绘
        });
    }

    // ---------- 温度 tooltip（悬停收起岛体 / 监控页：分级配色 + 状态词 + 权限提示） ----------
    private string? _tempTipKey;

    private void UpdateTempTooltip(Services.ThermalSnapshot? t)
    {
        string F(double? v) => v is { } x ? $"{Math.Round(x)}" : "--";
        // 末尾带上"缺驱动"：驱动装完的那一刻读数多半还是 "--"，只靠读数变化不会触发重建，状态词会一直停在"缺驱动"
        string key = $"{F(t?.CpuTemp)}|{F(t?.GpuTemp)}|{F(t?.SsdTemp)}|{F(t?.MotherboardTemp)}|{_tempNeedAdmin}|{_tempNoDriver}";
        if (key == _tempTipKey) return;   // 读数没变不重建（1s 一跳，避免抖动）
        _tempTipKey = key;

        var tip = BuildTempTip(t);
        islandCanvas.ToolTip = tip;
        if (_panel is not null) _panel.Monitor.ToolTip = tip;
    }

    private UIElement BuildTempTip(Services.ThermalSnapshot? t)
    {
        var pal = _palette;
        // 状态词在拼这段文本的这一刻才定，不提前缓存：驱动是启动后在后台装的，"缺驱动"会在运行途中变成读得到。
        // 重建时机由 UpdateTempTooltip 的 key 把关（读数变了、或驱动装完了才重建）。
        bool needAdmin = _tempNeedAdmin;
        bool noDriver = !needAdmin && !Services.ThermalService.DriverPresent;
        string unavailable = needAdmin ? "需管理员" : noDriver ? "缺驱动" : "读不到";
        (Color C, string Word) Grade(double? v) => v switch
        {
            null => (needAdmin || noDriver ? pal.Warning : pal.TextTertiary, unavailable),
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
                Foreground = new SolidColorBrush(g.C),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
            panel.Children.Add(row);
        }

        Row("CPU", t?.CpuTemp);
        // GPU 读不到的原因要看情况：缺权限 / 缺驱动时显示状态词；两者都不缺才是真的没有接口（Intel 核显）
        Row("GPU", t?.GpuTemp, t?.GpuTemp is null && !needAdmin && !noDriver ? "核显无接口" : null);
        Row("SSD", t?.SsdTemp, "传感器已禁用");
        Row("主板", t?.MotherboardTemp);

        // 这行只在这时成立：问题真的出在权限上。缺驱动时重启成管理员没有用（已经是了），不能这么提示
        if (needAdmin)
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

    // ---------- 高温告警（BrimDeck compact-alert 同款思路：超阈值在收起态弹 4 秒浮条） ----------
    private DateTime _lastTempAlert = DateTime.MinValue;

    private void CheckAlerts()
    {
        CheckTempAlert();
        CheckBatteryAlert();
    }

    private void CheckTempAlert()
    {
        if (_expanded) return;
        if (!_settings.TempAlertEnabled) return;
        var t = _thermal.Current;
        if (t is null) return;
        double th = _settings.TempAlertThreshold;
        double? hot = new[] { t.CpuTemp, t.GpuTemp }
            .Where(v => v is { } n && double.IsFinite(n) && n >= th).Max();
        if (hot is null) return;
        if ((DateTime.Now - _lastTempAlert).TotalSeconds < 60) return;   // 每分钟最多提醒一次
        if (!CanShowActivity("temp")) return;
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
        ApplyAutoCollapsedWidth();
        islandCanvas.InvalidateVisual();
    }

    // 低电量提醒（2026-11-25）：外设/电池电量低于阈值时弹一次浮条。
    private readonly Dictionary<string, DateTime> _batteryAlerts = new(StringComparer.Ordinal);

    private bool CanShowActivity(string kind)
    {
        var current = islandCanvas.Activity;
        return Settings.CanReplaceActivity(current?.Kind,
            current is null || DateTime.Now >= current.Until, kind);
    }

    private void CheckBatteryAlert()
    {
        if (_expanded || !_settings.BatteryAlertEnabled) return;
        // 只提醒带百分比的设备；手柄档位（LevelText 非 null）没有百分比，不参与 —— 换算就是伪装
        var devices = DeviceService.Snapshot;
        foreach (var device in devices)
            if (device.Charging || device.Percent >= _settings.BatteryAlertThreshold + 5)
                _batteryAlerts.Remove(device.Kind + "|" + device.Name);
        if (!CanShowActivity("battery")) return;
        var low = devices
            .Where(d => !d.Charging && d.LevelText is null && d.Percent >= 0 && d.Percent < _settings.BatteryAlertThreshold)
            .Where(d => !_batteryAlerts.TryGetValue(d.Kind + "|" + d.Name, out var shown)
                || (DateTime.Now - shown).TotalMinutes >= 60)
            .OrderBy(d => d.Percent).FirstOrDefault();
        if (low is null) return;
        _batteryAlerts[low.Kind + "|" + low.Name] = DateTime.Now;
        islandCanvas.Activity = new ActivityView
        {
            Kind = "battery",
            Label = "电量",
            Value = Math.Min(1, low.Percent / 100.0),
            RightText = $"{low.Percent}%",
            Until = DateTime.Now.AddSeconds(4),
        };
        islandCanvas.Pet.React("surprise");
        ApplyAutoCollapsedWidth();
        islandCanvas.InvalidateVisual();
    }

    /// <summary>温度/风扇更新（服务在后台线程触发）：回 UI 线程刷新自绘控件。</summary>
    private void OnThermalUpdated(Services.ThermalSnapshot t)
    {
        Dispatcher.BeginInvoke(() =>
        {
            islandCanvas.Thermal = t;
            CheckTempAlert();
            // 监控页：画面会变才重绘（2026-11-24 展开态降耗，见 MonitorPage.ThermalShown）。
            // 展开态下温度更新原来每次都让监控页整页重绘，而页面上只用得到取整后的几个数。
            if (_panel is not null && _panel.Monitor.ThermalUpdate(t))
                _panel.Monitor.InvalidateVisual();
        });
    }

    private void OnVolumeChanged(float vol, bool muted)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!CanShowActivity("volume")) return;
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
            ApplyAutoCollapsedWidth();
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
            if (_panel is not null) _panel.DataContext = pd;
        }));
    }

    private PanelData BuildPanelData()
    {
        var pd = new PanelData();
        // 控制页命令绑定（亮度步进 10%；执行后立即回读刷新显示）
        pd.BindControls(
            new RelayCommand(_ =>
            {
                Services.ControlService.SetBrightness(Math.Min(100, (Services.ControlService.GetBrightness() ?? 50) + 10));
                pd.UpdateControls();
            }),
            new RelayCommand(_ =>
            {
                Services.ControlService.SetBrightness(Math.Max(0, (Services.ControlService.GetBrightness() ?? 50) - 10));
                pd.UpdateControls();
            }),
            new RelayCommand(_ =>
            {
                // 读不到就不猜：状态未知时不动开关（旧的 bool 语义会把"未知"当成"已关闭"）
                var enabled = Services.ControlService.WlanAdapterEnabled;
                if (enabled is null)
                {
                    System.Windows.MessageBox.Show("Wi-Fi 状态读取失败，未做改动。", "MyIsland");
                    return;
                }
                var (wlanOk, wlanMsg) = Services.ControlService.SetWlan(!enabled.Value);
                if (!wlanOk) System.Windows.MessageBox.Show(wlanMsg, "MyIsland");
                pd.UpdateControls();
            }),
            new RelayCommand(_ =>
            {
                var (btOk, btMsg) = Services.ControlService.SetBluetooth(!Services.ControlService.BluetoothEnabled);
                if (!btOk) System.Windows.MessageBox.Show(btMsg, "MyIsland");
                pd.UpdateControls();
            }),
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

    private static (double Width, double Height) CollapsedSize(string view) => view switch
    {
        "clock" => (230, 44),   // 时间 + 天气 + 网速（参考 BrimDeck compact-clock 的信息密度）
        // 信息胶囊：时间 + 占用环组(C/M/G) + 温度/硬盘环组(C/G/D) + 网速。
        // 上限 640：内容宽按实测定（宿主 CurrentCollapsedSize 用 clamp(实测内容宽, 110, 上限)），
        // 上限只决定「最坏情况还装不装得下」——两个读数同时到 100%（4 字符，比最小槽 21 宽约 7px）
        // 时 560 会把网速整段挤进「+N」，640 才有余量。mac 的 CapsuleW 同样是按内容定宽。
        "combo" => (640, 44),
        // 2026-11-24：读数带的等宽数字 + 四个表盘 + 网速实测约 470 宽，原来上限 460 会把末尾的 "K" 裁掉
        "monitor" => (500, 44), // 单排高密度读数带
        "dash" => (560, 44),    // 全读数单行（核心柱│温度组│占用组│电池│网速，组间分隔线；对齐 mac）
        "window" => (360, 44),  // 活动窗口（YASB ActiveWindow 同款：进程名 + 标题）
        "devices" => (300, 64),
        "clipboard" => (240, 44),
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
        SetActiveWindow(ctx);
    }

    /// <summary>长期显示模式下也要注入前台窗口（UpdateFullscreenHidden 提前 return 的分支）。</summary>
    private void InjectActiveWindow() => SetActiveWindow(ForegroundDetector.Detect());

    /// <summary>
    /// 注入前台窗口（进程名 + 标题）：和上一次相同就什么都不做。
    /// ActiveWindow 是普通 CLR 属性（不是 DP），不会自动触发重绘 → 变了才显式重画一次，
    /// 否则活动窗口视图的标题最多滞后 3 秒（重绘只发生在忙时或每 3s 一次）。
    /// 以前这里每秒无条件重画（2026-11-24 空闲降耗）：前台窗口没变的那一拍，整块分层窗口都白重建一次。
    /// </summary>
    private void SetActiveWindow(ScreenContext ctx)
    {
        var active = (ctx.ProcessName, ctx.Title);
        if (islandCanvas.ActiveWindow == active) return;
        islandCanvas.ActiveWindow = active;
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

    /// <summary>当前收起态尺寸（指示条模式下固定 44×6；宽度按内容实测自动调节，上限=设计宽度）。</summary>
    private (double Width, double Height) CurrentCollapsedSize()
    {
        if (_indicatorMode) return (44, 6);
        if (islandCanvas.Activity is { } activity && DateTime.Now < activity.Until) return (280, 44);
        var view = islandCanvas.CompactView;
        var fix = CollapsedSize(view);
        GetScreenSizeDip(out double screenWidth, out _, out _, out _);
        double budget = Math.Max(110, Math.Min(fix.Width, screenWidth - 80));
        islandCanvas.CompactWidthBudget = budget;
        fix = (budget, fix.Height);
        double w = islandCanvas.HasMeasuredCompact
            ? Math.Clamp(islandCanvas.CompactContentWidth, 110, fix.Width)
            : fix.Width;
        return (w, fix.Height);
    }

    /// <summary>内容宽度变化时重新设定岛体目标宽度（弹簧动画过渡，1s 节流由调用方保证）。</summary>
    private double _lastAutoW = -1, _lastAutoH = -1;
    private void ApplyAutoCollapsedWidth()
    {
        if (_expanded || _indicatorMode) return;
        var s = CurrentCollapsedSize();
        if (Math.Abs(s.Width - _lastAutoW) > 3 || Math.Abs(s.Height - _lastAutoH) > 0.5)
        {
            _lastAutoW = s.Width;
            _lastAutoH = s.Height;
            _bodyW.To(s.Width);
            _bodyH.To(s.Height);
        }
    }

    private void ApplyTheme()
    {
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
        ApplyPalette(_settings.Theme switch
        {
            "Light" => Palette.Light,
            _ => Themes.Palette.ByName(_settings.Theme) ?? Palette.Dark,
        });
    }

    /// <summary>Auto 主题的异步代次：只有最后一次请求的结果会被应用。</summary>
    private int _autoThemeGen;

    /// <summary>应用配色（必须在 UI 线程调用）。</summary>
    private void ApplyPalette(Palette pal)
    {
        _palette = pal;
        // 收起态画布不等 Loaded：构造函数末尾就会调一次 ApplyTheme，那时 IsLoaded 还是 false。
        // 这一句以前关在下面的 if (IsLoaded) 里 ⇒ 每次启动后收起态都停在默认深色主题的配色上，
        // 展开面板用的却是用户选的主题，要到用户手动换一次主题才对得上
        //（2026-11-24 实机验证发现：赛博朋克下胶囊的 CPU 环是默认主题的青色，展开面板是黄色）。
        islandCanvas.Palette = _palette;
        if (IsLoaded)
        {
            if (_panel is not null)
            {
                _panel.Monitor.Palette = _palette;
                _panel.DevicesView.Palette = _palette;
                _panel.Monitor.InvalidateVisual();
            }
            islandCanvas.InvalidateVisual();
            // 换主题要让毛玻璃换一张底图（2026-11-24 空闲降耗）：截图里带着岛体自己的像素，
            // 而身后的桌面没变时毛玻璃靠像素比对不会再换图，岛体的新配色就反映不到底图里。
            // 排到本次重绘之后再截：此刻岛体还没按新配色画出来（重绘排在本次调用返回之后的 Render 优先级），
            // 立刻截到的还是旧像素，等于白强制一次。
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(_glass.Invalidate));
        }
        // 展开面板静态资源同步（CardBg/FgDim 等随主题变）
        UpdatePanelResources();
    }

    /// <summary>把当前主题刷进展开面板的资源字典（XAML 里用 DynamicResource，写面板自己的字典才实时生效）。</summary>
    private void UpdatePanelResources()
    {
        if (!IsLoaded || _palette is null || _panel is null) return;
        // 注意：面板 XAML 的引用已改为 DynamicResource，写面板自己的资源字典才会实时生效
        _panel.Resources["PanelBg"] = new SolidColorBrush(_palette.Surface) { Opacity = 0.96 };
        // 全套语义令牌（含派生的悬停 / 压下色、圆角）在 ExpandedPanel.ApplyTheme 里一次写齐
        ExpandedPanel.ApplyTheme(_panel.Resources, _palette);
        // 页签 / 电源计划按钮的选中色是创建时取的本地值，换主题后要重取
        _panel.Retint();
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
    /// <summary>按当前状态重新定位/定尺寸窗口（展开态或收起态）。</summary>
    private void PositionOnEdge() => ApplyWindowSize(_expanded);

    /// <summary>
    /// 窗口矩形 = 本体矩形 + 边距（40 DIP）。
    ///
    /// 展开态：覆盖展开面板最大范围（560×420 + 边距 = 640×500）。
    /// 收起态：只覆盖本体（如 44 + 边距 = 124 高）—— 分层窗口(AllowsTransparency=True)
    /// 要为整块窗口开渲染表面/DIB/合成缓冲，**面积差 4 倍就是几十 MB 的 native 内存**
    /// （同机最小 WPF 探针实测：640×500 分层窗 182MB vs 640×124 165MB；MyIsland 本体 272MB）。
    ///
    /// ⚠️ **宽度恒定**：本体宽度随内容（读数/天气）自动变化，窗口跟着变就会抖；
    ///    只调高度，且只在状态切换时调（展开动画期间不 resize，避免分层窗闪帧）。
    /// </summary>
    private void ApplyWindowSize(bool expanded)
    {
        const double margin = 40;
        double wantW = ExpandW + margin * 2;
        double wantH, cx, cy;
        if (expanded)
        {
            wantH = ExpandH + margin * 2;
            var r = ComputeBodyRect(ExpandW, ExpandH);
            cx = r.X + r.Width / 2;
            cy = r.Y + r.Height / 2;
        }
        else
        {
            var s = CurrentCollapsedSize();
            wantH = Math.Max(64, s.Height + margin * 2);
            var r = ComputeBodyRect(s.Width, s.Height);
            cx = r.X + r.Width / 2;
            cy = r.Y + r.Height / 2;
        }
        double wantLeft = cx - wantW / 2;
        double wantTop = cy - wantH / 2;
        if (Math.Abs(Width - wantW) < 0.5 && Math.Abs(Height - wantH) < 0.5 &&
            Math.Abs(Left - wantLeft) < 0.5 && Math.Abs(Top - wantTop) < 0.5) return;
        Width = wantW; Height = wantH; Left = wantLeft; Top = wantTop;
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
            _squash.Snap(0.88); _squash.To(1, 260, 0.7);   // 拖入反馈
        }
    }

    /// <summary>滚轮切收起视图。
    /// 只能挂一处：XAML 的 IslandCanvas.MouseWheel="OnMouseWheel"（冒泡阶段）已经挂了，
    /// 再挂一份 PreviewMouseWheel（隧道阶段）会让一次滚轮跳两格视图。</summary>
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_expanded) return;
        string[] views = { "clock", "dash", "pet", "combo", "monitor", "devices", "clipboard", "window" };
        int i = Array.IndexOf(views, islandCanvas.CompactView);
        i = (i + (e.Delta > 0 ? -1 : 1) + views.Length) % views.Length;
        islandCanvas.CompactView = views[i];
        var s = CurrentCollapsedSize();
        _bodyW.To(s.Item1); _bodyH.To(s.Item2);
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
        _settings.ExpandMode = "hover";
        _lockedClosed = false;
        SetExpanded(!_expanded);
        UpdatePointerTracking();   // 展开方式被改成 hover：原来是长关 / 穿透的话，跟踪要重新跑起来
    }
    public void ApplyThemeExternal() => ApplyTheme();
    public void RepositionExternal() => PositionOnEdge();

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
        ["clock", "dials", "cputemp", "gputemp", "disk", "net"];

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
        if (_panel is not null)
        {
            _panel.Monitor.Items = ParseItems(_settings.MonitorItems);
            _panel.Monitor.InvalidateVisual();
        }
        islandCanvas.InvalidateVisual();
    }

    /// <summary>打开 dash 显示项设置（勾选 + 实时预览）。</summary>
    public void ShowDashSettings()
    {
        var preview = new IslandCanvas
        {
            CompactView = "dash", Palette = _palette, IsHitTestVisible = false,
            Width = 660, Height = 69,
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
        };
        // 显示项变多，预览按比例缩一点才塞得进设置窗口
        preview.LayoutTransform = new ScaleTransform(0.62, 0.62);
        OpenModuleSettings(
            "信息胶囊显示项",
            new ModuleSettingsWindow.Module[]
            {
                new("clock", "时间"), new("dials", "读数表盘(C/M/G 占用)"),
                new("cputemp", "CPU 温度"), new("gputemp", "GPU 温度"), new("disk", "硬盘占用"),
                new("net", "网速"),
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
                new("power", "功耗/主板行"), new("devices", "外设电量"), new("topproc", "进程榜"),
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
        // 预览用的是实时快照（含监控页的进程榜等明细）。收起状态下明细采样是关的（2026-11-24 空闲降耗），
        // 不临时打开的话，监控页预览只会显示"采样中…"。对话框在的那段时间打开，关掉后按展开/收起恢复。
        _monitor.SetDetailWanted(true);
        try
        {
            var win = new ModuleSettingsWindow(
                title, modules, _palette, preview,
                () => (_snapshot, _thermal.Current),
                getItems, saveItems, applyFilter, ApplyItemFilters);
            win.ShowDialog();
        }
        finally { _monitor.SetDetailWanted(_expanded); }   // 面板还展开着就继续要明细，否则停
    }

    /// <summary>切收起视图（菜单入口），带挤压回弹反馈。</summary>
    public void SetCompactView(string view)
    {
        islandCanvas.CompactView = view;
        // 立即落盘：以前只靠 OnClosed 同步设置，中途杀进程就丢（2026-11-23 复审 P1-3）
        _settings.CompactView = view;
        _settings.RequestSave();
        if (!_expanded)
        {
            var s = CurrentCollapsedSize();
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
        UpdatePointerTracking();   // 悬停展开 / 收起只在没穿透时才有意义：跟踪随之启停
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
