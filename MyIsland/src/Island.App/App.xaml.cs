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
    private Mutex? _mutex;
    private IslandWindow? _island;
    private TrayIcon? _tray;
    private Settings _settings = null!;

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
        // Per-Monitor DPI Awareness V2（必须在创建任何窗口前调用）
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }

        base.OnStartup(e);

        // 单实例
        _mutex = new Mutex(true, "Local\\MyIsland.Instance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("Island 已在运行", "Island");
            Shutdown();
            return;
        }
        GC.KeepAlive(_mutex);

        // 三路异常捕获（参考 BrimDeck）：UI 线程 / 后台线程 / 未观测 Task
        // 后台线程（监控采样、毛玻璃、媒体轮询）异常若不挂钩会直接杀死进程
        System.AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogException(args.ExceptionObject as Exception ?? new Exception("非 UI 线程异常"));
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException(args.Exception);
            args.SetObserved();
        };

        _settings = Settings.Load();
        _island = new IslandWindow(_settings);
        _island.ContextMenu = BuildContextMenu();
        _island.Show();

        _tray = new TrayIcon("Island · Windows 灵动岛");
        _tray.LeftClicked += () => _island?.ToggleExpand();
        _tray.RightClicked += () =>
        {
            if (_island?.ContextMenu is { } menu)
            {
                menu.PlacementTarget = _island;
                menu.IsOpen = true;
            }
        };

        DispatcherUnhandledException += (_, args) =>
        {
            LogException(args.Exception);
            args.Handled = true;
        };
    }

    internal static void LogException(Exception? ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "MyIsland");
            Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now}] {ex}\n");
        }
        catch { }
    }

    private System.Windows.Controls.ContextMenu BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        menu.Items.Add(MenuItem("展开 / 收起", (_, _) => _island?.ToggleExpand()));
        menu.Items.Add(new System.Windows.Controls.Separator());

        var theme = new System.Windows.Controls.MenuItem { Header = "面板主题" };
        var themes = new[] { ("Dark", "深色"), ("Light", "浅色"), ("OneDark", "One Dark Pro"),
                 ("GitHubDark", "GitHub Dark"), ("Dracula", "Dracula"), ("NightOwl", "Night Owl"),
                 ("Island", "海岛假日"), ("Cyberpunk", "赛博朋克"), ("Pixel", "像素方块"), ("Auto", "跟随系统+壁纸") };
        for (int i = 0; i < themes.Length; i++)
        {
            int idx = i;
            var item = MenuItem(themes[i].Item2, (_, _) => { SetTheme(themes[idx].Item1); CheckSiblings(theme, idx); });
            item.IsChecked = _settings.Theme == themes[idx].Item1;
            theme.Items.Add(item);
        }
        menu.Items.Add(theme);

        var edge = new System.Windows.Controls.MenuItem { Header = "停靠边缘" };
        var edges = new[] { ("top", "顶部"), ("bottom", "底部"), ("left", "左侧"), ("right", "右侧") };
        for (int i = 0; i < edges.Length; i++)
        {
            int idx = i;
            var item = MenuItem(edges[i].Item2, (_, _) => { SetEdge(edges[idx].Item1); CheckSiblings(edge, idx); });
            item.IsChecked = _settings.Edge == edges[idx].Item1;
            edge.Items.Add(item);
        }
        menu.Items.Add(edge);

        var expand = new System.Windows.Controls.MenuItem { Header = "展开方式" };
        var modes = new[] { ("hover", "悬停展开"), ("always", "常驻展开"), ("never", "长关") };
        for (int i = 0; i < modes.Length; i++)
        {
            int idx = i;
            var item = MenuItem(modes[i].Item2, (_, _) => { _island?.SetExpandMode(modes[idx].Item1); CheckSiblings(expand, idx); });
            item.IsChecked = _settings.ExpandMode == modes[idx].Item1;
            expand.Items.Add(item);
        }
        menu.Items.Add(expand);

        var view = new System.Windows.Controls.MenuItem { Header = "收起视图" };
        var views = new[] { ("dash", "仪表盘"), ("clock", "时间+天气"), ("combo", "信息胶囊"), ("monitor", "系统读数"),
                 ("devices", "设备电量"), ("clipboard", "剪贴板"), ("pet", "小形象") };
        for (int i = 0; i < views.Length; i++)
        {
            int idx = i;
            var item = MenuItem(views[i].Item2, (_, _) => { _island?.SetCompactView(views[idx].Item1); CheckSiblings(view, idx); });
            item.IsChecked = _settings.CompactView == views[idx].Item1;
            view.Items.Add(item);
        }
        menu.Items.Add(view);

        // 小形象款式（小形象视图 10s → 信息胶囊 30s 自动轮换，款式即时生效）
        var petStyle = new System.Windows.Controls.MenuItem { Header = "小形象款式" };
        var styles = new[] { ("classic", "经典双眼"), ("mono", "独眼"), ("cat", "猫咪"), ("robot", "机器人"), ("pixel", "像素") };
        for (int i = 0; i < styles.Length; i++)
        {
            int idx = i;
            var item = MenuItem(styles[i].Item2, (_, _) => { _island?.SetPetStyle(styles[idx].Item1); CheckSiblings(petStyle, idx); });
            item.IsChecked = _settings.PetStyle == styles[idx].Item1;
            petStyle.Items.Add(item);
        }
        menu.Items.Add(petStyle);

        // 最大化时缩成指示条（可勾选）
        var indItem = new System.Windows.Controls.MenuItem
        {
            Header = "最大化时缩成指示条",
            IsCheckable = true,
            IsChecked = _settings.IndicatorOnMaximize,
        };
        indItem.Click += (_, _) =>
        {
            _settings.IndicatorOnMaximize = indItem.IsChecked;
            _settings.Save();
        };
        menu.Items.Add(indItem);

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
        // 温度读数需要管理员（PawnIO/存储 IOCTL）：非管理员时给重启入口
        if (!IsElevated())
            menu.Items.Add(MenuItem("以管理员重启（获取温度读数）", (_, _) => RestartAsAdmin()));
        menu.Items.Add(MenuItem("退出", (_, _) => Shutdown()));
        return menu;
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>以管理员重启：exe 直跑模式重启自身；内存加载模式调用 start-admin.ps1。</summary>
    private static void RestartAsAdmin()
    {
        try
        {
            System.Diagnostics.ProcessStartInfo psi;
            string? islandDir = Environment.GetEnvironmentVariable("ISLAND_DIR");
            string exe = Environment.ProcessPath ?? "";
            if (islandDir is null && exe.EndsWith("Island.App.exe", StringComparison.OrdinalIgnoreCase))
            {
                psi = new() { FileName = exe, UseShellExecute = true, Verb = "runas" };
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

    /// <summary>单选组勾选态：选中项打勾、其余取消（主题/边缘/展开方式/视图/款式共用）。</summary>
    private static void CheckSiblings(System.Windows.Controls.MenuItem parent, int index)
    {
        for (int i = 0; i < parent.Items.Count; i++)
            if (parent.Items[i] is System.Windows.Controls.MenuItem mi)
                mi.IsChecked = i == index;
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
        _tray?.Dispose();
        _settings?.Save();
        base.OnExit(e);
    }
}
