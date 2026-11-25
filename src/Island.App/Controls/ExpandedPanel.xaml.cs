using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Island.Core.Capabilities;

namespace Island.App.Controls;

/// <summary>静音按钮文字（true→取消静音 / false→静音）。</summary>
public sealed class MuteTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "取消静音" : "静音";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true→Collapsed / false→Visible（"无数据"提示用）。</summary>
public sealed class InverseBoolToVis : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 展开面板（BrimDeck 式页面结构）：顶栏分段胶囊切页 + 中央时间 + 右侧收起，
/// 页面 = 监控 / 剪贴板·文件 / 设备电量 / 控制，一次只显示一页。
/// </summary>
public partial class ExpandedPanel : UserControl
{
    /// <summary>
    /// 展开页签的键顺序 —— **取自能力清单**（两端唯一真相源）：
    /// Windows 侧 = monitor / clips / devices / control（共享页签在前、平台专属页签在后）。
    /// 键 → XAML 控件的对应关系在下面按键写死（控件名是 XAML 的 x:Name，清单给不出），
    /// 但**顺序与页签索引都由清单决定** ⇒ 清单改顺序时不必再手改索引常量。
    /// </summary>
    private static readonly string[] TabKeys = CapabilityManifest.ExpandedTabsFor(macOS: false).ToArray();

    /// <summary>控制页的页签索引（由清单顺序推出，页签增删后不用手改）。</summary>
    private static readonly int ControlPageIndex = Array.IndexOf(TabKeys, "control");

    private Button[] _pageBtns = null!;
    private UIElement[] _pages = null!;

    /// <summary>页签的三样零件：激活底（Border）/ 图标（GlyphIcon）/ 文字（TextBlock）。
    /// 与 XAML 里 x:Name 一一对应，顺序 = 页签顺序（监控 │ 剪贴板 │ 设备 │ 控制）。</summary>
    private Border[] _tabFills = null!;
    private GlyphIcon[] _tabIcons = null!;
    private TextBlock[] _tabTexts = null!;

    private readonly DispatcherTimer _clock;

    /// <summary>监控页（宿主直接注入快照数据，不走绑定）。</summary>
    public MonitorPage Monitor => monitorPage;

    /// <summary>设备电量页（自绘，数据直接读 DeviceService）。</summary>
    public DevicesPage DevicesView => devicesPage;

    /// <summary>
    /// 把主题写进面板的资源字典：面板 XAML 与 PanelStyles.xaml 里的 DynamicResource 都从这里取色
    /// （2026-11-24 视觉重做）。宿主换主题时调用；单独渲染面板的探针也调它，保证和真实程序一致。
    /// 派生色（悬停 / 压下 / 轨道 / 滚动条滑块）都由文字色加不透明度得到，主题只管定义底色、文字和强调色。
    /// </summary>
    public static void ApplyTheme(ResourceDictionary res, Island.App.Themes.Palette pal)
    {
        Color T(double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), pal.Text.R, pal.Text.G, pal.Text.B);
        bool mono = pal.Accent == pal.Text;   // 单色主题（像素）：强调色就是文字色，圆钮不能也用它
        res["CardBg"] = new SolidColorBrush(pal.SurfaceAlt);
        // 浮层（右键浮层 / 详情浮层）：用不透明的面板色，半透明会把底下的字透上来
        res["OverlayBg"] = new SolidColorBrush(pal.SurfaceAlt);
        res["Fg"] = new SolidColorBrush(pal.Text);
        res["FgDim"] = new SolidColorBrush(pal.TextDim);
        res["FgTertiary"] = new SolidColorBrush(pal.TextTertiary);
        res["Accent"] = new SolidColorBrush(pal.Accent);
        res["Track"] = new SolidColorBrush(pal.Track);
        res["Hairline"] = new SolidColorBrush(pal.Hairline);
        res["Hover"] = new SolidColorBrush(T(pal.IsDark ? 0.08 : 0.05));
        res["Pressed"] = new SolidColorBrush(T(pal.IsDark ? 0.12 : 0.09));
        res["FillStrong"] = new SolidColorBrush(T(pal.IsDark ? 0.18 : 0.12));
        res["Knob"] = new SolidColorBrush(mono ? pal.Text : Colors.White);
        res["KnobOn"] = new SolidColorBrush(mono ? pal.Surface : Colors.White);
        res["ScrollThumb"] = new SolidColorBrush(T(pal.IsDark ? 0.19 : 0.22));
        // 圆角随主题：像素主题是直角（2px 微圆角）
        bool square = pal.SquareCorners;
        res["RadiusBtn"] = new CornerRadius(square ? 2 : 7);
        res["RadiusPill"] = new CornerRadius(square ? 2 : 11);
        res["RadiusTrack"] = new CornerRadius(square ? 0 : 2);
        res["RadiusCard"] = new CornerRadius(square ? 3 : 14);
        // 暗色科技（只属于深色主题）：分组卡片 / 按钮的半透明底、实心强调色按钮上的字色，
        // 以及一份强调色辉光 —— 选中的页签和选中的电源计划共用（发光是稀缺资源，全面板只有这两处）。
        res["CardFill"] = new SolidColorBrush(T(pal.IsDark ? 0.035 : 0.04));
        res["OnAccent"] = new SolidColorBrush(pal.IsDark ? pal.Surface : Colors.White);
        if (pal.IsDark)
        {
            var glow = new DropShadowEffect { Color = pal.Accent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55 };
            glow.Freeze();   // 冻结后才能被多个元素共用
            res["Glow"] = glow;
        }
        else res.Remove("Glow");
    }

    /// <summary>请求收起面板（顶栏 ✕）。</summary>
    public event Action? CloseRequested;

    /// <summary>请求打开设置中心（顶栏齿轮，2026-11-25 UI 对齐 BrimDeck）。</summary>
    public event Action? SettingsRequested;

    /// <summary>
    /// 绑定回写抑制标志：DataContext 换新实例时，绑定会把源上的值推回 CheckBox，
    /// 触发 Checked/Unchecked —— 那不是用户操作。
    /// 不抑制的话，"鼠标穿透"开关一被回写就自我收起面板（而此时鼠标已穿透，用户也点不回来）。
    /// 只有用户真的点了开关才走业务逻辑。
    /// </summary>
    private bool _suppressToggle;

    /// <summary>抑制标志的开关时机：DataContext 变化那一轮绑定更新内一律吞掉回写。</summary>
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.Property == DataContextProperty)
        {
            // 先置位再走基类：绑定回写就发生在 DataContext 变化的传播过程里
            _suppressToggle = true;
            // 放开放到 Background 优先级：同一轮绑定更新里的回写都被吞掉，
            // 之后的用户点击才是真操作
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _suppressToggle = false));
        }
        base.OnPropertyChanged(e);
    }

    public ExpandedPanel()
    {
        InitializeComponent();
        // 页签零件按**清单顺序**装配（键 → 控件的映射见下面的能力清单页签区）。
        var parts = TabKeys.Select(TabPartsOf).ToArray();
        _pageBtns = parts.Select(p => p.Btn).ToArray();
        _pages = parts.Select(p => p.Page).ToArray();
        _tabFills = parts.Select(p => p.Fill).ToArray();
        _tabIcons = parts.Select(p => p.Icon).ToArray();
        _tabTexts = parts.Select(p => p.Text).ToArray();
        // 页签 Tag = 页索引，也按清单顺序回写：XAML 里写死的 Tag 只是初值，
        // 运行时以清单为准 ⇒ 清单改了顺序，索引不会错位。
        for (int i = 0; i < _pageBtns.Length; i++) _pageBtns[i].Tag = i;
        // 页签的**视觉顺序**也跟着清单走（XAML 里写的顺序只是初值）。
        ReorderTabBar();
        ApplySpecGeometry();
        ApplyClipGeometry();
        UpdateClipTabVisuals();
        clipList.ItemsSource = _clipRows;

        // 剪贴板页：1.5s 轮询一次历史（mac 同款节拍），只在页可见时跑
        _clipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _clipTimer.Tick += (_, _) => { if (_page == ClipPageIndex) SyncClipPage(); };

        SelectPage(0);

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _clock.Tick += (_, _) => TickClock();
        _clock.Start();
        TickClock();
    }

    // ===== 能力清单页签区：开始 =====
    /// <summary>一个页签的五样零件：按钮 / 页面 / 激活底 / 图标 / 文字。</summary>
    private readonly record struct TabParts(Button Btn, UIElement Page, Border Fill, GlyphIcon Icon, TextBlock Text);

    /// <summary>
    /// 页签键 → XAML 控件的映射。**键来自能力清单**，控件名是 XAML 里的 x:Name（清单给不出）。
    /// 新增一个页签要改三处：能力清单加一条 + 这里加一个分支 + XAML 里加按钮；
    /// 少改任何一处，tests/Island.Tests/CapabilityManifestTests.cs 的漂移检查都会红。
    /// </summary>
    private TabParts TabPartsOf(string key) => key switch
    {
        "monitor" => new TabParts(btnMonitor, monitorPage, tabFillMonitor, tabIconMonitor, tabTextMonitor),
        "clips" => new TabParts(btnShelf, pageShelf, tabFillShelf, tabIconShelf, tabTextShelf),
        "devices" => new TabParts(btnDevices, pageDevices, tabFillDevices, tabIconDevices, tabTextDevices),
        "control" => new TabParts(btnControl, pageControl, tabFillControl, tabIconControl, tabTextControl),
        _ => throw new InvalidOperationException($"能力清单里的页签「{key}」在展开面板没有对应控件"),
    };

    /// <summary>
    /// 把页签条里的宿主 Grid 按**清单顺序**重排（XAML 里写死的顺序只是初值）。
    /// 每个页签的激活底与按钮同属一个宿主 Grid ⇒ 移动 Grid 就等于移动整个页签。
    /// </summary>
    private void ReorderTabBar()
    {
        for (int i = 0; i < _tabFills.Length; i++)
        {
            if (_tabFills[i].Parent is not Grid host) continue;
            int at = tabBar.Children.IndexOf(host);
            if (at == i) continue;
            tabBar.Children.Remove(host);
            tabBar.Children.Insert(i, host);
        }
    }
    // ===== 能力清单页签区：结束 =====

    private void TickClock() => clockText.Text = DateTime.Now.ToString("HH:mm");

    private void OnRestoreClipboard(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        Services.ClipboardItem? item = button.DataContext is ClipRowView row ? row.Item : null;
        try
        {
            bool restored = Services.ClipboardService.TryRestore(item);
            button.ToolTip = restored ? "已恢复到剪贴板，可在目标窗口按 Ctrl+V"
                : "内容已过期或剪贴板正被占用，请刷新后重试";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            button.ToolTip = "剪贴板正被占用，请稍后重试";
        }
        e.Handled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    // 音量滑杆 / 静音 / 系统浅色模式：走 PanelData 回调（面板不直接持有服务引用）
    private void OnVolumeSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DataContext is PanelData pd && Math.Abs(e.NewValue - pd.VolumeValue) > 0.001)
            pd.ApplyVolume(e.NewValue);
    }

    private void OnMuteToggle(object sender, RoutedEventArgs e)
    {
        if (DataContext is PanelData pd) pd.ApplyToggleMute();
    }

    private void OnMicToggle(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PanelData pd) return;
        pd.ApplyToggleMicMute();
        btnMic.Content = pd.MicMuted ? "已静音" : "麦克风";
    }

    /// <summary>内存清理（LiteMonitor 同款）：按钮点击即清，结果显示在副标题。
    /// 清理要遍历全系统进程 EmptyWorkingSet，必须放后台线程（ControlService.CleanMemoryAsync）。</summary>
    private void OnCleanMemory(object sender, RoutedEventArgs e)
    {
        if (_cleaning) return;
        _cleaning = true;
        memCleanResult.Text = "正在清理…";
        Services.ControlService.CleanMemoryAsync().ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _cleaning = false;
                if (t.Status != TaskStatus.RanToCompletion)
                {
                    memCleanResult.Text = "清理失败";
                    return;
                }
                long freed = t.Result;
                // 只报实测到的数字：0 MB 只说明"可用内存计数器没变"，
                // 不能据此断言"内存压力本就不高"
                memCleanResult.Text = freed > 0
                    ? $"已释放约 {freed} MB 可用内存"
                    : "已清理（可用内存变化 0 MB）";
            });
        });
    }

    /// <summary>清理进行中标志：避免连点重复扫全系统进程。</summary>
    private bool _cleaning;

    /// <summary>控制页打开时刷新电源计划按钮（powercfg 枚举，高亮当前项）。
    /// powercfg 是子进程（实测 27–65ms），放后台线程读，UI 线程只负责画按钮。</summary>
    private void RefreshPowerPlans()
    {
        powerPlanHost.Children.Clear();
        powerPlanHost.Children.Add(new TextBlock
        {
            Text = "读取中…", FontSize = 10,
            Foreground = TryFindResource("FgDim") as Brush,
        });
        Task.Run(() => Services.ControlService.GetPowerPlans()).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (t.Status != TaskStatus.RanToCompletion)
                {
                    powerPlanHost.Children.Clear();
                    powerPlanHost.Children.Add(new TextBlock
                    {
                        Text = "读取失败", FontSize = 10,
                        Foreground = TryFindResource("FgDim") as Brush,
                    });
                    return;
                }
                var (plans, active) = t.Result;
                FillPowerPlans(plans, active);
            });
        });
    }

    /// <summary>电源计划按钮（UI 线程）：按后台读到的结果铺按钮，当前项是实心强调色 + 辉光（AccentBtn）。</summary>
    private void FillPowerPlans(List<(string Id, string Name)> plans, int active)
    {
        _plans = plans;
        _activePlan = active;
        powerPlanHost.Children.Clear();
        for (int i = 0; i < plans.Count; i++)
        {
            bool on = i == active;
            var b = new Button
            {
                Content = plans[i].Name,
                Style = (Style)FindResource(on ? "AccentBtn" : "QuietBtn"),
                Margin = new Thickness(0, 0, 6, 0),
                Tag = plans[i].Id,
            };
            if (on) b.Effect = TryFindResource("Glow") as Effect;
            b.Click += (_, _) =>
            {
                if (b.Tag is string guid && Services.ControlService.SetPowerPlan(guid))
                    RefreshPowerPlans();
            };
            powerPlanHost.Children.Add(b);
        }
        if (plans.Count == 0)
            powerPlanHost.Children.Add(new TextBlock
            {
                Text = "读取失败", FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Foreground = TryFindResource("FgDim") as Brush,
            });
    }

    private void OnLightThemeToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;   // 绑定回写不是用户操作
        if (DataContext is PanelData pd && sender is CheckBox cb)
            pd.ApplyLightTheme(cb.IsChecked == true);
    }

    private void OnNightLightToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;   // 绑定回写不是用户操作
        if (DataContext is PanelData pd && sender is CheckBox cb)
            pd.ApplyNightLight(cb.IsChecked == true);
    }

    private void OnClickThroughToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;   // 绑定回写不是用户操作
        if (DataContext is PanelData pd && sender is CheckBox cb)
        {
            pd.ApplyClickThrough(cb.IsChecked == true);
            // 用户主动开穿透 → 收起面板（鼠标已穿透，面板留着也没法交互）
            if (cb.IsChecked == true) CloseRequested?.Invoke();
        }
    }

    private void OnPage(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && int.TryParse(b.Tag?.ToString(), out int i)) SelectPage(i);
    }

    /// <summary>当前页（与页签按钮的 Tag 一致）；最近一次读到的电源计划：换主题后重涂选中项用。</summary>
    private int _page;
    private List<(string Id, string Name)>? _plans;
    private int _activePlan;

    /// <summary>切页：显示目标页，页签里对应那个亮起。</summary>
    public void SelectPage(int idx)
    {
        if (idx < 0 || idx >= _pages.Length) return;
        _page = idx;
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;
        if (idx == ControlPageIndex)   // 控制页：刷新电源计划（标出当前项）
        {
            RefreshPowerPlans();
            if (DataContext is PanelData pd)
                btnMic.Content = pd.MicMuted ? "已静音" : "麦克风";
        }
        if (idx == ClipPageIndex)   // 剪贴板页：开始轮询历史
        {
            SyncClipPage();
            _clipTimer.Start();
        }
        else
        {
            _clipTimer.Stop();
            CloseClipMenu();
            CloseClipDetail();
        }
        UpdateTabVisuals();
    }

    /// <summary>换主题后重取选中态的颜色（页签 / 电源计划按钮的选中色是创建时取的本地值，不会自己跟着资源变）。</summary>
    public void Retint()
    {
        UpdateTabVisuals();
        UpdateClipTabVisuals();
        if (_plans is not null) FillPowerPlans(_plans, _activePlan);
    }

    /// <summary>
    /// 把规格常量（<see cref="ExpandedPanelSpec"/>）落到顶栏几何上。
    /// XAML 里写的是同一批数值（XAML 读不到 const），这里再按常量覆写一次 —— 规格类才是唯一事实来源。
    /// </summary>
    private void ApplySpecGeometry()
    {
        // 面板内容区内边距：左右 18 / 上 14 / 下 18
        rootGrid.Margin = new Thickness(
            ExpandedPanelSpec.ContentPadX, ExpandedPanelSpec.ContentPadTop,
            ExpandedPanelSpec.ContentPadX, ExpandedPanelSpec.ContentPadBottom);

        // 顶栏高度 = 图标 14 + 上下各 5 = 24；顶栏到内容区 40（= 24 + 16）
        double barH = ExpandedPanelSpec.TabIconSize + ExpandedPanelSpec.TabPadY * 2;
        topBar.Height = barH;
        topBar.Margin = new Thickness(0, 0, 0, ExpandedPanelSpec.ContentTopOffset - barH);

        // 页签左端离面板左边缘 20（内容区左内边距 18 + 2）
        tabBar.Margin = new Thickness(
            ExpandedPanelSpec.TabLeftOffset - ExpandedPanelSpec.ContentPadX, 0, 0, 0);

        for (int i = 0; i < _pageBtns.Length; i++)
        {
            Button b = _pageBtns[i];
            b.Width = double.NaN;    // Auto：页签宽度跟着「图标 + 文字」走
            b.Height = double.NaN;
            b.Padding = new Thickness(ExpandedPanelSpec.TabPadX, ExpandedPanelSpec.TabPadY,
                                      ExpandedPanelSpec.TabPadX, ExpandedPanelSpec.TabPadY);
            b.Margin = new Thickness(0);   // 页签间距由外层 Grid 承担

            GlyphIcon icon = _tabIcons[i];
            icon.Width = ExpandedPanelSpec.TabIconSize;
            icon.Height = ExpandedPanelSpec.TabIconSize;

            TextBlock txt = _tabTexts[i];
            txt.FontSize = ExpandedPanelSpec.TabFontSize;
            txt.Margin = new Thickness(ExpandedPanelSpec.TabIconGap, 0, 0, 0);   // 图标与文字的间距

            // 激活底的圆角（mac：8）
            _tabFills[i].CornerRadius = new CornerRadius(ExpandedPanelSpec.TabActiveCorner);
        }

        // 相邻页签间距 6（最后一个不留尾距）
        for (int i = 0; i < _tabFills.Length; i++)
        {
            if (_tabFills[i].Parent is Grid host)
                host.Margin = new Thickness(0, 0,
                    i < _tabFills.Length - 1 ? ExpandedPanelSpec.TabGap : 0, 0);
        }

        // 时钟：12 号 Regular 比例字体（不套 DataText 的等宽体），右侧留白 24
        clockText.FontSize = ExpandedPanelSpec.ClockFontSize;
        clockText.FontWeight = FontWeights.Normal;
        clockText.Margin = new Thickness(0, 0, ExpandedPanelSpec.ClockRightMargin, 0);
    }

    /// <summary>
    /// 页签的选中态（对齐 mac 的 Tab(...)）：激活 = Accent 20% 底（圆角 8）+ 文字主色 + 图标强调色；
    /// 未激活 = 文字次色 + 图标次色（底透明）。
    /// </summary>
    private void UpdateTabVisuals()
    {
        // 不给页签挂辉光：Effect 会把按钮先画成位图再合成，软件渲染下图标会被重新采样，强调色发灰、边缘发糊。
        // 发光留给实心的电源计划按钮（AccentBtn）和监控页的 CPU 主读数。
        var accent = TryFindResource("Accent") as Brush;
        var fg = TryFindResource("Fg") as Brush;
        var dim = TryFindResource("FgDim") as Brush;
        for (int i = 0; i < _pageBtns.Length; i++)
        {
            bool on = i == _page;
            _tabFills[i].Background = on ? AccentFill(accent) : Brushes.Transparent;
            TextElement.SetForeground(_tabIcons[i], on ? accent ?? Brushes.White : dim ?? Brushes.Gray);
            _tabTexts[i].Foreground = on ? fg ?? Brushes.White : dim ?? Brushes.Gray;
            _pageBtns[i].Foreground = on ? fg ?? Brushes.White : dim ?? Brushes.Gray;
        }
    }

    /// <summary>激活页签的底色：强调色按 TabActiveFillAlpha（20%）调淡（不是把整个元素 Opacity 调淡，
    /// 否则文字和图标也会跟着变淡）。</summary>
    private static Brush AccentFill(Brush? accent, double alpha = ExpandedPanelSpec.TabActiveFillAlpha)
    {
        if (accent is not SolidColorBrush scb) return Brushes.Transparent;
        Color c = scb.Color;
        byte a = (byte)Math.Round(c.A * alpha);
        var fill = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        fill.Freeze();   // 冻结后才能安全共用
        return fill;
    }

    // ==================== 剪贴板页（对齐 mac 的单区结构）====================
    // 基准：src/Island.Avalonia/IslandCanvas.cs 的 DrawClipsPage（分类标签栏 / 头部 / 列表 /
    // 自绘 3px 覆盖式滚动条）与 DrawClipMenu（自绘右键浮层）。
    // XAML 里读不到 const，页面几何以这里的常量为唯一事实来源（ApplyClipGeometry 落到控件上）。

    /// <summary>剪贴板页的页签索引（与顶栏页签的 Tag 一致；由清单顺序推出）。</summary>
    private static readonly int ClipPageIndex = Array.IndexOf(TabKeys, "clips");

    /// <summary>列表行高（mac：rowH = 30）。</summary>
    private const double ClipRowHeight = 30;

    /// <summary>列表列宽（mac：列表列 ≈246；单区总宽 = 列表列 + 卡片内边距 20 + 描边 2）。</summary>
    private const double ClipListWidth = 246;

    /// <summary>分类标签的高（mac：标签框高 22）。</summary>
    private const double ClipTabHeight = 22;

    /// <summary>分类标签的圆角（mac：RoundedRect(box, 7)）。</summary>
    private const double ClipTabCorner = 7;

    /// <summary>分类标签文字的字号（mac：RenderKit.Text(label, 11, ...)）。</summary>
    private const double ClipTabFontSize = 11;

    /// <summary>分类标签激活底的不透明度（mac：Faint(Accent, 0.18)）。</summary>
    private const double ClipTabFillAlpha = 0.18;

    /// <summary>自绘滚动条的宽度（mac：3px 覆盖式细条）。</summary>
    private const double ClipScrollWidth = 3;

    /// <summary>自绘滚动条滑块的最小高度（mac：24）。</summary>
    private const double ClipScrollThumbMin = 24;

    /// <summary>右键浮层的项高 / 宽度 / 圆角（mac：itemH 26、menuW 152、圆角 9）。</summary>
    private const double ClipMenuItemHeight = 26;
    private const double ClipMenuWidth = 152;
    private const double ClipMenuCorner = 9;

    /// <summary>浮层离面板边缘的最小留白（mac：8）。</summary>
    private const double ClipOverlayInset = 8;

    /// <summary>剪贴板行的视图模型集合（绑定到 clipList）。</summary>
    private readonly ObservableCollection<ClipRowView> _clipRows = new();

    /// <summary>剪贴板页的轮询定时器（mac：1.5s 一次；只在页可见时跑）。</summary>
    private readonly DispatcherTimer _clipTimer;

    /// <summary>当前分类筛选（all / common / work / code）—— 对应 mac 的 _clipFilter。</summary>
    private string _clipFilter = "all";

    /// <summary>上次同步列表时的签名（条数 + 最新一条），变了才重建（防闪烁）。</summary>
    private string _clipSig = "";

    /// <summary>右键浮层针对的行。</summary>
    private ClipRowView? _clipMenuTarget;

    /// <summary>右键浮层是否开着。</summary>
    private bool _clipMenuOpen;

    /// <summary>正在拖自绘滚动条的滑块。</summary>
    private bool _clipThumbDragging;

    /// <summary>列表的滚动偏移（px）+ 位移变换（自绘滚动用）。</summary>
    private double _clipOffset;
    private readonly TranslateTransform _clipShift = new();

    /// <summary>底部提示（操作反馈）的内容与到期时刻。</summary>
    private string _clipToast = "";
    private DateTime _clipToastUntil = DateTime.MinValue;

    /// <summary>
    /// 把剪贴板页的 mac 口径常量落到控件上（XAML 读不到 const，这里是唯一事实来源）。
    /// </summary>
    private void ApplyClipGeometry()
    {
        // 单区宽度 = 列表列 246 + 卡片内边距 20 + 描边 2
        pageShelf.ColumnDefinitions[0].Width = new GridLength(ClipListWidth + 22);
        clipTabBar.Height = ClipTabHeight;
        foreach (var f in new[] { clipFillAll, clipFillCommon, clipFillWork, clipFillCode })
            f.CornerRadius = new CornerRadius(ClipTabCorner);
        foreach (var t in new[] { clipTextAll, clipTextCommon, clipTextWork, clipTextCode })
            t.FontSize = ClipTabFontSize;
        clipMenu.Width = ClipMenuWidth;
        clipMenu.CornerRadius = new CornerRadius(ClipMenuCorner);
        // 列表内容整体位移（自绘滚动的实现方式）
        clipList.RenderTransformOrigin = new Point(0, 0);
        clipList.RenderTransform = _clipShift;
        clipScrollThumb.Width = ClipScrollWidth;
        clipScrollTrack.Width = ClipScrollWidth;
    }

    /// <summary>
    /// 剪贴板页同步：重建列表 + 计数 + 相对时间 + 自绘滚动条。
    /// 直接读服务（权威来源）；PanelData.Clipboard 只是它的镜像 ——
    /// 删条目之后只有服务是准的，面板不自己再复制一份维护。
    /// </summary>
    private void SyncClipPage()
    {
        var items = Services.ClipboardService.Items;
        string sig = items.Count + "|" + (items.Count > 0 ? items[0].At.Ticks + "|" + items[0].Preview : "");
        if (sig != _clipSig)
        {
            _clipSig = sig;
            RebuildClipRows(items);
        }
        else
        {
            // 内容没变：只刷新相对时间（"3分钟前" 会随时间自己走）
            var now = DateTime.Now;
            foreach (var r in _clipRows) r.UpdateTime(now);
        }
        clipCount.Text = items.Count + " 条";
        UpdateClipEmptyState();
        UpdateClipScrollBar();
        UpdateClipFooter();
    }

    /// <summary>按当前分类重建列表（mac 的筛选同样在这一步做）。</summary>
    private void RebuildClipRows(IReadOnlyList<Services.ClipboardItem> items)
    {
        double keep = _clipOffset;
        _clipRows.Clear();
        var now = DateTime.Now;
        foreach (var it in items)
        {
            var v = new ClipRowView(it, now);
            if (_clipFilter == "all" || v.Tag == _clipFilter) _clipRows.Add(v);
        }
        SetClipOffset(keep);
        UpdateClipEmptyState();
        UpdateClipScrollBar();
    }

    /// <summary>空态：一条都没有 vs 当前分类下没有（mac 的两句话）。</summary>
    private void UpdateClipEmptyState()
    {
        bool none = _clipRows.Count == 0;
        clipEmptyAll.Visibility = none && _clipFilter == "all" ? Visibility.Visible : Visibility.Collapsed;
        clipEmptyFilter.Visibility = none && _clipFilter != "all" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>点分类标签：切换筛选（全部 / 常用 / 工作 / 代码）。</summary>
    private void OnClipFilter(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string id) SetClipFilter(id);
    }

    private void SetClipFilter(string id)
    {
        if (_clipFilter == id) return;
        _clipFilter = id;
        UpdateClipTabVisuals();
        RebuildClipRows(Services.ClipboardService.Items);
    }

    /// <summary>分类标签的选中态（mac：激活 = 强调色 18% 底 + 文字主色；其余次色）。</summary>
    private void UpdateClipTabVisuals()
    {
        var accent = TryFindResource("Accent") as Brush;
        var fg = TryFindResource("Fg") as Brush;
        var dim = TryFindResource("FgDim") as Brush;
        string[] ids = { "all", "common", "work", "code" };
        var fills = new[] { clipFillAll, clipFillCommon, clipFillWork, clipFillCode };
        var texts = new[] { clipTextAll, clipTextCommon, clipTextWork, clipTextCode };
        for (int i = 0; i < ids.Length; i++)
        {
            bool on = _clipFilter == ids[i];
            fills[i].Background = on ? AccentFill(accent, ClipTabFillAlpha) : Brushes.Transparent;
            texts[i].Foreground = on ? fg ?? Brushes.White : dim ?? Brushes.Gray;
        }
    }

    // ---------- 自绘 3px 覆盖式滚动条（XAML 的 ScrollViewer 画不出这种细条） ----------

    /// <summary>设置列表滚动偏移（超出范围夹回来）并刷新自绘滚动条。</summary>
    private void SetClipOffset(double value)
    {
        double max = Math.Max(0, ClipContentHeight - clipListHost.ActualHeight);
        _clipOffset = Math.Clamp(value, 0, max);
        _clipShift.Y = -_clipOffset;
        UpdateClipScrollBar();
        UpdateClipFooter();
    }

    /// <summary>列表内容的总高（行高 × 行数）。</summary>
    private double ClipContentHeight => _clipRows.Count * ClipRowHeight;

    /// <summary>列表区尺寸变了（刚展开 / 面板高度变了）→ 重算自绘滚动条。</summary>
    private void OnClipHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateClipScrollBar();
        UpdateClipFooter();
    }

    /// <summary>列表区滚轮：自绘滚动自己做（系统 ScrollViewer 已不用）。</summary>
    private void OnClipListWheel(object sender, MouseWheelEventArgs e)
    {
        if (ClipContentHeight <= clipListHost.ActualHeight) return;
        SetClipOffset(_clipOffset - e.Delta / 120.0 * ClipRowHeight * 2);
        e.Handled = true;
    }

    /// <summary>自绘滚动条：内容超出可见区才画（右侧竖条 3px，盖在内容上），滑块按比例、最小 24。</summary>
    private void UpdateClipScrollBar()
    {
        double view = clipListHost.ActualHeight;
        double content = ClipContentHeight;
        bool need = view > 0 && content > view + 1;
        clipScrollBar.Visibility = Visibility.Visible;
        clipScrollTrack.Visibility = need ? Visibility.Visible : Visibility.Collapsed;
        clipScrollThumb.Visibility = need ? Visibility.Visible : Visibility.Collapsed;
        if (!need) return;
        double track = clipScrollBar.ActualHeight;
        if (track <= 0) return;
        double thumbH = Math.Max(ClipScrollThumbMin, track * view / content);
        clipScrollThumb.Height = thumbH;
        double maxScroll = content - view;
        double y = maxScroll > 0 ? _clipOffset / maxScroll * (track - thumbH) : 0;
        clipScrollThumb.Margin = new Thickness(0, y, 0, 0);
    }

    private void OnClipThumbDown(object sender, MouseButtonEventArgs e)
    {
        _clipThumbDragging = true;
        clipScrollThumb.CaptureMouse();
        e.Handled = true;
    }

    private void OnClipThumbMove(object sender, MouseEventArgs e)
    {
        if (!_clipThumbDragging) return;
        double track = clipScrollBar.ActualHeight;
        double view = clipListHost.ActualHeight;
        double content = ClipContentHeight;
        double maxScroll = Math.Max(0, content - view);
        if (maxScroll <= 0 || track <= clipScrollThumb.Height) return;
        double y = e.GetPosition(clipScrollBar).Y - clipScrollThumb.Height / 2;
        double frac = Math.Clamp(y / (track - clipScrollThumb.Height), 0, 1);
        SetClipOffset(frac * maxScroll);
    }

    private void OnClipThumbUp(object sender, MouseButtonEventArgs e)
    {
        if (!_clipThumbDragging) return;
        _clipThumbDragging = false;
        clipScrollThumb.ReleaseMouseCapture();
    }

    /// <summary>自绘条盖住了列表右缘，滚轮落不到 ScrollViewer 上 —— 自己转发。</summary>
    private void OnClipScrollWheel(object sender, MouseWheelEventArgs e)
    {
        if (ClipContentHeight <= clipListHost.ActualHeight) return;
        SetClipOffset(_clipOffset - e.Delta / 120.0 * ClipRowHeight * 2);
        e.Handled = true;
    }

    /// <summary>底部一行：有操作提示时显示提示，否则显示「还有 N 条」没露出来。</summary>
    private void UpdateClipFooter()
    {
        if (DateTime.Now < _clipToastUntil)
        {
            clipMore.Text = _clipToast;
            clipMore.Visibility = Visibility.Visible;
            return;
        }
        double view = clipListHost.ActualHeight;
        int total = _clipRows.Count;
        int visible = view > 0 ? (int)Math.Ceiling(view / ClipRowHeight) : total;
        int first = view > 0 ? (int)Math.Floor(_clipOffset / ClipRowHeight) : 0;
        int rest = total - (first + visible);
        if (rest > 0)
        {
            clipMore.Text = $"还有 {rest} 条未显示";
            clipMore.Visibility = Visibility.Visible;
        }
        else
        {
            clipMore.Text = "";
            clipMore.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>底部提示（2.5 秒后自动让位给「还有 N 条」）。</summary>
    private void ShowClipToast(string text)
    {
        _clipToast = text;
        _clipToastUntil = DateTime.Now.AddSeconds(2.5);
        UpdateClipFooter();
    }

    // ---------- 行内动作 ----------

    private void OnDeleteClip(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is Services.ClipboardItem item) DeleteClipItem(item);
        e.Handled = true;
    }

    /// <summary>删掉一条剪贴板记录（行尾 × 与右键菜单「删除」共用）。
    /// ClipboardService 只给了「清空全部」，没有「按条删除」的入口，而那个文件不在本轮可改的范围里，
    /// 所以这里用反射去动它内部的列表；反射失败就当没删掉（不崩、不假装成功）。</summary>
    private void DeleteClipItem(Services.ClipboardItem item)
    {
        if (!RemoveFromClipboardService(item))
        {
            ShowClipToast("删除失败：剪贴板历史没能改掉");
            return;
        }
        var row = _clipRows.FirstOrDefault(r => ReferenceEquals(r.Item, item));
        if (row is not null) _clipRows.Remove(row);
        ShowClipToast("已删除这条记录");
        SyncClipPage();
    }

    /// <summary>
    /// 从剪贴板历史里去掉一条（行内 × / 右键「删除」）。
    /// 走 ClipboardService 的公开入口 —— 不用反射捅它的私有字段（字段一改就静默失效）。
    /// </summary>
    private static bool RemoveFromClipboardService(Services.ClipboardItem item)
    {
        try
        {
            return Services.ClipboardService.Remove(item);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>导出全部：文字存 txt、图片存 png，到「下载」目录（mac 的 ClipExportAll 同款思路）。</summary>
    private void OnClipExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var items = Services.ClipboardService.Items;
            string dir = ResolveDownloadDir();
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            int textCount = 0, imgCount = 0;
            var sb = new StringBuilder();
            foreach (var it in items)
            {
                if (it.Kind != "text" || string.IsNullOrEmpty(it.Full)) continue;
                sb.AppendLine($"[{it.At:yyyy-MM-dd HH:mm:ss}] {it.Full}");
                textCount++;
            }
            if (textCount > 0)
                File.WriteAllText(Path.Combine(dir, $"剪贴板导出-{stamp}.txt"), sb.ToString(), new UTF8Encoding(true));
            foreach (var it in items)
            {
                if (it.Kind != "image" || it.Image is null) continue;
                string path = Path.Combine(dir, $"剪贴板-{stamp}-{++imgCount}.png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(it.Image));
                using var fs = File.Create(path);
                enc.Save(fs);
            }
            ShowClipToast(textCount + imgCount > 0
                ? $"已导出 {textCount} 条文字、{imgCount} 张图片"
                : "没有可导出的记录");
        }
        catch (Exception ex)
        {
            ShowClipToast("导出失败：" + ex.Message);
        }
    }

    /// <summary>导出目录：优先「下载」，没有就用「文档」。</summary>
    private static string ResolveDownloadDir()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dl = Path.Combine(home, "Downloads");
        if (Directory.Exists(dl)) return dl;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>清空全部（mac 的 ClipCleared）。</summary>
    private void OnClipClear(object sender, RoutedEventArgs e)
    {
        Services.ClipboardService.Clear();
        SyncClipPage();
        ShowClipToast("已清空剪贴板历史");
    }

    // ---------- 自绘右键浮层（替换系统 ContextMenu；mac 的 DrawClipMenu） ----------

    /// <summary>行内右键：打开浮层（贴在鼠标处，超出面板就往里收）。</summary>
    private void OnClipRowRightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ClipRowView v)
        {
            OpenClipMenu(v, e.GetPosition(rootGrid));
            e.Handled = true;
        }
    }

    private void OpenClipMenu(ClipRowView target, Point at)
    {
        _clipMenuTarget = target;
        clipMenuItems.Children.Clear();
        AddClipMenuItem("copy", "复制到剪贴板");
        if (target.LinkUrl is not null) AddClipMenuItem("open", "打开链接");
        AddClipMenuItem("detail", "查看详情");
        AddClipMenuItem("delete", "删除", danger: true);

        clipMenu.Visibility = Visibility.Visible;
        overlayHost.Visibility = Visibility.Visible;
        double h = clipMenuItems.Children.Count * ClipMenuItemHeight + 10;
        double x = Math.Min(at.X, rootGrid.ActualWidth - ClipMenuWidth - ClipOverlayInset);
        double y = Math.Min(at.Y, rootGrid.ActualHeight - h - ClipOverlayInset);
        clipMenu.Margin = new Thickness(Math.Max(ClipOverlayInset, x), Math.Max(ClipOverlayInset, y), 0, 0);
        _clipMenuOpen = true;
    }

    private void AddClipMenuItem(string id, string label, bool danger = false)
    {
        // Border 不支持在 Style 里写 Template（MC4005），菜单项直接在代码里搭：
        // 圆角 6、项高 26（mac），悬停给一层底色。
        var hover = TryFindResource("Hover") as Brush;
        var bd = new Border
        {
            Height = ClipMenuItemHeight,
            Padding = new Thickness(7, 0, 7, 0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = id,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = danger
                    ? (TryFindResource("ClipDanger") as Brush) ?? Brushes.IndianRed
                    : (TryFindResource("Fg") as Brush) ?? Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        bd.MouseEnter += (_, _) => { if (hover is not null) bd.Background = hover; };
        bd.MouseLeave += (_, _) => bd.Background = Brushes.Transparent;
        bd.MouseLeftButtonDown += (s, e) =>
        {
            string mid = (string)bd.Tag;
            var row = _clipMenuTarget;
            CloseClipMenu();
            if (row is not null) RunClipMenuAction(mid, row);
            e.Handled = true;
        };
        clipMenuItems.Children.Add(bd);
    }

    /// <summary>浮层里的动作：复制 / 打开链接 / 查看详情 / 删除。</summary>
    private void RunClipMenuAction(string id, ClipRowView row)
    {
        switch (id)
        {
            case "copy":
                bool ok = Services.ClipboardService.TryRestore(row.Item);
                ShowClipToast(ok ? "已复制到剪贴板，可在目标窗口按 Ctrl+V" : "复制失败：内容已过期或剪贴板被占用");
                break;
            case "open":
                if (row.LinkUrl is string url)
                {
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch (Exception ex) { ShowClipToast("打不开链接：" + ex.Message); }
                }
                break;
            case "detail":
                ShowClipDetail(row);
                break;
            case "delete":
                DeleteClipItem(row.Item);
                break;
        }
    }

    private void CloseClipMenu()
    {
        _clipMenuOpen = false;
        _clipMenuTarget = null;
        clipMenu.Visibility = Visibility.Collapsed;
        if (clipDetail.Visibility != Visibility.Visible) overlayHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>详情浮层：Windows 侧没有独立的 ClipDetailWindow 窗口（那是 mac 工程的文件），
    /// 这里在面板内自绘一个等价的详情层。</summary>
    private void ShowClipDetail(ClipRowView row)
    {
        var item = row.Item;
        bool isImage = item.Image is not null;
        clipDetailText.Text = isImage ? item.Preview : item.Full;
        clipDetailText.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
        clipDetailImage.Source = item.Image;
        clipDetailImage.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        clipDetail.Visibility = Visibility.Visible;
        overlayHost.Visibility = Visibility.Visible;
    }

    private void OnCloseClipDetail(object sender, RoutedEventArgs e) => CloseClipDetail();

    private void CloseClipDetail()
    {
        clipDetail.Visibility = Visibility.Collapsed;
        if (!_clipMenuOpen) overlayHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>面板里点别处 → 收起浮层（mac 的浮层也是点外面就收）。</summary>
    private void OnPanelPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (overlayHost.Visibility != Visibility.Visible) return;
        var src = e.OriginalSource as DependencyObject;
        if (src is not null && (IsInside(src, clipMenu) || IsInside(src, clipDetail))) return;
        CloseClipMenu();
        CloseClipDetail();
    }

    /// <summary>Esc → 收起浮层。</summary>
    private void OnPanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseClipMenu();
        CloseClipDetail();
        e.Handled = true;
    }

    /// <summary>点是不是落在某个元素里（沿视觉树往上找）。</summary>
    private static bool IsInside(DependencyObject? node, DependencyObject container)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, container)) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}

/// <summary>剪贴板条目的类型（对齐 mac 的 ClipKind）。</summary>
public enum ClipRowKind { Text, Link, Code, Color, Image }

/// <summary>
/// 剪贴板列表的一行（自绘列表用的视图模型）。
/// 每个字段对应 mac 的 DrawClipsPage 行内绘制：类型彩点 / 按类型分渲染的正文 / 额外行数 / 相对时间。
/// </summary>
public sealed class ClipRowView : INotifyPropertyChanged
{
    public ClipRowView(Services.ClipboardItem item, DateTime now)
    {
        Item = item;
        string full = item.Full ?? "";
        string trimmed = full.Trim();
        if (item.Kind == "image") Kind = ClipRowKind.Image;
        else if (ClipContentClassifier.IsLink(trimmed)) Kind = ClipRowKind.Link;
        else if (ClipContentClassifier.IsColor(trimmed)) Kind = ClipRowKind.Color;
        else if (ClipContentClassifier.LooksLikeCode(full)) Kind = ClipRowKind.Code;
        else Kind = ClipRowKind.Text;

        // 分类（mac 的 ClipTagOf）：代码 → 代码，链接 → 工作，其余 → 常用
        Tag = Kind switch
        {
            ClipRowKind.Code => "code",
            ClipRowKind.Link => "work",
            _ => "common",
        };
        DotBrush = Tag switch
        {
            "code" => Frozen(Color.FromRgb(0x8E, 0x9B, 0xF5)),
            "work" => Frozen(Color.FromRgb(0x60, 0xC0, 0x78)),
            _ => Frozen(Color.FromRgb(0x5F, 0xD3, 0xA0)),
        };

        switch (Kind)
        {
            case ClipRowKind.Link:
                if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
                {
                    LinkHost = uri.Host;
                    LinkPath = uri.PathAndQuery;
                    LinkUrl = trimmed;
                }
                else
                {
                    LinkHost = trimmed;
                }
                Preview = trimmed;
                break;
            case ClipRowKind.Color:
                Preview = trimmed.ToUpperInvariant();
                SwatchBrush = ClipContentClassifier.ParseColor(trimmed) is { } c ? Frozen(c) : null;
                break;
            case ClipRowKind.Code:
                Preview = ClipContentClassifier.FirstLine(full, 120);
                break;
            case ClipRowKind.Image:
                Preview = item.Preview;
                Image = item.Image;
                break;
            default:
                Preview = item.Preview;
                break;
        }
        Lines = ClipContentClassifier.CountLines(full);
        MoreText = Lines > 1 ? $"+{Lines - 1} 行" : "";
        TimeText = RelTime(item.At, now);
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>原始条目（恢复 / 删除都按它来）。</summary>
    public Services.ClipboardItem Item { get; }

    /// <summary>条目类型（决定正文怎么渲染）。</summary>
    public ClipRowKind Kind { get; }

    /// <summary>分类标签（all / common / work / code 里的一个）。</summary>
    public string Tag { get; }

    /// <summary>行首类型彩点（5px）。</summary>
    public Brush DotBrush { get; }

    /// <summary>颜色条目的色块（14×14）。</summary>
    public Brush? SwatchBrush { get; }

    /// <summary>图片条目的缩略（14×14）。</summary>
    public System.Windows.Media.Imaging.BitmapSource? Image { get; }

    /// <summary>正文文本。</summary>
    public string Preview { get; }

    /// <summary>链接的主机名（链接色）。</summary>
    public string LinkHost { get; } = "";

    /// <summary>链接的路径与查询串。</summary>
    public string LinkPath { get; } = "";

    /// <summary>可打开的链接原文（不是链接则 null）。</summary>
    public string? LinkUrl { get; }

    /// <summary>原文行数。</summary>
    public int Lines { get; }

    /// <summary>多行文本的额外行数提示（mac 的「+N 行」）。</summary>
    public string MoreText { get; }

    /// <summary>相对时间（会随时间自己走）。</summary>
    public string TimeText { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>刷新相对时间（只有文本变了才发通知，避免整列表重绘）。</summary>
    public void UpdateTime(DateTime now)
    {
        string t = RelTime(Item.At, now);
        if (t == TimeText) return;
        TimeText = t;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeText)));
    }

    /// <summary>相对时间：刚刚 / X分钟前 / X小时前 / 昨天 HH:mm / X天前（mac 的 RelTime 加上「昨天」）。</summary>
    public static string RelTime(DateTime at, DateTime now)
    {
        var d = now - at;
        if (d.TotalSeconds < 60) return "刚刚";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}分钟前";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}小时前";
        if (at.Date == now.Date.AddDays(-1)) return "昨天 " + at.ToString("HH:mm");
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}天前";
        return at.ToString("M月d日 HH:mm");
    }
}

/// <summary>剪贴板内容分类（对齐 mac 的 ClipClassifier：链接 → 颜色 → 代码 → 文本）。纯启发式，无网络调用。</summary>
internal static class ClipContentClassifier
{
    private static readonly Regex ColorHex = new(@"^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);
    private static readonly Regex ColorRgb = new(@"^rgba?\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*(,\s*[\d.]+\s*)?\)$", RegexOptions.Compiled);
    private static readonly Regex CodeKeyword = new(
        @"^\s*(using\s|import\s|from\s+\S+\s+import|package\s|def\s|class\s|function\s|public\s|private\s|protected\s|static\s|const\s|let\s|var\s|fn\s|pub\s|struct\s|enum\s|interface\s|type\s+\w+\s*=|#include|#define|if\s*\(|for\s*\(|while\s*\(|return\s)",
        RegexOptions.Compiled);

    /// <summary>单行、无空白、http(s) 且有 host。</summary>
    public static bool IsLink(string text)
        => !text.Any(char.IsWhiteSpace)
        && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.Host.Length > 0;

    /// <summary>颜色文本（#RGB / #RRGGBB / #RRGGBBAA / rgb() / rgba()）。</summary>
    public static bool IsColor(string text) => ColorHex.IsMatch(text) || ColorRgb.IsMatch(text);

    /// <summary>多行：命中行过半才算代码；单行：需带括号或等号（避免把「return 餐厅」误判）。</summary>
    public static bool LooksLikeCode(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return false;
        if (lines.Length == 1)
        {
            string l = lines[0];
            if (!CodeKeyword.IsMatch(l)) return false;
            return l.Contains('(') || l.Contains('=') || l.EndsWith(';') || l.EndsWith('{');
        }
        int hits = lines.Count(l => CodeKeyword.IsMatch(l) || l.EndsWith(';') || l.EndsWith('{') || l.EndsWith('}'));
        return hits * 2 >= lines.Length;
    }

    /// <summary>颜色文本 → ARGB（识别失败返回 null）。</summary>
    public static Color? ParseColor(string text)
    {
        string t = text.Trim();
        try
        {
            if (t.StartsWith('#'))
            {
                string h = t[1..];
                return h.Length switch
                {
                    3 => Color.FromRgb(Hex(h[0].ToString() + h[0]), Hex(h[1].ToString() + h[1]), Hex(h[2].ToString() + h[2])),
                    6 => Color.FromRgb(Hex(h[..2]), Hex(h[2..4]), Hex(h[4..6])),
                    8 => Color.FromArgb(Hex(h[..2]), Hex(h[2..4]), Hex(h[4..6]), Hex(h[6..8])),
                    _ => null,
                };
            }
            var m = Regex.Match(t, @"rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})");
            if (m.Success)
                return Color.FromRgb(byte.Parse(m.Groups[1].Value), byte.Parse(m.Groups[2].Value), byte.Parse(m.Groups[3].Value));
        }
        catch { /* 格式合法但数值越界等 */ }
        return null;
    }

    private static byte Hex(string s) => Convert.ToByte(s, 16);

    /// <summary>原文的行数（多行时预览只显示第一行，剩下的用「+N 行」标出来）。</summary>
    public static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        int n = 1;
        foreach (char c in text) if (c == '\n') n++;
        return n;
    }

    /// <summary>第一行（截断到 max 个字符）。</summary>
    public static string FirstLine(string text, int max)
    {
        int i = text.IndexOf('\n');
        string s = (i >= 0 ? text[..i] : text).Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }
}

/// <summary>按条目类型挑正文模板（对齐 mac 按 ClipKind 分支渲染正文）。</summary>
public sealed class ClipBodySelector : DataTemplateSelector
{
    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        if (item is not ClipRowView v) return null!;
        string key = v.Kind switch
        {
            ClipRowKind.Link => "ClipLinkBody",
            ClipRowKind.Code => "ClipCodeBody",
            ClipRowKind.Color => "ClipColorBody",
            ClipRowKind.Image => "ClipImageBody",
            _ => "ClipTextBody",
        };
        return (Find(key, container) as DataTemplate)!;
    }

    private static object? Find(string key, DependencyObject container)
    {
        if (container is FrameworkElement fe)
        {
            var t = fe.TryFindResource(key);
            if (t is not null) return t;
        }
        return Application.Current?.TryFindResource(key);
    }
}
