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
    private readonly DispatcherTimer _pointerTrack;
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
    private Point _dragStart;
    private DateTime _pointerEnterSince = DateTime.MinValue;
    private DateTime _pointerLeftSince = DateTime.MinValue;
    private bool _lockedClosed;  // 双击"长关"：不再自动展开
    private Palette _palette = Palette.Dark;
    private MonitorSnapshot? _snapshot;

    // 本体的屏幕矩形（DIP），每帧由弹簧值计算
    private Rect _bodyRect;

    public IslandWindow(Settings settings)
    {
        _settings = settings;
        _monitor = new SystemMonitor(settings.MonitorIntervalMs);

        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewMouseWheel += OnMouseWheel;
        PreviewKeyDown += OnPreviewKeyDown;

        expandedPanel.CloseRequested += () => SetExpanded(false);   // 顶栏 ✕

        _frame = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _frame.Tick += Frame_Tick;

        _pointerTrack = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
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

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        IntPtr ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long style = ex.ToInt64() | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
        // DPI 变化（拖到别的显示器/系统缩放调整）：让光标换算与毛玻璃重新取缩放
        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(OnWndProc);
        PositionOnEdge();
    }

    private IntPtr OnWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DPICHANGED) _dpiScale = 0;
        else if (msg == WM_SETTINGCHANGE && _settings.Theme == "Auto") ApplyTheme();   // 壁纸/深浅切换跟随
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
            System.IO.File.AppendAllText(Path.Combine(dir, "run.log"),
                $"[{DateTime.Now:HH:mm:ss}] island-up pid={Environment.ProcessId} edge={_settings.Edge}\n");
        }
        catch { }

        _monitor.Snapshot += OnMonitorSnapshot;
        _monitor.Start();

        // 设备电量：30s 轮询（BLE/HID/WMI），收起态 devices 视图消费
        Services.DeviceService.Start();

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
        _thermal.Updated += t => Dispatcher.BeginInvoke(() =>
        {
            islandCanvas.Thermal = t;
            expandedPanel.Monitor.Thermal = t;
            expandedPanel.Monitor.InvalidateVisual();
        });
        // 启动 5s 后采一次温度做诊断（一次性；此前写法每 5s 空跑一次，违背按需采样设计）
        var prime = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        prime.Tick += (_, _) => { prime.Stop(); _thermal.PrimeOnce(); };
        prime.Start();

        _frame.Start();
        _pointerTrack.Start();
        _fullscreenCheck.Start();
        if (_settings.ClickThrough) SetClickThrough(true);   // 恢复上次的穿透状态
        islandCanvas.Pet.Style = _settings.PetStyle;
        expandedPanel.Monitor.Palette = _palette;   // 监控页主题（ApplyTheme 在 Loaded 前跳过）
        ApplyItemFilters();                          // dash/combo/监控页 显示项注入
        islandCanvas.CompactView = _settings.CompactView;
        StartPetCycle();   // 必须在恢复视图之后：仅 pet/combo 视图才会启动轮换
        ApplyExpandMode();
        var s = CollapsedSize(islandCanvas.CompactView);
        _bodyW.Snap(s.Width); _bodyH.Snap(s.Height);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _frame.Stop(); _pointerTrack.Stop(); _fullscreenCheck.Stop();
        _cycleTimer.Stop(); _weatherTimer.Stop();
        Services.ClipboardService.Detach();   // 摘掉剪贴板监听（窗口关了就不该再记录）
        _monitor.Dispose();
        _volume.Dispose();
        _thermal.Dispose();
        _glass.Dispose();
        _settings.CompactView = islandCanvas.CompactView;
        _settings.Save();
    }

    // ---------- 主循环 ----------
    private readonly Services.VolumeService _volume = new();
    private readonly Services.ThermalService _thermal = new();

    // 历史环形缓冲（走势图）：36 点
    private readonly double[] _cpuHist = new double[36];
    private readonly double[] _netHist = new double[36];
    private int _histIdx;
    private double _netMax = 1; // 网速自归一化的滑动峰值

    private void Frame_Tick(object? sender, EventArgs e)
    {
        double dt = 1.0 / 60.0;
        _bodyW.Step(dt); _bodyH.Step(dt); _open.Step(dt);
        _neckStrength.Step(dt); _neckStretch.Step(dt); _drip.Step(dt); _squash.Step(dt);

        // 动态帧率：弹簧全静止且无临时活动时降到 10fps（常驻省电，参考 BrimDeck/wisland）
        bool busy = !_bodyW.AtRest || !_bodyH.AtRest || !_open.AtRest || !_neckStrength.AtRest ||
                    !_squash.AtRest || !_drip.AtRest ||
                    (islandCanvas.Activity is { } a && DateTime.Now < a.Until) ||
                    (!_expanded && (islandCanvas.CompactView is "pet" or "dash")); // 宠物/dash 时间更新需连续帧
        _frame.Interval = busy ? TimeSpan.FromMilliseconds(16) : TimeSpan.FromMilliseconds(100);

        _bodyRect = ComputeBodyRect(_bodyW.Value, _bodyH.Value);

        // 本体矩形（窗口相对 DIP）注入自绘控件
        islandCanvas.BodyRect = new Rect(
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
        // 按需重绘：忙时（弹簧/律动/宠物/仪表盘）每帧画；静止时 10fps 且每 3s 才画一帧。
        // 监控快照等数据经 DependencyProperty(AffectsRender) 变化时自动触发重绘，无需兜底。
        _frameTick++;
        if (busy || (!_expanded && islandCanvas.CompactView is "devices" or "clipboard")
            || _frameTick % 30 == 0)
            islandCanvas.InvalidateVisual();

        expandedPanel.Opacity = Easing.Clamp(_open.Value, 0, 1);
        expandedPanel.IsHitTestVisible = _open.Value > 0.9;
        if (_open.Value > 0.05 && expandedPanel.DataContext is null)
            expandedPanel.DataContext = BuildPanelData();

        // 温度数据：面板可见或 dash 视图需要时采（按需采样）
        bool thermalWanted = _open.Value > 0.3 || (!_expanded && islandCanvas.CompactView == "dash");
        _thermal.SetWanted(thermalWanted);
        if (thermalWanted)
            islandCanvas.Thermal = _thermal.Current;

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
        double sw = SystemParameters.PrimaryScreenWidth;
        double sh = SystemParameters.PrimaryScreenHeight;
        return _settings.Edge switch
        {
            "bottom" => new Rect(sw * t - bodyW / 2, sh - bodyH, bodyW, bodyH),
            "left"   => new Rect(0, sh * t - bodyH / 2, bodyW, bodyH),
            "right"  => new Rect(sw - bodyW, sh * t - bodyH / 2, bodyW, bodyH),
            _        => new Rect(sw * t - bodyW / 2, 0, bodyW, bodyH),
        };
    }

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
            _bodyW.To(ExpandW); _bodyH.To(ExpandH); _open.To(1);
            _neckStrength.To(0);
        }
        else
        {
            var s = CurrentCollapsedSize();
            _bodyW.To(s.Width); _bodyH.To(s.Height); _open.To(0);
            _neckStrength.To(1);
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
            _histIdx++;
            var cpuSeq = new double[_cpuHist.Length];
            var netSeq = new double[_netHist.Length];
            for (int i = 0; i < _cpuHist.Length; i++)
            {
                cpuSeq[i] = _cpuHist[(_histIdx + i) % _cpuHist.Length];
                netSeq[i] = _netHist[(_histIdx + i) % _netHist.Length];
            }
            islandCanvas.CpuHistory = cpuSeq;
            islandCanvas.NetHistory = netSeq;

            // 展开页监控页（自绘，不走绑定）
            var mon = expandedPanel.Monitor;
            mon.Snapshot = s;
            mon.CpuHistory = cpuSeq;
            mon.NetHistory = netSeq;
            mon.Thermal = _thermal.Current;
            mon.InvalidateVisual();
            UpdateTempTooltip(_thermal.Current);
            ApplyAutoCollapsedWidth();   // 内容增减（显示项/电池/天气）→ 岛体宽度自动跟随

            CheckAlerts();

            if (expandedPanel.DataContext is PanelData pd)
            {
                pd.UpdateDevices();     // 设备电量（内部 5s 节流）
                pd.UpdateClipboard();   // 剪贴板（内部 2s 节流）
                pd.UpdateFiles();       // 文件中转
            }
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

    // ---------- 高温告警（BrimDeck compact-alert 同款思路：超阈值在收起态弹 4 秒浮条） ----------
    private DateTime _lastTempAlert = DateTime.MinValue;

    private void CheckAlerts()
    {
        if (_expanded) return;
        var t = _thermal.Current;
        if (t is null) return;
        double? hot = t.CpuTemp is { } c && c >= 88 ? c
                    : t.GpuTemp is { } g && g >= 88 ? g : null;
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
                Services.ControlService.SetWlan(!Services.ControlService.WlanEnabled);
                pd.UpdateControls();
            }),
            new RelayCommand(_ =>
            {
                Services.ControlService.SetBluetooth(!Services.ControlService.BluetoothEnabled);
                pd.UpdateControls();
            }),
            new RelayCommand(_ => Services.ControlService.OpenBluetoothSettings()),
            new RelayCommand(_ => Services.ControlService.Lock()),
            new RelayCommand(_ => Services.ControlService.Sleep()),
            new RelayCommand(p => Services.ControlService.OpenSettings(p as string ?? "")));
        // 音量 / 深色模式回调与初值
        pd.OnSetVolume = v => _volume.SetVolume((float)v);
        pd.OnToggleMute = () => _volume.SetMute(!_volume.IsMutedNow);
        pd.OnSetLightTheme = light => Services.ControlService.SetSystemLightTheme(light);
        pd.OnSetNightLight = on =>
        {
            // 本机从未配置过夜灯（无 Data 值）：带去系统设置先开启一次
            if (!Services.ControlService.SetNightLight(on))
                Services.ControlService.OpenSettings("nightlight");
        };
        pd.OnSetClickThrough = on => SetClickThrough(on);
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
        "combo" => (440, 44),   // 信息胶囊：配额环 + 时间 + 系统读数表盘 + 网速
        "monitor" => (384, 64), // 核心柱 + C/M/G/D 四个表盘 + 网速
        "dash" => (660, 69),    // LiteMonitor 同款全读数行（含硬盘/SSD 温度/电池）
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
        var view = islandCanvas.CompactView;
        var fix = CollapsedSize(view);
        double w = islandCanvas.HasMeasuredCompact
            ? Math.Clamp(islandCanvas.CompactContentWidth, 110, fix.Width)
            : fix.Width;
        return (w, fix.Height);
    }

    /// <summary>内容宽度变化时重新设定岛体目标宽度（弹簧动画过渡，1s 节流由调用方保证）。</summary>
    private double _lastAutoW = -1;
    private void ApplyAutoCollapsedWidth()
    {
        if (_expanded || _indicatorMode) return;
        var s = CurrentCollapsedSize();
        if (Math.Abs(s.Width - _lastAutoW) > 3)
        {
            _lastAutoW = s.Width;
            _bodyW.To(s.Width);
            _bodyH.To(s.Height);
        }
    }

    private void ApplyTheme()
    {
        _palette = _settings.Theme switch
        {
            "Light" => Palette.Light,
            "Auto" => AutoPalette(),   // 跟随系统深浅 + 壁纸取色（macOS 式）
            _ => Themes.Palette.ByName(_settings.Theme) ?? Palette.Dark,
        };
        if (IsLoaded)
        {
            islandCanvas.Palette = _palette;
            expandedPanel.Monitor.Palette = _palette;
            islandCanvas.InvalidateVisual();
            expandedPanel.Monitor.InvalidateVisual();
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
    /// <summary>窗口矩形覆盖本体+液桥+阴影+展开面板的最大范围，避免分层窗口 resize 闪帧（BrimDeck 手法）。</summary>
    private void PositionOnEdge()
    {
        const double margin = 40;
        double maxW = ExpandW + margin * 2;
        double maxH = ExpandH + margin * 2;
        Width = maxW; Height = maxH;

        // 以展开态本体中心为窗口中心：展开动画只动本体，窗口不移动
        var expandRect = ComputeBodyRect(ExpandW, ExpandH);
        double cx = expandRect.X + expandRect.Width / 2;
        double cy = expandRect.Y + expandRect.Height / 2;
        Left = cx - maxW / 2;
        Top = cy - maxH / 2;
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
        _dragging = true;
        _dragStart = CursorDip();
        _squash.To(0.82, 300, 0.6);
        CaptureMouse();
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

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_expanded) return;
        string[] views = { "clock", "dash", "pet", "combo", "monitor", "devices", "clipboard" };
        int i = Array.IndexOf(views, islandCanvas.CompactView);
        i = (i + (e.Delta > 0 ? -1 : 1) + views.Length) % views.Length;
        islandCanvas.CompactView = views[i];
        var s = _indicatorMode ? (44, 6) : CollapsedSize(views[i]);
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

    /// <summary>把 Settings.DashItems 注入自绘控件（空 = 全部显示）。</summary>
    private void ApplyDashItems()
    {
        islandCanvas.DashItems = ParseItems(_settings.DashItems);
        islandCanvas.InvalidateVisual();
    }

    private static HashSet<string>? ParseItems(string? items)
        => string.IsNullOrWhiteSpace(items) ? null
            : new HashSet<string>(items.Split(',', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>三处显示项过滤统一注入（dash / combo / 监控页）。</summary>
    private void ApplyItemFilters()
    {
        ApplyDashItems();
        islandCanvas.ComboItems = ParseItems(_settings.ComboItems);
        expandedPanel.Monitor.Items = ParseItems(_settings.MonitorItems);
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
            Width = 440, Height = 44,
        };
        OpenModuleSettings(
            "信息胶囊显示项",
            new ModuleSettingsWindow.Module[]
            {
                new("clock", "时间"), new("dials", "读数表盘"), new("net", "网速"),
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

    /// <summary>切收起视图（菜单入口），带挤压回弹反馈。</summary>
    public void SetCompactView(string view)
    {
        islandCanvas.CompactView = view;
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

    /// <summary>鼠标穿透模式：WS_EX_TRANSPARENT 动态切换；开启时强制收起面板。</summary>
    public void SetClickThrough(bool on)
    {
        _settings.ClickThrough = on;
        _settings.Save();
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        IntPtr ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long style = ex.ToInt64();
        style = on ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
        if (on) SetExpanded(false);
    }

    /// <summary>当前本体屏幕矩形（DIP），供毛玻璃等模块定位截屏区域。</summary>
    public Rect BodyScreenRect => _bodyRect;
    public double MonitorDpiScale => DpiScale;
}
