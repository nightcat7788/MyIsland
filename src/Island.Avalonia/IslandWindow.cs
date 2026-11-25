using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Island.App.Animation;
using Island.App.Themes;
using Island.Core.Monitoring;

namespace Island.Avalonia;

/// <summary>
/// 灵动岛主窗口（Avalonia 版）：透明置顶无框窗口，贴屏幕顶部中央。
/// 悬停展开监控页（150ms 开 / 350ms 关防抖，与 Windows 版一致），
/// 滚轮切换 dash/clock 收起视图；弹簧驱动形变。
/// 窗口铺满比本体大的区域，本体（胶囊）锚定顶边——与 Windows 版同一坐标约定。
/// </summary>
public sealed class IslandWindow : Window
{
    // 窗口画布宽度：必须 ≥ 任何收起视图在最宽数据下的胶囊宽度，否则胶囊两端被裁
    // （之前写死 640，dash 网速改成一行后需要 671 → 左边缘 -15.8，两侧各裁 16px）。
    // 800 = dash 最宽约 720 + 余量；展开面板 560。
    private double WinH = 600;
    /// <summary>
    /// 画布宽度（窗口透明，实际只有胶囊那一块可见）。**随视图需求自适应**：
    /// dash 收起态加了频谱柱 / 剪贴板数 / 设备环后需要 806px，画布不够就会把内容裁掉。
    /// 窗口比胶囊宽没关系 —— 胶囊居中，多余部分是透明的。
    /// </summary>
    private double WinW = 800;
    private const double WinWMax = 1200;
    /// <summary>窗口高度上限（屏幕够高才不会挡住太多内容）。</summary>
    private const double WinHMax = 1400;
    private const double ExpandW = 560;
    /// <summary>展开面板高度：按当前页的内容算，不写死（原来固定 560 导致卡片被截断或大片留白）。</summary>
    private double ExpandHeight => _canvas.Page switch
    {
        "monitor" => 42 + 181 + 12 + 156 + 30 + 34,   // 页签 + 上排(3行×44+间距) + 间距 + 下排 + 状态行 + 边距
        // 剪贴板：标签行 + 按实际条数算的行高 + 边距。列表是**两列**，行数 = ceil(条数/2)。
        // 原来写死 42 + 20*30 + 30（恒按 20 条满额）→ 只有几条时下方留一大片空白
        // （1.1.0 真机截图：5 条记录撑到 695 高）。与 screens / devices 一样按内容量高。
        // 但给一个最小高度：空列表 / 1~2 条时面板不至于缩成一条缝，提示文字才有地方放。
        "clips" => Math.Max(42 + 3 * 30 + 30, 42 + Math.Ceiling(_canvas.Clips.Count / 2.0) * 30 + 30),
        "screens" => 42 + _canvas.MeasureScreensHeight() + 24,   // 页签 + 实际内容高度 + 边距
        "devices" => 42 + _canvas.MeasureDevicesHeight() + 24,   // 页签 + 设备卡 + 边距
        _ => 560,
    };
    private const int HoverOpenMs = 150, HoverCloseMs = 350;

    private readonly IslandCanvas _canvas = new();
    // 应用图标缓存：探针 app-icon 随包编译，取 .app 包的图标（应用榜用）
    private readonly AppIconCache _appIcons = new(Path.Combine(AppContext.BaseDirectory, "app-icon"));
    private DispatcherTimer? _appIconTimer;
    private DispatcherTimer? _deviceTimer;

    /// <summary>读设备电量（后台线程；探针要起子进程，不能在 UI 线程等）。</summary>
    private void RefreshDevices()
    {
        if (_deviceTimer is null)
        {
            _deviceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _deviceTimer.Tick += (_, _) => RefreshDevices();
        }
        if (!_deviceTimer.IsEnabled) _deviceTimer.Start();
        RunBg(() =>
        {
            var list = DeviceBattery.Read();
            Dispatcher.UIThread.Post(() =>
            {
                _canvas.Devices = list;
                _canvas.MarkDirty();
                UpdateExpandHeight();
            });
        });
    }

    /// <summary>后台批量取应用图标（探针 app-icon），取完标脏让界面刷新。</summary>
    private void FlushAppIcons()
    {
        var s = _canvas.Snapshot;
        if (s?.TopProcesses is null && s?.TopProcessesByMem is null) return;
        RunBg(() =>
        {
            _appIcons.Flush();
            Dispatcher.UIThread.Post(() => _canvas.MarkDirty());
        });
    }
    private readonly ISampler _sampler;
    private readonly DispatcherTimer _frame;
    private readonly DispatcherTimer _tick;
    private TrayIcon? _tray;
    private readonly Spring _bodyW = new(550, 260, 0.82);
    private readonly Spring _bodyH = new(69, 300, 0.8);

    private bool _expanded;
    /// <summary>用户是否手动固定了收起视图（true = 不再 pet⇄combo 自动轮换）。</summary>
    private bool _viewPinned;
    /// <summary>剪贴板保留策略：session / 1d / 7d / 30d（第五十九轮）。</summary>
    private string _clipRetention = "session";
    /// <summary>收起视图自动循环（第六十轮）：小形象 ⇄ 信息胶囊。</summary>
    private bool _autoCycle = true;
    /// <summary>剪贴板定期落盘计时器（60 秒一次，清理过期 + 写盘）。</summary>
    private DispatcherTimer? _clipSaveTimer;
    // 悬停防抖计时器复用一个：原来每次 PointerMoved 都 new 一个 DispatcherTimer（鼠标移动时每秒几十个）
    private readonly DispatcherTimer _hoverTimer;
    private bool _hoverOpen;
    private readonly DispatcherTimer _cycleTimer;
    private readonly DispatcherTimer _weatherTimer;
    private readonly DispatcherTimer _weatherUiTimer;
    private readonly DispatcherTimer _clipsTimer;
    private readonly DispatcherTimer _screensTimer;
    private readonly DispatcherTimer _builtinCountdownTimer;
    private AutoFollowEngine? _autoFollow;
    private readonly Island.App.Services.WeatherService _weather = new();
    private readonly ClipboardHistory _clips = new();
    private const int PetPhaseSec = 10, ComboPhaseSec = 30;

    public IslandWindow()
    {
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Mica };
        Background = Brushes.Transparent;
        Topmost = true;
        CanResize = false;
        ShowInTaskbar = false;
        Width = WinW; Height = WinH;
        Content = _canvas;
        // macOS 关键：无装饰窗口默认仍带一块不透明的窗口背景（服务器端合成），
        // 看起来就是一整块"方形"底板而不是透明悬浮胶囊。
        // ExtendClientAreaChromeHints=None + ExtendClientAreaTitleBarHeightHint=-1
        // 才能让 NSWindow 进入 full-size content view + 逐像素透明（与 iOS 灵动岛同视觉）。
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;

        // 弹簧初值按初始视图尺寸直接到位（否则从 550 宽慢慢收缩）
        var s0 = ViewSize(_canvas.View);
        _bodyW.Snap(s0.W); _bodyH.Snap(s0.H);
       

        Opened += (_, _) =>
        {
            Console.Error.WriteLine($"[startup] Opened: expandMode={_expandMode} expanded={_expanded}");
            // always 模式：构造期 LoadAndApplySettings 里的 SetExpanded(true) 早于窗口布局，
            // spring 目标可能被后续的 Snap/ViewSize 覆盖 → 启动后仍是收起态。Opened 后幂等补一次。
            if (_expandMode == "always") SetExpanded(true);
            PositionTopCenter();
            // 置顶层级：构造期 ApplyAlwaysOnTop 拿不到有效 NSWindow 句柄（窗口还没 Opened），
            // 这里在窗口已创建后再补一次原生层级设置（NSStatusWindowLevel + collectionBehavior）。
            ApplyAlwaysOnTop();
            // 全局快捷键必须等平台初始化完成后再注册：Carbon 的
            // GetApplicationEventTarget() 在 Avalonia 初始化早期返回无效值，
            // InstallEventHandler 会直接崩掉整个进程（实测：注册即启动失败）。
            Dispatcher.UIThread.Post(() => SetupGlobalHotKeys(), DispatcherPriority.Background);
            // 记住的「关闭内建屏」选择：延迟 2 秒后台自动执行（等屏幕枚举就绪）。
            // 退出时程序会安全恢复内建屏，所以下次启动内建屏是开着的，需要按用户上次选择再关一次。
            if (OperatingSystem.IsMacOS())
                Dispatcher.UIThread.Post(async () =>
                {
                    // 内建屏自动关之前，先做 panic 侦测（都是后台 IO，一次跑完）：
                    // 2026-11-25 14:59 那次 DCP 固件 panic（dual_pipe.c:321）后加的取证机制。
                    // 若上次会话以来有内核 panic，把时刻+签名写进 app.log —— 复发时证据自动在手。
                    // 注意顺序：侦测必须在 TryAutoCloseBuiltin 之前，日志里的 [panic] 行
                    // 才能出现在「我们又动了内建屏」之前，读起来因果才顺。
                    await System.Threading.Tasks.Task.Run(ReportPanicIfAny);
                    await System.Threading.Tasks.Task.Delay(2000);
                    await System.Threading.Tasks.Task.Run(TryAutoCloseBuiltin);
                }, DispatcherPriority.Background);
        };

        _canvas.AppIcons = _appIcons;   // 应用榜的图标来源

        _sampler = Samplers.Create(1000);
        _sampler.Snapshot += OnSnapshot;
        _sampler.Start();

        _frame = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _frame.Tick += Frame_Tick;
        _frame.Start();

        // 秒级心跳：时钟秒数跳、pet 待机动画需要周期性标脏。帧循环已改成脏标记驱动，
        // 没有这个心跳，静止的时钟视图会停在第一次绘制的那一秒。
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => _canvas.MarkDirty();
        _tick.Start();

        // 悬停开/关防抖（150ms 开 / 350ms 关）：同一个计时器反复复用
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(HoverOpenMs) };
        _hoverTimer.Tick += OnHoverTick;

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
            if (_canvas.DraggingVcp != 0 && e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed)
                _canvas.HandleScreenDrag(pos);
            // pet 视线/互动
            var bc = _canvas.BodyRect.Center;
            _canvas.LookPoint = new Point(pos.X - bc.X, pos.Y - bc.Y);
            // 悬停微长大 + 触觉反馈（NotchBar 同款）：指针进入胶囊附近即触发
            bool near = !_expanded && _canvas.BodyRect.Inflate(new Thickness(24, 18)).Contains(pos);
            _canvas.PointerNear = near;
            ApplyHoverGrow(near);
        };
        // 松开/移出不需要强制 flush：实时写入是"合并最新目标 + 写完自动续排"，最后一次一定会落地
        _canvas.PointerReleased += (_, _) => _canvas.EndScreenDrag();
        _canvas.PointerExited += (_, _) =>
        {
            ArmHover(open: false); _canvas.PointerNear = false;
            ApplyHoverGrow(false);
            _canvas.EndScreenDrag();
        };
        _canvas.PointerPressed += (_, e) =>
        {
            var pos0 = e.GetPosition(_canvas);
            // 右键菜单开着时：先判菜单项，点到哪项执行哪项；点外面就关掉。
            if (_canvas.ClipMenuVisible)
            {
                var hit = _canvas.HitClipMenu(pos0);
                if (hit is not null)
                {
                    _canvas.CloseClipMenu();
                    _canvas.RaiseClipMenuAction(hit);
                    e.Handled = true;
                    return;
                }
                _canvas.CloseClipMenu();
                e.Handled = true;
                return;
            }
            if (_canvas.HandleTabClick(pos0))
            {
                e.Handled = true;
                // 采集器启停已由 PageChanged → UpdateDemand 统一处理
                return;
            }
            if (_canvas.HandleScreenPress(pos0)) { e.Handled = true; return; }
            if (_canvas.HandleClipPress(pos0)) { e.Handled = true; return; }

            // 点击胶囊本体 = 展开/收起切换。
            // 「仅点击展开」模式（never）必须靠它；hover 模式下也保留（点一下比悬停更明确）。
            var pos = e.GetPosition(_canvas);
            if (_canvas.BodyRect.Contains(pos) && !_expanded)
            {
                SetExpanded(true);
                _hoverTimer.Stop();
                e.Handled = true;
                return;
            }
            if (!_expanded && _canvas.View == "pet") _canvas.Pet.React("surprise");
        };
        // 右键：在剪贴板条目上弹出自定义菜单（BrimDeck 同款交互）
        _canvas.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(_canvas).Properties.IsRightButtonPressed) return;
            if (_canvas.Page != "clips" || !_expanded) return;
            var pos = e.GetPosition(_canvas);
            foreach (var (vis, rect) in _canvas.ClipRows)
            {
                if (!rect.Contains(pos)) continue;
                _canvas.OpenClipMenu(vis, pos);
                e.Handled = true;
                return;
            }
        };
        _canvas.SliderDragged += OnSliderDragged;
        _canvas.FollowToggled += OnFollowToggled;
        _canvas.PresetAction += OnPresetAction;
        _clips.ItemsChanged += () => Dispatcher.UIThread.Post(() =>
        {
            _canvas.Clips = _clips.Items;   // 收起态实时显示剪贴板条数
            _canvas.MarkDirty();
        });
        _canvas.RetryRequested += OnRetryRequested;
        _canvas.MuteToggled += OnMuteToggled;
        _canvas.SettingsRowClicked += _ => UpdateExpandHeight();   // 展开/收起"设置与预设"后面板高度要跟着变
        // 剪贴板条数变了 → 面板高度重算（高度按条数算，不再恒按 20 条满额）。
        // UpdateExpandHeight 内部有 `if (!_expanded) return`，收起态下不会误触发弹簧。
        _canvas.ClipsCountChanged += () => UpdateExpandHeight();
        _canvas.ProcessSortToggled += () => { _canvas.ProcessListByMemory = !_canvas.ProcessListByMemory; _canvas.MarkDirty(); };
        // 颜色设置：RGB 增益拖动 —— 走与亮度相同的「目标合并 + 串行写入」通道。
        // 不能直接 Task.Run 立即写：DDC/CI over I2C 是单总线，读操作会打断写操作
        // （实测：屏幕页 3 秒轮询时写入被显示器拒绝，返回上一次残留值）。
        // 合并 + 冷却保证同一时刻只有一个写事务在飞，轮询也会因"有待写目标"而暂停。
        _canvas.RgbDragged += (id, vcp, pct) =>
        {
            var scr = _canvas.Screens.FirstOrDefault(s => s.Id == id);
            if (scr is null) return;
            // 乐观更新（拖动要跟手）
            _canvas.Screens = _canvas.Screens.Select(s => s.Id == id ? s with
            {
                Red = vcp == DisplayControl.VcpRed ? pct : s.Red,
                Green = vcp == DisplayControl.VcpGreen ? pct : s.Green,
                Blue = vcp == DisplayControl.VcpBlue ? pct : s.Blue,
            } : s).ToList();
            _canvas.MarkDirty();
            QueueLiveTarget(id, vcp, pct, builtin: false);
        };
        RebuildColorTempOptions();



        // 「自动色温」：开 = 让显示器用自己校准的标准白点（预设回「用户」+ RGB 归 100/100/100）；
        // 关 = 下面的 RGB / 色温档由用户手动调。
        _canvas.AutoColorToggled += (id, on) =>
        {
            var scr = _canvas.Screens.FirstOrDefault(s => s.Id == id);
            if (scr is null) return;
            string u = scr.Uuid; uint sid = id;
            _canvas.ScreenAutoColorState[sid] = on;
            _canvas.MarkDirty();
            RunBg(() =>
            {
                bool ok = true;
                if (on)
                {
                    // 显示器标准白点：色温预设回「用户」，RGB 增益全部回 100
                    ok &= DisplayControl.SetPercentEx(u, sid, DisplayControl.VcpColorTempPreset, DisplayControl.ColorTempUserPreset).Ok;
                    foreach (var vcp in new[] { DisplayControl.VcpRed, DisplayControl.VcpGreen, DisplayControl.VcpBlue })
                        ok &= DisplayControl.SetPercentEx(u, sid, vcp, 100).Ok;
                }
                Dispatcher.UIThread.Post(() =>
                {
                    if (ok)
                    {
                        _canvas.Screens = _canvas.Screens.Select(s => s.Id == sid ? s with
                        {
                            Red = 100, Green = 100, Blue = 100,
                            ColorTempPreset = on ? DisplayControl.ColorTempUserPreset : s.ColorTempPreset,
                            Message = on ? "已切回显示器标准白点（自动色温）" : s.Message,
                        } : s).ToList();
                        UpdateSetting(st => st.AutoColorByScreen = WriteAutoColor(st.AutoColorByScreen, u, on));
                    }
                    else
                    {
                        _canvas.ScreenAutoColorState[sid] = !on;   // 失败就回滚开关
                        UpdateSetting(st => st.AutoColorByScreen = WriteAutoColor(st.AutoColorByScreen, u, !on));
                        _canvas.Screens = _canvas.Screens.Select(s => s.Id == sid ? s with
                        { Message = "切回标准白点失败，请检查显示器的 DDC/CI 设置" } : s).ToList();
                    }
                    _canvas.MarkDirty();
                });
            });
        };

        // 选择显示器的色温档位（VCP 20）
        _canvas.ColorTempPresetSelected += (id, preset) =>
        {
            var scr = _canvas.Screens.FirstOrDefault(s => s.Id == id);
            if (scr is null) return;
            string u = scr.Uuid; uint sid = id;
            RunBg(() =>
            {
                var res = DisplayControl.SetPercentEx(u, sid, DisplayControl.VcpColorTempPreset, preset);
                Dispatcher.UIThread.Post(() =>
                {
                    if (res.Ok)
                    {
                        _canvas.Screens = _canvas.Screens.Select(s => s.Id == sid ? s with
                        {
                            ColorTempPreset = preset,
                            Message = preset == DisplayControl.ColorTempUserPreset
                                ? "已切到「用户」色温（可用下方 RGB 手动微调）"
                                : $"已切到显示器的色温档位（预设 {preset}）",
                        } : s).ToList();
                        // 非「用户」档时，显示器接管白点 → 自动色温关（同步落盘）
                        if (preset != DisplayControl.ColorTempUserPreset)
                        {
                            _canvas.ScreenAutoColorState[sid] = false;
                            UpdateSetting(st => st.AutoColorByScreen = WriteAutoColor(st.AutoColorByScreen, u, false));
                        }
                    }
                    else
                    {
                        _canvas.Screens = _canvas.Screens.Select(s => s.Id == sid ? s with
                        { Message = $"该显示器不支持色温档位 {preset}" } : s).ToList();
                    }
                    _canvas.MarkDirty();
                });
            });
        };

        _canvas.ColorResetClicked += id =>
        {
            var scr = _canvas.Screens.FirstOrDefault(s => s.Id == id);
            if (scr is null) return;
            // 走统一写入通道（与拖动同一条路径）：三个通道各排一个目标，串行写，
            // 避免和屏幕页 DDC 轮询抢总线。
            _canvas.Screens = _canvas.Screens.Select(s => s.Id == id ? s with
            {
                Red = 100, Green = 100, Blue = 100,
                Message = "正在重置颜色…",
            } : s).ToList();
            _canvas.MarkDirty();
            foreach (var vcp in new[] { DisplayControl.VcpRed, DisplayControl.VcpGreen, DisplayControl.VcpBlue })
                QueueLiveTarget(id, vcp, 100, builtin: false);
        };
        _canvas.RangeSelected += (id, lo, hi) =>
        {
            var scr = _canvas.Screens.FirstOrDefault(s => s.Id == id);
            if (scr is null) return;
            string u = scr.Uuid;
            RunBg(() =>
            {
                if (LunarSettings.SetRange(u, lo, hi)) DisplayControl.SetBrightnessRange(u, lo, hi);
                Dispatcher.UIThread.Post(() =>
                {
                    _canvas.ScreenSettings = LunarSettings.Read();
                    SyncSettingsToKernel(_canvas.ScreenSettings);
                    _canvas.MarkDirty();
                });
            });
        };
        _canvas.BuiltinToggleClicked += OnBuiltinToggle;
        _canvas.PageChanged += page =>
        {
            CancelLiveWrites(); UpdateDemand(); UpdateExpandHeight();
            UpdateSetting(s => s.LastPage = page);   // 页签点击也落盘，重启后回到同一页
        };
        // 屏幕页高度依赖 Screens 内容，异步补读后再算一次
        // 点一条剪贴板记录：图片 → 弹出大图预览（可直接看/存文件）；文字 → 写回剪贴板供 ⌘V
        _canvas.ClipPicked += async i =>
        {
            var items = _canvas.Clips;
            if (i < 0 || i >= items.Count) return;
            var item = items[i];
            if (item.Kind == ClipKind.Image && item.Image is { } png)
            {
                try
                {
                    var preview = new ImagePreviewWindow(png, item.Preview);
                    preview.Show(this);   // 非模态：用户可留着预览继续操作岛
                }
                catch (Exception ex) { Console.Error.WriteLine($"[clipboard] preview open: {ex.Message}"); }
            }
            else await _clips.PickAsync(this, item);
            SetExpanded(false);
        };
        // 删除单条：复制了密码/敏感信息后立刻清掉（安全刚需）
        _canvas.ClipDeleted += i =>
        {
            var items = _canvas.Clips;
            if (i < 0 || i >= items.Count) return;
            var it = items[i];
            _clips.Remove(it.Text, it.At);
            _canvas.Clips = _clips.Items;
            _canvas.MarkDirty();
            UpdateExpandHeight();
        };
        // 右键菜单动作（第五十九轮）：粘贴 / 只复制 / 标记常驻 / 查看详情 / 删除
        _canvas.ClipMenuAction += async act =>
        {
            var items = _canvas.Clips;
            int i = _canvas.ClipMenuTarget;
            if (i < 0 || i >= items.Count) return;
            var it = items[i];
            switch (act)
            {
                case "paste":
                    // 写回系统剪贴板（用户再 ⌘V 到目标应用）——「粘贴到当前窗口」
                    // 在无焦点浮窗里做不到真粘贴，只能写回剪贴板，这是能做到的最接近语义。
                    await _clips.PickAsync(this, it);
                    SetExpanded(false);
                    break;
                case "copy":
                    await _clips.PickAsync(this, it);
                    break;
                case "pin":
                    _clips.TogglePin(it.Text, it.At);
                    _canvas.Clips = _clips.Items;
                    _canvas.MarkDirty();
                    break;
                case "detail":
                    ShowClipDetail(it);
                    break;
                case "delete":
                    _clips.Remove(it.Text, it.At);
                    _canvas.Clips = _clips.Items;
                    _canvas.MarkDirty();
                    UpdateExpandHeight();
                    break;
            }
        };
        _canvas.ClipCleared += () =>
        {
            _clips.ClearAll();
            _canvas.Clips = _clips.Items;
            _canvas.MarkDirty();
            UpdateExpandHeight();
        };
        _canvas.ClipExportAll += ExportClipsToDownloads;
        _canvas.BuiltinKeepClicked += () => { DisplayControl.KeepBuiltinOff(); _canvas.MarkDirty(); };

        // 内建屏关闭后：每秒刷新倒计时（守护进程 15s 超时前需点"保持关闭"）
        _builtinCountdownTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _builtinCountdownTimer.Tick += (_, _) =>
        {
            if (!DisplayControl.IsBuiltinOff)
            {
                // 守护进程已退出（15s 超时自动恢复 / 崩溃）→ 同步 UI 并停表
                if (_canvas.BuiltinOff) { _canvas.BuiltinOff = false; _canvas.BuiltinOffCountdown = null; _canvas.MarkDirty(); }
                _builtinCountdownTimer.Stop();
                return;
            }
            _canvas.BuiltinOff = true;
            _canvas.BuiltinOffCountdown = DisplayControl.BuiltinOffCountdown;
            _canvas.MarkDirty();
        };
        // 不自动启动：内建屏关闭后才 Start（见 OnBuiltinToggle）

        // 自动跟随引擎（移植月光）：每 2 秒读内建屏亮度，外接屏分步逼近。
        // 用户正在拖或还有待写目标的屏，本轮跳过（手动优先，对齐参考版 pollAutomaticBrightness 的 liveTargets 判断）。
        _autoFollow = new AutoFollowEngine
        {
            IsBusy = uuid =>
            {
                var scr = _canvas.Screens.FirstOrDefault(s => s.Uuid == uuid);
                if (scr is null) return false;
                return (_canvas.DraggingVcp != 0 && _canvas.DraggingScreen == scr.Id)
                    || HasLiveTarget(scr.Id, DisplayControl.VcpBrightness);
            },
        };

        // 屏幕页数据：3 秒刷新（只在展开且在屏幕页时读，DDC 读取较慢）
        _screensTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(3) };
        _screensTimer.Tick += (_, _) => RefreshScreens();
        // 启停由 UpdateDemand 统一管（只在屏幕页可见时读 DDC）

        // 天气 UI 轮询计时器 —— **必须在这里创建，不能等到下面天气逻辑那段**。
        // UpdateDemand() 会启停它（时钟视图才轮询），而 UpdateDemand 在 LoadAndApplySettings()
        // 里就会被调用（常驻展开模式 → SetExpanded(true)），那要早于后面的天气代码。
        // 之前它创建在 583 行左右 → 构造期那次 UpdateDemand 必然抛 NullReferenceException，
        // 被 SetExpanded 的 catch 静默吞掉：每次启动都错一次，且 UpdateDemand 后半段
        // （屏幕页 / 自动跟随 / 天气的启停）在构造期全部没执行。Tick 订阅见下方天气逻辑。
        _weatherUiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };

        // 海岛假日·自动日夜：到点重算主题，并排程下一次
        _islandSwitchTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _islandSwitchTimer.Tick += (_, _) => { _islandSwitchTimer.Stop(); ApplyTheme(); ScheduleIslandSwitch(); };
        // 读回上次保存的主题
        try
        {
            var saved = Island.App.Services.Settings.Load().Theme;
            if (!string.IsNullOrEmpty(saved) && (saved == "IslandAuto" || Palette.ByName(saved) is not null
                    || saved is "IslandDay" or "IslandNight")) SetTheme(saved);
        }
        catch { /* 读不到用默认 Dark */ }

        // 收起样式：读回上次选的（dash / combo / clock / pet）。
        // CompactViewPinned=true 表示用户手动固定过 → 恢复后不再启动 pet⇄combo 自动轮换，
        // 否则「选了小形象，重启后又自己变回信息胶囊」（用户实测反馈）。
        try
        {
            var st = Island.App.Services.Settings.Load();
            var cv = st.CompactView;
            _viewPinned = st.CompactViewPinned;
            // 自动轮换与小形象款式也要**先**读出来：SetView 内部会 StartPetCycle，
            // 那时 _autoCycle 必须是设置里的真值（默认 true 会让「关了循环」的用户启动就轮起来）。
            _autoCycle = st.AutoCycleViews;
            _canvas.Pet.Style = st.PetStyle;
            Console.Error.WriteLine($"[startup] view={cv} pinned={_viewPinned} page={st.LastPage} cycle={_autoCycle} pet={_canvas.Pet.Style}");
            // 上次展开页也恢复（用户希望重启后回到同一页）。
            // 必须在 LoadAndApplySettings()（里面会按展开方式 SetExpanded(true)）**之前**恢复：
            // 顺序反了的话，面板先按默认的 monitor 页展开，Page 后改不会触发内容切换
            // —— 表现为「设置了常驻展开+剪贴板页，启动后却是监控页」。
            if (st.LastPage is "monitor" or "clips" or "screens" or "devices") _canvas.Page = st.LastPage;
            if (cv is "dash" or "combo" or "clock" or "pet") SetView(cv);
        }
        catch { /* 读不到用默认 */ }

        // 其余设置项（置顶 / 穿透 / 展开方式 / 收起样式）——Settings 里本来就有，
        // 但 Mac 版此前从未实现，这里接上并真正生效。
        LoadAndApplySettings();

        // 自动跟随的启停交给 UpdateDemand 统一管（只在屏幕页可见时跑）

        // 天气：Open-Meteo（30 分钟刷新，内部缓存节流；城市随 settings.json 热更新）
        _weather.Settings = Island.App.Services.Settings.Load();
        _canvas.Weather = _weather.Get();   // 立即拉一次（内部后台线程）
        _weatherTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMinutes(5) };
        _weatherTimer.Tick += async (_, _) =>
        {
            // Settings.Load 读盘+解析 JSON，放后台，别占 UI 线程（哪怕 5 分钟才一次）
            var s = await System.Threading.Tasks.Task.Run(() => Island.App.Services.Settings.Load());
            if (s.City != _weather.Settings.City) { _weather.Settings = s; _weather.Invalidate(); }
            _canvas.Weather = _weather.Get();
        };
        _weatherTimer.Start();

        // 天气 UI 轮询（30 秒）：首次拉取是异步的，_weather.Get() 立刻返回 null，
        // 不轮询的话时钟胶囊要等满 5 分钟才显示天气。
        // 天气 UI 轮询：首次拉取是异步的（IP 定位 + 天气查询两次请求），_weather.Get() 立刻返回 null，
        // 不轮询的话时钟胶囊要等满 5 分钟才显示天气。
        // 开头 40 秒内每 4 秒查一次（尽快显示），之后每 30 秒一次。
        int weatherPolls = 0;
        // 计时器本身已在上方 _screensTimer 旁边创建（UpdateDemand 需要它早于 LoadAndApplySettings 存在）；
        // 这里只订阅 Tick。开头 40 秒内每 4 秒查一次（尽快显示），之后每 30 秒一次。
        _weatherUiTimer.Tick += (_, _) =>
        {
            var info = _weather.Get();
            if (info is null)
            {
                if (++weatherPolls >= 10) _weatherUiTimer.Interval = TimeSpan.FromSeconds(30);   // 10 次后放慢
                return;
            }
            bool changed = _canvas.Weather is null;
            _canvas.Weather = info;   // setter 自动标脏
            _weatherUiTimer.Interval = TimeSpan.FromSeconds(30);
            // 天气内容变了 → 胶囊宽度要跟着变（时间 + 日期 + 天气，实测宽度）
            if (changed && !_expanded && _canvas.View == "clock")
            {
                var s = ViewSize("clock");
                _bodyW.To(s.W);
            }
        };
        // 启停由 UpdateDemand 统一管（只在时钟视图时轮询天气）

        // 剪贴板历史（跨平台轮询，1.5s；内容变化时刷新面板）
        // 先按保留策略读回历史（保留 N 天时才有档），再开始轮询。
        try
        {
            _clips.Load(_clipRetention);
            _clips.ApplyRetention(_clipRetention);
            _canvas.Clips = _clips.Items;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[clipboard] restore: {ex.Message}"); }
        _clips.Start(this);
        _clipsTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(2) };
        _clipsTimer.Tick += (_, _) =>
        {
            if (_expanded && _canvas.Page == "clips")
            {
                _canvas.Clips = _clips.Items;
                _canvas.MarkDirty();
            }
        };
        // 保留期清理 + 落盘（每 60 秒一次）：过期条目自动清，磁盘上的档同步更新。
        var clipSaveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
        clipSaveTimer.Tick += (_, _) =>
        {
            if (ClipboardHistory.RetentionDays(_clipRetention) <= 0) return;
            try
            {
                _clips.ApplyRetention(_clipRetention);
                _clips.Save(_clipRetention);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[clipboard] autosave: {ex.Message}"); }
        };
        clipSaveTimer.Start();
        _clipSaveTimer = clipSaveTimer;
        // 启停由 UpdateDemand 统一管（只在剪贴板页可见时刷新列表）

        UpdateDemand();   // 初始化各采集器的启停状态
        SetupTray();
    }

    /// <summary>主屏顶部中央（Position 是物理像素，Screen.Bounds 是逻辑像素，两者比例由系统处理）。</summary>
    /// <summary>画布宽度变了 → 调整窗口尺寸（透明窗口，只影响居中和热区计算）。</summary>
    /// <summary>
    /// 后台任务的安全包装：异常捕获并记日志，不让未观察的 Task 异常炸掉进程。
    /// 之前 12 处 Task.Run 全裸奔——DDC 子进程失败 / plist 读写异常时进程直接消失，
    /// 用户只看到"应用没了"，现场只在日志里。
    /// </summary>
    private static void RunBg(Action act, [System.Runtime.CompilerServices.CallerMemberName] string from = "")
        => System.Threading.Tasks.Task.Run(() =>
        {
            try { act(); }
            catch (Exception ex) { Console.Error.WriteLine($"[bg:{from}] {ex.GetType().Name}: {ex.Message}"); }
        });

    private void ResizeCanvas()
    {
        Width = WinW;
        Height = WinH;
        // Avalonia 的 Width 对**已显示**的窗口不生效 → 胶囊宽过窗口时两端被裁平。
        // 直接走 AppKit 改 NSWindow 的 frame。
        try
        {
            if (TryGetNativeWindowHandle(this, out var h))
            {
                var scr = Screens.Primary;
                if (scr is not null)
                {
                    double px = scr.Bounds.X + (scr.Bounds.Width - WinW) / 2;
                    MacInterop.SetWindowSize(h, px, scr.Bounds.Y, WinW, WinH);
                    // 改 frame 后再确认一次层级：AppKit 偶尔会重置 collectionBehavior，
                    // 导致胶囊在全屏应用后面消失。幂等，代价极小。
                    if (_alwaysOnTop)
                        MacInterop.SetWindowLevel(h, MacInterop.NsStatusWindowLevel, MacInterop.CollectionAlwaysOnTop);
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[mac] resize: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void PositionTopCenter()
    {
        var scr = Screens.Primary;
        if (scr is null) return;
        // Screen.Bounds 已是逻辑像素，窗口的 Width/Height 也是逻辑像素（Avalonia 负责缩放），
        // 这里不能再乘 Scaling —— 早期版本乘了导致 Retina 下窗口偏到屏幕左边。
        double pw = WinW;
        var pos = new PixelPoint(
            scr.Bounds.X + (int)((scr.Bounds.Width - pw) / 2),
            scr.Bounds.Y);
        Position = pos;
    }

    // ---------- 悬停展开（开 150ms / 关 350ms 防抖） ----------
    private void ArmHover(bool open)
    {
        // 展开方式分流（设置 › 行为 › 展开方式）：
        //   hover  = 悬停展开（默认）
        //   always = 常驻展开，悬停不参与
        //   never  = 只允许点开，悬停不展开也不收起
        if (_expandMode == "always") return;
        if (_expandMode == "never")
        {
            // 只在"从未展开"时阻止悬停；已展开则允许悬停收起（否则关不掉）
            if (!open || _expanded) { if (!open) return; }
            else return;
        }
        // 鼠标在窗口内每移动一下都会进来；意图没变就别重排定时器（Stop+改 Interval+Start 每次都 RescheduleTimers）
        if (_hoverOpen == open && _hoverTimer.IsEnabled) return;
        _hoverOpen = open;
        _hoverTimer.Stop();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(open ? HoverOpenMs : HoverCloseMs);
        _hoverTimer.Start();
    }

    private void OnHoverTick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        SetExpanded(_hoverOpen);
    }

    // ---------- 弹簧参数（借鉴 NotchBar 的参数体系：不同动作不同手感） ----------
    // 展开：阻尼 0.76 → 有轻微回弹，"弹开"的感觉
    // 收起：阻尼 0.95 → 几乎不回弹，避免缩回去还在抖
    // 悬停微动：阻尼 0.68 → 明显回弹，"活了一下"
    private const double SpringOpenRatio = 0.76;
    private const double SpringCloseRatio = 0.95;
    private const double SpringHoverRatio = 0.68;
    private const double HoverGrowW = 12, HoverGrowH = 3;

    /// <summary>悬停时胶囊轻微长大（NotchBar 同款：宽 +12 / 高 +3），并给一次触觉反馈。</summary>
    private void ApplyHoverGrow(bool hovering)
    {
        if (_expanded) return;                 // 展开态不参与
        bool was = _hoverGrow;
        if (was == hovering) return;
        _hoverGrow = hovering;
        if (!_canvas.View.Equals("pet", StringComparison.Ordinal))
        {
            var s = ViewSize(_canvas.View);
            _bodyW.To(s.W + (hovering ? HoverGrowW : 0), null, SpringHoverRatio);
            _bodyH.To(s.H + (hovering ? HoverGrowH : 0), null, SpringHoverRatio);
        }
        // 触觉反馈：只在"进入"时给一次，离开不给（NotchBar 同款 .alignment）
        if (hovering) Haptics.Alignment();
    }

    private bool _hoverGrow;

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        _canvas.IsExpanded = expanded;
        // 展开/收起要重新设采样需求：展开监控页才需要进程榜 / GPU / 频率，
        // 收起后就不该继续采（按需采样，省 ioreg 子进程和 DDC 总线占用）。
        // 之前漏了这里 → 展开监控页时需求还是 Collapsed（无 Processes）→ 应用榜永远是空的。
        // 【顺序】UpdateDemand() 放在宽度目标设置**之后**。
        // 历史事故：_weatherUiTimer 原先创建得比这里晚，UpdateDemand 启停它时抛
        // NullReferenceException，把后面的 _bodyW.To(ExpandW) 一起带走 → 弹簧目标停在
        // 收起宽度、面板画成小胶囊（"常驻展开不展开"）。该字段现已挪到 _screensTimer
        // 旁边创建，异常根因已除；下面的 try/catch 作为兜底保留 ——
        // 采样需求切换失败不该连累展开/收起本身。
        if (expanded)
        {
            _bodyW.To(ExpandW, null, SpringOpenRatio);
            _bodyH.To(ExpandHeight, null, SpringOpenRatio);
        }
        else
        {
            var s = ViewSize(_canvas.View);
            _bodyW.To(s.W + (_hoverGrow ? HoverGrowW : 0), null, SpringCloseRatio);
            _bodyH.To(s.H + (_hoverGrow ? HoverGrowH : 0), null, SpringCloseRatio);
            StartPetCycle();   // 收起后恢复轮换计时
        }
        // 采样需求切换失败不该连累展开/收起本身 —— 记日志继续。
        try { UpdateDemand(); }
        catch (Exception ex) { Console.Error.WriteLine($"[demand] set failed: {ex.GetType().Name}: {ex.Message}"); }
        // 收起面板：取消待写目标（对齐参考版 cancelLiveBrightness；已发出的那一次写入可能完成）
        if (!expanded) CancelLiveWrites();
    }

    /// <summary>切页后展开高度要跟着变（不同页内容高度不同）。</summary>
    private void UpdateExpandHeight()
    {
        if (!_expanded) return;
        _bodyH.To(ExpandHeight, null, SpringOpenRatio);
    }

    /// <summary>
    /// 按需采样（借鉴 BrimDeck ExpandedDemand + NotchBar 的 CombineLatest 采集开关）：
    /// UI 当前画什么才读什么、什么不可见就完全不采集。
    ///
    /// - 收起态胶囊：CPU/内存/网速/磁盘/温度/电量（Collapsed 集）
    /// - 展开到监控页：+ 进程榜 / GPU / CPU 频率
    /// - 剪贴板页：只监听剪贴板，不读系统监控的进程/GPU
    /// - 屏幕页：只读 DDC，不跑自动跟随引擎（它每 2 秒读一次设置 + 起 DDC 子进程）
    /// - 天气轮询：只在时钟视图时跑
    /// </summary>
    /// <summary>
    /// 重建每屏的色温档位列表。显示器厂商的预设索引不统一（实测 DELL U3225QE 有效的是
    /// 1/4/5/6/8/9/11/12，2/3/7/10 会被拒绝；12 = 用户自定义），所以给一组候选；
    /// 写失败会如实提示「该显示器不支持此档」，不会假装成功。
    /// </summary>
    private void RebuildColorTempOptions()
    {
        _canvas.ScreenColorTempValues.Clear();
        foreach (var sc in _canvas.Screens)
        {
            if (sc.Builtin || sc.ColorTempPreset is null) continue;
            int cur = sc.ColorTempPreset.Value;
            var list = new List<int> { 1, 4, 5, 6, 8, 9, 11, 12 };
            if (!list.Contains(cur)) list.Insert(0, cur);   // 读到的是别的索引时也显示出来
            _canvas.ScreenColorTempValues[sc.Id] = list.ToArray();
        }
    }

    private void UpdateDemand()
    {
        bool expanded = _expanded;
        string page = _canvas.Page;
        string view = _canvas.View;

        var d = expanded && page == "monitor"
            ? Island.Core.Monitoring.SamplerDemand.MonitorPage
            // 信息胶囊（combo）和仪表盘（dash）都画 GPU → 需要 GPU 采样（ioreg 子进程，只在该视图时开）
            : !expanded && view == "combo"
            ? Island.Core.Monitoring.SamplerDemand.Combo
            : !expanded && view == "dash"
            ? Island.Core.Monitoring.SamplerDemand.Dash
            : Island.Core.Monitoring.SamplerDemand.Collapsed;
        _sampler.SetDemand(d);

        // 应用榜图标：监控页可见时后台取一次（app-icon 探针）。
        // 取图标要起子进程（~100ms），绝不能在 UI 线程同步等——放线程池，结果下一帧生效。
        if (expanded && page == "monitor")
        {
            _appIconTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _appIconTimer.Tick -= (_, _) => FlushAppIcons();
            _appIconTimer.Tick += (_, _) => FlushAppIcons();
            _appIconTimer.Start();
            FlushAppIcons();
        }
        else _appIconTimer?.Stop();

        // 设备电量：设备页可见时读；收起态的 dash/combo 也要显示电量环，
        // 所以这两个视图下也保持低频采集（5 秒一次，探针 ~100ms）。
        bool wantDevices = (expanded && page == "devices") || (!expanded && view is "dash" or "combo");
        if (wantDevices) RefreshDevices();
        else _deviceTimer?.Stop();

        // 剪贴板：**全程监听**。之前只在 clips 页 / dash 视图可见时轮询——
        // 结果在 clock/pet/combo 视图下复制的东西全部丢失（历史不完整，等切回来只剩最后一条）。
        // 轮询本身极便宜（1.5s 一次 GetTextAsync，无子进程），不值得为省它丢数据。
        _clips.Enabled = true;
        { _canvas.Clips = _clips.Items; _canvas.MarkDirty(); }

        // 屏幕页：只在可见时刷 DDC（每次要起 3-4 个子进程读 DDC，很贵）
        _screensTimer.IsEnabled = expanded && page == "screens";
        if (expanded && page == "screens") RefreshScreens();

        // 自动跟随引擎：只在屏幕页可见时跑（每 2 秒读设置 + 读 DDC）；macOS 专属
        if (OperatingSystem.IsMacOS() && expanded && page == "screens") _autoFollow?.Start();
        else _autoFollow?.Stop();

        // 天气轮询：只在时钟视图时跑
        _weatherUiTimer.IsEnabled = !expanded && view == "clock";
    }

    // ---------- 小形象 ⇄ 信息胶囊轮换 ----------
    private void StartPetCycle()
    {
        _cycleTimer.Stop();
        // 自动循环关闭 → 不轮换，停在用户手选的视图
        if (!_autoCycle) { Console.Error.WriteLine($"[cycle] StartPetCycle skipped: auto-cycle off (view={_canvas.View})"); return; }
        // 注意：这里**不再看 _viewPinned** —— 那个旧开关（用户手动选过就不自动改）
        // 会把整个循环挡掉，是「小形象⇄信息胶囊不轮换」的真正原因。
        // 循环现在只由 AutoCycleViews 一个开关决定，_viewPinned 仅用于记录手选结果。
        if (_canvas.View is not ("pet" or "combo")) return;
        _cycleTimer.Interval = TimeSpan.FromSeconds(_canvas.View == "pet" ? PetPhaseSec : ComboPhaseSec);
        _cycleTimer.Start();
        Console.Error.WriteLine($"[cycle] started: view={_canvas.View} interval={_cycleTimer.Interval.TotalSeconds}s");
    }

    private void CycleCompact()
    {
        if (_expanded) { StartPetCycle(); return; }
        // 自动循环关闭 → 不动（用户手选哪个就停在哪个）
        if (!_autoCycle)
        {
            _cycleTimer.Stop();
            Console.Error.WriteLine("[cycle] auto-change blocked (auto-cycle off)");
            return;
        }
        if (_canvas.View is not ("pet" or "combo")) { _cycleTimer.Stop(); return; }
        string autoNext = _canvas.View == "pet" ? "combo" : "pet";
        Console.Error.WriteLine($"[cycle] auto-change {_canvas.View} → {autoNext}");
        SetView(autoNext);
    }

    /// <summary>
    /// 用户主动切换收起视图：落盘 CompactView。
    /// 自动循环**开启**时：只切过去，循环继续（下一拍又切走）——
    /// 用户想「一直停在这个视图」应该去关自动轮换，而不是靠这里的隐式副作用。
    /// 自动循环**关闭**时：钉死（CompactViewPinned=true），不再被自动轮换改动。
    /// 所有入口（滚轮 / 托盘菜单 / 快捷键 D）都走这里，避免只改 UI 不保存。
    /// </summary>
    private void UserPickedView(string view)
    {
        Console.Error.WriteLine($"[cycle] UserPickedView {_canvas.View} → {view}");
        SetView(view);
        if (_autoCycle)
        {
            // 循环继续：重新按新视图的时长起计时（pet 10s / combo 30s）
            StartPetCycle();
            UpdateSetting(s => { s.CompactView = view; s.CompactViewPinned = false; });
        }
        else
        {
            _viewPinned = true;
            _cycleTimer.Stop();           // 用户明确选了，且不开自动轮换 → 钉死
            UpdateSetting(s => { s.CompactView = view; s.CompactViewPinned = true; });
        }
    }

    /// <summary>用户主动切换展开页：落盘 LastPage。</summary>
    private void UserPickedPage(string page)
    {
        _canvas.Page = page;
        UpdateDemand();
        UpdateExpandHeight();
        _canvas.MarkDirty();
        UpdateSetting(s => s.LastPage = page);
    }

    private void OnSnapshot(MonitorSnapshot s)
        => Dispatcher.UIThread.Post(() =>
        {
            _canvas.Snapshot = s;   // setter 自动标脏
            // 宽度实测的视图（dash / combo）：内容随数据变（网速位数、温度有无、表盘有无），
            // 胶囊宽度必须跟着变。之前只在切视图/悬停/展开时设宽度，**新数据到了不重量**——
            // 启动瞬间 Snapshot 还是 null，combo 被量成 192（只有时间+网速），数据到了内容变多，
            // 窗口却还是 192 → 整个右半截画到胶囊外面（2026-11-24 用户截图）。
            // 天气那条路径早就处理了同样的问题（见 _weather_Tick），这里补上监控数据这条。
            if (_expanded || _canvas.View is not ("dash" or "combo")) return;
            double want = ViewSize(_canvas.View).W;
            double cur = _bodyW.Target;
            // 容差 1.5px：避免数据小幅抖动让胶囊不停微动
            if (Math.Abs(want - cur) > 1.5) { _bodyW.To(want + (_hoverGrow ? HoverGrowW : 0)); }
        });

    private DateTime _lastFrame = DateTime.UtcNow;
    private bool _busyLast = true;

    /// <summary>
    /// 帧循环：脏标记驱动。只有弹簧在动、面板展开着且有内容变化、或有人显式标脏时才重绘。
    /// 之前每帧无条件 InvalidateVisual：静止的时钟视图也每 100ms 全量重排 50 段文本，
    /// 展开态更是每 16ms 重排 125 段——这是卡顿的主因。
    /// </summary>
    private void Frame_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastFrame).TotalSeconds, 1.0 / 240, 0.1);   // 真实 dt，不写死 1/60
        _lastFrame = now;
        _bodyW.Step(dt); _bodyH.Step(dt);

        // 窗口宽度必须 ≥ 胶囊宽度 + 余量，否则胶囊两端会被窗口裁平
        // （dash 内容宽时可到 874，窗口固定 800 时两端就被切了，看起来像"扁条"而不是胶囊）。
        {
            double need = _bodyW.Value + 24;
            // 变宽：立即跟。变窄：**等内容稳定后再收**——
            // 展开态收起时 _bodyW 是从 800 往回收的动画过程，中途收窗会让胶囊被裁。
            // 阈值 24px：小于这个差值不值得改窗口（抖动）。
            if (need > WinW && need <= WinWMax)
            {
                WinW = need;
                ResizeCanvas();
                PositionTopCenter();
            }
            else if (need < WinW - 24 && !_expanded && _bodyW.AtRest)
            {
                // 收起态且内容明显更窄 → 收窗口。多余的透明区域虽不可见，
                // 但它会吃掉底下 200+px 宽的鼠标事件（影响别的窗口交互），该收。
                // 必须等弹簧静止（AtRest）再收：展开→收起的动画过程中 _bodyW 还在从
                // 800 往回收，中途改窗口宽会把胶囊裁掉。
                WinW = Math.Max(need, 240);
                ResizeCanvas();
                PositionTopCenter();
            }
            // 高度：内容比窗口高时加高（否则底部被裁）；比窗口矮**明显多**时收缩
            // （否则从屏幕页切回监控页，底部留一大块空白）。回差 40px 防抖：
            // 收缩只在"矮得多"时发生，小幅内容变化不引起窗口忽高忽矮。
            double needH = _bodyH.Value + 16;
            if (needH > WinH && needH <= WinHMax)
            {
                WinH = needH;
                ResizeCanvas();
                PositionTopCenter();   // 高度变了要重新贴顶（否则窗口会跑到屏幕下方）
            }
            else if (WinH - needH > 40 && needH >= 600)
            {
                WinH = Math.Max(600, needH);
                ResizeCanvas();
                PositionTopCenter();
            }
            else if (WinH - needH > 40 && !_expanded && _bodyH.AtRest)
            {
                // 收起态：高度同理收缩到内容实际高度（展开态的 600+ 高度在收起时是纯透明区域）。
                // 下限 56px：胶囊 38px + 上下各留一点余量 —— 留太少会让 resize 过程
                // 误触发 PointerExited，把正悬停在胶囊上的用户判成"鼠标已离开"。
                WinH = Math.Max(needH, 56);
                ResizeCanvas();
                PositionTopCenter();
            }
        }

        bool springMoving = !_bodyW.AtRest || !_bodyH.AtRest;
        bool pet = _canvas.View == "pet";
        // 忙 = 弹簧运动中 / pet 视图有动画 / 有脏内容待画
        bool busy = springMoving || pet || _canvas.Dirty;
        // 展开态本身不是重绘理由：读数 1 秒才变一次，文本/滑块变化时 OnSnapshot/事件会标脏
        // 只在 busy 翻转时改 Interval：DispatcherTimer.Interval setter 每次都会 RescheduleTimers
        if (busy != _busyLast)
        {
            _busyLast = busy;
            _frame.Interval = busy ? TimeSpan.FromMilliseconds(16) : TimeSpan.FromMilliseconds(250);
        }
        _canvas.BodyRect = IslandCanvas.SafeRect((WinW - _bodyW.Value) / 2, 0, _bodyW.Value, _bodyH.Value);
        if (busy) { _canvas.Dirty = false; _canvas.InvalidateVisual(); }
    }

    private (double W, double H) ViewSize(string view)
    {
        var size = ViewSizeRaw(view);
        // 胶囊不能比画布宽，否则两端被裁。撞到上限说明某个视图的内容超预期——记日志别静默。
        // 画布不够就加宽（而不是夹住）——夹住会把内容裁掉，那正是要避免的。
        // 超出绝对上限才夹（说明某处算错了，宁可裁也不能让窗口超出屏幕）。
        if (size.W > WinW - 8)
        {
            double want = Math.Min(size.W + 8, WinWMax);
            if (want > WinW) { WinW = want; ResizeCanvas(); PositionTopCenter(); }
            if (size.W > WinWMax - 8)
            {
                Console.Error.WriteLine($"[layout] 视图 {view} 需要宽 {size.W:F0}，超过绝对上限 {WinWMax}，已夹住");
                size = (WinWMax - 8, size.H);
            }
        }
        return size;
    }

    private (double W, double H) ViewSizeRaw(string view)
    {
        // 第五十七轮：左右装饰带已移除，胶囊宽度 = 内容实测宽度（不再有额外装饰宽度）
        return view switch
        {
            // 时钟胶囊：时间 + 日期 + 天气。宽度按实际内容实测，避免固定宽度留大片空白
            // 第五十八轮压缩：combo 高度 44→38（内容实测：22px 环 + 17px 数字，38px 已够用）
            "clock" => (_canvas.MeasureClockWidth(), 46),
            "pet" => (110, 44),
            // 信息胶囊：时间 + 4 个表盘 + 3 个温度 + 网速。宽度实测（原来固定 440，加了读数会溢出）
            "combo" => (_canvas.MeasureComboWidth(), 38),
            _ => (_canvas.MeasureDashWidth(), 46),   // dash：核心柱 + 温度 + 占用 + 电池 + 网速（实测宽度）
        };
    }

    /// <summary>装饰开关切换后重测收起态宽度（装饰带改变胶囊总宽）。</summary>
    private void RefreshCollapsedWidth()
    {
        if (_expanded) return;
        var s = ViewSize(_canvas.View);
        _bodyW.To(s.W + (_hoverGrow ? HoverGrowW : 0), null, SpringHoverRatio); 
        _canvas.MarkDirty();
    }

    private static readonly string[] Views = { "dash", "clock", "pet", "combo" };

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // 展开态：剪贴板页用滚轮滚动列表（展开时本来就不切视图，滚轮闲着也是闲着）
        if (_expanded)
        {
            if (_canvas.Page == "clips" && _canvas.ScrollClips(e.Delta.Y * 40))
                e.Handled = true;
            return;
        }
        int i = Array.IndexOf(Views, _canvas.View);
        i = (i + (e.Delta.Y > 0 ? -1 : 1) + Views.Length) % Views.Length;   // Avalonia: Delta 是 Vector
        UserPickedView(Views[i]);
    }

    public void SetView(string view)
    {
        _canvas.View = view;
        if (!_expanded)
        {
            var s = ViewSize(view);
            _bodyW.To(s.W + (_hoverGrow ? HoverGrowW : 0), null, SpringHoverRatio); 
            _bodyH.To(s.H + (_hoverGrow ? HoverGrowH : 0), null, SpringHoverRatio);
        }
        _canvas.MarkDirty();
        StartPetCycle();
        UpdateDemand();   // 视图变了 → 天气轮询等采集需求要跟着变
    }

    /// <summary>按名称切主题（托盘菜单）。</summary>
    /// <summary>当前主题名（"IslandAuto" = 海岛假日按本地时间日夜自动切换）。</summary>
    private string _themeName = "Dark";

    // ---------- 设置（Mac 端落地：Settings 类里已有、但 Mac 版此前没实现的那些项） ----------
    /// <summary>展开方式：hover=悬停展开 / always=常驻展开 / never=只能点开。</summary>
    private string _expandMode = "hover";
    /// <summary>常驻置顶。</summary>
    private bool _alwaysOnTop = true;
    /// <summary>鼠标穿透（点击穿过面板，只能用快捷键展开）。</summary>
    private bool _clickThrough = false;
    /// <summary>全屏时自动隐藏。</summary>
    private bool _hideFullscreen = true;
    /// <summary>前台窗口最大化时缩成指示条。</summary>
    private bool _indicatorOnMaximize = true;

    private readonly HotKeyClient _hotKeys = new();

    /// <summary>
    /// 注册全局快捷键（对齐 BrimDeck 的「展开或隐藏面板 / 切换收起样式 / 打开指定页面」）。
    /// 默认：⌘⌥M 展开或隐藏、⌘⌥D 切换收起样式、⌘⌥S 依次切换展开页。
    /// 快捷键被别的应用占用时如实提示到日志，不静默失败。
    /// </summary>
    private void SetupGlobalHotKeys()
    {
        var bindings = new List<(char Key, bool Cmd, bool Shift, bool Alt, Action Act)>();
        void Add(char key, Action act, string desc)
        {
            bindings.Add((key, true, false, true, act));
            Console.Error.WriteLine($"[hotkey] ⌘⌥{key} → {desc}");
        }
        Add('M', () => SetExpanded(!_expanded), "展开/隐藏面板");
        Add('D', () =>
        {
            // 收起样式轮换：仪表盘 → 信息胶囊 → 时钟 → 小形象
            var order = new[] { "dash", "combo", "clock", "pet" };
            int i = Array.IndexOf(order, _canvas.View);
            UserPickedView(order[(i + 1) % order.Length]);
        }, "切换收起样式");
        Add('S', () =>
        {
            // 展开页轮换：监控 → 剪贴板 → 屏幕 → 设备
            var pages = new[] { "monitor", "clips", "screens", "devices" };
            int i = Array.IndexOf(pages, _canvas.Page);
            UserPickedPage(pages[(i + 1) % pages.Length]);
        }, "切换展开页");

        // 交给 Swift 探针注册（Carbon ABI 在 C# 里极易踩坑，见 HotKeyClient 的说明）
        _hotKeys.Start(Path.Combine(AppContext.BaseDirectory, "island-hotkey"), bindings);

        // 注册失败要**让用户看见**（不只写日志）：3 秒后查一次，失败就发系统通知。
        // 常见原因是组合被别的应用占用（如 ⌘⌥D 与某些词典软件冲突）。
        Dispatcher.UIThread.Post(async () =>
        {
            await System.Threading.Tasks.Task.Delay(3000);
            var err = _hotKeys.LastError;
            if (err.Length > 0)
            {
                Console.Error.WriteLine($"[hotkey] 用户可见提示: {err}");
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("osascript")
                    { UseShellExecute = false, CreateNoWindow = true,
                      RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    var proc = System.Diagnostics.Process.Start(psi);
                    if (proc is not null)
                    {
                        await proc.StandardInput.WriteLineAsync($"display notification \"{err}\" with title \"MyIsland 快捷键\"");
                        proc.StandardInput.Close();
                        await proc.WaitForExitAsync();
                    }
                }
                catch { /* 通知失败不影响运行 */ }
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>读取设置并应用（启动时 + 设置变更时）。</summary>
    private void LoadAndApplySettings()
    {
        try
        {
            var st = Island.App.Services.Settings.Load();
            _expandMode = st.ExpandMode;
            _alwaysOnTop = st.AlwaysOnTop;
            _clickThrough = st.ClickThrough;
            _hideFullscreen = st.HideFullscreen;
            _indicatorOnMaximize = st.IndicatorOnMaximize;
            // 收起态装饰四开关：从设置恢复到画布（setter 自动标脏）
            _canvas.DecorNotch = st.DecorNotch;
            _canvas.DecorLetters = st.DecorLetters;
            // 胶囊样式开关：从设置恢复到画布
            _canvas.ComboNumericHero = st.ComboNumericHero;
            // 剪贴板保留策略
            _clipRetention = st.ClipboardRetention;
            // 小形象款式 + 收起视图自动循环（第六十轮）
            _canvas.Pet.Style = st.PetStyle;
            _autoCycle = st.AutoCycleViews;
            ApplyAlwaysOnTop();
            ApplyClickThrough();
            ApplyExpandMode();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[settings] load: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>改一项设置并落盘（托盘菜单用）。</summary>
    private void UpdateSetting(Action<Island.App.Services.Settings> mutate)
    {
        try
        {
            var st = Island.App.Services.Settings.Load();
            mutate(st);
            st.RequestSave();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[settings] save: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>
    /// 导出全部剪贴板记录到 ~/Downloads/剪贴板导出-yyyyMMdd-HHmmss/：
    /// 图片存 .png，文字按类型存 .txt（链接 .url 风格的纯文本）。文件名带序号+时间，避免覆盖。
    /// 纯本地写盘，不联网；导出后只提示，不自动清历史。
    /// </summary>
    private void ExportClipsToDownloads()
    {
        try
        {
            var items = _canvas.Clips;
            if (items.Count == 0) return;
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", $"剪贴板导出-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(dir);
            int img = 0, txt = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var c = items[i];
                string stamp = c.At.ToString("HHmmss");
                if (c.Kind == ClipKind.Image && c.Image is { } png)
                {
                    File.WriteAllBytes(Path.Combine(dir, $"{i + 1:00}-{stamp}-图片.png"), png);
                    img++;
                }
                else if (!string.IsNullOrEmpty(c.Text))
                {
                    string safe = string.Concat(c.Preview.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)));
                    if (safe.Length > 24) safe = safe[..24];
                    File.WriteAllText(Path.Combine(dir, $"{i + 1:00}-{stamp}-{safe}.txt"), c.Text);
                    txt++;
                }
            }
            Console.Error.WriteLine($"[clipboard] exported {items.Count} items ({txt} txt / {img} png) → {dir}");
            if (OperatingSystem.IsMacOS())
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { dir } }); }
                catch { /* Finder 打不开不影响已导出的文件 */ }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[clipboard] export failed: {ex.Message}"); }
    }

    // ---------- 每屏「自动色温」开关持久化（按 UUID，屏幕 Id 重启后会变） ----------
    /// <summary>某屏自动色温是否开（从 settings 的 "UUID:1,UUID:0" 串里读）。</summary>
    private static bool ReadAutoColor(string spec, string uuid)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int cut = part.LastIndexOf(':');
            if (cut > 0 && part[..cut] == uuid) return part[(cut + 1)..] == "1";
        }
        return false;
    }

    /// <summary>把某屏自动色温开关写进 settings（保留其它屏）。</summary>
    private static string WriteAutoColor(string spec, string uuid, bool on)
    {
        var pairs = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.StartsWith(uuid + ":", StringComparison.Ordinal)).ToList();
        pairs.Add($"{uuid}:{(on ? 1 : 0)}");
        return string.Join(",", pairs);
    }

    /// <summary>刷新屏幕后，按 UUID 把已保存的自动色温开关状态填回画布（屏幕 Id 已变也能对上）。</summary>
    private void RestoreAutoColorState()
    {
        try
        {
            var spec = Island.App.Services.Settings.Load().AutoColorByScreen;
            _canvas.ScreenAutoColorState.Clear();
            foreach (var s in _canvas.Screens)
                _canvas.ScreenAutoColorState[s.Id] = ReadAutoColor(spec, s.Uuid);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[settings] aut color restore: {ex.Message}"); }
    }

    /// <summary>
    /// 常驻置顶。Avalonia 的 <c>Topmost</c> 在 macOS 上只到 NSFloatingWindowLevel(3)，
    /// 压不过普通应用窗口 —— 这就是「置顶看起来没生效」的根因。
    /// 这里额外用 AppKit 把窗口提到 NSStatusWindowLevel(25)，并设好 collectionBehavior
    /// （所有桌面跟随 / 全屏时也在 / 不进 Cmd+Tab），对齐 Vibe Island 的做法。
    /// </summary>
    private void ApplyAlwaysOnTop()
    {
        Topmost = _alwaysOnTop;
        if (!OperatingSystem.IsMacOS() || !_alwaysOnTop) return;
        try
        {
            if (TryGetNativeWindowHandle(this, out var h))
            {
                MacInterop.SetWindowLevel(h, MacInterop.NsStatusWindowLevel, MacInterop.CollectionAlwaysOnTop);
                Console.Error.WriteLine("[mac] window level -> statusBar(25), collectionBehavior set");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[mac] alwaysOnTop: {ex.Message}"); }
    }

    /// <summary>打开剪贴板条目的详情窗口（右键菜单「查看详情」）。</summary>
    private void ShowClipDetail(ClipItem item)
    {
        try
        {
            var w = new ClipDetailWindow(item);
            w.Show(this);   // 非模态：用户可留着详情继续操作岛
        }
        catch (Exception ex) { Console.Error.WriteLine($"[clipboard] detail open: {ex.Message}"); }
    }

    /// <summary>
    /// 鼠标穿透：Avalonia 没有内建，要操作底层 NSWindow 的 ignoresMouseEvents。
    /// 句柄从 TopLevel 的原生句柄取；拿不到就静默跳过（不崩）。
    /// </summary>
    private void ApplyClickThrough()
    {
        try
        {
            if (TryGetNativeWindowHandle(this, out var h))
                MacInterop.SetIgnoresMouseEvents(h, _clickThrough);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[mac] click-through: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>取窗口的原生句柄（Avalonia 11：TopLevel.TryGetFeature&lt;INativeControlHandle&gt;）。</summary>
    private static bool TryGetNativeWindowHandle(TopLevel top, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        try
        {
            // Avalonia 11 公开 API：TopLevel.TryGetPlatformHandle() → IPlatformHandle
            var ph = top.TryGetPlatformHandle();
            handle = ph?.Handle ?? IntPtr.Zero;
            return handle != IntPtr.Zero;
        }
        catch { return false; }
    }

    /// <summary>展开方式：always=一直展开；never=只允许点开（悬停不展开）；hover=悬停展开。</summary>
    private void ApplyExpandMode()
    {
        if (_expandMode == "always")
        {
            SetExpanded(true);
        }
        else if (_expandMode == "never")
        {
            _hoverOpen = false;
            _hoverTimer.Stop();
            if (!_expanded) SetExpanded(false);
        }
    }
    private readonly DispatcherTimer _islandSwitchTimer;

    public void SetTheme(string name)
    {
        _themeName = name;
        ApplyTheme();
        // 自动日夜模式：排程到下一个切换时刻（06:30 / 18:30）
        _islandSwitchTimer.Stop();
        if (name == "IslandAuto") ScheduleIslandSwitch();
        // 持久化（防抖写盘）
        try
        {
            var s = Island.App.Services.Settings.Load();
            s.Theme = name;
            s.RequestSave();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[theme] save: {ex.Message}"); }
    }

    private void ApplyTheme()
    {
        _canvas.Palette = _themeName switch
        {
            "IslandAuto" => Palette.IslandAt(DateTime.Now),
            "IslandDay" => Palette.IslandDay,
            "IslandNight" => Palette.IslandNight,
            var n => Palette.ByName(n) ?? Palette.Dark,
        };
        _canvas.MarkDirty();
    }

    /// <summary>海岛自动模式：定时器对齐到下一次日出/日落边界；最长 10 分钟醒一次以应对睡眠/改时钟。</summary>
    private void ScheduleIslandSwitch()
    {
        var next = Palette.NextIslandSwitch(DateTime.Now);
        double sec = Math.Clamp((next - DateTime.Now).TotalSeconds, 1, 600);
        _islandSwitchTimer.Interval = TimeSpan.FromSeconds(sec);
        _islandSwitchTimer.Start();
    }

    /// <summary>
    private List<LunarSettings.ScreenSetting> _lunarMenuCache = new();
    private DateTime _lunarMenuReadAt = DateTime.MinValue;

    /// <summary>
    /// 动态重建"屏幕设置"菜单：每块屏幕列出自动跟随开关 + 亮度范围档位。
    /// 菜单 Opening 是同步事件，不能在里面等 plutil 子进程（之前每次右键托盘冻结一下）。
    /// 策略：先用 2 秒内的缓存立即建菜单；过期则后台读、读完再重建一次。
    /// </summary>
    private void RebuildLunarSettingsMenu(NativeMenu menu)
    {
        bool fresh = (DateTime.Now - _lunarMenuReadAt).TotalSeconds < 2;
        if (!fresh)
        {
            RunBg(() =>
            {
                var list = LunarSettings.Read();
                Dispatcher.UIThread.Post(() =>
                {
                    _lunarMenuCache = list; _lunarMenuReadAt = DateTime.Now;
                    if (menu.Parent is not null) BuildLunarSettingsMenu(menu, list);   // 菜单还开着才重建
                });
            });
        }
        BuildLunarSettingsMenu(menu, _lunarMenuCache);
    }

    private void BuildLunarSettingsMenu(NativeMenu menu, List<LunarSettings.ScreenSetting> screens)
    {
        menu.Items.Clear();
        // 屏幕清单：优先用画布上已加载的（带真实 DDC 读数），没有就即时枚举一次
        var live = _canvas.Screens.Count > 0
            ? _canvas.Screens
            : DisplayControl.ListNames();
        if (live.Count == 0)
        {
            menu.Items.Add(new NativeMenuItem("未检测到屏幕（打开一次屏幕页即可）") { IsEnabled = false });
            return;
        }

        foreach (var sc in live)
        {
            var setting = screens.FirstOrDefault(k => k.Uuid == sc.Uuid);
            string uuid = sc.Uuid;
            var sub = new NativeMenu();

            // ---- 直接调亮度：每 10% 一档，勾选当前值 ----
            // 原来这个子菜单只有"亮度范围档位"（写 plist），没有任何能立刻改亮度的入口。
            // 内建屏/外接屏共用一条路：都走 SetPercentEx —— 它内部已经做了
            // 范围钳制、暂停检查、写后回读。旧实现在这里另写了一份"写成功就把 UI 设成目标值"，
            // 没用设备回读值，也没走范围和暂停，和面板两个入口行为不一致。
            {
                var bri = new NativeMenuItem("亮度");
                var briSub = new NativeMenu();
                bool briPaused = DisplayControl.IsWritePaused(sc.Uuid, DisplayControl.VcpBrightness);
                var (rmin, rmax) = DisplayControl.GetBrightnessRange(sc.Uuid);
                for (int p = 100; p >= 10; p -= 10)
                {
                    int pct = p;
                    bool inRange = pct >= rmin && pct <= rmax;
                    var item = new NativeMenuItem(inRange ? $"{pct}%" : $"{pct}%（范围外）")
                    {
                        ToggleType = NativeMenuItemToggleType.Radio,
                        IsChecked = sc.BrightnessOk && sc.ValuesLoaded && Math.Abs(sc.Brightness - pct) < 6,
                        // 范围外的档位照常可点：内核会钳到边界，点 90% 实际写到上限 ——
                        // 但要让用户看得见"这一档不在范围内"。亮度项被暂停时全部禁用。
                        IsEnabled = !briPaused,
                    };
                    if (briPaused) item.ToolTip = "此项写入已暂停，请到屏幕页点\"允许重试\"";
                    string u = sc.Uuid; uint id = sc.Id; bool builtin = sc.Builtin;
                    item.Click += (_, _) => RunBg(() =>
                    {
                        var res = DisplayControl.SetPercentEx(u, id, DisplayControl.VcpBrightness, pct, builtin);
                        int? back = res.Level?.PercentRounded;
                        string msg = res.Status switch
                        {
                            DisplayControl.DdcStatus.Ok => "亮度已调整并读取确认",
                            DisplayControl.DdcStatus.WriteUnconfirmed => "写入已暂停：显示器未确认目标值",
                            DisplayControl.DdcStatus.Paused => "此项写入已暂停",
                            _ => "写入失败：" + (res.Message ?? "请检查连接后重试"),
                        };
                        Dispatcher.UIThread.Post(() =>
                        {
                            // 用设备回读值刷新（而不是目标值），并把结果写进这张卡的状态行
                            _canvas.Screens = _canvas.Screens.Select(x => x.Id == id ? x with
                            {
                                Brightness = res.Ok && back is { } rb ? rb : x.Brightness,
                                Message = msg,
                            } : x).ToList();
                            _canvas.MarkDirty();
                        });
                    });
                    briSub.Items.Add(item);
                }
                if (briPaused)
                    briSub.Items.Add(new NativeMenuItem("写入已暂停 · 到屏幕页点\"允许重试\"") { IsEnabled = false });
                else if (rmin > 0 || rmax < 100)
                    briSub.Items.Add(new NativeMenuItem($"当前范围 {rmin}–{rmax}%，范围外会写到边界") { IsEnabled = false });
                bri.Menu = briSub;
                sub.Items.Add(bri);
            }

            // 自动跟随内建屏（只在真有内建屏时才有意义）
            if (live.Any(s => s.Builtin))
            {
                var follow = new NativeMenuItem("自动亮度 · 跟随内建屏")
                {
                    ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = setting?.FollowBuiltin ?? false,
                };
                follow.Click += (_, _) =>
                {
                    bool next = !follow.IsChecked;
                    System.Threading.Tasks.Task.Run(() => LunarSettings.SetFollow(uuid, next));
                    follow.IsChecked = next;
                };
                sub.Items.Add(follow);
            }

            // 静音（VCP 141）。显示器不支持时不显示这一项（不摆假控件）。
            if (sc.Mute is { } mv)
            {
                bool muted = mv == 1;   // 1=静音 2=有声
                var muteItem = new NativeMenuItem(muted ? "取消静音" : "静音")
                {
                    ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = muted,
                };
                string mu = sc.Uuid; uint mid = sc.Id;
                muteItem.Click += (_, _) =>
                {
                    bool next = !muteItem.IsChecked;
                    RunBg(() =>
                    {
                        bool ok = DisplayControl.SetMute(mu, mid, next);
                        Dispatcher.UIThread.Post(() =>
                        {
                            muteItem.IsChecked = ok ? next : muted;
                            _canvas.Screens = _canvas.Screens.Select(s => s.Id == mid ? s with
                            {
                                Mute = ok ? (next ? 1 : 2) : s.Mute,
                                Message = ok ? (next ? "已静音" : "已取消静音") : "静音失败，请检查显示器设置",
                            } : s).ToList();
                            _canvas.MarkDirty();
                        });
                    });
                };
                sub.Items.Add(muteItem);
            }

            // 亮度范围档位（写 plist，给自动跟随引擎限定范围）
            foreach (var (name, lo, hi) in new[] { ("全范围 0-100", 0, 100), ("护眼 10-90", 10, 90), ("窄范围 20-80", 20, 80) })
            {
                string n = name;
                var item = new NativeMenuItem(n)
                {
                    ToggleType = NativeMenuItemToggleType.Radio,
                    IsChecked = setting is not null && setting.Min == lo && setting.Max == hi,
                };
                item.Click += (_, _) => RunBg(() =>
                {
                    if (LunarSettings.SetRange(uuid, lo, hi)) DisplayControl.SetBrightnessRange(uuid, lo, hi);
                });
                sub.Items.Add(item);
            }
            sub.Items.Add(new NativeMenuItem(
                setting is null ? "当前亮度 " + (sc.ValuesLoaded ? $"{sc.Brightness}%" : "读取中")
                                : $"当前 {setting.Min}-{setting.Max}% · 预设 {setting.Brightness:F0}%") { IsEnabled = false });

            string name2 = sc.Name.Length > 12 ? sc.Name[..12] : sc.Name;
            menu.Items.Add(new NativeMenuItem($"屏幕 {name2}") { Menu = sub });
        }
    }

    // ---------- 屏幕控制页（完整移植 LunarCN） ----------
    /// <summary>只在展开且停在屏幕页时读 DDC（每次读要起子进程，较慢）。</summary>
    private bool _refreshingScreens;

    /// <summary>
    /// 把 plist 里的每屏设置同步进控制内核。**亮度范围只有同步进来才真正约束写入**——
    /// 旧实现只把范围写进 plist，滑块/菜单/自动跟随的写入完全无视它，三档范围是摆设
    /// （对齐参考版：LiveBrightnessRequest / AutomaticBrightnessPolicy 都按 range 钳制目标）。
    /// 每次读完 LunarSettings 都调一次：启动、改档位、恢复预设后都会生效。
    /// </summary>
    private static void SyncSettingsToKernel(IReadOnlyList<LunarSettings.ScreenSetting> settings)
    {
        foreach (var st in settings)
            DisplayControl.SetBrightnessRange(st.Uuid, st.Min, st.Max);
    }

    /// <summary>
    /// 刷新屏幕列表：两阶段加载。
    /// 第一阶段只枚举（58ms）→ 卡片立刻出现；
    /// 第二阶段补 DDC 读数（~2 秒）→ 滑块值随后填上。
    /// 之前是一次性读全部（DDC 串行 4 个 VCP，实测 7 秒），用户切到屏幕页是干等的。
    /// </summary>
    private async void RefreshScreens()
    {
        if (!_expanded || _canvas.Page != "screens") return;
        // 拖动中 / 还有待写目标时不刷新：3 秒轮询的设备读数会把用户刚拖到的位置拽回去
        if (_canvas.DraggingVcp != 0) return;
        if (_liveTargets.Values.Any(m => m.Values.Any(t => !t.Done))) return;
        // 重入保护：切页 + 展开会连着触发两次 RefreshScreens，
        // 两次都去跑 DDC 读取还互相抢锁（实测列表迟迟不出现）。只让一个在跑。
        if (_refreshingScreens) return;
        _refreshingScreens = true;
        try
        {
            // 第一阶段：瞬时列出屏幕（不读 DDC）——卡片立刻出现，高度也按它重算。
            // **只在还没有列表时更新 UI**：_screensTimer 每 3 秒调一次 RefreshScreens，
            // 若每次都覆盖成"未加载版"（值=0），就会和第二阶段（2 秒后才补上真值）打架——
            // 结果滑块永远停在 0%（2026-11-24 真实运行验证才发现，离屏渲染看不出来）。
            var names = await System.Threading.Tasks.Task.Run(() => DisplayControl.ListNames());
            if (names.Count > 0 && _canvas.Screens.Count == 0)
            { _canvas.Screens = names; _canvas.MarkDirty(); UpdateExpandHeight(); }

            // 第二阶段：后台补 DDC 读数
            var withValues = await System.Threading.Tasks.Task.Run(() => DisplayControl.ReadValues(names));
            // 已有卡片的"状态消息"（如"亮度已实时调整并读取确认"）保留，不被例行轮询抹掉
            var prev = _canvas.Screens.ToDictionary(s => s.Id, s => s.Message);
            _canvas.Screens = withValues.Select(s => prev.TryGetValue(s.Id, out var pm) && pm.Length > 0 ? s with { Message = pm } : s).ToList();
            RebuildColorTempOptions();
            // 恢复每屏「自动色温」开关（显示器自己不存这个逻辑状态，按 UUID 从 settings 读回）
            RestoreAutoColorState();
            _canvas.ScreenSettings = await System.Threading.Tasks.Task.Run(() => LunarSettings.Read());
            SyncSettingsToKernel(_canvas.ScreenSettings);
            _canvas.BuiltinOff = DisplayControl.IsBuiltinOff;
            _canvas.BuiltinOffCountdown = DisplayControl.BuiltinOffCountdown;
            _canvas.AutoFollowStatus = _autoFollow?.Status ?? "";
            _canvas.MarkDirty();
        }
        // async void 里未捕获的异常会直接拉崩整个进程 —— 这里是 3 秒一次的轮询，
        // 读 DDC / 读 plist 偶发失败很正常，绝不能让它带走灵动岛。
        catch (Exception ex) { Console.Error.WriteLine($"[screens] refresh: {ex.GetType().Name}: {ex.Message}"); }
        finally { _refreshingScreens = false; }
    }

    // ---- 滑块写入：节流 + 尾沿保证（审查 M-2：中间值可丢，最后一个值必须落地）----
    private DispatcherTimer? _flushTimer;

    // ---------- 实时亮度/对比度/音量：目标合并 + 串行写入 + 回读刷新 ----------
    // 对齐参考版 LiveBrightnessBuffer / scheduleLiveBrightness / writeNextLiveBrightness：
    //   * 每屏每项只保留**最新**一个待写目标（旧实现每次拖动都排一个写入，拖快了排队堆积）；
    //   * 上一次写**完成**后才排下一次（内建屏 ≥50ms、外接屏 ≥150ms），不靠固定节流猜；
    //   * 写完用**设备回读值**更新 UI（旧实现乐观显示目标值，设备没跟上时界面在说谎）；
    //   * 写入未确认 → 该项暂停并丢弃其待写目标，面板出"允许重试"。
    private sealed class PendingTarget
    {
        public int Vcp, Percent; public bool Builtin; public long Seq; public bool InFlight, Done;
    }
    private readonly Dictionary<uint, Dictionary<int, PendingTarget>> _liveTargets = new();
    private DateTime _lastWriteDone = DateTime.MinValue;

    /// <summary>某屏某项是否有待写/在写的目标（面板/自动跟随用：有则不覆盖用户的手动值）。</summary>
    private bool HasLiveTarget(uint id, int vcp)
        => _liveTargets.TryGetValue(id, out var m) && m.TryGetValue(vcp, out var t) && (t.InFlight || !t.Done);

    /// <summary>把一次写入请求合并到该屏该项的待写目标（每项只保留最新），并排一次写入。</summary>
    private void QueueLiveTarget(uint id, int vcp, int pct, bool builtin)
    {
        if (!_liveTargets.TryGetValue(id, out var map)) _liveTargets[id] = map = new Dictionary<int, PendingTarget>();
        if (!map.TryGetValue(vcp, out var t)) map[vcp] = t = new PendingTarget { Vcp = vcp };
        t.Percent = pct; t.Builtin = builtin; t.Seq++; t.Done = false;
        ScheduleLiveWrite();
    }

    /// <summary>滑块拖动：合并到该屏该项的最新目标，然后尝试排一次写入。</summary>
    private void OnSliderDragged(uint id, int vcp, int pct)
    {
        var screen = _canvas.Screens.FirstOrDefault(s => s.Id == id);
        if (screen is null) return;
        // 该项写入已被暂停（未确认）→ 不接受拖动，面板上有"允许重试"
        if (DisplayControl.IsWritePaused(screen.Uuid, vcp)) return;

        // 手动拖亮度 → 关闭该屏自动跟随（对齐参考版：手动接管）。
        // SetFollow 要起 plutil 子进程，后台跑。
        if (vcp == DisplayControl.VcpBrightness && _canvas.ScreenSettings.FirstOrDefault(k => k.Uuid == screen.Uuid) is { FollowBuiltin: true })
        {
            _canvas.ScreenSettings = _canvas.ScreenSettings.Select(k => k.Uuid == screen.Uuid ? k with { FollowBuiltin = false } : k).ToList();
            string u = screen.Uuid;
            System.Threading.Tasks.Task.Run(() => LunarSettings.SetFollow(u, false));
        }

        // 目标按该屏亮度范围钳制（与内核一致，滑块拖到范围外时界面立刻显示真实会写入的值）
        if (vcp == DisplayControl.VcpBrightness)
        {
            var (min, max) = DisplayControl.GetBrightnessRange(screen.Uuid);
            pct = (int)Math.Clamp(pct, min, max);
        }

        if (!_liveTargets.TryGetValue(id, out var map)) _liveTargets[id] = map = new Dictionary<int, PendingTarget>();
        if (!map.TryGetValue(vcp, out var t)) map[vcp] = t = new PendingTarget { Vcp = vcp };
        t.Percent = pct; t.Builtin = screen.Builtin; t.Seq++; t.Done = false;

        _canvas.Screens = _canvas.Screens.Select(s => s.Id == id ? s with
        {
            Brightness = vcp == DisplayControl.VcpBrightness ? pct : s.Brightness,
            Contrast = vcp == DisplayControl.VcpContrast ? pct : s.Contrast,
            Volume = vcp == DisplayControl.VcpVolume ? pct : s.Volume,
        } : s).ToList();
        _canvas.MarkDirty();
        ScheduleLiveWrite();
    }

    /// <summary>如果该屏该项没有在写的任务，且距上次写完已过冷却 → 排一次写入。</summary>
    private void ScheduleLiveWrite()
    {
        foreach (var (id, map) in _liveTargets)
        foreach (var t in map.Values)
        {
            if (t.InFlight || t.Done) continue;
            int cooldown = t.Builtin ? 50 : 150;
            double since = (DateTime.Now - _lastWriteDone).TotalMilliseconds;
            if (since < cooldown)
            {
                // 还在冷却：一次性定时器到点再试
                _flushTimer?.Stop();
                _flushTimer ??= new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(cooldown - since) };
                _flushTimer.Tick -= OnFlushTick; _flushTimer.Tick += OnFlushTick;
                _flushTimer.Start();
                return;
            }
            t.InFlight = true;
            long seq = t.Seq; int vcp = t.Vcp, pct = t.Percent; bool builtin = t.Builtin;
            var scr = _canvas.Screens.FirstOrDefault(s => s.Id == id);
            if (scr is null) { t.InFlight = false; t.Done = true; continue; }
            string uuid = scr.Uuid; uint sid = id;
            RunBg(() =>
            {
                var res = DisplayControl.SetPercentEx(uuid, sid, vcp, pct, builtin);
                Dispatcher.UIThread.Post(() => CompleteLiveWrite(sid, vcp, seq, res));
            });
            return;   // 一次只派一个：DDC 是串行总线，派多了只会互相撞
        }
    }

    /// <summary>一次写入完成：回读值刷新 UI；若期间有更新的目标则继续排。</summary>
    private void CompleteLiveWrite(uint id, int vcp, long seq, DisplayControl.DdcResult res)
    {
        _lastWriteDone = DateTime.Now;
        if (_liveTargets.TryGetValue(id, out var map) && map.TryGetValue(vcp, out var t))
        {
            t.InFlight = false;
            // 写的时候用户又拖了（Seq 变了）→ 这个结果已过时，保留最新目标继续写
            bool stale = t.Seq != seq;
            t.Done = !stale;
            if (res.Status == DisplayControl.DdcStatus.WriteUnconfirmed || res.Status == DisplayControl.DdcStatus.Unsupported)
            {
                // 未确认/不支持：丢弃该屏该项的待写目标（对齐参考版 liveBuffer.remove）
                t.Done = true;
            }
        }

        int? readBack = res.Level?.PercentRounded;
        string msg = res.Status switch
        {
            DisplayControl.DdcStatus.Ok => vcp == DisplayControl.VcpBrightness ? "亮度已实时调整并读取确认" : "已应用并读取确认",
            DisplayControl.DdcStatus.WriteUnconfirmed => "写入已暂停：显示器未确认目标值",
            DisplayControl.DdcStatus.Unsupported => "显示器不支持此项",
            DisplayControl.DdcStatus.Paused => "此项写入已暂停",
            _ => "写入失败：" + (res.Message ?? "请检查连接后重试"),
        };
        _canvas.Screens = _canvas.Screens.Select(s => s.Id == id ? s with
        {
            Brightness = vcp == DisplayControl.VcpBrightness && readBack is { } rb && res.Ok ? rb : s.Brightness,
            Contrast = vcp == DisplayControl.VcpContrast && readBack is { } rc && res.Ok ? rc : s.Contrast,
            Volume = vcp == DisplayControl.VcpVolume && readBack is { } rv && res.Ok ? rv : s.Volume,
            // RGB 回读值也要写回 UI（拖完可能没真正生效，回读才是真值）
            Red = vcp == DisplayControl.VcpRed && readBack is { } rr && res.Ok ? rr : s.Red,
            Green = vcp == DisplayControl.VcpGreen && readBack is { } gr && res.Ok ? gr : s.Green,
            Blue = vcp == DisplayControl.VcpBlue && readBack is { } br && res.Ok ? br : s.Blue,
            Message = msg,
        } : s).ToList();
        _canvas.MarkDirty();
        UpdateExpandHeight();
        ScheduleLiveWrite();   // 有更新的目标就接着写
    }

    private void OnFlushTick(object? s, EventArgs e) { _flushTimer?.Stop(); ScheduleLiveWrite(); }

    /// <summary>取消所有待写目标（切页/收起/关闭内建屏时调；已发出的那一次写入可能完成）。</summary>
    private void CancelLiveWrites()
    {
        foreach (var map in _liveTargets.Values) foreach (var t in map.Values) t.Done = true;
    }

    private async void OnFollowToggled(uint id)
    {
        // async void：未捕获异常会拉崩进程。落盘失败时把 UI 翻回去，别让开关状态和磁盘对不上。
        var screen = _canvas.Screens.FirstOrDefault(s => s.Id == id);
        if (screen is null) return;
        var st = _canvas.ScreenSettings.FirstOrDefault(k => k.Uuid == screen.Uuid);
        bool next = !(st?.FollowBuiltin ?? false);
        // 乐观更新：先翻转 UI，再后台落盘
        _canvas.ScreenSettings = _canvas.ScreenSettings.Select(k => k.Uuid == screen.Uuid ? k with { FollowBuiltin = next } : k).ToList();
        _canvas.MarkDirty();
        string uuid = screen.Uuid;
        try
        {
            await System.Threading.Tasks.Task.Run(() => LunarSettings.SetFollow(uuid, next));
            _canvas.ScreenSettings = await System.Threading.Tasks.Task.Run(() => LunarSettings.Read());
            SyncSettingsToKernel(_canvas.ScreenSettings);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[screens] follow toggle: {ex.GetType().Name}: {ex.Message}");
            // 回滚乐观更新：磁盘上没改成，界面也不能显示成改了
            _canvas.ScreenSettings = _canvas.ScreenSettings.Select(k => k.Uuid == uuid ? k with { FollowBuiltin = !next } : k).ToList();
            _canvas.AutoFollowStatus = "自动跟随设置失败，请重试";
            _canvas.MarkDirty();
        }
    }

    /// <summary>允许重试写入（对齐参考版 allowRetry）：清该屏所有项的暂停，并重读设备值。</summary>
    private void OnRetryRequested(uint id)
    {
        var screen = _canvas.Screens.FirstOrDefault(s => s.Id == id);
        if (screen is null) return;
        DisplayControl.AllowRetry(screen.Uuid);
        _canvas.Screens = _canvas.Screens.Select(s => s.Id == id ? s with { Message = "已允许再次尝试写入，请调整目标值。" } : s).ToList();
        _canvas.MarkDirty();
        UpdateExpandHeight();
        _autoFollow?.PollNow();
    }

    /// <summary>静音开关。</summary>
    private void OnMuteToggled(uint id, bool muted)
    {
        var screen = _canvas.Screens.FirstOrDefault(s => s.Id == id);
        if (screen is null) return;
        // 乐观翻转，后台写；失败回读纠正
        _canvas.Screens = _canvas.Screens.Select(s => s.Id == id ? s with { Mute = muted ? 1 : 2 } : s).ToList();
        _canvas.MarkDirty();
        string uuid = screen.Uuid; uint sid = id;
        RunBg(() =>
        {
            var res = DisplayControl.SetPercentEx(uuid, sid, DisplayControl.VcpMute, muted ? 1 : 2);
            Dispatcher.UIThread.Post(() =>
            {
                var back = DisplayControl.ReadMuteState(uuid, sid);
                _canvas.Screens = _canvas.Screens.Select(s => s.Id == sid ? s with
                {
                    Mute = back.Ok ? back.Level!.Value.Current : s.Mute,
                    Message = res.Ok ? "静音已应用并读取确认" : (res.Status == DisplayControl.DdcStatus.WriteUnconfirmed ? "静音写入未确认" : "静音写入失败"),
                } : s).ToList();
                _canvas.MarkDirty();
            });
        });
    }

    /// <summary>
    /// 保存 / 恢复预设。async void 的异常会拉崩进程 —— 读 plist、写 DDC 偶发失败很常见，
    /// 所以方法体挪到 <see cref="OnPresetActionCore"/>，这里只负责兜住异常并告诉用户。
    /// </summary>
    private async void OnPresetAction(uint id, bool save)
    {
        try { await OnPresetActionCore(id, save); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[screens] preset: {ex.GetType().Name}: {ex.Message}");
            _canvas.AutoFollowStatus = save ? "预设保存失败，请重试" : "预设恢复失败，请重试";
            _canvas.MarkDirty();
        }
    }

    private async System.Threading.Tasks.Task OnPresetActionCore(uint id, bool save)
    {
        var screen = _canvas.Screens.FirstOrDefault(s => s.Id == id);
        if (screen is null) return;
        if (save)
        {
            // 对齐参考版：保存的是设备确认值；有任何待写目标时不保存
            if (_liveTargets.TryGetValue(id, out var m) && m.Values.Any(t => !t.Done))
            {
                _canvas.Screens = _canvas.Screens.Select(s => s.Id == id ? s with { Message = "亮度调节完成后才能保存预设。" } : s).ToList();
                _canvas.MarkDirty();
                return;
            }
            string su = screen.Uuid; int sb = screen.Brightness, sc = screen.Contrast, sv = screen.Volume;
            bool ok = await System.Threading.Tasks.Task.Run(() => LunarSettings.SavePreset(su, sb, sc, sv));
            _canvas.Screens = _canvas.Screens.Select(s => s.Id == id ? s with { Message = ok ? "当前设备值已保存为此显示器的预设。" : "预设保存失败。" } : s).ToList();
        }
        else
        {
            // 恢复预设：只写"与当前不同且未被暂停"的项（对齐参考版 restorePreset.changed）
            var st = _canvas.ScreenSettings.FirstOrDefault(k => k.Uuid == screen.Uuid);
            string uuid = screen.Uuid;
            bool wasFollowing = st?.FollowBuiltin == true;
            if (wasFollowing)
            {
                _canvas.ScreenSettings = _canvas.ScreenSettings.Select(k => k.Uuid == uuid ? k with { FollowBuiltin = false } : k).ToList();
                _ = System.Threading.Tasks.Task.Run(() => LunarSettings.SetFollow(uuid, false));   // 应用预设关闭跟随
            }
            RunBg(() =>
            {
                var paused = DisplayControl.PausedControls(uuid);
                int failed = 0, total = 0, skipped = 0;
                string msg;
                if (st?.Brightness is { } b && !paused.Contains(DisplayControl.VcpBrightness)) { total++; if (!DisplayControl.SetPercent(uuid, id, DisplayControl.VcpBrightness, (int)b)) failed++; }
                else if (st?.Brightness is not null) skipped++;
                if (st?.Contrast is { } c && !paused.Contains(DisplayControl.VcpContrast)) { total++; if (!DisplayControl.SetPercent(uuid, id, DisplayControl.VcpContrast, (int)c)) failed++; }
                else if (st?.Contrast is not null) skipped++;
                if (st?.Volume is { } v && !paused.Contains(DisplayControl.VcpVolume)) { total++; if (!DisplayControl.SetPercent(uuid, id, DisplayControl.VcpVolume, (int)v)) failed++; }
                else if (st?.Volume is not null) skipped++;
                msg = total == 0 ? "预设的可用项目与当前设置一致；不可用或暂停的项目已跳过。"
                    : failed == 0 ? "预设已恢复并读取确认"
                    : $"{failed}/{total} 项写入未确认（{skipped} 项已跳过）";
                Dispatcher.UIThread.Post(() => { _canvas.AutoFollowStatus = msg; _canvas.MarkDirty(); RefreshScreens(); });
            });
        }
        _canvas.ScreenSettings = await System.Threading.Tasks.Task.Run(() => LunarSettings.Read());
        SyncSettingsToKernel(_canvas.ScreenSettings);
        _canvas.MarkDirty();
    }

    // ---------- 内建屏关闭选择持久化 ----------
    /// <summary>
    /// 关闭内建屏并记住选择（下次启动有外接屏时自动关）。返回错误信息，null=成功。
    /// 阻塞操作，调用方须在后台线程。
    /// </summary>
    private static string? CloseBuiltinAndRemember(uint id, string uuid, bool hasExternal)
    {
        string? err = DisplayControl.TurnOffBuiltin(id, uuid, hasExternal);
        if (err is null)
            UpdateSettingStatic(s => s.AutoCloseBuiltin = true);
        return err;
    }

    /// <summary>恢复内建屏并清掉「自动关闭」选择（用户主动恢复 = 不想再自动关）。</summary>
    private static void RestoreBuiltinAndForget()
    {
        DisplayControl.RestoreBuiltin();
        UpdateSettingStatic(s => s.AutoCloseBuiltin = false);
    }

    /// <summary>静态版设置落盘（供上面的静态 helper 用；与实例版 UpdateSetting 同一语义）。</summary>
    private static void UpdateSettingStatic(Action<Island.App.Services.Settings> mutate)
    {
        try
        {
            var st = Island.App.Services.Settings.Load();
            mutate(st);
            st.Save();   // 立即写：关屏是低频关键操作，不等防抖
        }
        catch (Exception ex) { Console.Error.WriteLine($"[settings] builtin save: {ex.Message}"); }
    }

    /// <summary>
    /// 启动时自动关闭内建屏：仅当用户上次选过「关闭」、当前有外接屏、且内建屏在线时执行。
    /// 无外接屏 → 安全跳过（避免整机黑屏）。延迟到窗口起来后跑（枚举屏幕需要平台就绪）。
    /// </summary>
    private void TryAutoCloseBuiltin()
    {
        try
        {
            if (!Island.App.Services.Settings.Load().AutoCloseBuiltin) return;
            if (DisplayControl.IsBuiltinOff) return;   // 已经在关闭态（守护进程还活着）
            var (b, ext) = DisplayControl.FindBuiltinFast();
            if (b.id == 0 || !ext)
            {
                Console.Error.WriteLine("[display] 启动自动关内建屏跳过：未检测到外接屏（避免黑屏）");
                return;
            }
            string? err = CloseBuiltinAndRemember(b.id, b.uuid, ext);
            if (err is null)
            {
                // 自动关成功 → 自动「保持关闭」：否则守护进程 15 秒后自动恢复，
                // 用户还是看到内建屏回来 = 等于没记住（用户反馈的核心痛点）。
                DisplayControl.KeepBuiltinOff();
                Console.Error.WriteLine("[display] 启动自动关闭内建屏成功并保持（记住的上次选择）");
            }
            else Console.Error.WriteLine($"[display] 启动自动关内建屏失败：{err}");
            Dispatcher.UIThread.Post(() =>
            {
                _canvas.BuiltinOff = DisplayControl.IsBuiltinOff;
                _canvas.BuiltinOffCountdown = DisplayControl.BuiltinOffCountdown;
                _canvas.Screens = _canvas.Screens.Where(s => !s.Builtin).ToList();
                if (_canvas.BuiltinOff) _builtinCountdownTimer.Start();
                _canvas.MarkDirty();
                RefreshScreens();
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[display] auto-close builtin: {ex.Message}"); }
    }

    /// <summary>内建屏开关（审查 C-1：Process I/O 必须 offload 到线程池，否则 UI 冻结 1~4 秒）。</summary>
    /// <summary>
    /// 启动 panic 侦测（2026-11-25 事件后加）：上次会话以来的内核 panic 写进 app.log。
    /// 基线 = app.log.1 的最后写入时间（上会话结束时刻）。panic 侦测失败绝不影响启动。
    /// </summary>
    private static void ReportPanicIfAny()
    {
        try
        {
            // 基线：上一个会话日志的 LastWriteTime（那正是上次运行的最后时刻）。
            // 用它而不是「本次启动时间」：从上次退出到这次启动之间发生的 panic 才是
            // 「上次会话之后」的；用启动时间会把这段漏掉。
            string? log1 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Logs", "MyIsland", "app.log.1");
            var baseline = File.Exists(log1) ? File.GetLastWriteTimeUtc(log1) : DateTime.MinValue.ToUniversalTime();
            var info = PanicWatch.ScanSince(baseline);
            if (info is null) return;   // 干净：不打日志（正常启动不想多一行噪声）

            Console.Error.WriteLine($"[panic] 上次会话后发生内核 panic：{info.At:yyyy-MM-dd HH:mm:ss}");
            Console.Error.WriteLine($"[panic] 签名：{info.Signature}");
            Console.Error.WriteLine($"[panic] 文件：{info.File}");
            // dual_pipe 是「双显示管道」路径 —— 正是我们自动关内建屏所处的拓扑。
            // 同一签名复发 → 基本坐实诱因关联，应移除该功能（见 PanicWatch 类注释）。
            if (info.Signature.Contains("dual_pipe", StringComparison.Ordinal))
                Console.Error.WriteLine("[panic] 注意：dual_pipe 路径与「自动关闭内建屏」的双管道拓扑相关，若反复出现请考虑关闭该功能（托盘 → 行为）");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[panic] report: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>
    /// 关闭 / 恢复内建屏。这是后果最重的按钮：如果关屏后这里崩了，用户面对的是黑掉的
    /// 笔记本屏且没有应用能恢复它。所以方法体挪到 <see cref="OnBuiltinToggleCore"/>，
    /// 这里兜住一切异常 —— 最多显示一句失败，绝不拉崩进程。
    /// </summary>
    private async void OnBuiltinToggle()
    {
        try { await OnBuiltinToggleCore(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[display] builtin toggle: {ex.GetType().Name}: {ex.Message}");
            _canvas.BuiltinOff = DisplayControl.IsBuiltinOff;   // 以真实状态为准，别信乐观值
            _canvas.AutoFollowStatus = "内建屏开关操作失败：" + ex.Message;
            _canvas.MarkDirty();
        }
    }

    private async System.Threading.Tasks.Task OnBuiltinToggleCore()
    {
        if (DisplayControl.IsBuiltinOff)
        {
            await System.Threading.Tasks.Task.Run(RestoreBuiltinAndForget);
            _canvas.BuiltinOff = false;
            _canvas.BuiltinOffCountdown = null;
        }
        else
        {
            // 不依赖 UI 缓存，也不走 DisplayControl.List()（那会读 4 个 VCP 码，DDC 慢时可达 20+ 秒）。
            // 关闭内建屏只需要 id/uuid：用 island-screens 枚举即可（毫秒级）。
            uint id; string uuid; bool hasExternal;
            try
            {
                var (b, ext) = await System.Threading.Tasks.Task.Run(() => DisplayControl.FindBuiltinFast());
                (id, uuid, hasExternal) = (b.id, b.uuid, ext);
            }
            catch (InvalidOperationException ex) { _canvas.AutoFollowStatus = ex.Message; _canvas.MarkDirty(); return; }
            string? err = await System.Threading.Tasks.Task.Run(() => CloseBuiltinAndRemember(id, uuid, hasExternal));
            _canvas.BuiltinOff = DisplayControl.IsBuiltinOff;
            _canvas.BuiltinOffCountdown = DisplayControl.BuiltinOffCountdown;
            if (err is not null) { _canvas.AutoFollowStatus = "关闭失败：" + err; Console.Error.WriteLine($"[display] turn off: {err}"); }
            else
            {
                // 乐观更新：立即剔除内建屏卡片（不等 DDC 重读，避免关闭后卡片残留几秒）
                _canvas.Screens = _canvas.Screens.Where(s => !s.Builtin).ToList();
                _builtinCountdownTimer.Start();
            }
        }
        _canvas.MarkDirty();
    }

    // ---------- 菜单栏托盘（macOS 菜单栏 / Windows 通知区） ----------
    private void SetupTray()
    {
        try
        {
            // 菜单栏专用单色模板图标（黑+alpha）：macOS 自动按深/浅菜单栏反相，
            // 品牌彩色 logo 在深色栏上会糊成一团，不适合当状态栏图标
            using var stream = AssetLoader.Open(new Uri("avares://Island.Avalonia/menubar-44.png"));
            _tray = new TrayIcon { Icon = new WindowIcon(stream), ToolTipText = "MyIsland" };
            NativeMenuItem Item(string header, Action act)
            {
                var mi = new NativeMenuItem(header);
                mi.Click += (_, _) => act();
                return mi;
            }
            var menu = new NativeMenu();
            menu.Items.Add(Item("仪表盘", () => UserPickedView("dash")));
            menu.Items.Add(Item("时钟", () => UserPickedView("clock")));
            menu.Items.Add(Item("小形象", () => UserPickedView("pet")));
            menu.Items.Add(Item("信息胶囊", () => UserPickedView("combo")));
            // Mac 功能开关：显示器控制（内置月光功能，无需另装/另启动）
            if (OperatingSystem.IsMacOS())
            {
                var lunarMenu = new NativeMenu();
                lunarMenu.Items.Add(Item("打开屏幕控制面板", () => { UserPickedPage("screens"); SetExpanded(true); RefreshScreens(); }));
                var settingsHost = new NativeMenuItem("屏幕设置") { Menu = new NativeMenu() };
                lunarMenu.Items.Add(settingsHost);
                lunarMenu.Items.Add(new NativeMenuItemSeparator());
                // 内建屏开关：菜单里补上"关闭"入口。
                // 原来菜单只有"恢复内建屏（紧急）"，关闭只能在屏幕页顶栏点——
                // 而顶栏只在检测到内建屏时才出现，导致部分场景下"关闭屏幕"功能完全找不到
                // （2026-11-24 用户反馈"关闭屏幕功能也没有了"）。
                // 这里按当前状态二选一：有内建屏且未关 → "关闭"；已关 → "恢复"。
                // 只有真的存在内建屏时才给"关闭"入口（台式机没有可关的内建屏，
                // 摆一个必然报错的菜单项反而添乱）。探测走 ListNames（58ms，不读 DDC）。
                bool hasBuiltin = false;
                try { hasBuiltin = DisplayControl.ListNames().Any(s => s.Builtin); } catch { /* 失败降级：保留默认值/上次值 */ }
                if (DisplayControl.IsBuiltinOff)
                {
                    // 和面板的"恢复内建屏"按钮走同一个处理函数：恢复完成后才刷 UI（原来 fire-and-forget，
                    // 恢复还没完成菜单/面板就显示已恢复）
                    lunarMenu.Items.Add(Item("恢复内建屏", async () => await System.Threading.Tasks.Task.Run(RestoreBuiltinAndForget)));
                }
                else if (hasBuiltin)
                {
                    lunarMenu.Items.Add(Item("关闭内建屏", () =>
                    {
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            try
                            {
                                var (b, ext) = await System.Threading.Tasks.Task.Run(() => DisplayControl.FindBuiltinFast());
                                string? err = await System.Threading.Tasks.Task.Run(() => CloseBuiltinAndRemember(b.id, b.uuid, ext));
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    _canvas.BuiltinOff = DisplayControl.IsBuiltinOff;
                                    _canvas.BuiltinOffCountdown = DisplayControl.BuiltinOffCountdown;
                                    if (err is not null) _canvas.AutoFollowStatus = "关闭失败：" + err;
                                    else _canvas.Screens = _canvas.Screens.Where(s => !s.Builtin).ToList();
                                    _builtinCountdownTimer.Start();
                                    _canvas.MarkDirty();
                                });
                            }
                            catch (Exception ex)
                            {
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    _canvas.AutoFollowStatus = "关闭失败：" + ex.Message;
                                    _canvas.MarkDirty();
                                });
                            }
                        });
                    }));
                }
                lunarMenu.Opening += (_, _) => RebuildLunarSettingsMenu(settingsHost.Menu!);
                menu.Items.Add(new NativeMenuItem("显示器控制") { Menu = lunarMenu });
            }
            // 主题子菜单（9 套，与 Windows 版同名；海岛假日拆成日/夜/自动三项）
            var themeMenu = new NativeMenu();
            foreach (var t in new[] { ("Dark", "深色"), ("Light", "浅色"), ("OneDark", "One Dark Pro"),
                     ("GitHubDark", "GitHub Dark"), ("Dracula", "Dracula"), ("NightOwl", "Night Owl"),
                     ("IslandAuto", "海岛假日 · 自动日夜"), ("IslandDay", "海岛假日 · 白天"), ("IslandNight", "海岛假日 · 夜晚"),
                     ("Cyberpunk", "赛博朋克"), ("Pixel", "像素方块") })
            {
                string n = t.Item1;
                var mi = new NativeMenuItem(t.Item2) { ToggleType = NativeMenuItemToggleType.Radio, IsChecked = _themeName == n };
                mi.Click += (_, _) => SetTheme(n);
                themeMenu.Items.Add(mi);
            }
            var themeHost = new NativeMenuItem("面板主题") { Menu = themeMenu };
            themeMenu.Opening += (_, _) =>
            {
                int i = 0;
                foreach (var t in new[] { "Dark", "Light", "OneDark", "GitHubDark", "Dracula", "NightOwl", "IslandAuto", "IslandDay", "IslandNight", "Cyberpunk", "Pixel" })
                    if (i < themeMenu.Items.Count) ((NativeMenuItem)themeMenu.Items[i++]).IsChecked = _themeName == t;
            };
            menu.Items.Add(themeHost);

            // ---------- 收起样式（单选）+ 自动轮换 ----------
            // 三种收起视图本来就有，但此前用户无法切换 —— 这里给出入口。
            var styleMenu = new NativeMenu();
            var styleItems = new List<(string Name, string Label, NativeMenuItem Item)>();
            foreach (var (n, label) in new[] { ("dash", "仪表盘"), ("combo", "信息胶囊"), ("clock", "时钟"), ("pet", "小形象") })
            {
                var mi = new NativeMenuItem(label) { ToggleType = NativeMenuItemToggleType.Radio, IsChecked = _canvas.View == n };
                mi.Click += (_, _) => UserPickedView(n);   // 落盘 + 记录手选
                styleItems.Add((n, label, mi));
                styleMenu.Items.Add(mi);
            }
            styleMenu.Items.Add(new NativeMenuItemSeparator());
            // 自动轮换（第六十轮）：小形象 ⇄ 信息胶囊，与「用哪款小形象」解耦。
            var cycleItem = new NativeMenuItem("自动轮换（小形象 ⇄ 信息胶囊）")
            { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = _autoCycle };
            cycleItem.Click += (_, _) =>
            {
                _autoCycle = !_autoCycle;
                UpdateSetting(s => s.AutoCycleViews = _autoCycle);
                cycleItem.IsChecked = _autoCycle;
                if (_autoCycle) StartPetCycle();   // 立刻起轮换计时
                else _cycleTimer.Stop();
            };
            styleMenu.Items.Add(cycleItem);
            var styleHost = new NativeMenuItem("收起样式") { Menu = styleMenu };
            styleMenu.Opening += (_, _) =>
            {
                foreach (var it in styleItems) it.Item.IsChecked = _canvas.View == it.Name;
                cycleItem.IsChecked = _autoCycle;
            };
            menu.Items.Add(styleHost);

            // ---------- 小形象款式（第六十轮）----------
            var petStyleMenu = new NativeMenu();
            var petStyleItems = new List<(string Id, NativeMenuItem Item)>();
            foreach (var (id, label) in PetFaceAv.Styles)
            {
                var mi = new NativeMenuItem(label) { ToggleType = NativeMenuItemToggleType.Radio, IsChecked = _canvas.Pet.Style == id };
                mi.Click += (_, _) =>
                {
                    _canvas.Pet.Style = id;                 // 画布即时生效
                    _canvas.MarkDirty();
                    UpdateSetting(s => s.PetStyle = id); // 落盘，重启保留
                };
                petStyleItems.Add((id, mi));
                petStyleMenu.Items.Add(mi);
            }
            var petStyleHost = new NativeMenuItem("小形象款式") { Menu = petStyleMenu };
            petStyleMenu.Opening += (_, _) =>
            {
                foreach (var it in petStyleItems) it.Item.IsChecked = _canvas.Pet.Style == it.Id;
            };
            menu.Items.Add(petStyleHost);

            // ---------- 收起态装饰（参考 Vibe Island，两元素逐项开关）----------
            var decorMenu = new NativeMenu();
            void AddDecor(string label, Func<bool> get, Action<bool> set, Action<Island.App.Services.Settings, bool> persist)
            {
                var mi = new NativeMenuItem(label) { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = get() };
                mi.Click += (_, _) =>
                {
                    bool next = !get();
                    set(next);                                   // 画布即时生效（setter 标脏）
                    UpdateSetting(s => persist(s, next));        // 落盘，重启保留
                    RefreshCollapsedWidth();                     // 造型变了 → 胶囊要重新测宽
                };
                decorMenu.Items.Add(mi);
            }
            AddDecor("刘海融合造型", () => _canvas.DecorNotch, v => _canvas.DecorNotch = v, (s, v) => s.DecorNotch = v);
            AddDecor("字母标识环 C/M/G/D", () => _canvas.DecorLetters, v => _canvas.DecorLetters = v, (s, v) => s.DecorLetters = v);
            var decorHost = new NativeMenuItem("收起态装饰") { Menu = decorMenu };
            decorMenu.Opening += (_, _) =>
            {
                var items = decorMenu.Items.OfType<NativeMenuItem>().ToList();
                if (items.Count == 2)
                {
                    items[0].IsChecked = _canvas.DecorNotch;
                    items[1].IsChecked = _canvas.DecorLetters;
                }
            };
            menu.Items.Add(decorHost);

            // ---------- 信息胶囊样式（第五十四轮审美改造的开关：新版 ↔ 经典版）----------
            var style2Menu = new NativeMenu();
            var heroItem = new NativeMenuItem("数字主角") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = _canvas.ComboNumericHero };
            var classicItem = new NativeMenuItem("经典版（时间同级）") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = !_canvas.ComboNumericHero };
            void SetComboStyle(bool hero)
            {
                _canvas.ComboNumericHero = hero;              // 画布即时生效（setter 标脏）
                UpdateSetting(s => s.ComboNumericHero = hero); // 落盘，重启保留
                RefreshCollapsedWidth();                       // 字号变了 → 胶囊要重新测宽
                heroItem.IsChecked = hero;
                classicItem.IsChecked = !hero;
            }
            heroItem.Click += (_, _) => SetComboStyle(true);
            classicItem.Click += (_, _) => SetComboStyle(false);
            style2Menu.Items.Add(heroItem);
            style2Menu.Items.Add(classicItem);
            var style2Host = new NativeMenuItem("胶囊样式") { Menu = style2Menu };
            style2Menu.Opening += (_, _) =>
            {
                heroItem.IsChecked = _canvas.ComboNumericHero;
                classicItem.IsChecked = !_canvas.ComboNumericHero;
            };
            menu.Items.Add(style2Host);

            // ---------- 行为设置 ----------
            var behMenu = new NativeMenu();
            // 常驻置顶
            var topItem = new NativeMenuItem("常驻置顶") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = _alwaysOnTop };
            topItem.Click += (_, _) =>
            {
                _alwaysOnTop = !_alwaysOnTop;
                ApplyAlwaysOnTop();
                UpdateSetting(s => s.AlwaysOnTop = _alwaysOnTop);
            };
            behMenu.Items.Add(topItem);
            // 鼠标穿透（游戏时不误触）
            var thruItem = new NativeMenuItem("鼠标穿透") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = _clickThrough };
            thruItem.Click += (_, _) =>
            {
                _clickThrough = !_clickThrough;
                ApplyClickThrough();
                UpdateSetting(s => s.ClickThrough = _clickThrough);
            };
            behMenu.Items.Add(thruItem);
            behMenu.Items.Add(new NativeMenuItemSeparator());
            // 展开方式
            var expMenu = new NativeMenu();
            var expItems = new List<(string Name, string Label, NativeMenuItem Item)>();
            foreach (var (n, label) in new[] { ("hover", "悬停展开"), ("always", "常驻展开"), ("never", "仅点击展开") })
            {
                var mi = new NativeMenuItem(label) { ToggleType = NativeMenuItemToggleType.Radio, IsChecked = _expandMode == n };
                mi.Click += (_, _) =>
                {
                    _expandMode = n;
                    ApplyExpandMode();
                    UpdateSetting(s => s.ExpandMode = n);
                };
                expItems.Add((n, label, mi));
                expMenu.Items.Add(mi);
            }
            var expHost = new NativeMenuItem("展开方式") { Menu = expMenu };
            expMenu.Opening += (_, _) =>
            {
                foreach (var it in expItems) it.Item.IsChecked = _expandMode == it.Name;
            };
            behMenu.Items.Add(expHost);

            // 剪贴板保留策略（第五十九轮）：退出时清除 / 1 天 / 7 天 / 30 天。
            // 非「退出时清除」会把记录落到本机（%LOCALAPPDATA%/MyIsland/clipboard），过期自动清。
            var clipRetMenu = new NativeMenu();
            var clipRetItems = new List<(string Name, NativeMenuItem Item)>();
            foreach (var n in ClipboardHistory.RetentionOptions)
            {
                var mi = new NativeMenuItem(ClipboardHistory.RetentionLabel(n)) { ToggleType = NativeMenuItemToggleType.Radio, IsChecked = _clipRetention == n };
                mi.Click += (_, _) =>
                {
                    _clipRetention = n;
                    UpdateSetting(s => s.ClipboardRetention = n);
                    // 立即按新策略处理：切到「退出时清除」→ 删档；切到 N 天 → 落盘当前记录
                    if (ClipboardHistory.RetentionDays(n) <= 0) _clips.Save(n);
                    else { _clips.ApplyRetention(n); _clips.Save(n); }
                    _canvas.Clips = _clips.Items;
                    _canvas.MarkDirty();
                };
                clipRetItems.Add((n, mi));
                clipRetMenu.Items.Add(mi);
            }
            var clipRetHost = new NativeMenuItem("剪贴板保留") { Menu = clipRetMenu };
            clipRetMenu.Opening += (_, _) =>
            {
                foreach (var it in clipRetItems) it.Item.IsChecked = _clipRetention == it.Name;
            };
            behMenu.Items.Add(clipRetHost);

            // 启动时自动关闭内建屏（第六十四轮加开关）。
            // 背景：2026-11-25 14:59 的 DCPEXT1 固件 panic（dual_pipe.c:321，双显示管道路径）
            // 发生在本功能制造的「内建屏禁用 + 外接屏」拓扑下 —— 归因是苹果固件 bug，
            // 但诱因关联无法排除。此前这个功能只有「用户在屏幕页主动关/恢复过」这一条
            // 隐式设置路径，用户既不知道它存在、也没有总开关。现在给一个显式开关：
            // 复发 dual_pipe panic 时可一键停用观察（参见 app.log 里的 [panic] 侦测输出）。
            bool autoClose = false;
            try { autoClose = Island.App.Services.Settings.Load().AutoCloseBuiltin; } catch { }
            var autoCloseItem = new NativeMenuItem("启动时关闭内建屏") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = autoClose };
            autoCloseItem.Click += (_, _) =>
            {
                // 开关语义：
                // - 开→关：若内建屏当前处于本应用禁用的状态，立即恢复（否则开关与实态不符）。
                // - 关→开：不立即关（启动时才执行 —— 关屏需要外接屏在线等前提，启动路径已含全部判断）。
                bool next = !autoCloseItem.IsChecked;
                if (!next && DisplayControl.IsBuiltinOff)
                    RunBg(() => DisplayControl.RestoreBuiltin());
                UpdateSetting(s => s.AutoCloseBuiltin = next);
                autoCloseItem.IsChecked = next;
                Console.Error.WriteLine($"[display] 启动自动关内建屏开关 → {next}");
            };
            behMenu.Items.Add(autoCloseItem);

            menu.Items.Add(new NativeMenuItem("行为") { Menu = behMenu });

            menu.Items.Add(Item("退出", Close));
            _tray.Menu = menu;
            _tray.IsVisible = true;
        }
        catch (Exception ex)
        {
            // 托盘不可用的环境：降级，但把原因写出来（之前静默吞掉，菜单栏图标消失无从排查）
            Console.Error.WriteLine($"[tray] init failed: {ex}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // 退出前保存剪贴板历史（仅在「保留 N 天」策略下；session 策略 = 退出即清，
        // Save 会把旧档目录删掉，正好实现「退出时清除」）
        try { _clips.Save(_clipRetention); }
        catch (Exception ex) { Console.Error.WriteLine($"[clipboard] save on exit: {ex.Message}"); }
        // 全部计时器必须停：_cycleTimer 若处于 pet/combo 轮换中漏停，
        // 其 Tick 闭包捕获 this → Dispatcher 根引用整个窗口无法 GC，且退出后仍在渲染。
        _frame.Stop();
        _tick.Stop();
        _hoverTimer.Stop();
        _hoverTimer.Tick -= OnHoverTick;
        _cycleTimer.Stop();
        _weatherTimer.Stop();
        _weatherUiTimer.Stop();
        _clipsTimer.Stop();
        _clipSaveTimer?.Stop();
        _screensTimer.Stop();
        _builtinCountdownTimer.Stop();
        _islandSwitchTimer.Stop();
        _clips.Dispose();
        _hotKeys.Dispose();   // 热键探针：发 quit → 等待 → 兜底 Kill（确定性退出，不靠 stdin EOF 偶然生效）
        // 退出前必须恢复内建屏：否则用户会面对一块黑掉的笔记本屏。
        // 同步等最多 3 秒（进程要退出了，不能 fire-and-forget）；超时则守护进程靠 stdin 断开兜底。
        if (OperatingSystem.IsMacOS())
        {
            var restore = System.Threading.Tasks.Task.Run(() => DisplayControl.RestoreBuiltin());
            restore.Wait(3000);
        }
        _autoFollow?.Dispose();
        _weather.Dispose();
        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();   // 释放原生菜单栏句柄（菜单项闭包捕获 this）
            _tray = null;
        }
        _sampler.Dispose();
        base.OnClosed(e);
    }
}