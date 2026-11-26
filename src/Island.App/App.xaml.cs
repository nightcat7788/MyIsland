using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Island.App.Controls;
using Island.App.Services;
using static Island.Native.NativeMethods;

namespace Island.App;

/// <summary>应用入口：单实例、Per-Monitor DPI、托盘图标、右键菜单、主窗口生命周期。</summary>
public partial class App : Application
{
    // 单实例互斥体与二次启动唤起事件。
    // 用 Local\ 前缀（按会话隔离）：不同用户登录在不同会话，各自的 Local 命名空间天然独立，
    // 不需要再拼用户名——拼了反而会让安装包的 AppMutex 匹配不上（升级时关不掉旧实例）。
    private static string MutexName => @"Local\MyIsland.Instance";
    private static string RelaunchEventName => @"Local\MyIsland.Relaunch";

    /// <summary>崩溃日志上限（约 1MB）：超过就删掉旧文件重开一份，免得日志无限膨胀。</summary>
    private const long CrashLogLimit = 1_048_576;

    private Mutex? _mutex;
    private bool _firstInstance;
    private IslandWindow? _island;
    private TrayIcon? _tray;
    private System.Windows.Controls.ContextMenu? _menu;
    private Settings _settings = null!;
    private Themes.Palette _palette = Themes.Palette.Dark;

    // 二次启动唤起通道：第一个实例建事件并等信号，第二个实例发信号后静默退出
    private EventWaitHandle? _relaunchEvent;
    private RegisteredWaitHandle? _relaunchListener;
    // 唤起信号早于岛体窗口建成时先记在这里，窗口一出现就补上（否则这次唤起会丢）
    private bool _relaunchPending;

    /// <summary>
    /// 内存加载引导（run.ps1 调用）：自定义 ALC 插件模式。
    /// 把应用与全部第三方依赖按字节载入独立 AssemblyLoadContext，
    /// 由 ALC.Load 统一解析（框架 DLL 回落默认 ALC 磁盘加载）。
    /// 阻塞运行直到应用退出（消息泵在本 STA 线程）。
    /// </summary>
    public static void Bootstrap(string dir)
    {
        var alc = new IslandAlc(dir);
        using var ms = new MemoryStream(File.ReadAllBytes(Path.Combine(dir, "Island.App.dll")));
        var inner = alc.LoadFromStream(ms);
        var appType = inner.GetType("Island.App.App") ?? throw new InvalidOperationException("App type not found");
        var launch = appType.GetMethod("Launch", Type.EmptyTypes) ?? throw new InvalidOperationException("Launch not found");
        launch.Invoke(null, null);
    }

    /// <summary>独立 ALC：请求任何程序集都优先从发布目录按字节加载（绕 WDAC）。</summary>
    private sealed class IslandAlc : System.Runtime.Loader.AssemblyLoadContext
    {
        private readonly string _dir;
        public IslandAlc(string dir) : base("IslandAlc") => _dir = dir;

        protected override Assembly? Load(AssemblyName name)
        {
            if (string.IsNullOrEmpty(name.Name)) return null;
            string p = Path.Combine(_dir, name.Name + ".dll");
            if (File.Exists(p))
            {
                try
                {
                    using var ms = new MemoryStream(File.ReadAllBytes(p));
                    return LoadFromStream(ms);
                }
                catch { return null; }
            }
            return null; // 回落默认 ALC（微软签名框架 DLL，磁盘加载）
        }
    }

    /// <summary>反射/内存加载启动入口（run.ps1 用）：等价于自动生成的 Main()。</summary>
    public static void Launch()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 软件渲染（2026-11-25，产品拍板）：本应用只有 AllowsTransparency 的分层窗口，永远用不上
        // 硬件加速 —— WPF 却为它创建 D3D 设备，把 Intel 显卡驱动的两百多兆拉进进程（实测工作集省约 57 MB）。
        // 必须在创建任何窗口之前设置。代价（已接受，见改进记录十一节）：文字抗锯齿的亚像素差异
        //（约 1% 像素，放大 5 倍肉眼才看出）、不透明对话框从 ClearType 变灰度、栅格化落到 UI 线程
        //（实测收起态/展开态 CPU 无可见回退）。要回硬件渲染：删掉这一行。
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // 启动按步走，每步单独兜住异常（Step）。
        // 以前是一整段顺序执行：中途抛异常就把后面全跳过 —— 岛体窗口一失败，
        // 托盘图标、右键菜单、设置中心全都没有，用户既进不去设置也退不出进程。
        // 分步之后某一步失败只影响它自己，岛体挂了托盘照样出来，用户还能从托盘退出/打开设置。

        // Per-Monitor DPI Awareness V2（必须在创建任何窗口前调用；失败只影响清晰度，不影响后续）
        Step("DPI 感知", () =>
        {
            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
        });

        base.OnStartup(e);

        // 单实例互斥体：拿到锁的才是主实例
        // 以管理员重启：新实例先等旧实例真正退出，再去抢单实例互斥体。
        // 不等的话两个进程抢同一个互斥体，谁后启动谁就判定"已有实例"→ 发完唤起信号自己退出，
        // 而旧实例紧接着也退出 ⇒ 岛体和托盘一起消失（2026-11-23 审查 P1-17）。
        int oldPid = ParseRestartPid(e.Args);
        if (oldPid > 0) WaitForProcessExit(oldPid, 15000);

        Step("单实例互斥", () =>
        {
            _mutex = new Mutex(true, MutexName, out _firstInstance);
            GC.KeepAlive(_mutex);
        });
        if (_mutex is null)
        {
            // 连互斥体都没拿到（权限/句柄问题），后面的一切都没有意义
            Shutdown();
            return;
        }
        if (!_firstInstance)
        {
            // 已有实例在跑：给它发唤起信号，让它把岛体显示出来，然后自己静默退出
            if (TryWakeRunningInstance())
            {
                Shutdown();
                return;
            }
            // 对方还没建好唤起事件（刚起来就被再次拉起），只能退回原来的提示框
            // MessageBox 置顶：分层窗口应用没有任务栏入口，普通提示框可能埋在窗口堆后面
            System.Windows.MessageBox.Show(
                "Island 已在运行（托盘图标可管理）。", "Island",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information,
                System.Windows.MessageBoxResult.OK, System.Windows.MessageBoxOptions.DefaultDesktopOnly);
            Shutdown();
            return;
        }

        // 三路异常捕获（参考 BrimDeck）：UI 线程 / 后台线程 / 未观测 Task
        // 后台线程（监控采样、毛玻璃、媒体轮询）异常若不挂钩会直接杀死进程
        Step("异常捕获", () =>
        {
            System.AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                LogException(args.ExceptionObject as Exception ?? new Exception("非 UI 线程异常"));
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                LogException(args.Exception);
                args.SetObserved();
            };
            // UI 线程异常：记日志后继续跑，窗口不崩
            DispatcherUnhandledException += (_, args) =>
            {
                LogException(args.Exception);
                args.Handled = true;
            };
        });

        // 二次启动唤起通道：尽早建立，缩短"第二个实例发信号时对方还没建好事件"的空窗期
        Step("二次启动唤起通道", () =>
        {
            _relaunchEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RelaunchEventName);
            // AutoReset + 无限等待：第二个实例 Set() 一次，第一个实例的回调就被唤起一次
            _relaunchListener = ThreadPool.RegisterWaitForSingleObject(_relaunchEvent,
                (_, _) => { try { Dispatcher.BeginInvoke(new Action(ShowIslandFromRelaunch)); } catch { } },
                null, Timeout.Infinite, false);
        });

        // 加载设置
        Step("加载设置", () =>
        {
            // Settings.Load 内部已经兜住损坏的文件；这里再兜一层：
            // 万一它抛异常，也用默认设置继续，不让后面的步骤因为空引用整体失败
            try { _settings = Settings.Load(); }
            catch (Exception ex) { LogStepFailure("加载设置", ex); _settings = new Settings(); }
        });

        // 温度驱动（PawnIO，2026-11-24）：LibreHardwareMonitorLib 只会打开驱动设备、不会装驱动，
        // 干净的机器上即使以管理员运行也永远读不到 CPU 温度。以管理员运行且驱动缺失/过旧时，
        // 在后台把它装好（PawnIoInstaller.EnsureInstalled 会等安装器退出，可能长达两分钟）。
        // 所以放线程池：UI 线程不等，启动也不等。装完之后温度服务会在一个采样间隔内自己恢复读数
        //（ThermalService 的自愈），这里不需要、也不应该去等它。
        // 非管理员：不起任务（装驱动要管理员；这里也从不拉 UAC，提权与否由用户从托盘菜单决定）。
        Step("温度驱动", StartDriverInstall);

        // SMN 直读默认关闭：它会加载未签名内核驱动并映射 PCIe 物理内存，不能因 CPU 厂商
        // 或温度读数缺失而替用户开启。AMD 设备只在设置中心由用户主动确认后才允许启用。
        // 保留 SmnAutoDone 仅用于兼容旧配置，不再执行自动开启或自动纠正。
        Step("SMN 安全策略", () =>
        {
            if (!_settings.EnableSmnDirectRead)
                _settings.SmnAutoDone = true;
        });

        // 显卡探测（2026-11-24）：在后台先问清楚"机器上的 Intel 显卡是不是全是核显"（约 55ms，
        // 走开源库自己的 D3D 探测），结果缓存；温度服务创建第一个 Computer 时直接用，不在 UI 线程上现探。
        // 只有核显的机器上，随后会拦下库对 Intel GCL 计算栈的初始化 —— 那一步实测会造成
        // 700 MB – 4.5 GB 的启动内存尖峰（之前一直没查到的"峰值工作集 929 MB"），见 IntelComputePolicy。
        Step("显卡探测", () => _ = System.Threading.Tasks.Task.Run(Services.IntelComputePolicy.ShouldSkipOnThisMachine));

        // 剪贴板：接上"要排除的来源进程"设置项。
        // 此前 SetExcludedProcesses 全仓没有调用方 ⇒ 排除列表恒为空 ⇒
        // 默认连密码管理器复制的内容一起记录（最长 24 小时）。
        Step("剪贴板排除列表", () =>
            Services.ClipboardService.SetExcludedProcesses(_settings.ClipboardExcludedProcesses));

        // 创建并显示岛体窗口（最容易失败的一步：毛玻璃、显示器枚举、监控服务）
        // 失败只记日志，托盘图标与右键菜单照样起来，用户仍能从托盘退出或打开设置
        Step("创建并显示岛体窗口", () =>
        {
            _island = new IslandWindow(_settings);
            // 面板顶栏的设置齿轮 → 设置中心（2026-11-25 UI 对齐 BrimDeck；齿轮在面板上，弹出的设置窗口置顶）
            _island.SettingsRequested += () => ShowSettingsWindow();
            // 非管理员运行时，监控页温度位显示"需管理员"提示（温度链路需管理员 + PawnIO）
            _island.MonitorTempNeedAdmin = !IsElevated();
            // 管理员运行、但温度驱动还没到位（正在后台装，或装失败了）：显示"缺驱动"。
            // 只在管理员时读注册表；装完之后由 OnDriverInstallDone 再同步一次。
            _island.MonitorTempNoDriver = IsElevated() && !Services.ThermalService.DriverPresent;
            _island.Show();
            // 唤起信号比窗口先到时记过一次，这里补上
            if (_relaunchPending) { _relaunchPending = false; ShowIslandFromRelaunch(); }
        });

        // 托盘图标：岛体窗口失败时也要有，否则用户没有任何入口退出
        Step("设置托盘图标", () =>
        {
            _tray = new TrayIcon("Island · Windows 灵动岛");
            _tray.LeftClicked += () => _island?.ToggleExpand();
            _tray.RightClicked += OpenTrayMenu;
        });

        // 右键菜单：托盘右键与岛体右键共用一份
        Step("右键菜单", () =>
        {
            _menu = BuildContextMenu();
            // 岛体自带右键菜单（窗口内部右键会打开它）；岛体没起来时菜单只挂在托盘上
            if (_island is { } island) island.ContextMenu = _menu;
        });

        // 设置中心：先把主题色板解析出来（设置窗口按它上色）
        // 打开设置中心时仍会按当时的设置重新解析，用户在设置里换了主题才立刻生效
        Step("设置中心", () => _palette = ResolvePalette());
    }

    /// <summary>
    /// 启动分步：每步单独兜住异常，某一步失败不影响后面的步骤（失败写进 crash.log）。
    /// 内存耗尽时不再往下走：堆已经不可信，继续跑只会把问题放大。
    /// </summary>
    private void Step(string name, Action step)
    {
        try { step(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { LogStepFailure(name, ex); }
    }

    /// <summary>第二个实例：给已在跑的实例发唤起信号。发成功就静默退出。</summary>
    private static bool TryWakeRunningInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(RelaunchEventName);
            return handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }  // 对方还没建好事件
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>第二个实例发来信号：把已有实例的岛体显示出来并展开。</summary>
    private void ShowIslandFromRelaunch()
    {
        if (_island is null)
        {
            // 岛体窗口还没建好（启动期被唤起）：先记下来，等窗口出来再补
            _relaunchPending = true;
            return;
        }
        // 唤起即"展开到"，不是"切换"：已经是展开态时不该被收起
        _island.ShowAndExpand();
    }

    /// <summary>托盘右键：打开右键菜单。岛体没起来时菜单只从这里用。</summary>
    private void OpenTrayMenu()
    {
        if (_menu is null) return;
        RefreshDeviceMenu();
        if (_island is { } island) _menu.PlacementTarget = island;
        _menu.IsOpen = true;
    }

    // 设备电量子菜单（BuildContextMenu 里挂好，打开时刷新内容）
    private System.Windows.Controls.MenuItem? _devicesMenu;

    /// <summary>
    /// 托盘菜单里的「鼠标穿透」勾选项（2026-11-27 加）。
    /// 为什么必须有它：穿透一开，岛体整体对鼠标透明 ⇒ **面板看得见却点不动**，
    /// 连面板里那个开关都点不回来（鸡生蛋）。托盘图标在岛体窗口之外、不受穿透影响，
    /// 是唯一能一键退出的地方 —— 之前只有「设置中心…」这一条远路。
    /// </summary>
    private System.Windows.Controls.MenuItem? _clickThroughItem;

    /// <summary>把「鼠标穿透」的当前状态刷进托盘菜单（面板里改过也要同步）。</summary>
    private void RefreshClickThroughItem()
    {
        if (_clickThroughItem is null) return;
        bool on = _settings.ClickThrough;
        if (_clickThroughItem.IsChecked == on) return;
        _clickThroughItem.IsChecked = on;   // 会触发 Checked/Unchecked ⇒ SetClickThrough（幂等）
    }

    /// <summary>把当前设备电量读数刷进"设备电量"子菜单（纯展示行，不可点）。</summary>
    private void RefreshDeviceMenu()
    {
        if (_devicesMenu is null) return;
        Services.DeviceService.Poke();   // 定时器停着时也补一拍：下次打开就是新值
        _devicesMenu.Items.Clear();
        var devices = Services.DeviceService.Snapshot;
        if (devices.Count == 0)
        {
            _devicesMenu.Items.Add(new System.Windows.Controls.MenuItem
            { Header = "（暂无设备读数）", IsEnabled = false });
            return;
        }
        foreach (var d in devices)
        {
            // 档位设备（手柄）显示"空/低/中/满"，百分比设备显示数字；充电态跟一句说明
            string value = d.LevelText ?? $"{d.Percent}%";
            string row = $"{d.Name}  {value}{(d.Charging ? "（充电中）" : "")}";
            _devicesMenu.Items.Add(new System.Windows.Controls.MenuItem { Header = row, IsEnabled = false });
        }
    }

    /// <summary>主题色板：设置中心按它上色；解析不出就用上次解析成功的那份。</summary>
    private Themes.Palette ResolvePalette()
    {
        string theme = _settings.Theme;
        _palette = Themes.Palette.ByName(theme)
            ?? (theme == "Light" ? Themes.Palette.Light : Themes.Palette.Dark);
        return _palette;
    }

    internal static void LogException(Exception? ex) => WriteLog($"[{DateTime.Now}] {ex}\n");

    /// <summary>启动某一步失败：连步骤名一起写进日志，便于定位是哪一步挂的。</summary>
    private static void LogStepFailure(string step, Exception ex)
        => WriteLog($"[{DateTime.Now}] [启动步骤:{step}] {ex}\n");

    /// <summary>
    /// 崩溃日志：写在 %LOCALAPPDATA%\MyIsland\crash.log。
    /// 以前是无脑追加，日志会一直涨到把磁盘塞满；现在超过约 1MB 就删掉旧文件重开一份。
    /// 写日志失败只吞掉：不能因为日志写不进去把应用带崩。
    /// </summary>
    private static void WriteLog(string text)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "MyIsland");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "crash.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > CrashLogLimit) File.Delete(path);
            File.AppendAllText(path, text);
        }
        catch { /* 日志写失败不影响运行 */ }
    }

    private System.Windows.Controls.ContextMenu BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        menu.Items.Add(MenuItem("展开 / 收起", (_, _) => _island?.ToggleExpand()));
        // 鼠标穿透：可勾选，一键进出（穿透状态下这是唯一能退出的入口）
        _clickThroughItem = new System.Windows.Controls.MenuItem
        {
            Header = "鼠标穿透（游戏时不拦截点击）",
            IsCheckable = true,
        };
        _clickThroughItem.Checked += (_, _) => _island?.SetClickThrough(true);
        _clickThroughItem.Unchecked += (_, _) => _island?.SetClickThrough(false);
        menu.Items.Add(_clickThroughItem);
        // 菜单打开时同步状态（穿透可能是在面板里改的）：菜单只有一条 Opening 钩子，
        // 托盘右键与岛体右键共用这一份菜单 ⇒ 两条入口都覆盖到。
        menu.Opened += (_, _) => RefreshClickThroughItem();
        menu.Items.Add(MenuItem("设置中心…", (_, _) => ShowSettingsWindow()));
        // 设备电量（2026-11-25）：菜单是启动时建好的单例，内容在打开时刷新（RefreshDeviceMenu）
        _devicesMenu = new System.Windows.Controls.MenuItem { Header = "设备电量" };
        menu.Items.Add(_devicesMenu);
        menu.Items.Add(new System.Windows.Controls.Separator());

        // 工具：剪贴板/文件中转
        var tools = new System.Windows.Controls.MenuItem { Header = "工具" };
        tools.Items.Add(MenuItem("打开文件中转目录", (_, _) =>
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "MyIsland", "FileTray");
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = dir, UseShellExecute = true });
        }));
        tools.Items.Add(MenuItem("清空剪贴板历史", (_, _) => Services.ClipboardService.Clear()));
        menu.Items.Add(tools);

        menu.Items.Add(new System.Windows.Controls.Separator());
        // 温度读数需要管理员（PawnIO）：非管理员时给重启入口
        if (!IsElevated())
            menu.Items.Add(MenuItem("以管理员重启（获取温度读数）", (_, _) => RestartAsAdmin()));
        menu.Items.Add(MenuItem("退出", (_, _) => Shutdown()));
        return menu;
    }

    /// <summary>打开设置中心（全部设置项统一入口）。</summary>
    private void ShowSettingsWindow()
    {
        // 岛体窗口没起来时也能开：宿主回调空转，用户照样能改主题、看设置并从托盘退出
        var palette = ResolvePalette();
        var win = new Island.App.Controls.SettingsWindow(_settings, palette, new Island.App.Controls.SettingsWindow.Host
        {
            SetTheme = SetTheme,
            SetEdge = SetEdge,
            SetExpandMode = m => _island?.SetExpandMode(m),
            SetCompactView = v => _island?.SetCompactView(v),
            SetPetStyle = s => _island?.SetPetStyle(s),
            SetWeatherCity = c => _island?.SetWeatherCity(c),
            ShowDashSettings = () => _island?.ShowDashSettings(),
            ShowComboSettings = () => _island?.ShowComboSettings(),
            ShowMonitorSettings = () => _island?.ShowMonitorSettings(),
            RestartAsAdmin = RestartAsAdmin,
            SetClickThrough = on => _island?.SetClickThrough(on),
            SetComboNumericHero = on => _island?.SetComboNumericHero(on),
            SetSmnDirectRead = on => _island?.SetSmnDirectRead(on),
            SetMonitorInterval = ms => _island?.SetMonitorInterval(ms),
            SetPreferredNic = id => _island?.SetPreferredNic(id),
            PromptCity = PromptCity,
            IsElevated = IsElevated(),
        });
        win.ShowDialog();
        // 设置窗口已关闭：申请一次"用完即还"（2026-11-24；不阻塞，确认 UI 空闲后才回收）
        Services.MemoryReclaim.Request();
    }

    /// <summary>
    /// 当前进程是否以管理员运行。温度链路用它：非管理员时不装驱动、监控页显示"需管理员"。
    /// 取不到身份时按非管理员处理：最坏只是不装驱动、不显示温度，不会在没权限时去跑安装器。
    /// </summary>
    internal static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>驱动安装任务：只有"以管理员启动，且需要安装"时才有；null = 没有在装。</summary>
    internal static System.Threading.Tasks.Task<string>? DriverInstall { get; private set; }

    /// <summary>驱动安装的结果，与写进 run.log 的那一行相同；没装过为 null。</summary>
    internal static string? DriverInstallStatus { get; private set; }

    /// <summary>在后台装温度驱动（启动步骤"温度驱动"）。立刻返回，不等安装。</summary>
    private void StartDriverInstall()
    {
        if (!IsElevated()) return;
        var task = System.Threading.Tasks.Task.Run(Services.PawnIoInstaller.EnsureInstalled);
        DriverInstall = task;
        // 继续体固定在线程池上执行，不挂在当时的同步上下文上：写日志用不着 UI 线程
        _ = task.ContinueWith(
            OnDriverInstallDone,
            System.Threading.CancellationToken.None,
            System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
            System.Threading.Tasks.TaskScheduler.Default);
    }

    /// <summary>驱动安装结束（成功、失败、超时都算）：记一行 run.log，再让窗口把"缺驱动"状态词同步一次。</summary>
    private void OnDriverInstallDone(System.Threading.Tasks.Task<string> task)
    {
        // EnsureInstalled 自己不抛；这里再兜一层，状态行里不出现"没有结果"这种空话
        string status = task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion
            ? task.Result
            : $"任务异常结束（{task.Status}）";
        DriverInstallStatus = status;
        // 走 SystemMonitor.AppendRunLog：与采样线程写 run.log 共用同一把进程内锁，否则两行会写花
        Native.SystemMonitor.AppendRunLog($"[{DateTime.Now:HH:mm:ss}] pawnio: {status}\n");
        // 这里在线程池线程上：窗口只能在它的 UI 线程上碰
        try { _island?.Dispatcher.BeginInvoke(new Action(() => _island?.SyncTempHints())); }
        catch { /* 窗口已经在关闭：没有可同步的东西 */ }
    }

    /// <summary>以管理员重启时传给新实例的标记：--elevated-restart &lt;旧实例PID&gt;。</summary>
    private const string RestartFlag = "--elevated-restart";

    /// <summary>
    /// 解析"我是被提权重启拉起来的"标记，返回旧实例的 PID（没有则 0）。
    /// 命令行参数在 exe 直跑模式下可用；内存加载模式用环境变量 ISLAND_ELEVATED_RESTART。
    /// </summary>
    private static int ParseRestartPid(string[]? args)
    {
        if (args is not null)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].Equals(RestartFlag, StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int pid) && pid > 0) return pid;
                return 0;
            }
        }
        string? env = Environment.GetEnvironmentVariable("ISLAND_ELEVATED_RESTART");
        return int.TryParse(env, out int envPid) && envPid > 0 ? envPid : 0;
    }

    /// <summary>等某个进程真正退出（最多 timeoutMs 毫秒）。</summary>
    private static void WaitForProcessExit(int pid, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                if (p.HasExited) return;
            }
            catch (ArgumentException) { return; }   // 进程已不存在
            System.Threading.Thread.Sleep(100);
        }
    }

    /// <summary>以管理员重启：exe 直跑模式重启自身；内存加载模式调用 start-admin.ps1。</summary>
    private static void RestartAsAdmin()
    {
        try
        {
            System.Diagnostics.ProcessStartInfo psi;
            string? islandDir = Environment.GetEnvironmentVariable("ISLAND_DIR");
            string exe = Environment.ProcessPath ?? "";
            int myPid = Environment.ProcessId;
            if (islandDir is null && exe.EndsWith("Island.App.exe", StringComparison.OrdinalIgnoreCase))
            {
                // 把"我是被提权重启拉起来的 + 旧实例 PID"告诉新实例：
                // 新实例会等旧进程退出后再接管单实例互斥体，两个实例就不会互相把对方当成"第二实例"退出。
                psi = new() { FileName = exe, UseShellExecute = true, Verb = "runas", Arguments = $"{RestartFlag} {myPid}" };
            }
            else if (islandDir is not null &&
                     System.IO.File.Exists(System.IO.Path.Combine(islandDir, "start-admin.ps1")))
            {
                psi = new()
                {
                    FileName = "powershell.exe", UseShellExecute = true, Verb = "runas",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{System.IO.Path.Combine(islandDir, "start-admin.ps1")}\"",
                };
            }
            else
            {
                System.Windows.MessageBox.Show("内存加载模式：请用 start-admin.ps1 以管理员启动。", "MyIsland");
                return;
            }
            System.Diagnostics.Process.Start(psi);
            Current?.Shutdown();
        }
        catch { /* 用户取消 UAC：保持当前实例 */ }
    }

    private System.Windows.Controls.MenuItem MenuItem(string header, RoutedEventHandler click)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };
        item.Click += click;
        return item;
    }

    /// <summary>自定义城市输入对话框（支持中文/英文城市名，走 Open-Meteo 地理编码）。</summary>
    private static string? PromptCity()
    {
        var win = new System.Windows.Window
        {
            Title = "自定义天气城市",
            Width = 320, Height = 150,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            Topmost = true,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 30, 38)),
        };
        var tb = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(16, 14, 16, 0),
            Height = 30, FontSize = 14,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 43, 54)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            ToolTip = "输入城市名（中文或英文），如：成都 / Hangzhou",
        };
        string? result = null;
        var ok = new System.Windows.Controls.Button
        {
            Content = "确定", Width = 72, Height = 28, IsDefault = true,
            Margin = new Thickness(0, 12, 12, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "取消", Width = 72, Height = 28, IsCancel = true,
            Margin = new Thickness(0, 12, 16, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        ok.Click += (_, _) =>
        {
            string t = tb.Text.Trim();
            if (t.Length > 0) { result = t; win.Close(); }
            else tb.Focus();
        };
        var btnPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        btnPanel.Children.Add(cancel);
        btnPanel.Children.Add(ok);
        var root = new System.Windows.Controls.StackPanel();
        root.Children.Add(tb);
        root.Children.Add(btnPanel);
        win.Content = root;
        win.ShowDialog();
        return result;
    }

    private void SetTheme(string theme)
    {
        _settings.Theme = theme;
        _settings.Save();
        _island?.ApplyThemeExternal();
    }

    private void SetEdge(string edge)
    {
        _settings.Edge = edge;
        _settings.Save();
        _island?.RepositionExternal();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 唤起通道要释放：不释放的话，第二个实例会对着一个已经没人等的信号发信号
        _relaunchListener?.Unregister(null);
        _relaunchEvent?.Dispose();
        _tray?.Dispose();
        _settings?.Save();
        base.OnExit(e);
    }
}
