using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

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
    /// <summary>页签索引（与 ExpandedPanel.xaml 里页签按钮的 Tag 一致）：
    /// 0=监控 1=剪贴板·文件 2=设备电量 3=控制。页签增删后这里要同步。</summary>
    private const int ControlPageIndex = 3;

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
        _pageBtns = new[] { btnMonitor, btnShelf, btnDevices, btnControl };
        _pages = new UIElement[] { monitorPage, pageShelf, pageDevices, pageControl };
        _tabFills = new[] { tabFillMonitor, tabFillShelf, tabFillDevices, tabFillControl };
        _tabIcons = new[] { tabIconMonitor, tabIconShelf, tabIconDevices, tabIconControl };
        _tabTexts = new[] { tabTextMonitor, tabTextShelf, tabTextDevices, tabTextControl };
        ApplySpecGeometry();
        SelectPage(0);

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _clock.Tick += (_, _) => TickClock();
        _clock.Start();
        TickClock();
    }

    private void TickClock() => clockText.Text = DateTime.Now.ToString("HH:mm");

    private void OnRestoreClipboard(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not PanelData.ClipboardRow row) return;
        try
        {
            bool restored = Services.ClipboardService.TryRestore(row.Item);
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
        UpdateTabVisuals();
    }

    /// <summary>换主题后重取选中态的颜色（页签 / 电源计划按钮的选中色是创建时取的本地值，不会自己跟着资源变）。</summary>
    public void Retint()
    {
        UpdateTabVisuals();
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
    private static Brush AccentFill(Brush? accent)
    {
        if (accent is not SolidColorBrush scb) return Brushes.Transparent;
        Color c = scb.Color;
        byte a = (byte)Math.Round(c.A * ExpandedPanelSpec.TabActiveFillAlpha);
        var fill = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        fill.Freeze();   // 冻结后才能安全共用
        return fill;
    }
}
