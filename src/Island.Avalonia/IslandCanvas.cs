using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Island.App.Animation;
using Island.App.Themes;
using Island.Core.Capabilities;
using Island.Core.Monitoring;

namespace Island.Avalonia;

/// <summary>
/// 岛体自绘（Avalonia 版，与 Windows 版 IslandCanvas/MonitorPage 同源）：
/// 收起态 = dash 仪表盘 / clock 时钟；展开态 = ftop 式系统监控页
/// （CPU 表盘行 → 核心柱 → MEM 进度条 → GPU/硬盘行 → 网速行 → 右栏 Top 进程）。
/// </summary>
public sealed class IslandCanvas : Control
{
    /// <summary>矩形夹紧：非有限值→0，负宽高→0。
    /// Avalonia 的 Rect 构造器对负宽高直接抛 ArgumentException，
    /// 而渲染链路上任何一处坏值（弹簧值、快照值、窗口尺寸）都会让整窗崩掉。
    /// 与 Windows 端 RenderKit.SafeRect 同款。</summary>
    internal static Rect SafeRect(double x, double y, double w, double h)
        => new(Fin(x), Fin(y), Size(w), Size(h));

    private static double Fin(double v) => double.IsFinite(v) ? v : 0;

    private static double Size(double v) => double.IsFinite(v) ? (v < 0 ? 0 : v) : 0;

    public Rect BodyRect { get; set; }          // 窗口 DIP 坐标
    public Point LookPoint { get; set; }        // 光标相对本体中心（pet 视线）
    public bool PointerNear { get; set; }
    public PetFaceAv Pet { get; } = new();

    // ---- 内容属性：setter 自动标脏（Dirty=true），Frame_Tick 据此决定是否重绘 ----
    // 静止时零重绘是这次性能优化的核心；把标脏收进 setter，新增UI状态不用记得手动调 InvalidateVisual。
    private bool _isExpanded; public bool IsExpanded { get => _isExpanded; set { if (_isExpanded != value) Dirty = true; _isExpanded = value; } }
    private string _view = "clock"; public string View { get => _view; set { if (_view != value) Dirty = true; _view = value; } }   // 默认时间胶囊；dash/clock/pet/combo 可滚轮切换
    private string _page = "monitor"; public string Page { get => _page; set { if (_page != value) Dirty = true; _page = value; } }   // monitor / clips / screens / devices
    private Core.Monitoring.MonitorSnapshot? _snapshot; public Core.Monitoring.MonitorSnapshot? Snapshot { get => _snapshot; set { if (!ReferenceEquals(_snapshot, value)) { Dirty = true; if (value is not null) _history.Push(value); } _snapshot = value; } }

    /// <summary>
    /// 历史曲线缓冲（对齐 BrimDeck 监控页的曲线）。纯 UI 侧：每来一个快照追加一个点，
    /// 不碰采样器。固定容量环形缓冲，CPU / GPU / 内存 / 下行 / 上行 / 磁盘读写各一条。
    /// </summary>
    private readonly HistoryBuffer _history = new(capacity: 90);   // 1s 一个点 ≈ 90 秒
    public HistoryBuffer History => _history;    private Island.App.Services.WeatherInfo? _weather; public Island.App.Services.WeatherInfo? Weather { get => _weather; set { Dirty = true; _weather = value; } }
    private IReadOnlyList<ClipItem> _clips = Array.Empty<ClipItem>();
    /// <summary>
    /// 剪贴板列表。setter 只在**条数变化**时触发 <see cref="ClipsCountChanged"/> ——
    /// 展开面板的高度按条数算（两列，行数 = ceil(条数/2)），条数变了高度必须跟着重算，
    /// 否则新条目会被裁掉、删到最后几条时下方留大片空白。
    /// 在 setter 里联动而不是在 9 个赋值点各调一次：以后谁再赋值都不会漏。
    /// 内容变但条数不变（如相对时间刷新）不触发，避免每 2 秒的无谓重算。
    /// </summary>
    public IReadOnlyList<ClipItem> Clips
    {
        get => _clips;
        set
        {
            Dirty = true;
            bool countChanged = _clips.Count != value.Count;
            _clips = value;
            if (countChanged) ClipsCountChanged?.Invoke();
        }
    }
    /// <summary>剪贴板条数变化（宿主据此重算展开面板高度）。</summary>
    public event Action? ClipsCountChanged;
    private IReadOnlyList<DdcScreen> _screens = Array.Empty<DdcScreen>(); public IReadOnlyList<DdcScreen> Screens { get => _screens; set { Dirty = true; _screens = value; } }
    /// <summary>每屏的跟随/范围/预设状态（来自 LunarSettings）。</summary>
    private IReadOnlyList<LunarSettings.ScreenSetting> _screenSettings = Array.Empty<LunarSettings.ScreenSetting>();
    public IReadOnlyList<LunarSettings.ScreenSetting> ScreenSettings { get => _screenSettings; set { Dirty = true; _screenSettings = value; } }
    private string _autoFollowStatus = ""; public string AutoFollowStatus { get => _autoFollowStatus; set { if (_autoFollowStatus != value) Dirty = true; _autoFollowStatus = value; } }
    private bool _builtinOff; public bool BuiltinOff { get => _builtinOff; set { if (_builtinOff != value) Dirty = true; _builtinOff = value; } }

    /// <summary>脏标记：内容变了需要重绘。Frame_Tick 据此决定是否 InvalidateVisual（静止时零重绘）。</summary>
    public bool Dirty { get; set; } = true;
    /// <summary>标脏（替代散落各处的 InvalidateVisual 直调）。</summary>
    public void MarkDirty() => Dirty = true;

    /// <summary>屏幕页热区（绘制时记录）。键：屏幕Id。</summary>
    public Dictionary<uint, Rect> ScreenBrightnessBars { get; } = new();
    public Dictionary<uint, Rect> ScreenContrastBars { get; } = new();
    public Dictionary<uint, Rect> ScreenVolumeBars { get; } = new();
    public Dictionary<uint, Rect> ScreenFollowToggles { get; } = new();
    public Dictionary<uint, Rect> ScreenPresetSaveButtons { get; } = new();
    public Dictionary<uint, Rect> ScreenPresetRestoreButtons { get; } = new();
    // ---- 设备电量页（对齐 BrimDeck「设备电量」页）：大电量环 + 设备名 + 状态 ----
    /// <summary>设备电量列表（读不到就是空，绝不编造）。</summary>
    public IReadOnlyList<DeviceBattery.Device> Devices { get; set; } = Array.Empty<DeviceBattery.Device>();

    // ---- 应用榜（BrimDeck 监控页右区）：真实应用图标 + CPU/内存切换 ----
    /// <summary>应用榜切到「内存」模式。</summary>
    public bool ProcessListByMemory { get; set; }
    /// <summary>「CPU / 内存」切换按钮热区（点击切换榜单排序依据）。</summary>
    public Rect ProcessSortButton;
    public event Action? ProcessSortToggled;
    /// <summary>应用图标缓存（PNG 字节 → 位图）。</summary>
    public AppIconCache? AppIcons { get; set; }

    /// <summary>每屏"允许重试写入"按钮（写入未确认被暂停后出现）。对齐参考版 allowRetry。</summary>
    public Dictionary<uint, Rect> ScreenRetryButtons { get; } = new();
    /// <summary>每屏「设置与预设」折叠行的热区（点击展开/收起）。</summary>
    public Dictionary<uint, Rect> ScreenSettingsRows { get; } = new();
    /// <summary>每屏亮度范围三档的热区（展开后才有效）。</summary>
    public Dictionary<uint, Rect[]> ScreenRangeOptions { get; } = new();
    /// <summary>每屏 RGB 增益滑条热区（颜色设置）：红 / 绿 / 蓝。</summary>
    public Dictionary<uint, Rect[]> ScreenRgbBars { get; } = new();
    /// <summary>每屏「颜色重置」按钮热区。</summary>
    public Dictionary<uint, Rect> ScreenColorResetButtons { get; } = new();
    /// <summary>「自动色温」开关热区（每屏）。</summary>
    public Dictionary<uint, Rect> ScreenAutoColorToggles { get; } = new();
    /// <summary>色温档位分段按钮热区（每屏）：Boxes 按序，Values 是对应的 VCP 20 预设索引。</summary>
    public Dictionary<uint, (Rect[] Boxes, int[] Values)> ScreenColorTempOptions { get; } = new();
    /// <summary>该屏支持的色温档位（VCP 20 预设索引）。空 = 显示器不支持色温预设。</summary>
    public Dictionary<uint, int[]> ScreenColorTempValues { get; } = new();
    /// <summary>每屏「自动色温」开关状态（true = 交给显示器标准白点，false = 手动 RGB）。</summary>
    public Dictionary<uint, bool> ScreenAutoColorState { get; } = new();
    /// <summary>某屏是否处于「自动色温」状态。</summary>
    public bool IsAutoColorOn(uint id) => ScreenAutoColorState.TryGetValue(id, out var v) && v;
    /// <summary>点击「设置与预设」行。</summary>
    public HashSet<uint> ScreenSettingsOpen { get; } = new();
    /// <summary>点击「设置与预设」行。</summary>
    public event Action<uint>? SettingsRowClicked;
    /// <summary>RGB 增益拖动：屏幕Id, vcp(22/24/26), 百分比。</summary>
    public event Action<uint, int, int>? RgbDragged;
    /// <summary>「颜色重置」：把 RGB 恢复成 100/100/100。</summary>
    public event Action<uint>? ColorResetClicked;
    /// <summary>「自动色温」开关：true = 交给显示器（标准白点），false = 手动调 RGB。</summary>
    public event Action<uint, bool>? AutoColorToggled;
    /// <summary>选择显示器的色温档位（VCP 20 预设索引）。</summary>
    public event Action<uint, int>? ColorTempPresetSelected;
    /// <summary>点击亮度范围档位。屏幕Id, (min,max)。</summary>
    public event Action<uint, int, int>? RangeSelected;
    /// <summary>窗口底部的说明文字（屏幕页专用，对齐参考界面）。</summary>
    public Rect ScreensFooterHint;
    /// <summary>每屏静音开关热区。</summary>
    public Dictionary<uint, Rect> ScreenMuteToggles { get; } = new();
    public Rect BuiltinToggleButton;
    /// <summary>内建屏关闭后的倒计时秒数（守护进程 15s 超时前需确认；null=无需确认）。</summary>
    private int? _builtinOffCountdown; public int? BuiltinOffCountdown { get => _builtinOffCountdown; set { if (_builtinOffCountdown != value) Dirty = true; _builtinOffCountdown = value; } }
    public Rect BuiltinKeepButton;
    public event Action? BuiltinKeepClicked;

    /// <summary>亮度范围三档（与托盘菜单的档位一致）。</summary>
    public static readonly (string name, int min, int max)[] ScreenRangeChoices =
        { ("全范围 0–100", 0, 100), ("护眼 10–90", 10, 90), ("窄范围 20–80", 20, 80) };

    /// <summary>当前 (min,max) 对应哪一档；都不匹配时返回 -1（不选中任何一档）。</summary>
    public static int RangeChoiceIndex(int min, int max)
    {
        for (int i = 0; i < ScreenRangeChoices.Length; i++)
            if (ScreenRangeChoices[i].min == min && ScreenRangeChoices[i].max == max) return i;
        return -1;
    }

    /// <summary>屏幕页交互回调。</summary>
    public event Action<uint, int, int>? SliderDragged;      // 屏幕Id, vcpCode, 百分比
    public event Action<uint>? FollowToggled;
    public event Action<uint, bool>? PresetAction;          // 屏幕Id, save(true)/restore(false)
    public event Action<uint>? RetryRequested;              // 屏幕Id：允许重试写入
    public event Action<uint, bool>? MuteToggled;           // 屏幕Id, 静音
    public event Action? BuiltinToggleClicked;
    private Palette _palette = Palette.Dark; public Palette Palette { get => _palette; set { if (!ReferenceEquals(_palette, value)) Dirty = true; _palette = value; } }

    // ---- 收起态装饰（参考 Vibe Island 刘海岛造型，逐项可开关；所有收起态视图共用）----
    private bool _decorLetters = true; public bool DecorLetters { get => _decorLetters; set { if (_decorLetters != value) Dirty = true; _decorLetters = value; } }
    private bool _decorNotch = true;   public bool DecorNotch   { get => _decorNotch;   set { if (_decorNotch != value) Dirty = true; _decorNotch = value; } }
    /// <summary>信息胶囊样式：true=数字主角（第五十四轮新版），false=经典版（时间与读数同级）。</summary>
    private bool _comboNumericHero = true; public bool ComboNumericHero { get => _comboNumericHero; set { if (_comboNumericHero != value) Dirty = true; _comboNumericHero = value; } }
    // 注：第五十七轮已移除「左侧彩色状态环」与「右侧彩色柱状指示」，
    // 连带删掉了 DecorExtraW / DecorLeftBandW / DecorRightBandW —— 胶囊不再有左右装饰带。

    // 页签热区（绘制时记录，宿主点击命中用）
    private Rect _tabMonitor, _tabClips, _tabScreens, _tabDevices;

    /// <summary>页签点击命中；命中返回 true 并切页。</summary>
    public bool HandleTabClick(Point pos)
    {
        if (!IsExpanded) return false;
        string before = Page;
        if (_tabMonitor.Contains(pos)) Page = "monitor";
        else if (_tabClips.Contains(pos)) Page = "clips";
        else if (_tabScreens.Contains(pos)) Page = "screens";
        else if (_tabDevices.Contains(pos)) Page = "devices";
        else return false;
        Dirty = true;   // Page 的 setter 已标脏；这里兜底
        if (Page != before) PageChanged?.Invoke(Page);
        return true;
    }

    /// <summary>展开面板的页切换（监控/剪贴板/屏幕）——宿主据此决定采样需求。</summary>
    public event Action<string>? PageChanged;

    /// <summary>当前拖动中的 VCP 码（0=未拖动）。</summary>
    public int DraggingVcp { get; private set; }
    public uint DraggingScreen { get; private set; }

    /// <summary>屏幕页：按下。命中滑块返回 true 并开始拖动。</summary>
    public bool HandleScreenPress(Point pos)
    {
        if (!IsExpanded || Page != "screens") return false;
        if (BuiltinToggleButton.Contains(pos)) { BuiltinToggleClicked?.Invoke(); return true; }
        if (BuiltinKeepButton.Contains(pos)) { BuiltinKeepClicked?.Invoke(); return true; }
        foreach (var (id, r) in ScreenFollowToggles)
            if (r.Contains(pos)) { FollowToggled?.Invoke(id); return true; }
        foreach (var (id, r) in ScreenPresetSaveButtons)
            if (r.Contains(pos)) { PresetAction?.Invoke(id, true); return true; }
        foreach (var (id, r) in ScreenPresetRestoreButtons)
            if (r.Contains(pos)) { PresetAction?.Invoke(id, false); return true; }
        if (ProcessSortButton.Contains(pos)) { ProcessSortToggled?.Invoke(); return true; }
        foreach (var (id, r) in ScreenColorResetButtons)
            if (r.Contains(pos)) { ColorResetClicked?.Invoke(id); return true; }
        foreach (var (id, r) in ScreenAutoColorToggles)
            if (r.Contains(pos)) { AutoColorToggled?.Invoke(id, !IsAutoColorOn(id)); return true; }
        foreach (var (id, opt) in ScreenColorTempOptions)
            for (int i = 0; i < opt.Boxes.Length; i++)
                if (opt.Boxes[i].Contains(pos)) { ColorTempPresetSelected?.Invoke(id, opt.Values[i]); return true; }
        foreach (var (id, arr) in ScreenRgbBars)
            for (int i = 0; i < arr.Length; i++)
                if (arr[i].Contains(pos))
                {
                    DraggingScreen = id; DraggingVcp = DisplayControl.VcpRed + i * 2;
                    ApplyRgbDrag(pos);
                    return true;
                }
        foreach (var (id, r) in ScreenRetryButtons)
            if (r.Contains(pos)) { RetryRequested?.Invoke(id); return true; }
        foreach (var (id, r) in ScreenRangeOptions)
            for (int i = 0; i < r.Length; i++)
                if (r[i].Contains(pos))
                {
                    RangeSelected?.Invoke(id, ScreenRangeChoices[i].min, ScreenRangeChoices[i].max);
                    return true;
                }
        foreach (var (id, r) in ScreenSettingsRows)
            if (r.Contains(pos))
            {
                if (!ScreenSettingsOpen.Remove(id)) ScreenSettingsOpen.Add(id);
                Dirty = true;
                SettingsRowClicked?.Invoke(id);
                return true;
            }
        foreach (var (id, r) in ScreenMuteToggles)
            if (r.Contains(pos))
            {
                // 切换而不是"一律设为静音"：之前写死 true，点第二次也还是静音。
                var cur = Screens.FirstOrDefault(x => x.Id == id);
                bool nowMuted = cur?.Mute is null ? true : cur.Mute.Value == 1;
                MuteToggled?.Invoke(id, !nowMuted);
                return true;
            }
        foreach (var (id, r) in ScreenBrightnessBars)
            if (r.Contains(pos)) { DraggingVcp = DisplayControl.VcpBrightness; DraggingScreen = id; ApplyDrag(pos); return true; }
        foreach (var (id, r) in ScreenContrastBars)
            if (r.Contains(pos)) { DraggingVcp = DisplayControl.VcpContrast; DraggingScreen = id; ApplyDrag(pos); return true; }
        foreach (var (id, r) in ScreenVolumeBars)
            if (r.Contains(pos)) { DraggingVcp = DisplayControl.VcpVolume; DraggingScreen = id; ApplyDrag(pos); return true; }
        return false;
    }

    /// <summary>屏幕页：拖动中。</summary>
    public bool HandleScreenDrag(Point pos)
    {
        if (DraggingVcp == 0) return false;
        if (DraggingVcp is DisplayControl.VcpRed or DisplayControl.VcpGreen or DisplayControl.VcpBlue)
        {
            ApplyRgbDrag(pos);
            return true;
        }
        var dict = DraggingVcp switch
        {
            DisplayControl.VcpContrast => ScreenContrastBars,
            DisplayControl.VcpVolume => ScreenVolumeBars,
            _ => ScreenBrightnessBars,
        };
        if (dict.TryGetValue(DraggingScreen, out var bar)) ApplyDrag(pos);
        return true;
    }

    public void EndScreenDrag() => DraggingVcp = 0;

    private void ApplyDrag(Point pos)
    {
        var dict = DraggingVcp switch
        {
            DisplayControl.VcpContrast => ScreenContrastBars,
            DisplayControl.VcpVolume => ScreenVolumeBars,
            _ => ScreenBrightnessBars,
        };
        if (!dict.TryGetValue(DraggingScreen, out var bar)) return;
        int pct = (int)Math.Round(Math.Clamp((pos.X - bar.X) / bar.Width, 0, 1) * 100);
        SliderDragged?.Invoke(DraggingScreen, DraggingVcp, pct);
    }

    /// <summary>RGB 增益拖动（颜色设置）。热区数组下标 0/1/2 = 红/绿/蓝。</summary>
    private void ApplyRgbDrag(Point pos)
    {
        if (!ScreenRgbBars.TryGetValue(DraggingScreen, out var arr)) return;
        int idx = DraggingVcp switch
        {
            DisplayControl.VcpGreen => 1,
            DisplayControl.VcpBlue => 2,
            _ => 0,
        };
        if (idx >= arr.Length) return;
        var bar = arr[idx];
        int pct = (int)Math.Round(Math.Clamp((pos.X - bar.X) / bar.Width, 0, 1) * 100);
        RgbDragged?.Invoke(DraggingScreen, DraggingVcp, pct);
    }

    // ---- 资源色：每种资源一个固定色，环 / 图标 / 数字三处同色（对齐 BrimDeck）----
    // 这些色**独立于主题**（主题换的是底色和文字色，资源色不变），只在浅色主题下过 Readable() 压对比度。
    private static readonly Color CpuC = Color.FromRgb(0x2E, 0x9B, 0xFF);   // CPU  蓝
    private static readonly Color MemC = Color.FromRgb(0x4F, 0xE3, 0xD7);   // 内存 青
    private static readonly Color GpuC = Color.FromRgb(0xF2, 0x72, 0xA8);   // GPU  粉
    private static readonly Color DiskC = Color.FromRgb(0x5F, 0xD3, 0xA0);  // 硬盘 青绿（BrimDeck 同款；原来是蓝紫，和内存的青分不开）
    private static readonly Color BattC = Color.FromRgb(0x3F, 0xB8, 0xFF);  // 电池 蓝
    private static readonly Color NetC  = Color.FromRgb(0x6F, 0xB8, 0xFF);  // 网络 蓝

    /// <summary>按资源取色（环 / 图标 / 数字共用一个来源）。</summary>
    private enum Res { Cpu, Mem, Gpu, Disk, Batt, Net }
    private static Color ResColor(Res r, Palette pal) => r switch
    {
        Res.Cpu => CpuCFor(pal), Res.Mem => MemCFor(pal), Res.Gpu => GpuCFor(pal),
        Res.Disk => DiskCFor(pal), Res.Batt => ReadableCached(BattC, pal), _ => ReadableCached(NetC, pal),
    };
    private static readonly Color DownC = Color.FromRgb(0x60, 0xC0, 0x78);
    private static readonly Color UpC = Color.FromRgb(0xD8, 0x90, 0x78);
    private static readonly Color PowerC = Color.FromRgb(0xE8, 0xC8, 0x8F);

    /// <summary>
    /// 对比度自动校正（借鉴 BrimDeck PanelThemes 的 Readable）：数值色是为深色底调的亮色，
    /// 直接放到浅色主题上会糊成一团。这里沿色相不动、只压亮度/加饱和，直到对背景的对比度 ≥ min。
    /// 深色主题原样返回，不改变现有外观。
    /// </summary>
    private static Color Readable(Color c, Palette pal, double min = 3.0)
    {
        if (pal.IsDark) return c;
        double L(Color x) => RelativeLuminance(x);
        double bg = L(pal.Surface);
        if (ContrastRatio(L(c), bg) >= min) return c;
        // 浅底：逐步压暗（保留色相），最多 12 步
        var hsv = ColorToHsv(c);
        for (int i = 1; i <= 12; i++)
        {
            double v = hsv.v * (1 - i * 0.07);
            double s = Math.Min(1, hsv.s + i * 0.03);
            var cand = ColorFromHsv(hsv.h, s, Math.Max(0.08, v));
            if (ContrastRatio(L(cand), bg) >= min) return cand;
        }
        return ColorFromHsv(hsv.h, Math.Min(1, hsv.s + 0.3), 0.15);
    }

    /// <summary>WCAG 相对亮度。</summary>
    private static double RelativeLuminance(Color c)
    {
        static double Lin(byte b) { double x = b / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double ContrastRatio(double l1, double l2)
    {
        double a = Math.Max(l1, l2), b = Math.Min(l1, l2);
        return (a + 0.05) / (b + 0.05);
    }

    private static (double h, double s, double v) ColorToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60; if (h < 0) h += 360;
        }
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static Color ColorFromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        (double r, double g, double b) rgb = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        double r = rgb.Item1, g = rgb.Item2, b = rgb.Item3;
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    // 对比度校正结果按 (颜色, 主题) 缓存：浅色主题下每次要跑最多 12 次 Pow 循环，时钟视图一帧 3 次、监控页 15 次。
    private static readonly Dictionary<(Color, Palette), Color> _readableCache = new();
    private static Color ReadableCached(Color c, Palette pal)
    {
        var k = (c, pal);
        if (_readableCache.TryGetValue(k, out var r)) return r;
        return _readableCache[k] = Readable(c, pal);
    }
    /// <summary>按当前主题校正后的数值色（结果缓存，不每帧重算）。</summary>
    private static Color CpuCFor(Palette pal) => ReadableCached(CpuC, pal);
    private static Color MemCFor(Palette pal) => ReadableCached(MemC, pal);
    private static Color GpuCFor(Palette pal) => ReadableCached(GpuC, pal);
    private static Color DiskCFor(Palette pal) => ReadableCached(DiskC, pal);

    private static Color TempColor(double? t, Palette pal) => t switch
    {
        null => Color.FromRgb(0x90, 0xA8, 0xF0),
        <= 60 => Color.FromRgb(0x60, 0xC0, 0x78),
        <= 75 => pal.Warning,
        _ => pal.Up,
    };

    // macOS 无 root 读不到温度数值时用 NSProcessInfo.thermalState 档位降级显示。
    private static Color ThermalColor(string state, Palette pal) => state switch
    {
        "正常" => Color.FromRgb(0x60, 0xC0, 0x78),
        "偏高" => Color.FromRgb(0xE8, 0xC8, 0x8F),
        "较高" => pal.Warning,
        "过高" => pal.Up,
        _ => Color.FromRgb(0x90, 0xA8, 0xF0),
    };

    public override void Render(DrawingContext dc)
    {
        var pal = Palette;
        var r = BodyRect;
        if (r.Width < 40 || r.Height < 20) return;

        if (IsExpanded && r.Width > 300)
        {
            DrawExpanded(dc, pal, r);
            return;
        }

        double cy = r.Y + r.Height / 2;
        // 刘海融合造型：两端上扬、顶部中间凹口露出后方刘海（默认开，可在托盘关闭回到纯圆角）
        if (_decorNotch && DrawNotchBody(dc, pal, r, cy))
        {
            // 造型已画好，走装饰带分支
        }
        else
        {
            double corner = r.Height / 2;
            // 胶囊背景用不透明：避免后方窗口/桌面图标透过半透明底，干扰图标+读数的辨识
            dc.DrawRectangle(RenderKit.Faint(pal.Surface, 1.0), RenderKit.PenOf(pal.Rim, 1, 0.45),
                new RoundedRect(r, corner));
            // 玻璃质感：**单层**极淡高光，只压顶部 1/3。
            dc.DrawRectangle(RenderKit.Faint(Colors.White, 0.035), null,
                new RoundedRect(SafeRect(r.X + 1, r.Y + 1, r.Width - 2, r.Height * 0.34), corner - 1));
        }

        // 第五十七轮：左右装饰带（左状态环 / 右彩柱）已移除，胶囊内容直接占满整个区域。

        if (View == "pet")
        {
            Pet.CpuTemp = Snapshot?.CpuTemp;                  // 情境（高温流汗）
            Pet.DiskActive = Snapshot?.DiskUsagePercent ?? 0;   // 情境（晕眩）
            Pet.SurfaceColor = pal.Surface;                     // 眼睑遮罩跟随主题
            Pet.Draw(dc, r.X + r.Width / 2, cy, 1.0, 1, LookPoint, PointerNear);
        }
        else if (View == "clock") DrawClock(dc, pal, r, cy);
        else if (View == "combo") DrawCombo(dc, pal, r, cy);
        else DrawDash(dc, pal, r, cy);
    }

    // ============ 收起态装饰（参考 Vibe Island 刘海岛）============
    // 现存两元素：刘海造型 / 字母标识环。均可独立开关。
    // 左侧状态环与右侧柱状指示已于第五十七轮移除。

    /// <summary>
    /// 环中心：DecorLetters 开 → 画资源字母 C/M/G/D/B/N（参考图同款）；关 → 画矢量图标（原样）。
    /// </summary>
    private void DrawGaugeCenter(DrawingContext dc, Palette pal, Glyph icon, Res res,
        double x, double cy, double dial, Color color)
    {
        if (_decorLetters)
        {
            string letter = res switch
            {
                Res.Cpu => "C", Res.Mem => "M", Res.Gpu => "G", Res.Disk => "D",
                Res.Batt => "B", Res.Net => "N", _ => "?",
            };
            var t = RenderKit.Text(letter, dial * 0.52, color, true);
            dc.DrawText(t, new Point(x + (dial - t.Width) / 2, cy - t.Height / 2));
        }
        else
        {
            DrawIcon(dc, icon, x + (dial - 12 * 0.6) / 2, cy, color, 0.6);
        }
    }

    /// <summary>
    /// 刘海融合造型：两端上扬 + 顶部中间凹口（露出后方刘海）的胶囊形。
    /// 用 StreamGeometry 画一条闭合路径；凹口是向内的圆弧，宽度取窗口 1/3 且限幅，
    /// 太窄会像缺口、太宽会把两端"耳朵"吃掉。渲染不依赖窗口高度变化（#100 再定夺高度）。
    /// 返回 false 表示窗口太窄画不了，调用方退回普通圆角。
    /// </summary>
    private bool DrawNotchBody(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        if (r.Width < 120 || r.Height < 26) return false;
        double h = r.Height, corner = h / 2;
        double notchW = Math.Clamp(r.Width * 0.32, 96, 150);
        double notchDepth = Math.Min(15, h * 0.34);
        double cx = r.X + r.Width / 2;
        double nl = cx - notchW / 2, nr = cx + notchW / 2;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            // 从左上角圆角起点出发，沿顶边 → 凹口 → 右端 → 底边 → 左端闭合。
            // 凹口用**单段** ArcTo：屏幕坐标 y 向下，从左侧 nl 到右侧 nr 走 Clockwise 弧 = 向下凸（凹口）。
            // 之前拆两段 CounterClockwise 方向反了，画成了平边，看不见缺口。
            ctx.BeginFigure(new Point(r.X + corner, r.Y), true);
            ctx.LineTo(new Point(nl, r.Y));
            ctx.ArcTo(new Point(nr, r.Y), new Size(notchW / 2, notchDepth),
                0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(r.Right - corner, r.Y));
            ctx.ArcTo(new Point(r.Right, cy), new Size(corner, corner), 0, false, SweepDirection.Clockwise);
            ctx.ArcTo(new Point(r.Right - corner, r.Bottom), new Size(corner, corner), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(r.X + corner, r.Bottom));
            ctx.ArcTo(new Point(r.X, cy), new Size(corner, corner), 0, false, SweepDirection.Clockwise);
            ctx.ArcTo(new Point(r.X + corner, r.Y), new Size(corner, corner), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }
        dc.DrawGeometry(RenderKit.Faint(pal.Surface, 1.0), RenderKit.PenOf(pal.Rim, 1.2, 0.85), g);
        // 凹口弧线单独再描一道稍亮的边：深色桌面上凹口与胶囊同色，不描边就看不出缺口
        var notchLine = new StreamGeometry();
        using (var nx = notchLine.Open())
        {
            nx.BeginFigure(new Point(nl, r.Y + 0.5), false);
            nx.ArcTo(new Point(nr, r.Y + 0.5), new Size(notchW / 2, notchDepth),
                0, false, SweepDirection.Clockwise);
            nx.EndFigure(false);
        }
        dc.DrawGeometry(null, RenderKit.PenOf(pal.TextDim, 1, 0.7), notchLine);
        // 高光只画在两端"耳朵"上，不盖住凹口（原来整条高光矩形会把凹口糊平）
        double ear = (r.Width - notchW) / 2 - corner;
        if (ear > 4)
        {
            dc.DrawRectangle(RenderKit.Faint(Colors.White, 0.035), null,
                new RoundedRect(SafeRect(r.X + 1, r.Y + 1, ear, r.Height * 0.34), corner - 1));
            dc.DrawRectangle(RenderKit.Faint(Colors.White, 0.035), null,
                new RoundedRect(SafeRect(nr - 1, r.Y + 1, ear, r.Height * 0.34), corner - 1));
        }
        return true;
    }

    // ============ 信息胶囊（combo）布局 ============
    // 和时钟一样：测量与绘制**共用** LayoutCombo 算出的数字。之前两边各写一遍公式，
    // 和时钟出框是同一种病（宽度测量靠手算魔数：24 / 30 / 58 …，和实际绘制对不上）。
    private const double ComboPadX = 10;       // 胶囊左右内边距（第五十八轮压缩：14→10）
    private const double ComboFigureFont = 13, ComboDial = 20, ComboDialGap = 5, ComboGaugeGap = 10, ComboClockGap = 12;
    // 收起态三个视图共用同一套图标槽尺寸与组间距 —— 风格统一的基础。
    private const double CompactIconBox = 12 * 0.9, CompactIconGap = 4, CompactTempGap = 10;
    /// <summary>指标数值的最小占位宽度，避免窄读数导致后续内容跳动。</summary>
    private const double CompactValueMinW = 21;
    /// <summary>收起态分组之间的分隔留白（dash / combo 共用）。第五十八轮压缩：18→12。</summary>
    private const double CompactGroupGap = 12;   // 组与组之间（分隔线居中）

    /// <summary>
    /// 信息胶囊保留时钟主次两种风格，所有系统指标共用同一套度量。
    /// 嵌套类 ComboLayout 不是外层实例的内部类，拿不到实例属性 —— 所以时钟样式显式传进去。
    /// </summary>
    private struct ComboStyle
    {
        public bool ClockStrong;
    }

    /// <summary>
    /// 当前胶囊时钟层级：true = 时钟弱化；false = 时钟强调。
    /// </summary>
    private ComboStyle CurrentComboStyle => new() { ClockStrong = !_comboNumericHero };


    /// <summary>信息胶囊版式；所有环形指标共用字号、直径、间距和最小数值槽宽。</summary>
    private sealed class ComboLayout
    {
        public FormattedText Clock = null!;
        public FormattedText NetDownArrow = null!, NetDownValue = null!, NetUpArrow = null!, NetUpValue = null!;
        public readonly List<(Glyph Icon, Res Res, double? Usage, Color Arc, FormattedText Figure)> Gauges = new();
        public readonly List<(Glyph Icon, Res Res, double? Usage, Color Arc, FormattedText Figure)> SystemGauges = new();

        /// <summary>当前风格度量（数字主角 / 经典版）。</summary>
        public ComboStyle Style;

        public double ClockW => Clock.WidthIncludingTrailingWhitespace;
        public double ClockGap => ComboClockGap;
        public double DialGap => ComboDialGap;
        public double GaugeGap => ComboGaugeGap;
        public double Dial => ComboDial;
        public double FigureFont => ComboFigureFont;
        public double NetGap => 7;
        public double NetDownW => NetDownArrow.WidthIncludingTrailingWhitespace + 3 + NetDownValue.WidthIncludingTrailingWhitespace;
        public double NetUpW => NetUpArrow.WidthIncludingTrailingWhitespace + 3 + NetUpValue.WidthIncludingTrailingWhitespace;
        public double GaugesW => GaugeWidth(Gauges);
        public double SystemGaugesW => GaugeWidth(SystemGauges);
        private double GaugeWidth(List<(Glyph Icon, Res Res, double? Usage, Color Arc, FormattedText Figure)> gauges)
            => gauges.Sum(g => Dial + DialGap + Math.Max(g.Figure.WidthIncludingTrailingWhitespace, CompactValueMinW) + GaugeGap)
                - (gauges.Count > 0 ? GaugeGap : 0);

        // 分组之间有分隔线（宽度 = 两侧留白 + 1px 线），和 dash 一样
        public double ContentW => ClockW + ClockGap + GaugesW
            + (SystemGauges.Count > 0 ? CompactGroupGap * 2 + SystemGaugesW : 0)
            + CompactGroupGap * 2 + NetDownW + NetGap + NetUpW;
        public double CapsuleW => ContentW + ComboPadX * 2;

    }

    private ComboLayout LayoutCombo(Palette pal)
    {
        var snap = Snapshot;
        var style = CurrentComboStyle;
        var l = new ComboLayout { Style = style };
        l.Clock = RenderKit.Text(DateTime.Now.ToString("HH:mm"), FontValue,
            style.ClockStrong ? pal.Text : pal.TextDim, style.ClockStrong);
        l.NetDownArrow = RenderKit.Text("↓", FontValue, pal.Down, true);
        l.NetDownValue = RenderKit.Text(MonitorSnapshot.FormatSpeedFixed(snap?.DownloadSpeed ?? 0), FontValue, pal.Text, true);
        l.NetUpArrow = RenderKit.Text("↑", FontValue, pal.Up, true);
        l.NetUpValue = RenderKit.Text(MonitorSnapshot.FormatSpeedFixed(snap?.UploadSpeed ?? 0), FontValue, pal.Text, true);
        if (snap is not null)
        {
            foreach (var (icon, res, usage) in new[]
                     { (Glyph.Cpu, Res.Cpu, snap.CpuUsage), (Glyph.Mem, Res.Mem, snap.MemoryUsagePercent),
                       (Glyph.Gpu, Res.Gpu, snap.GpuUsage) })
            {
                Color arc = ResColor(res, pal);
                Color value = RenderKit.LevelColor(usage, pal.Text, pal);
                l.Gauges.Add((icon, res, usage / 100, arc,
                    RenderKit.Text($"{Math.Round(usage)}%", l.FigureFont, value, true)));
            }
            double? cpuTemp = snap.CpuTemp is > 0 ? snap.CpuTemp : null;
            double? gpuTemp = snap.GpuTemp is > 0 ? snap.GpuTemp : null;
            AddSystemGauge(Glyph.Cpu, Res.Cpu, cpuTemp,
                cpuTemp is { } ct ? Math.Clamp(ct / 125, 0, 1) : null,
                cpuTemp is { } ctv ? $"{Math.Round(ctv)}°" : "—", TempColor(cpuTemp, pal));
            AddSystemGauge(Glyph.Gpu, Res.Gpu, gpuTemp,
                gpuTemp is { } gt ? Math.Clamp(gt / 125, 0, 1) : null,
                gpuTemp is { } gtv ? $"{Math.Round(gtv)}°" : "—", TempColor(gpuTemp, pal));
            AddSystemGauge(Glyph.Disk, Res.Disk, snap.DiskUsagePercent,
                snap.DiskUsagePercent / 100, $"{Math.Round(snap.DiskUsagePercent)}%", ResColor(Res.Disk, pal));
        }
        void AddSystemGauge(Glyph icon, Res res, double? usage, double? fill, string text, Color arc)
        {
            Color value = usage is null ? pal.TextTertiary : res == Res.Disk
                ? RenderKit.LevelColor(usage.Value, pal.Text, pal)
                : TempColor(usage, pal);
            l.SystemGauges.Add((icon, res, fill, arc,
                RenderKit.Text(text, l.FigureFont, value, true)));
        }
        return l;
    }

    /// <summary>信息胶囊所需宽度（宿主用它设置窗口大小）。</summary>
    public double MeasureComboWidth() => LayoutCombo(Palette).CapsuleW;

    /// <summary>
    /// 信息胶囊：时间 + CPU/内存/GPU、CPU/GPU 温度、磁盘占用与网速。
    /// 各指标共用环形表盘；网速数值中性，箭头保留方向色。
    /// </summary>
    private void DrawCombo(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        var L = LayoutCombo(pal);
        if (Snapshot is null) return;
        // 背景由外层统一画（与 dash 同一层）
        double x = r.X + (r.Width - L.ContentW) / 2;      // 内容在胶囊里水平居中

        // 分组分隔线（与 dash 视图同一套：竖线 + 两侧留白）
        void Divider()
            => dc.DrawLine(RenderKit.PenOf(pal.Hairline, 1, 0.8), new Point(x + CompactGroupGap / 2, cy - 9), new Point(x + CompactGroupGap / 2, cy + 9));
        void Gap() { x += CompactGroupGap; Divider(); x += CompactGroupGap; }

        // ① 时间（数字主角版为次级 13px 弱化；经典版为主角 13px 粗体）
        RenderKit.Baseline(dc, L.Clock, x, Math.Round(cy + FontValue * 0.36));
        x += L.ClockW + L.ClockGap;

        void DrawGaugeGroup(List<(Glyph Icon, Res Res, double? Usage, Color Arc, FormattedText Figure)> gauges)
        {
            for (int i = 0; i < gauges.Count; i++)
            {
                var g = gauges[i];
                RenderKit.Ring(dc, new Point(x + L.Dial / 2, cy), L.Dial, Math.Max(1.8, L.Dial * 0.14),
                    g.Usage ?? 0, pal.Track, g.Arc);
                DrawGaugeCenter(dc, pal, g.Icon, g.Res, x, cy, L.Dial, g.Arc);
                x += L.Dial + L.DialGap;
                RenderKit.Baseline(dc, g.Figure, x, Math.Round(cy + L.FigureFont * 0.36));
                x += Math.Max(g.Figure.WidthIncludingTrailingWhitespace, CompactValueMinW);
                if (i < gauges.Count - 1) x += L.GaugeGap;
            }
        }
        DrawGaugeGroup(L.Gauges);
        if (L.Gauges.Count > 0 && L.SystemGauges.Count > 0) Gap();
        DrawGaugeGroup(L.SystemGauges);
        if (L.SystemGauges.Count > 0) Gap();

        // 网速方向由箭头表达，数值统一使用中性色。
        double netBaseline = Math.Round(cy + FontValue * 0.36);
        RenderKit.Baseline(dc, L.NetDownArrow, x, netBaseline);
        x += L.NetDownArrow.WidthIncludingTrailingWhitespace + 3;
        RenderKit.Baseline(dc, L.NetDownValue, x, netBaseline);
        x += L.NetDownValue.WidthIncludingTrailingWhitespace + L.NetGap;
        RenderKit.Baseline(dc, L.NetUpArrow, x, netBaseline);
        x += L.NetUpArrow.WidthIncludingTrailingWhitespace + 3;
        RenderKit.Baseline(dc, L.NetUpValue, x, netBaseline);
    }

    // ==================== 展开态：页签 + 监控页 / 剪贴板页 ====================
    private void DrawExpanded(DrawingContext dc, Palette pal, Rect r)
    {
        // 与收起态一致用完全不透明背景：0.96 会让后方窗口文字透过面板形成重影
        dc.DrawRectangle(RenderKit.Faint(pal.Surface, 1.0), RenderKit.PenOf(pal.Rim, 1, 0.45),
            new RoundedRect(r, 20));

        // ---- 顶部工具栏：图标 + 文字页签（参考图顶栏）+ 右侧时间 ----
        double tabX = r.X + 20, tabY = r.Y + 14;
        void Tab(string label, string page, Glyph icon, ref Rect rect)
        {
            bool active = Page == page;
            var t = RenderKit.Text(label, 12, active ? pal.Text : pal.TextDim, active);
            Color ic = active ? pal.Accent : pal.TextDim;
            const double iconW = 14;
            rect = SafeRect(tabX - 7, tabY - 5, iconW + 5 + t.WidthIncludingTrailingWhitespace + 14, t.Height + 10);
            if (active)
                dc.DrawRectangle(RenderKit.Faint(pal.Accent, 0.20), null, new RoundedRect(rect, 8));
            DrawIcon(dc, icon, tabX, tabY, ic, 1.0);
            dc.DrawText(t, new Point(tabX + iconW + 5, tabY));
            tabX += rect.Width + 6;
        }
        // 展开页签：**取自能力清单**（两端唯一真相源）。
        // 清单顺序 = 共享页签在前、平台专属页签在后（总纲 §1 的统一呈现规则），
        // 所以 mac 侧是 监控 / 剪贴板 / 设备 / 屏幕（"屏幕"从"设备"前面挪到最后）。
        // 清单决定"有哪些页签、什么顺序、叫什么"；IsMacOS() 只决定"本机能不能真的服务这个页签"
        // （设备页要 IOKit 探针、屏幕页要 DDC 工具链，dev 机跑 mac 工程时都没有）。
        // ===== 能力清单页签区：开始 =====
        foreach (string key in CapabilityManifest.ExpandedTabsFor(macOS: true))
        {
            string title = CapabilityManifest.ExpandedTabs.First(e => e.Key == key).Title;
            switch (key)
            {
                case "monitor": Tab(title, "monitor", Glyph.Spark, ref _tabMonitor); break;
                case "clips": Tab(title, "clips", Glyph.ClipBox, ref _tabClips); break;
                case "devices":
                    if (OperatingSystem.IsMacOS()) Tab(title, "devices", Glyph.Battery, ref _tabDevices);
                    break;
                case "screens":
                    if (OperatingSystem.IsMacOS()) Tab(title, "screens", Glyph.SunLine, ref _tabScreens);
                    break;
            }
        }
        // ===== 能力清单页签区：结束 =====

        // 顶栏右侧：当前时间（参考图 18:10 的位置）
        var clockText = RenderKit.Text(DateTime.Now.ToString("HH:mm"), 12, pal.TextDim);
        dc.DrawText(clockText, new Point(r.Right - 24 - clockText.WidthIncludingTrailingWhitespace, tabY));

        double contentY = tabY + 24 + 16;
        if (Page == "clips")
        {
            DrawClipsPage(dc, pal, r, contentY);
            DrawClipMenu(dc, pal, r);   // 右键菜单浮层画在最上层
            return;
        }
        if (Page == "screens") { DrawScreensPage(dc, pal, r, contentY); return; }
        if (Page == "devices") { DrawDevicesPage(dc, pal, r, contentY); return; }
        DrawMonitorPage(dc, pal, r, contentY);
    }

    /// <summary>
    /// 屏幕页：显示器控制。布局对齐参考界面（LunarCN 主窗口）的**纵向流式卡片**：
    ///   卡片头（行首图标 + 名称 / 副标题）→ 每个控制项独占一行（行首图标 + 标签 + 滑轨 + 数值）
    ///   → 开关行 → 状态行 → 可展开的「设置与预设」。面板宽 560（参考是 698），高度按内容自适应。
    /// 配色沿用主题 token，不照搬参考图的浅色。
    /// </summary>
    private void DrawScreensPage(DrawingContext dc, Palette pal, Rect r, double y0)
    {
        ScreenBrightnessBars.Clear(); ScreenContrastBars.Clear(); ScreenVolumeBars.Clear();
        ScreenFollowToggles.Clear(); ScreenPresetSaveButtons.Clear(); ScreenPresetRestoreButtons.Clear();
        ScreenRetryButtons.Clear(); ScreenMuteToggles.Clear();
        ScreenSettingsRows.Clear(); ScreenRangeOptions.Clear();
        BuiltinToggleButton = default; BuiltinKeepButton = default; ScreensFooterHint = default;

        double x = r.X + 20, w = r.Width - 40, y = y0;
        const double cardPad = 16, cardGap = 12, radius = 12, iconBox = 14, iconGap = 8, rowH = 34;
        double labelColW = Math.Ceiling(RenderKit.Text("显示器音量", 11, pal.Text).WidthIncludingTrailingWhitespace) + 16;

        if (Screens.Count == 0 && !BuiltinOff)
        {
            var t = RenderKit.Text("未检测到屏幕控制通道（随包工具缺失或非 Apple Silicon）", 11, pal.TextDim);
            dc.DrawText(t, new Point(x, y + 30));
            return;
        }

        // ---------- 共用绘制件 ----------
        // 卡片底必须在内容**之前**画（不透明，画在内容之后会把内容全盖住）。
        // 高度用 ScreenCardHeight 预先算出来，与下面逐行排布的常量保持一致。
        void Card(double cy, double ch)
            => dc.DrawRectangle(RenderKit.BrushOf(pal.Elevated), RenderKit.PenOf(pal.Rim, 1, 0.55),
                 new RoundedRect(SafeRect(x, cy, w, ch), radius));

        // 单张外接屏/内建屏卡片的高度（不含卡片间距）
        double ScreenCardHeight(DdcScreen s, bool hasBuiltin)
        {
            double card = 38 + 2 + rowH;                 // 头 + 亮度
            if (!s.Builtin)
            {
                card += 28;                              // 亮度范围（三档分段）
                card += rowH;                            // 对比度
                if (hasBuiltin) card += rowH;            // 跟随开关
                card += rowH;                            // 音量
                if (s.Mute is not null) card += rowH;    // 静音开关
            }
            // 颜色设置区（外接屏恒有；读不到时显示 --）
            if (!s.Builtin) card += 24 + 3 * 30 + 38;   // 标题 + 三条 RGB 滑条 + 重置按钮
            // 色温档位行（显示器支持色温预设时才有）
            if (!s.Builtin && ScreenColorTempValues.ContainsKey(s.Id)) card += 28;
            card += 30 + 30;                             // 状态行 + 折叠行
            if (ScreenSettingsOpen.Contains(s.Id)) card += 64;
            return card + cardPad - 4;
        }

        // 卡片头：行首图标（强调色）+ 名称 / 副标题。返回内容起始 y。
        double CardHead(double cy, Glyph g, string name, string sub)
        {
            double iy = cy + cardPad;
            DrawIcon(dc, g, x + cardPad, iy + 8, pal.Accent, 1.2);
            double tx = x + cardPad + iconBox + iconGap;
            dc.DrawText(RenderKit.Trimmed(name, 12.5, pal.Text, w - 2 * cardPad - 40, true), new Point(tx, iy - 3));
            dc.DrawText(RenderKit.Trimmed(sub, 9.5, pal.TextTertiary, w - 2 * cardPad - 40, false), new Point(tx, iy + 16));
            return iy + 38;
        }

        // 一行滑块：行首图标 + 标签 ·············· 滑轨 ······●······ 数值
        // 对齐参考图：滑轨有**刻度点**，滑块是**圆钮**（不是纯色填充条）。
        // 不支持的项：不画滑轨，数值处显示"不支持"，不返回热区。
        Rect Slider(double sy, Glyph g, string label, int pct,
                    bool loaded = true, bool ok = true, bool paused = false, (int min, int max)? range = null)
        {
            double ly = sy + 3;
            DrawIcon(dc, g, x + cardPad + 1, ly + 6, ok ? pal.TextDim : pal.TextTertiary, 1.0);
            double lx = x + cardPad + iconBox + iconGap;
            dc.DrawText(RenderKit.Text(label, 11, ok ? pal.Text : pal.TextTertiary), new Point(lx, ly - 1));
            // 标签列宽按最长标签（"显示器音量"）实测，滑轨起点统一 → 三行的滑轨左对齐
            double bx = lx + labelColW, bw = w - (bx - x) - cardPad - 52;
            double by = sy + 6, barH = 4;
            double kx0 = bx + 6, kx1 = bx + bw - 6;      // 圆钮可活动范围（两端各留 6）

            dc.DrawRectangle(RenderKit.BrushOf(pal.Track), null, new RoundedRect(SafeRect(bx, by, bw, barH), 2));
            if (ok)
            {
                // 范围外压暗（告诉用户"这里写不进去"）
                if (range is { } rg)
                {
                    if (rg.min > 0)
                        dc.DrawRectangle(RenderKit.Faint(pal.Surface, 0.55), null,
                            new RoundedRect(SafeRect(bx, by, Math.Max(1, bw * rg.min / 100.0), barH), 2));
                    if (rg.max < 100)
                        dc.DrawRectangle(RenderKit.Faint(pal.Surface, 0.55), null,
                            new RoundedRect(SafeRect(bx + bw * rg.max / 100.0, by, Math.Max(1, bw * (100 - rg.max) / 100.0), barH), 2));
                }
                double f = Math.Clamp(pct / 100.0, 0, 1);
                if (loaded && f > 0.002)
                    dc.DrawRectangle(RenderKit.BrushOf(paused ? pal.Warning : pal.Accent), null,
                        new RoundedRect(SafeRect(bx, by, Math.Max(3, (kx0 - bx) + (kx1 - kx0) * f), barH), 2));
            }
            // 刻度点（参考图同款：轨道下方一排小点）
            for (int i = 1; i < 20; i++)
                dc.DrawEllipse(RenderKit.BrushOf(pal.TextTertiary), null, new Point(bx + bw * i / 20.0, by + barH + 5), 0.8, 0.8);

            if (!ok)
            {
                dc.DrawText(RenderKit.Text("不支持", 11, pal.TextTertiary, true), new Point(bx + bw + 10, ly - 1));
                return default;
            }
            if (loaded)
            {
                double kx = kx0 + (kx1 - kx0) * Math.Clamp(pct / 100.0, 0, 1);
                double ky = by + barH / 2;
                dc.DrawEllipse(RenderKit.BrushOf(pal.Elevated), null, new Point(kx, ky), 9, 9);    // 圆钮外圈：与卡片底色同色，盖住轨道
                dc.DrawEllipse(RenderKit.BrushOf(paused ? pal.Warning : pal.Text), null, new Point(kx, ky), 6.5, 6.5);
            }
            dc.DrawText(RenderKit.Text(loaded ? $"{pct}%" : "--", 11, paused ? pal.Warning : pal.Text, true),
                new Point(bx + bw + 10, ly - 1));
            return SafeRect(bx, by - 10, bw, barH + 22);
        }

        Rect Toggle(double tx, double ty, bool on, bool enabled = true)
        {
            var box = SafeRect(tx, ty, 36, 20);
            dc.DrawRectangle(RenderKit.Faint(on ? pal.Accent : pal.Track, enabled ? (on ? 0.95 : 1) : 0.4), null, new RoundedRect(box, 10));
            dc.DrawEllipse(RenderKit.BrushOf(enabled && on ? pal.Surface : enabled ? pal.Text : pal.TextTertiary), null,
                new Point(tx + (on ? 26 : 10), ty + 10), 7, 7);
            return box.Inflate(new Thickness(6, 6));
        }

        // 按钮：primary=实心；否则浅底描边（对齐参考图：主操作是灰底实心，次要操作是浅底）
        Rect Button(double bx, double by, string label, bool enabled, bool primary = false, Color? tint = null)
        {
            var t = RenderKit.Text(label, 10.5, !enabled ? pal.TextTertiary : primary ? pal.Surface : (tint ?? pal.Text), true);
            double bw = t.WidthIncludingTrailingWhitespace + 20, bh = 24;
            var box = SafeRect(bx, by, bw, bh);
            var bg = primary ? RenderKit.BrushOf(tint ?? pal.Accent) : RenderKit.Faint(tint ?? pal.Text, enabled ? 0.08 : 0.03);
            dc.DrawRectangle(bg, primary ? null : RenderKit.PenOf(pal.Rim, 1, enabled ? 0.7 : 0.3), new RoundedRect(box, 7));
            dc.DrawText(t, new Point(bx + 10, by + 4));
            return box;
        }

        bool hasBuiltinScreen = Screens.Any(s => s.Builtin);

        // ---------- 卡片 ①：内建屏 ----------
        if (hasBuiltinScreen || BuiltinOff)
        {
            double cy = y;
            Card(cy, 38 + 2 + (Screens.Any(t => t.Builtin) ? rowH : 0) + 24 + cardPad);     // 先画底
            double inner = CardHead(cy, Glyph.Laptop, BuiltinOff ? "内建屏（已关闭）" : "笔记本屏幕",
                BuiltinOff
                    ? (BuiltinOffCountdown is { } c && c > 0 ? $"{c} 秒后自动恢复 · 不确认会自动打开" : "已保持关闭 · 退出或拔掉外接屏会自动恢复")
                    : "关闭内置屏幕后，键盘和触控板仍可使用。");
            double by = inner + 2;
            // 内建屏的亮度滑块并入这张卡（参考图：内建屏只有一张卡，不单独再出一张）
            var builtinScreen = Screens.FirstOrDefault(t => t.Builtin);
            if (builtinScreen is { } bs)
            {
                var bset = ScreenSettings.FirstOrDefault(k => k.Uuid == bs.Uuid);
                ScreenBrightnessBars[bs.Id] = Slider(by - 4, Glyph.SunLine, "亮度", bs.Brightness,
                    bs.ValuesLoaded, bs.BrightnessOk, false, (bset?.Min ?? 0, bset?.Max ?? 100));
                by += rowH;
            }
            if (BuiltinOff)
            {
                BuiltinToggleButton = Button(x + cardPad, by, "恢复内置屏幕", true, primary: true);
                if (BuiltinOffCountdown is { } cd && cd > 0)
                    BuiltinKeepButton = Button(x + cardPad + BuiltinToggleButton.Width + 8, by, "保持关闭", true, tint: pal.Warning);
            }
            else
            {
                BuiltinToggleButton = Button(x + cardPad, by, "关闭内置屏幕", true);
            }
            y = by + 24 + cardPad + cardGap;
        }

        // ---------- 卡片 ②：每块屏 ----------
        foreach (var s in Screens)
        {
            if (s.Builtin) continue;   // 内建屏已并入顶部「笔记本屏幕」卡片
            var setting = ScreenSettings.FirstOrDefault(k => k.Uuid == s.Uuid);
            var paused = DisplayControl.PausedControls(s.Uuid);
            bool briPaused = paused.Contains(DisplayControl.VcpBrightness);
            (int min, int max) range = (setting?.Min ?? 0, setting?.Max ?? 100);
            bool settingsOpen = ScreenSettingsOpen.Contains(s.Id);
            bool follow = setting?.FollowBuiltin ?? false;

            double cy = y;
            Card(cy, ScreenCardHeight(s, hasBuiltinScreen));     // 先画底
            double inner = CardHead(cy, s.Builtin ? Glyph.Laptop : Glyph.Disk, s.Name,
                s.Builtin ? "内置显示器 · 系统调光" : "外接显示器 · 硬件调光");

            // 亮度（内建屏只有这一行）
            double ry = inner + 2;
            ScreenBrightnessBars[s.Id] = Slider(ry, Glyph.SunLine, "亮度", s.Brightness,
                s.ValuesLoaded, s.BrightnessOk, briPaused, range);
            ry += rowH;

            // 亮度范围（每屏可调区间）：**提到主卡片** —— 之前藏在「设置与预设」折叠区里，
            // 用户几乎发现不了这个功能。它和亮度是直接相关的，放一起才说得通。
            if (!s.Builtin)
            {
                double lx = x + cardPad + iconBox + iconGap;
                dc.DrawText(RenderKit.Text("亮度范围", 10, pal.TextTertiary), new Point(lx, ry + 3));
                double cx = lx + 54;
                var rects = new Rect[ScreenRangeChoices.Length];
                int curIdx = RangeChoiceIndex(range.min, range.max);
                for (int i = 0; i < ScreenRangeChoices.Length; i++)
                {
                    var ch = ScreenRangeChoices[i];
                    bool sel = i == curIdx;
                    var st = RenderKit.Text(ch.name, 10, sel ? pal.Surface : pal.TextDim, sel);
                    double sw2 = st.WidthIncludingTrailingWhitespace + 14;
                    var box = SafeRect(cx, ry - 1, sw2, 21);
                    dc.DrawRectangle(sel ? RenderKit.BrushOf(pal.Accent) : RenderKit.Faint(pal.Text, 0.06),
                        sel ? null : RenderKit.PenOf(pal.Rim, 1, 0.6), new RoundedRect(box, 6));
                    dc.DrawText(st, new Point(cx + 7, ry + 2));
                    rects[i] = box; cx += sw2 + 5;
                }
                ScreenRangeOptions[s.Id] = rects;
                ry += 28;
            }

            if (!s.Builtin)
            {
                ScreenContrastBars[s.Id] = Slider(ry, Glyph.Contrast, "对比度", s.Contrast,
                    s.ValuesLoaded, s.ContrastOk, paused.Contains(DisplayControl.VcpContrast));
                ry += rowH;

                // 开关行：跟随内建屏（只有真有内建屏才有意义）
                if (hasBuiltinScreen)
                {
                    double lx = x + cardPad + iconBox + iconGap;
                    dc.DrawText(RenderKit.Text("自动亮度 · 跟随内建屏", 11, pal.Text), new Point(lx, ry + 4));
                    ScreenFollowToggles[s.Id] = Toggle(x + w - cardPad - 36, ry + 4, follow);
                    // 跟随开启时手动亮度禁用（避免和自动调节打架）
                    if (follow) ScreenBrightnessBars.Remove(s.Id);
                    ry += rowH;
                }

                // 音量
                ScreenVolumeBars[s.Id] = Slider(ry, Glyph.Speaker, "显示器音量", s.Volume,
                    s.ValuesLoaded, s.VolumeOk, paused.Contains(DisplayControl.VcpVolume));
                ry += rowH;

                // 静音开关（VCP 141）。显示器不支持时**不显示**（不摆假控件）。
                // 数据早就读到了（DdcScreen.Mute），只是之前没画出来。
                if (s.Mute is { } mv)
                {
                    bool muted = mv == 1;   // 1=静音 2=有声（参考版 MuteState 枚举）
                    double lx2 = x + cardPad + iconBox + iconGap;
                    dc.DrawText(RenderKit.Text("静音", 11, muted ? pal.Up : pal.Text),
                        new Point(lx2, ry + 4));
                    ScreenMuteToggles[s.Id] = Toggle(x + w - cardPad - 36, ry + 4, muted);
                    ry += rowH;
                }
            }

            // ---------- 颜色设置（RGB 硬件增益 + 重置，对齐 LunarCN 功能表 M08）----------
            // 只有显示器**真的支持** RGB 增益时才显示（读不到就不显示，不摆假控件）。
            if (!s.Builtin)
            {
                // 外接屏恒显示颜色区：读不到时显示 "--"，而不是把整个功能藏起来
                // （用户就永远看不到这个功能了）。三态同其余读数。
                int rv = s.Red ?? -1, gv = s.Green ?? -1, bv = s.Blue ?? -1;
                var ct = RenderKit.Text("颜色", 10, pal.TextTertiary);
                dc.DrawText(ct, new Point(x + cardPad + iconBox + iconGap, ry + 1));
                // 「重置颜色」不在标题行：色温行的右边还有「自动色温」开关，
                // 两个右对齐控件上下只隔一行会挤在一起。重置按钮挪到 RGB 滑条之后。
                ry += 24;

                // 色温档位（显示器自带的白点预设，VCP 20）：只列显示器**实测有效**的档
                if (ScreenColorTempValues.TryGetValue(s.Id, out var presets) && presets.Length > 0)
                {
                    var boxes = new Rect[presets.Length];
                    double bx = x + cardPad + iconBox + iconGap;
                    double by = ry - 2;
                    dc.DrawText(RenderKit.Text("色温", 10, pal.TextTertiary), new Point(x + cardPad, by + 3));
                    bx += 34;
                    for (int i = 0; i < presets.Length; i++)
                    {
                        int v = presets[i];
                        bool sel = s.ColorTempPreset == v;
                        var label = RenderKit.Text(PresetLabel(v, s.ColorTempPreset == v), 10, sel ? pal.Surface : pal.TextDim, sel);
                        double bw2 = label.WidthIncludingTrailingWhitespace + 14;
                        var box = SafeRect(bx, by, bw2, 21);
                        dc.DrawRectangle(sel ? RenderKit.BrushOf(pal.Accent) : RenderKit.Faint(pal.Text, 0.06),
                            sel ? null : RenderKit.PenOf(pal.Rim, 1, 0.6), new RoundedRect(box, 6));
                        dc.DrawText(label, new Point(bx + 7, by + 3));
                        boxes[i] = box;
                        bx += bw2 + 5;
                    }
                    ScreenColorTempOptions[s.Id] = (boxes, presets);

                    // 「自动色温」开关放这一行右侧：开 = 交给显示器自己的标准白点，
                    // 关 = 下面的 RGB / 色温档由用户手动调。
                    // （放在标题行会和「重置颜色」按钮打架 —— 宽度不够。）
                    bool autoOn = IsAutoColorOn(s.Id);
                    double aw = x + w - cardPad;
                    var at = RenderKit.Text("自动色温", 10, autoOn ? pal.Accent : pal.TextTertiary);
                    dc.DrawText(at, new Point(aw - at.WidthIncludingTrailingWhitespace - 40, by + 3));
                    ScreenAutoColorToggles[s.Id] = Toggle(aw - 36, by, autoOn);
                    ry += 28;
                }
                else
                {
                    // 显示器不支持色温预设时，自动色温开关单独占一行
                    bool autoOn = IsAutoColorOn(s.Id);
                    double aw = x + w - cardPad;
                    var at = RenderKit.Text("自动色温", 10, autoOn ? pal.Accent : pal.TextTertiary);
                    dc.DrawText(at, new Point(aw - at.WidthIncludingTrailingWhitespace - 40, ry + 1));
                    ScreenAutoColorToggles[s.Id] = Toggle(aw - 36, ry - 1, autoOn);
                    ry += 24;
                }

                // 三条 RGB 滑条（复用 Slider 的样式：轨道 + 刻度点 + 圆钮）
                var bars = new Rect[3];
                var rgbVals = new[] { rv, gv, bv };
                var rgbIcons = new[] { Glyph.RgbRed, Glyph.RgbGreen, Glyph.RgbBlue };
                var rgbCols = new[] { Color.FromRgb(0xFF, 0x6B, 0x6B), Color.FromRgb(0x5A, 0xD1, 0x5A), Color.FromRgb(0x6B, 0x9C, 0xFF) };
                var rgbNames = new[] { "红", "绿", "蓝" };
                for (int i = 0; i < 3; i++)
                {
                    double sy = ry + i * 30;
                    DrawIcon(dc, rgbIcons[i], x + cardPad + 1, sy + 8, rgbCols[i], 0.9);
                    dc.DrawText(RenderKit.Text(rgbNames[i], 10, pal.TextDim), new Point(x + cardPad + 14 + 4, sy + 3));
                    double bx = x + cardPad + 40, bw = w - (bx - x) - cardPad - 46;
                    double by = sy + 4, barH = 4;
                    dc.DrawRectangle(RenderKit.BrushOf(pal.Track), null, new RoundedRect(SafeRect(bx, by, bw, barH), 2));
                    double f = rgbVals[i] < 0 ? 0 : Math.Clamp(rgbVals[i] / 100.0, 0, 1);
                    if (rgbVals[i] >= 0 && f > 0.002)
                        dc.DrawRectangle(RenderKit.BrushOf(rgbCols[i]), null,
                            new RoundedRect(SafeRect(bx, by, Math.Max(4, bw * f), barH), 2));
                    for (int t = 1; t < 20; t++)
                        dc.DrawEllipse(RenderKit.BrushOf(pal.TextTertiary), null, new Point(bx + bw * t / 20.0, by + barH + 4), 0.7, 0.7);
                    if (rgbVals[i] >= 0)
                    {
                        double kx = bx + Math.Max(6, Math.Min(bw - 6, bw * f));
                        dc.DrawEllipse(RenderKit.BrushOf(pal.Elevated), null, new Point(kx, by + barH / 2), 8, 8);
                        dc.DrawEllipse(RenderKit.BrushOf(rgbCols[i]), null, new Point(kx, by + barH / 2), 6, 6);
                    }
                    dc.DrawText(RenderKit.Text(rgbVals[i] >= 0 ? $"{rgbVals[i]}%" : "--", 10,
                        rgbVals[i] >= 0 ? pal.Text : pal.TextTertiary, true), new Point(bx + bw + 8, sy + 3));
                    bars[i] = SafeRect(bx, by - 10, bw, barH + 20);
                }
                ScreenRgbBars[s.Id] = bars;
                ry += 3 * 30;

                // 重置颜色（放在 RGB 滑条之后，不和色温行的「自动色温」开关抢位置）
                var resetBtn = Button(x + cardPad + iconBox + iconGap, ry - 2, "重置颜色", true);
                ScreenColorResetButtons[s.Id] = resetBtn;
                ry += 38;
            }

            // 状态行 + 右侧动作（参考图：左"已读取当前设置"，右是动作按钮）
            bool pausedNow = paused.Count > 0;
            string msg = pausedNow
                ? "写入已暂停：显示器未确认目标值 · 检查连接线与 DDC/CI"
                : string.IsNullOrEmpty(s.Message) ? (s.ValuesLoaded ? "已读取当前设置" : "正在读取…") : s.Message;
            Color mc = pausedNow ? pal.Warning
                : (msg.Contains("失败") || msg.Contains("超时") || msg.StartsWith("亮度：")) ? pal.Warning : pal.TextTertiary;
            double right = x + w - cardPad;
            if (pausedNow)
            {
                var rb = Button(right - 84, ry - 4, "允许重试", true, tint: pal.Warning);
                ScreenRetryButtons[s.Id] = rb; right = rb.X - 10;
            }
            else if (setting?.Brightness is { } pb0)
            {
                var rb = Button(right - 84, ry - 4, "恢复预设", true);
                ScreenPresetRestoreButtons[s.Id] = rb; right = rb.X - 6;
                var pt = RenderKit.Text($"预设 {pb0:F0}%", 9.5, pal.TextTertiary);
                right -= pt.WidthIncludingTrailingWhitespace;
                dc.DrawText(pt, new Point(right, ry + 1));
            }
            dc.DrawText(RenderKit.Trimmed(msg, 10, mc, Math.Max(40, right - (x + cardPad + iconBox + iconGap) - 10), false),
                new Point(x + cardPad + iconBox + iconGap, ry + 1));
            ry += 30;

            // 「设置与预设」折叠行（参考图：› 设置与预设）
            var chevron = RenderKit.Text(settingsOpen ? "⌄" : "›", 13, pal.Accent, true);
            dc.DrawText(chevron, new Point(x + cardPad + 3, ry));
            dc.DrawText(RenderKit.Text("设置与预设", 11, pal.TextDim), new Point(x + cardPad + iconBox + iconGap - 2, ry + 3));
            ScreenSettingsRows[s.Id] = SafeRect(x + 4, ry - 6, w - 8, 28);
            ry += 30;

            if (settingsOpen)
            {
                double lx = x + cardPad + iconBox + iconGap;

                // 预设：保存 / 更新
                dc.DrawText(RenderKit.Text("预设", 10, pal.TextTertiary), new Point(lx, ry + 4));
                var saveBtn = Button(lx + 64, ry - 1, setting?.Brightness is null ? "保存当前为预设" : "更新预设", s.ValuesLoaded);
                ScreenPresetSaveButtons[s.Id] = saveBtn;
                var tip = RenderKit.Trimmed("保存亮度 / 对比度 / 音量的当前设备值", 9.5, pal.TextTertiary,
                    Math.Max(20, x + w - cardPad - saveBtn.Right - 10), false);
                dc.DrawText(tip, new Point(saveBtn.Right + 10, ry + 4));
                ry += 32;
            }

            y = cy + ScreenCardHeight(s, hasBuiltinScreen) + cardGap;
        }

        // ---------- 收尾：跟随状态 + 窗口底部说明（对齐参考图最下方那行灰字）----------
        if (!string.IsNullOrEmpty(AutoFollowStatus))
        {
            var st = RenderKit.Trimmed(AutoFollowStatus, 9.5, pal.TextTertiary, w, false);
            dc.DrawText(st, new Point(x, y + 2));
            y += 16;
        }
        var hint = RenderKit.Trimmed("亮度拖动即生效；对比度、音量同样即时生效。外接屏需在显示器菜单中开启 DDC/CI。", 9.5, pal.TextTertiary, w, false);
        dc.DrawText(hint, new Point(x, y + 4));
        ScreensFooterHint = SafeRect(x, y, w, 18);
    }

    /// <summary>
    /// 设备电量页（对齐 BrimDeck「设备电量」页）：每个设备一个大电量环，
    /// 环内是设备类型图标，下方是大号百分比 + 名称 + 状态（低电量/偏低/充电中）。
    /// 读不到电量的设备不显示——绝不编造数值。
    /// </summary>
    private void DrawDevicesPage(DrawingContext dc, Palette pal, Rect r, double y0)
    {
        double x = r.X + 20, w = r.Width - 40, y = y0;
        if (Devices.Count == 0)
        {
            var t = RenderKit.Text("未检测到设备（蓝牙 / 2.4G 接收器 / 鼠标键盘耳机，或本机内置电池）", 11.5, pal.TextDim);
            dc.DrawText(t, new Point(x, y + 30));
            return;
        }

        // 每个设备一张小卡：大环 + 环内图标 + 百分比 + 名称 + 状态
        const double cardW = MonitorLayoutSpec.DeviceCardWidth;
        const double cardH = MonitorLayoutSpec.DeviceCardHeight;
        const double gap = MonitorLayoutSpec.DeviceCardGap;
        int perRow = Math.Max(1, (int)((w + gap) / (cardW + gap)));
        for (int i = 0; i < Devices.Count; i++)
        {
            var dev = Devices[i];
            double cx = x + (i % perRow) * (cardW + gap);
            double cy = y + (i / perRow) * (cardH + gap);
            var box = SafeRect(cx, cy, cardW, cardH);
            dc.DrawRectangle(RenderKit.BrushOf(pal.Elevated), RenderKit.PenOf(pal.Rim, 1, 0.55), new RoundedRect(box, 12));

            // 电量色：低红 / 偏低橙 / 正常蓝；充电中绿。
            // 读不到电量的设备（只有识别没有读数）用暗色 —— 不能拿 0% 画成"满电环以外的样子"。
            bool unknown = dev.LevelText is not null;
            Color c = unknown ? pal.TextDim
                 : dev.Charging ? DownC
                 : dev.Percent <= 20 ? pal.Up
                 : dev.Percent <= 40 ? pal.Warning : ResColor(Res.Batt, pal);

            // 大环（60px）+ 环内设备图标
            const double ring = 60;
            double rx = cx + cardW / 2, ry = cy + 20 + ring / 2;
            RenderKit.Ring(dc, new Point(rx, ry), ring, 5, dev.Percent / 100.0, pal.Track, c);
            // 环内设备图标：按设备类型给 per-type 矢量剪影（battery / ble / mouse / keyboard /
            // headset / gamepad / other 七种），与 Windows 端同一套图形语言（16×16 网格、圆头描边）。
            // 认不出的类型兜底成"通用外设"，不抛异常、不画空白。
            // 尺寸 16×16 —— 与 Windows 端设备页一致（环内 60px 环里放得下，不压到下方文字）。
            DeviceGlyphs.Draw(dc, DeviceGlyphs.Of(dev.Kind), SafeRect(rx - 8, ry - 8, 16, 16), pal.Text);

            // 大号读数：有百分数显示百分比；只有档位/未知时显示那行文字（不画假的 0%）
            var pct = unknown
                ? RenderKit.Text(dev.LevelText!, 20, c, true)
                : RenderKit.Text($"{dev.Percent}%", 26, c, true);
            dc.DrawText(pct, new Point(rx - pct.WidthIncludingTrailingWhitespace / 2, cy + 20 + ring + 6));
            // 设备名
            var nm = RenderKit.Trimmed(dev.Name, 11, pal.Text, cardW - 16, true);
            dc.DrawText(nm, new Point(rx - nm.WidthIncludingTrailingWhitespace / 2, cy + 20 + ring + 38));
            // 状态："未知"必须排在百分数判断前面（否则读不到电量的设备会显示"正常"）
            string state = unknown ? "电量未知"
                     : dev.Charging ? "充电中"
                     : dev.Percent <= 20 ? "电量低"
                     : dev.Percent <= 40 ? "电量偏低" : "正常";
            var st = RenderKit.Text(state, 10, dev.Charging ? DownC : dev.Percent <= 40 ? pal.Warning : pal.TextTertiary);
            dc.DrawText(st, new Point(rx - st.WidthIncludingTrailingWhitespace / 2, cy + 20 + ring + 56));
        }
    }

    /// <summary>
    /// 色温档位标签。显示器厂商的预设索引含义各不相同（实测 DELL U3225QE 有效索引为
    /// 1/4/5/6/8/9/11/12，其中 12 = 用户自定义），没有统一标准，所以**不编造**色温名称，
    /// 只标出「用户」（我们手动调色的那一档）和其余的编号档。
    /// </summary>
    private static string PresetLabel(int preset, bool selected) => preset == 12 ? "用户" : $"档 {preset}";

    /// <summary>剪贴板页：20 条环形循环，两列各 10 条全部可见（纯本地内存，不写盘）。
    /// 按内容类型差异化显示（借鉴 BrimDeck）：链接 host 主色 + 🔗，颜色显示色块 + 等宽，代码等宽 + ⌘，点击粘回。</summary>
    private void DrawClipsPage(DrawingContext dc, Palette pal, Rect r, double y0)
    {
        ClipRows.Clear();
        ClipDeleteRows.Clear();
        ClipFilterButtons.Clear();
        _clipVisibleIdx.Clear();
        double x = r.X + 24, y = y0;
        if (Clips.Count == 0)
        {
            var t = RenderKit.Text("复制文字后自动记录（点一条即可粘回）", 12, pal.TextDim);
            dc.DrawText(t, new Point(x, y + 30));
            return;
        }

        // ---- 分类标签栏（参考图：全部 / ●常用 / ●工作 / ●代码）----
        // 彩色圆点沿用参考语义：常用青 / 工作绿 / 代码紫；"全部"无点。
        (string Id, string Label, Color? Dot)[] tabs =
        {
            ("all", "全部", null),
            ("common", "常用", Color.FromRgb(0x5F, 0xD3, 0xA0)),
            ("work", "工作", Color.FromRgb(0x60, 0xC0, 0x78)),
            ("code", "代码", Color.FromRgb(0x8E, 0x9B, 0xF5)),
        };
        double tx = x;
        foreach (var (id, label, dot) in tabs)
        {
            bool active = _clipFilter == id;
            double dotW = dot is { } ? 8 : 0;
            var lt = RenderKit.Text(label, 11, active ? pal.Text : pal.TextDim, active);
            var box = SafeRect(tx - 7, y - 5, dotW + lt.WidthIncludingTrailingWhitespace + 14, 22);
            if (active)
                dc.DrawRectangle(RenderKit.Faint(pal.Accent, 0.18), null, new RoundedRect(box, 7));
            if (dot is { } dc0)
            {
                dc.DrawEllipse(RenderKit.BrushOf(dc0), null, new Point(tx, y + 6), 3, 3);
            }
            dc.DrawText(lt, new Point(tx + dotW, y));
            ClipFilterButtons[id] = box;
            tx += box.Width + 2;
        }

        // 右上角从左到右：导出全部 · 计数 · 垃圾桶。
        // 【踩过的坑】三者必须从最右的图标出发、用图标的**真实边界**往左依次排。
        // 第五十九轮把垃圾桶移到这里时计数仍停在"最右端" → 图标压住计数（"5/20" 读成 "5量0"）。
        // 第一次修又把余量算反：DrawTrashIcon 的横向范围是 [x-5.6, x+5.6]（含 1.2px 描边），
        // 计数右缘 = trashRight - 8 却落到了 trashX+2 —— 仍压在图标中心上。
        // 这里以 IconHalf = 6 为图标半宽（取整上界），间距 8，不再依赖拍脑袋的数字。
        const double IconHalf = 6, IconGap = 8;
        double trashX = r.Right - 24 - 14;          // 垃圾桶中心：最右
        double iconLeft = trashX - IconHalf;        // 图标左缘
        var count = RenderKit.Text($"{Clips.Count}/20", 10, pal.TextTertiary);
        double countRight = iconLeft - IconGap;                                 // 计数右缘与图标留 8px
        double countX = countRight - count.WidthIncludingTrailingWhitespace;
        var export = RenderKit.Text("导出全部", 10, pal.TextDim);
        double exportX = countX - 10 - export.WidthIncludingTrailingWhitespace;  // 导出在计数左侧
        dc.DrawText(export, new Point(exportX, y));
        ClipExportButton = SafeRect(exportX - 6, y - 5, export.WidthIncludingTrailingWhitespace + 12, 22);
        dc.DrawText(count, new Point(countX, y));

        // 垃圾桶图标（BrimDeck 位置 —— 分类标签行的右端）
        DrawTrashIcon(dc, pal, trashX, y + 6);
        ClipClearButton = SafeRect(trashX - 7, y - 6, 20, 22);

        // ---- 筛选后的可见条目：记录 显示行→原始索引 的映射 ----
        var source = Clips.Take(20).Select((c, i) => (c, i)).ToList();
        if (_clipFilter != "all") source = source.Where(e => ClipTagOf(e.c) == _clipFilter).ToList();
        y += 30;   // 标签栏占一行

        if (source.Count == 0)
        {
            var none = RenderKit.Text("这个分类下暂无记录", 11, pal.TextDim);
            dc.DrawText(none, new Point(x, y + 10));
            return;
        }

        // 两列：左列前半、右列后半。每行 30px。
        double colW = (r.Width - 48 - 20) / 2;
        double rowH = 30;
        int perCol = (source.Count + 1) / 2;
        // 可见区高度：面板底部减去列表起点。内容超出时可滚动（BrimDeck 有滚动条）。
        double listTop = y;
        double listBottom = r.Bottom - 8;
        double viewH = Math.Max(rowH, listBottom - listTop);
        double rowsH = Math.Ceiling(source.Count / 2.0) * rowH;
        // 滚动偏移夹在合法范围：内容不足时为 0，超出时最多滚到底。
        _clipScroll = Math.Clamp(_clipScroll, 0, Math.Max(0, rowsH - viewH));
        _clipViewH = viewH;
        _clipContentH = rowsH;

        // 裁剪到列表区，滚动时超出部分不画（也避免画到面板边框外）
        using (dc.PushClip(new Rect(r.X, listTop, r.Width, viewH)))
        {
        for (int vis = 0; vis < source.Count; vis++)
        {
            var (c, origIdx) = source[vis];
            _clipVisibleIdx.Add(origIdx);
            double cx = x + (vis / perCol) * (colW + 20);
            double cy = listTop + (vis % perCol) * rowH - _clipScroll;
            // 完全在可见区外的行不画（但要保留索引映射，点击才对得上）
            if (cy + rowH < listTop || cy > listBottom) { ClipRows[vis] = new Rect(cx, cy, colW, rowH - 4); continue; }
            ClipRows[vis] = new Rect(cx, cy, colW, rowH - 4);

            // 类型圆点（参考图每条左侧带颜色点）
            Color tagColor = ClipTagOf(c) switch
            {
                "code" => Color.FromRgb(0x8E, 0x9B, 0xF5),
                "work" => Color.FromRgb(0x60, 0xC0, 0x78),
                _ => Color.FromRgb(0x5F, 0xD3, 0xA0),
            };
            dc.DrawEllipse(RenderKit.BrushOf(tagColor), null, new Point(cx + 3, cy + 11), 2.5, 2.5);

            double textStart = cx + 12;
            double textW = colW - 12 - 66;   // 右侧给相对时间 + 删除留位
            Color color = vis == 0 ? pal.Text : pal.TextDim;

            switch (c.Kind)
            {
                case ClipKind.Link:
                {
                    if (Uri.TryCreate(c.Text.Trim(), UriKind.Absolute, out var u))
                    {
                        var host = RenderKit.Trimmed(u.Host, 11, pal.Accent, textW * 0.55, false);
                        dc.DrawText(host, new Point(textStart, cy));
                        var path = RenderKit.Trimmed(u.PathAndQuery, 10, pal.TextTertiary, textW - host.WidthIncludingTrailingWhitespace - 4, false);
                        dc.DrawText(path, new Point(textStart + host.WidthIncludingTrailingWhitespace + 4, cy + 1));
                    }
                    else { var p = RenderKit.Trimmed(c.Preview, 11, pal.Accent, textW, false); dc.DrawText(p, new Point(textStart, cy)); }
                    break;
                }
                case ClipKind.Color:
                {
                    if (ClipClassifier.ParseColor(c.Text) is { } col)
                        dc.DrawRectangle(RenderKit.BrushOf(col),
                            col.A < 255 ? RenderKit.PenOf(pal.Rim, 1, 0.8) : null,
                            new RoundedRect(SafeRect(textStart, cy + 3, 14, 14), 3));
                    var hex = RenderKit.Trimmed(c.Text.Trim().ToUpperInvariant(), 10.5, color, textW - 20, false);
                    dc.DrawText(hex, new Point(textStart + 20, cy + 1));
                    break;
                }
                case ClipKind.Code:
                {
                    // 代码用等宽字体（BrimDeck 同款）：等宽才能看出缩进与对齐关系。
                    var prev = RenderKit.MonoTrimmed(c.Preview, 10.5, color, textW, false);
                    dc.DrawText(prev, new Point(textStart, cy + 1));
                    break;
                }
                case ClipKind.Image:
                {
                    if (GetClipThumb(c) is { } tb)
                        dc.DrawImage(tb, SafeRect(textStart, cy + 2, Math.Min(56, tb.Size.Width * 18 / Math.Max(1, tb.Size.Height)), Math.Min(18, tb.Size.Height)));
                    var sz = RenderKit.Trimmed(c.Preview, 10.5, color, textW - 60, false);
                    dc.DrawText(sz, new Point(textStart + 60, cy + 3));
                    break;
                }
                default:
                {
                    var plain = RenderKit.Trimmed(c.Preview, 11, color, textW, false);
                    dc.DrawText(plain, new Point(textStart, cy));
                    break;
                }
            }

            // 多行文本：末尾标「+N 行」（BrimDeck 同款 —— 预览只显示第一行，
            // 剩余行数用小字标出来，让人知道这条不止一行）。
            double right = cx + colW;
            if (c.Lines > 1 && c.Kind != ClipKind.Image)
            {
                var more = RenderKit.Text($"+{c.Lines - 1} 行", 9.5, pal.TextTertiary);
                double mx = textStart + Math.Min(textW, TextWidth(c, textW)) + 6;
                dc.DrawText(more, new Point(Math.Min(mx, right - 24 - more.WidthIncludingTrailingWhitespace), cy + 2));
            }

            // 右侧：相对时间（参考图同款）+ 删除 ×
            var tm = RenderKit.Text(RelTime(c.At), 9.5, pal.TextTertiary);
            dc.DrawText(tm, new Point(right - 20 - tm.WidthIncludingTrailingWhitespace, cy + 2));
            var del = RenderKit.Text("×", 12, pal.TextDim);
            dc.DrawText(del, new Point(right - 4, cy + 1));
            ClipDeleteRows[vis] = SafeRect(right - 10, cy - 2, 16, rowH - 4);
        }
        }   // ← 结束列表裁剪

        // 滚动条：内容超出可见区才画（右侧竖条，滑块高度按比例）
        if (rowsH > viewH + 2)
        {
            double trackX = r.Right - 8;
            double trackH = viewH;
            dc.DrawRectangle(RenderKit.Faint(pal.Hairline, 0.5), null,
                new RoundedRect(SafeRect(trackX, listTop, 3, trackH), 1.5));
            double ratio = viewH / rowsH;
            double thumbH = Math.Max(24, trackH * ratio);
            double maxScroll = rowsH - viewH;
            double thumbY = listTop + (maxScroll > 0 ? (_clipScroll / maxScroll) * (trackH - thumbH) : 0);
            dc.DrawRectangle(RenderKit.Faint(pal.TextDim, 0.45), null,
                new RoundedRect(SafeRect(trackX, thumbY, 3, thumbH), 1.5));
            ClipScrollBar = SafeRect(trackX - 4, listTop, 11, trackH);
        }
        else ClipScrollBar = new Rect();
    }

    private readonly Dictionary<ClipItem, global::Avalonia.Media.IImage> _clipThumbs = new();

    /// <summary>图片剪贴项的缩略图（解码一次缓存；ClipItem 是 record，按值相等）。</summary>
    private global::Avalonia.Media.IImage? GetClipThumb(ClipItem c)
    {
        if (c.Image is not { } png) return null;
        if (_clipThumbs.TryGetValue(c, out var cached)) return cached;
        try
        {
            using var ms = new System.IO.MemoryStream(png);
            var full = global::Avalonia.Media.Imaging.Bitmap.DecodeToWidth(ms, 112);
            _clipThumbs[c] = full;
            if (_clipThumbs.Count > 24) { foreach (var k in _clipThumbs.Keys.Take(4)) _clipThumbs.Remove(k); }
            return full;
        }
        catch { return null; }
    }

    /// <summary>剪贴板行热区（点击粘回）；键=显示行号，原始索引查 <see cref="_clipVisibleIdx"/>。</summary>
    public Dictionary<int, Rect> ClipRows { get; } = new();
    /// <summary>每行的删除小热区（行尾 ×）；键=显示行号。</summary>
    public Dictionary<int, Rect> ClipDeleteRows { get; } = new();
    /// <summary>剪贴板条目显示行号 → 原始 Clips 索引（筛选后行号与原始索引不一致，靠它映射，防止点错条目）。</summary>
    private readonly List<int> _clipVisibleIdx = new();

    /// <summary>
    /// 画剪贴板右键菜单浮层。自绘（不用 NSMenu）：这个浮窗没有系统焦点，
    /// NSMenu 的菜单跟踪跑不起来。
    /// </summary>
    private void DrawClipMenu(DrawingContext dc, Palette pal, Rect panel)
    {
        if (!_clipMenuOpen) return;
        _clipMenuItems.Clear();
        _clipMenuRects.Clear();

        // 菜单项：BrimDeck 右键菜单的精简版（macOS 无焦点浮窗里放太多项反而难用）
        var target = _clipMenuTarget >= 0 && _clipMenuTarget < Clips.Count ? Clips[_clipMenuTarget] : null;
        bool pinned = target is not null && Clips.OfType<ClipItem>()
            .Any(it => it.At == target.At && it.Text == target.Text && it.Pinned);
        _clipMenuItems.Add(new ClipMenuItem("paste", "粘贴到当前窗口"));
        _clipMenuItems.Add(new ClipMenuItem("copy", "只复制不粘贴"));
        _clipMenuItems.Add(new ClipMenuItem("pin", pinned ? "取消常驻" : "标记常驻", pinned));
        _clipMenuItems.Add(new ClipMenuItem("detail", "查看详情"));
        _clipMenuItems.Add(new ClipMenuItem("delete", "删除"));

        const double itemH = 26, padX = 12, menuW = 152;
        double h = _clipMenuItems.Count * itemH + 10;
        // 贴着鼠标弹出，超出面板就往内收
        double mx = Math.Min(_clipMenuPos.X, panel.Right - menuW - 8);
        double my = Math.Min(_clipMenuPos.Y, panel.Bottom - h - 8);
        mx = Math.Max(panel.X + 8, mx);
        my = Math.Max(panel.Y + 8, my);

        // 浮层：比面板更亮的表面 + 描边 + 阴影（用两层不同透明度模拟）
        var rect = SafeRect(mx, my, menuW, h);
        dc.DrawRectangle(RenderKit.Faint(Colors.Black, 0.35), null, new RoundedRect(rect, 9));
        dc.DrawRectangle(RenderKit.BrushOf(pal.Elevated), RenderKit.PenOf(pal.Rim, 1, 0.9), new RoundedRect(rect, 9));

        double iy = my + 5;
        foreach (var it in _clipMenuItems)
        {
            var row = SafeRect(mx + 5, iy, menuW - 10, itemH);
            // 已勾选项：左侧打勾
            if (it.Checked)
            {
                var ck = RenderKit.Text("✓", 11, pal.Accent, true);
                dc.DrawText(ck, new Point(mx + padX, iy + 4));
            }
            var label = RenderKit.Text(it.Label, 11.5, it.Id == "delete" ? pal.Up : pal.Text);
            dc.DrawText(label, new Point(mx + padX + (it.Checked ? 14 : 0), iy + 4));
            _clipMenuRects[it.Id] = row;
            iy += itemH;
        }
    }
    /// <summary>当前分类标签：all/common/work/code。</summary>
    private string _clipFilter = "all";
    /// <summary>剪贴板列表滚动偏移（px）。内容超出可见区时可滚动。</summary>
    private double _clipScroll;
    /// <summary>剪贴板列表上一次的总内容高度（算滚动条比例用）。</summary>
    private double _clipContentH;
    /// <summary>剪贴板列表可见区高度。</summary>
    private double _clipViewH;
    /// <summary>分类标签热区，键=标签 id，供点击切换筛选。</summary>
    public Dictionary<string, Rect> ClipFilterButtons { get; } = new();
    /// <summary>「清空」按钮热区。</summary>
    public Rect ClipClearButton { get; private set; }
    /// <summary>「导出全部」按钮热区。</summary>
    public Rect ClipExportButton { get; private set; }
    /// <summary>滚动条热区（内容超出时有效，空 Rect = 无需滚动）。</summary>
    public Rect ClipScrollBar { get; private set; }

    // ==================== 剪贴板右键菜单（自绘浮层）====================
    // macOS 上这个浮窗没有系统焦点体系，NSMenu 弹不出来（也没法在无焦点窗口里
    // 走 AppKit 的菜单跟踪），所以自己画一个菜单层：记录每项热区，点谁执行谁。
    /// <summary>菜单项定义。</summary>
    public readonly record struct ClipMenuItem(string Id, string Label, bool Checked = false, bool Separator = false);

    private readonly List<ClipMenuItem> _clipMenuItems = new();
    private readonly Dictionary<string, Rect> _clipMenuRects = new();
    private bool _clipMenuOpen;
    private Point _clipMenuPos;
    private int _clipMenuTarget = -1;      // 菜单针对的剪贴板显示行号
    /// <summary>右键菜单项被点中（id 见 ClipMenuItem.Id），由宿主执行实际动作。</summary>
    public event Action<string>? ClipMenuAction;

    /// <summary>触发右键菜单动作（宿主点击命中后调用）。</summary>
    public void RaiseClipMenuAction(string id) => ClipMenuAction?.Invoke(id);

    /// <summary>右键菜单是否开着（宿主点击时先判它）。</summary>
    public bool ClipMenuVisible => _clipMenuOpen;
    /// <summary>右键菜单项热区（供点击命中）。</summary>
    public Dictionary<string, Rect> ClipMenuRects => _clipMenuRects;
    /// <summary>菜单针对的条目显示行号（-1 = 无）。</summary>
    public int ClipMenuTarget => _clipMenuTarget;

    /// <summary>打开剪贴板右键菜单（针对某个显示行）。</summary>
    public void OpenClipMenu(int visRow, Point at)
    {
        _clipMenuTarget = visRow;
        _clipMenuPos = at;
        _clipMenuOpen = true;
        Dirty = true;
    }

    /// <summary>关闭右键菜单。</summary>
    public void CloseClipMenu()
    {
        if (!_clipMenuOpen) return;
        _clipMenuOpen = false;
        _clipMenuItems.Clear();
        _clipMenuRects.Clear();
        Dirty = true;
    }

    /// <summary>菜单里某项的命中（返回 id，未命中返回 null）。</summary>
    public string? HitClipMenu(Point pos)
    {
        if (!_clipMenuOpen) return null;
        foreach (var (id, rect) in _clipMenuRects)
            if (rect.Contains(pos)) return id;
        return null;
    }

    /// <summary>
    /// 剪贴板列表滚轮滚动。返回 true 表示已消费（避免被外层当成切视图）。
    /// </summary>
    public bool ScrollClips(double delta)
    {
        if (_clipContentH <= _clipViewH + 2) return false;
        _clipScroll = Math.Clamp(_clipScroll - delta, 0, Math.Max(0, _clipContentH - _clipViewH));
        Dirty = true;
        return true;
    }

    /// <summary>把一条剪贴记录归到参考图的四个标签之一（全部=不过滤）。基于已有 ClipKind，不编造来源。</summary>
    private static string ClipTagOf(ClipItem c) => c.Kind switch
    {
        ClipKind.Code => "code",       // 代码：紫点
        ClipKind.Link => "work",       // 链接：绿点（工作/资料链接）
        _ => "common",                 // 文字/颜色/图片：常用，青点
    };

    /// <summary>相对时间："刚刚" / "X分钟前" / "X小时前" / "X天前"。</summary>
    private static string RelTime(DateTime at)
    {
        var d = DateTime.Now - at;
        if (d.TotalSeconds < 60) return "刚刚";
        if (d.TotalMinutes < 60) return $"{Math.Max(1, (int)d.TotalMinutes)}分钟前";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}小时前";
        return $"{(int)d.TotalDays}天前";
    }
    /// <summary>用户点了一条剪贴板记录（宿主负责写回剪贴板）。</summary>
    public event Action<int>? ClipPicked;
    /// <summary>用户点了某行的删除 ×（敏感信息立刻清掉）。</summary>
    public event Action<int>? ClipDeleted;
    /// <summary>用户点了「清空」。</summary>
    public event Action? ClipCleared;
    /// <summary>用户点了「导出全部」（文字存 txt / 图片存 png 到下载目录）。</summary>
    public event Action? ClipExportAll;

    /// <summary>剪贴板页点击：命中标签/行返回 true。</summary>
    public bool HandleClipPress(Point pos)
    {
        if (!IsExpanded || Page != "clips") return false;
        // 标签切换（最优先）
        foreach (var (id, rect) in ClipFilterButtons)
            if (rect.Contains(pos))
            {
                if (_clipFilter != id) { _clipFilter = id; Dirty = true; }
                return true;
            }
        // 删除 × 优先于整行点击（× 在行热区内，先判小热区）
        foreach (var (vis, rect) in ClipDeleteRows)
            if (rect.Contains(pos))
            {
                if (vis < _clipVisibleIdx.Count) ClipDeleted?.Invoke(_clipVisibleIdx[vis]);
                return true;
            }
        if (ClipClearButton.Contains(pos)) { ClipCleared?.Invoke(); return true; }
        if (ClipExportButton.Contains(pos)) { ClipExportAll?.Invoke(); return true; }
        foreach (var (vis, rect) in ClipRows)
            if (rect.Contains(pos))
            {
                if (vis < _clipVisibleIdx.Count) ClipPicked?.Invoke(_clipVisibleIdx[vis]);
                return true;
            }
        return false;
    }

    // ==================== 展开态：四组卡片（占用 / 温度 / 上下速度 / 电池） ====================
    private void DrawMonitorPage(DrawingContext dc, Palette pal, Rect r, double y0)
    {
        var s = Snapshot;
        // 栅格常量取自 MonitorLayoutSpec（唯一真相源，探针与门禁链同一份）。
        // 局部抄一份的话，产品改了这里探针不会知道 ⇒ 探针的断言会悄悄失效。
        const double pad = MonitorLayoutSpec.ContentPadX;
        const double gap = MonitorLayoutSpec.GridGap;
        double cardW = MonitorLayoutSpec.MonitorCardWidth(r.Width);
        // 底部状态行（9px 单行，两端共享规格 MonitorStatusLineSpec）占的高度**先从可用高度里扣掉**：
        // TopMargin(6) + LineHeight(14) = 20。不扣的话最紧凑时状态行会压到栅格底边 / 越出面板底。
        double statusLineH = MonitorStatusLineSpec.TopMargin + MonitorStatusLineSpec.LineHeight;
        double availH = r.Bottom - y0 - pad - statusLineH;
        if (cardW < 120 || availH < 100) return;

        // 卡片高度按内容算，不再两行均分——原来 3 行内容撑满 230px 高度，下半大片空白。
        // 上排：标题 44 + 3 行 × 37 + 12 余量 = 167
        const double rowH = MonitorLayoutSpec.RowH;         // 行高 44：进度条下方留 16px 给历史曲线
        const double speedRowH = MonitorLayoutSpec.SpeedRowH;
        double topCardH = MonitorLayoutSpec.TopCardH;                        // 占用 / 温度：3 行 → 181
        double botCardH = MonitorLayoutSpec.BottomCardHeight;               // 速度：2 行 + 网络标签；电池：大字 → 156
        double topH = topCardH, botH = botCardH;

        // 卡片外壳 + 图标标题 + 分隔线，返回内容起始 y
        // Apple 风格：卡片用 Elevated 材质色区分层级，不加描边（描边只留给浅色主题的极淡轮廓）
        double Card(double cx, double cy0, double h, string title, Glyph icon, Color accent)
        {
            var box = SafeRect(cx, cy0, cardW, h);
            dc.DrawRectangle(RenderKit.BrushOf(pal.Elevated), null, new RoundedRect(box, 12));
            DrawIcon(dc, icon, cx + 15, cy0 + 19, accent);
            var t = RenderKit.Text(title, 12, pal.Text, true);
            dc.DrawText(t, new Point(cx + 33, cy0 + 12));
            dc.DrawLine(RenderKit.PenOf(pal.Hairline, 1, 0.9),
                new Point(cx + 15, cy0 + 36), new Point(cx + cardW - 15, cy0 + 36));
            return cy0 + MonitorLayoutSpec.CardContentTop;
        }

        // 曲线用的暂存数组（避免每帧分配）
        double[] _curveBuf = new double[96];

        // 一行读数：图标 + 标签 + 右对齐数值 + 进度条 +（可选）历史曲线
        // 图标 / 数值 / 进度条 / 曲线**同色**（资源色），和收起态保持一致（对齐 BrimDeck）。
        // 语义色（温度过高）优先于资源色——异常才喊。
        void Row(double cx, double yy, Glyph g, Color c, string label, string value, double frac,
                 string? warn = null, HistoryBuffer.Kind? curve = null, double curveMax = 0)
        {
            Color main = warn is not null ? pal.Warning : c;
            DrawIcon(dc, g, cx + 15, yy + 9, main);
            var lb = RenderKit.Text(label, 10, pal.TextDim);
            dc.DrawText(lb, new Point(cx + 33, yy + 3));
            var vl = RenderKit.Text(value, 14, main, true);
            dc.DrawText(vl, new Point(cx + cardW - 15 - vl.WidthIncludingTrailingWhitespace, yy - 1));
            double bx = cx + 33, bw = cardW - 48;
            dc.DrawRectangle(RenderKit.BrushOf(pal.Track), null,
                new RoundedRect(SafeRect(bx, yy + 22, bw, 4), 2));
            double f = Math.Clamp(frac, 0, 1);
            if (f > 0.002)
                dc.DrawRectangle(RenderKit.BrushOf(main), null,
                    new RoundedRect(SafeRect(bx, yy + 22, Math.Max(4, bw * f), 4), 2));
            // 历史曲线：画在进度条右侧（BrimDeck 每张读数卡旁都有一条曲线）
            if (curve is { } k && _curveBuf.Length > 1)
            {
                int n = _history.Series(k, _curveBuf.Length, _curveBuf, curveMax);
                if (n > 1)
                    RenderKit.Sparkline(dc, new Rect(bx, yy + 29, bw, 14), _curveBuf, n, main);
            }
        }


        double x1 = r.X + pad, x2 = x1 + cardW + gap;
        double y1 = y0, y2 = y1 + topH + gap;
        // 栅格底边与状态行的落点：状态行在栅格**下方** TopMargin 处；
        // 再夹在内容区底边内 ⇒ 绝不越出面板；实在放不下就不画（不压栅格）。
        double gridBottom = y2 + botH;
        double statusY = Math.Min(gridBottom + MonitorStatusLineSpec.TopMargin,
                                  r.Bottom - pad - MonitorStatusLineSpec.LineHeight);
        // 读不到显示 "--"（Reading 三态），读到 0 显示 0%
        var stt = s?.Status ?? ReadingStatus.Empty;
        string Pct(Reading<double> r, double v) => r.HasValue ? $"{Math.Round(v)}%" : "--";

        // ① 占用：CPU / 内存 / 磁盘，各用资源色 + 历史曲线（对齐 BrimDeck）
        double cy = Card(x1, y1, topH, MonitorPageLayoutSpec.TitleOf("usage"), Glyph.Cpu, ResColor(Res.Cpu, pal));
        if (s is not null && s.GpuUsage > 0)
        {
            var gx = RenderKit.Text($"GPU {Math.Round(s.GpuUsage)}%", 10, ResColor(Res.Gpu, pal), true);
            dc.DrawText(gx, new Point(x1 + cardW - 15 - gx.WidthIncludingTrailingWhitespace, y1 + 14));
        }
        // 行高 44：进度条下方留 16px 给历史曲线（否则曲线会压到下一行的标签上）
        Row(x1, cy,             Glyph.Cpu,  ResColor(Res.Cpu, pal),  MonitorPageLayoutSpec.LabelOf("usage", "cpu-usage"),  Pct(stt.Cpu, s?.CpuUsage ?? 0),           (s?.CpuUsage ?? 0) / 100,  null, HistoryBuffer.Kind.Cpu);
        Row(x1, cy + rowH,      Glyph.Mem,  ResColor(Res.Mem, pal),  MonitorPageLayoutSpec.LabelOf("usage", "memory"), Pct(stt.Memory, s?.MemoryUsagePercent ?? 0), (s?.MemoryUsagePercent ?? 0) / 100, null, HistoryBuffer.Kind.Mem);
        Row(x1, cy + rowH * 2,  Glyph.Disk, ResColor(Res.Disk, pal), MonitorPageLayoutSpec.LabelOf("usage", "disk"), Pct(stt.Disk, s?.DiskUsagePercent ?? 0),   (s?.DiskUsagePercent ?? 0) / 100, null, HistoryBuffer.Kind.DiskRead, 8 * 1024 * 1024);

        // ② 温度：CPU / GPU / SSD，条长按 0-110°C 映射
        // 温度用语义色：正常走强调色、偏高走警告色、过高走红色（规范：语义色要精确而非喊叫）
        cy = Card(x2, y1, topH, MonitorPageLayoutSpec.TitleOf("temp"), Glyph.Thermo, ResColor(Res.Cpu, pal));
        string Deg(double? v) => v is { } x && x > 0 ? $"{Math.Round(x)}°" : "--";
        // 返回 null=正常（用中性强调），非 null=超阈值（用警告色）
        string? TempWarn(double? v) => v is { } t ? (t >= 90 ? "过热" : t >= 70 ? "偏高" : null) : null;
        Row(x2, cy,             Glyph.Thermo, ResColor(Res.Cpu, pal),  MonitorPageLayoutSpec.LabelOf("temp", "cpu-temp"), Deg(s?.CpuTemp), (s?.CpuTemp ?? 0) / 110, TempWarn(s?.CpuTemp));
        Row(x2, cy + rowH,      Glyph.Gpu,    ResColor(Res.Gpu, pal),  MonitorPageLayoutSpec.LabelOf("temp", "gpu-temp"), Deg(s?.GpuTemp), (s?.GpuTemp ?? 0) / 110, TempWarn(s?.GpuTemp));
        Row(x2, cy + rowH * 2,  Glyph.Ssd,    ResColor(Res.Disk, pal), MonitorPageLayoutSpec.LabelOf("temp", "storage-temp"), Deg(s?.SsdTemp), (s?.SsdTemp ?? 0) / 110, TempWarn(s?.SsdTemp));

        // ③ 上下速度：图标 + 数值 + 流量条（2 MB/s 为满格）
        cy = Card(x1, y2, botH, MonitorPageLayoutSpec.TitleOf("speed"), Glyph.Down, pal.Accent);
        void Speed(double yy, Glyph g, Color c, string label, double v, HistoryBuffer.Kind? curve = null)
        {
            DrawIcon(dc, g, x1 + 15, yy + 11, c);
            var lb = RenderKit.Text(label, 10, pal.TextDim);
            dc.DrawText(lb, new Point(x1 + 33, yy + 6));
            var val = RenderKit.Text(MonitorSnapshot.FormatSpeedFixed(v), 16, pal.Text, true);
            dc.DrawText(val, new Point(x1 + cardW - 15 - val.WidthIncludingTrailingWhitespace, yy + 2));
            double bx = x1 + 33, bw = cardW - 48;
            dc.DrawRectangle(RenderKit.BrushOf(pal.Track), null,
                new RoundedRect(SafeRect(bx, yy + 27, bw, 3), 1.5));
            // 对数刻度：低速（几 KB/s）也能看到明显条长，2MB/s 为满格
            double f = v <= 0 ? 0 : Math.Clamp(Math.Log10(1 + v) / Math.Log10(1 + 2 * 1024 * 1024), 0, 1);
            if (f > 0.002)
                dc.DrawRectangle(RenderKit.BrushOf(c), null,
                    new RoundedRect(SafeRect(bx, yy + 27, Math.Max(4, bw * f), 3), 1.5));
            // 历史曲线（BrimDeck 每条速率旁都有曲线）
            if (curve is { } k && _curveBuf.Length > 1)
            {
                int n = _history.Series(k, _curveBuf.Length, _curveBuf, 2 * 1024 * 1024);
                if (n > 1) RenderKit.Sparkline(dc, new Rect(bx, yy + 33, bw, 14), _curveBuf, n, c);
            }
        }
        Speed(cy,      Glyph.Down, DownC, MonitorPageLayoutSpec.LabelOf("speed", "down"), s?.DownloadSpeed ?? 0, HistoryBuffer.Kind.Down);
        Speed(cy + speedRowH, Glyph.Up,   UpC,   MonitorPageLayoutSpec.LabelOf("speed", "up"), s?.UploadSpeed ?? 0, HistoryBuffer.Kind.Up);
        // 卡底信息位：网络类型 + 电池（应用榜占了右下角，电池信息挪到这里）
        var link = RenderKit.Text($"网络 {s?.LinkType ?? "--"}", 10, pal.TextTertiary);
        dc.DrawText(link, new Point(x1 + 15, y2 + botH - 18));
        if (s?.BatteryPercent is { } bp0)
        {
            Color bc0 = bp0 <= 20 ? pal.Up : ResColor(Res.Batt, pal);
            var bt = RenderKit.Text($"电池 {Math.Round(bp0)}%" + (s.BatteryCharging ? " · 充电中" : ""), 10, bc0, true);
            dc.DrawText(bt, new Point(x1 + cardW - 16 - bt.WidthIncludingTrailingWhitespace, y2 + botH - 18));
        }

        // ④ 应用榜（右下）：真实应用图标 + 名称 + 数值，右上角「CPU / 内存」切换排序依据。
        //    对齐 BrimDeck 监控页右区。电池信息挪到速度卡底部（那里本来就有"网络"这类信息位）。
        cy = Card(x2, y2, botH, MonitorPageLayoutSpec.TitleOf("topproc") + " · " + (ProcessListByMemory ? "内存" : "CPU"), Glyph.Ping, pal.Accent);

        // 右上角：CPU / 内存 分段切换
        {
            double sw = 96, sx2 = x2 + cardW - 15 - sw, sy2 = y2 + 10;
            dc.DrawRectangle(RenderKit.BrushOf(pal.Track), null, new RoundedRect(SafeRect(sx2, sy2, sw, 20), 6));
            void Seg(string label, bool on, double off)
            {
                var t = RenderKit.Text(label, 10, on ? pal.Surface : pal.TextDim, on);
                var bxx = SafeRect(sx2 + off, sy2, sw / 2, 20);
                if (on) dc.DrawRectangle(RenderKit.BrushOf(pal.Accent), null, new RoundedRect(bxx, 6));
                dc.DrawText(t, new Point(sx2 + off + (sw / 2 - t.WidthIncludingTrailingWhitespace) / 2, sy2 + 4));
            }
            Seg("CPU", !ProcessListByMemory, 0);
            Seg("内存", ProcessListByMemory, sw / 2);
            ProcessSortButton = SafeRect(sx2, sy2, sw, 20);
        }

        // 榜体：图标 + 名称 + 右侧数值
        var top = s?.TopProcessesByMem;
        var list = ProcessListByMemory ? (s?.TopProcessesByMem ?? System.Array.Empty<Island.Core.Monitoring.TopProcess>())
                                       : (s?.TopProcesses ?? System.Array.Empty<Island.Core.Monitoring.TopProcess>());
        int rows = Math.Min(list.Length, 5);   // 数组用 Length：.Count 是 LINQ 扩展方法（方法组）
        for (int i = 0; i < rows; i++)
        {
            var tp = list[i];
            double ry = cy + i * 21;
            // 图标：优先真实应用图标，取不到用首字母圆底兜底
            byte[]? iconBytes = tp.ExePath is { Length: > 0 } ep ? AppIcons?.Get(ep) : null;
            var bmp = RenderKit.Bitmap(iconBytes);
            if (bmp is not null)
            {
                RenderKit.DrawBitmap(dc, bmp, new Rect(x2 + 16, ry, 15, 15));
            }
            else
            {
                // 首字母兜底（取不到应用图标时）：用**次要文字色**，与 Windows 端一致 ——
                // 它是图标的替身，视觉上应比旁边的应用名更安静，不该抢眼。
                var initial = RenderKit.Text(string.IsNullOrEmpty(tp.Name) ? "?" : tp.Name[..1].ToUpperInvariant(), 9, pal.TextDim, true);
                dc.DrawEllipse(RenderKit.BrushOf(pal.Track), null, new Point(x2 + 23, ry + 8), 8, 8);
                dc.DrawText(initial, new Point(x2 + 23 - initial.Width / 2, ry + 3));
            }
            var nm = RenderKit.Trimmed(tp.Name, 11, pal.Text, cardW - 110, false);
            dc.DrawText(nm, new Point(x2 + 38, ry + 1));
            string val = ProcessListByMemory ? $"{tp.MemoryMB:F0} MB" : $"{tp.CpuPercent:F0}%";
            var vt = RenderKit.Text(val, 11, pal.TextDim, true);
            dc.DrawText(vt, new Point(x2 + cardW - 16 - vt.WidthIncludingTrailingWhitespace, ry + 1));
        }
        if (rows == 0)
        {
            var none = RenderKit.Text(s?.TopProcesses is null ? "未采集（仅监控页读取）" : "暂无高占用应用", 10.5, pal.TextTertiary);
            dc.DrawText(none, new Point(x2 + 16, cy + 8));
        }

        // ⑤ 底部状态行（9px 单行）：2×2 栅格**下方**，左边与栅格左缘对齐，一行不换行。
        //    字段位与顺序取自两端共享规格（SlotsFor 决定本平台显示哪些：平台不支持的**直接不出现**，
        //    不置灰、不留空位）；读不到显示「未知」，绝不用 0 / 默认值 / 换算百分比充数（项目硬红线）。
        if (statusY >= gridBottom)
        {
            double sx = x1;
            foreach (var slot in MonitorStatusLineSpec.SlotsFor(macOS: true))
            {
                string? value = slot.Key switch
                {
                    "storage-temp" => MonitorStatusLineText.SsdTemp(s?.SsdTemp),
                    "runtime" => MonitorStatusLineText.Uptime(s?.UptimeHours),
                    _ => null,   // 规格里有、但本端还没有数据源的键 ⇒ 不出现（不显示假的未知）
                };
                if (value is null) continue;
                var txt = RenderKit.Text($"{slot.Label} {value}", MonitorStatusLineSpec.FontSize, pal.TextTertiary);
                dc.DrawText(txt, new Point(sx, statusY));
                sx += txt.WidthIncludingTrailingWhitespace + MonitorStatusLineSpec.SlotGap;
            }
        }

        // ⑥ 底部：读数健康状态（Reading<T> 三态）。
        // 有读不到的项就把名字和原因列出来，让采样错误一眼可见，而不是在卡片里显示一个假的 0。
        if (s is not null)
        {
            var problems = new List<string>();
            void Check(string name, Reading<double> r)
            {
                if (r.HasValue || r.Reason == "尚未读取" || r.Reason == "首个采样周期") return;
                if (r.Reason == "无内置电池") return;   // 台式机，正常
                problems.Add($"{name}：{r.Reason}");
            }
            Check("CPU", stt.Cpu); Check("内存", stt.Memory); Check("磁盘", stt.Disk);
            Check("网速", stt.NetDown); Check("CPU 温度", stt.CpuTemp); Check("GPU 温度", stt.GpuTemp);
            Check("SSD 温度", stt.SsdTemp);
            // 状态行占了栅格底边下方第一行（TopMargin + LineHeight），健康行排在其下 ⇒ 两行不重叠。
            double sy = Math.Min(r.Bottom - 14, statusY + MonitorStatusLineSpec.LineHeight + 2);
            if (problems.Count > 0)
            {
                var line = RenderKit.Trimmed("⚠ " + string.Join(" · ", problems), 9, pal.Warning, r.Width - 2 * pad, false);
                dc.DrawText(line, new Point(r.X + pad, sy));
            }
            // 一切正常时**不画这行**（2026-11-26）：
            // Windows 端读不到时是**就地**标注（「需管理员」「缺驱动」），正常时没有任何多余的一行；
            // mac 端原来常态也画一行「读数正常」⇒ 正常情况下两端底部行数不一致（mac 多一行）。
            // 安静即正常 —— 只有真有问题时才多这一行警示，两端的常态底部就一致了。
        }
    }

    // ==================== 收起态 ====================
    // ============ 仪表盘（dash）布局 ============
    // 和时钟 / 信息胶囊一样：测量与绘制共用 LayoutDash 算出的数字。
    // 每项 = 小环（资源色）包同色图标 + 同色数值（对齐 BrimDeck「环 / 图标 / 数字三处同色」）。
    private const double DashPadX = 14;
    private const double DashRing = 16;        // 环直径
    private const double DashItemGap = 14;     // 项与项之间
    // 与 combo 共用同一套（CompactIconBox / CompactIconGap / CompactTempGap / CompactGroupGap）——统一后只有一处定义
    private const double DashIconBox = CompactIconBox, DashIconGap = CompactIconGap, DashTempGap = CompactTempGap;

    private sealed class DashLayout
    {
        public readonly List<(Glyph Icon, Res Res, bool Ok, FormattedText Text)> Temps = new();   // 温度组
        public readonly List<(Glyph Icon, Res Res, double Share, FormattedText Text)> Usage = new();  // 占用组（环包图标）
        public (Glyph Icon, double Share, Color Color, FormattedText Text)? Battery;
        public FormattedText NetDown = null!, NetUp = null!;
        /// <summary>渐变频谱柱（CPU 历史，对齐 BrimDeck 收起形态的柱状图）。</summary>
        public double[] Spectrum = Array.Empty<double>();
        public int SpectrumCount;
        /// <summary>剪贴板条数（对齐 BrimDeck 的收件箱图标 + 数字）。</summary>
        public int ClipCount;
        /// <summary>设备电量（收起态显示电量环，对齐 BrimDeck）。</summary>
        public IReadOnlyList<DeviceBattery.Device> Devices = Array.Empty<DeviceBattery.Device>();

        public double TempW => Temps.Sum(t => DashIconBox + DashIconGap + Math.Max(t.Text.WidthIncludingTrailingWhitespace, CompactValueMinW) + DashTempGap) - (Temps.Count > 0 ? DashTempGap : 0);
        public double UsageW => Usage.Sum(u => DashRing + 5 + Math.Max(u.Text.WidthIncludingTrailingWhitespace, 34) + DashItemGap) - (Usage.Count > 0 ? DashItemGap : 0);
        public double BattW => Battery is { } b ? DashRing + 5 + Math.Max(b.Text.WidthIncludingTrailingWhitespace, 34) : 0;
        public const double NetGap = 10;
        public double NetW => NetDown.WidthIncludingTrailingWhitespace + NetGap + NetUp.WidthIncludingTrailingWhitespace;

        public double ContentW
        {
            get
            {
                double w = NetW; int groups = 1;
                if (SpectrumCount > 1) { w += SpectrumW; groups++; }
                if (ClipCount > 0) { w += ClipW; groups++; }
                if (Temps.Count > 0) { w += TempW; groups++; }
                if (Usage.Count > 0) { w += UsageW; groups++; }
                if (Battery is not null) { w += BattW; groups++; }
                if (Devices.Count > 0) { w += DevW; groups++; }
                return w + (groups - 1) * CompactGroupGap * 2;
            }
        }

        /// <summary>频谱柱宽度（10 根柱 + 间距）。</summary>
        public const double SpectrumW = 46;
        public const double SpectrumH = 20;
        /// <summary>剪贴板图标 + 数字宽度。</summary>
        public double ClipW => 12 + 4 + 16;
        /// <summary>设备电量环宽度（每个 14px 环 + 间距）。</summary>
        public double DevW => Devices.Count * (14 + 6) - 6;
        public double CapsuleW => ContentW + DashPadX * 2;
    }

    private DashLayout LayoutDash(Palette pal)
    {
        var snap = Snapshot;
        var l = new DashLayout();
        if (snap is null) return l;
        var st = snap.Status;
        bool Known(Core.Monitoring.Reading<double> r) => r.HasValue || r.Reason == "首个采样周期";

        // ① 温度组：图标 + 数值，颜色用对应资源色（过热才转警告/告警）
        foreach (var (icon, res, t) in new[] { (Glyph.Cpu, Res.Cpu, snap.CpuTemp), (Glyph.Gpu, Res.Gpu, snap.GpuTemp), (Glyph.Ssd, Res.Disk, snap.SsdTemp) })
        {
            if (res == Res.Disk && snap.SsdTemp is null && !st.Disk.HasValue) continue;
            bool ok = t is { } tv0 && tv0 > 0;
            double tv = t ?? 0;
            Color c = !ok ? pal.TextTertiary : tv >= RenderKit.CritPct ? pal.Up : tv >= RenderKit.WarnPct ? pal.Warning : ResColor(res, pal);
            l.Temps.Add((icon, res, ok, RenderKit.Text(ok ? $"{Math.Round(tv)}°" : "--", FontValue, c, true)));
        }
        // ② 占用组：环包图标
        if (Known(st.Memory)) l.Usage.Add((Glyph.Mem, Res.Mem, snap.MemoryUsagePercent / 100, RenderKit.Text($"{snap.MemoryUsagePercent:F0}%", FontValue, ResColor(Res.Mem, pal), true)));
        if (snap.GpuUsage > 0) l.Usage.Add((Glyph.Gpu, Res.Gpu, snap.GpuUsage / 100, RenderKit.Text($"{snap.GpuUsage:F0}%", FontValue, ResColor(Res.Gpu, pal), true)));
        if (st.Disk.HasValue) l.Usage.Add((Glyph.Disk, Res.Disk, snap.DiskUsagePercent / 100, RenderKit.Text($"{snap.DiskUsagePercent:F0}%", FontValue, ResColor(Res.Disk, pal), true)));
        // ③ 电池：低电量红、偏低橙、充电绿、否则蓝
        if (snap.BatteryPercent is { } bp)
        {
            Color bc = bp <= 20 ? pal.Up : bp <= 40 ? pal.Warning : snap.BatteryCharging ? DownC : ResColor(Res.Batt, pal);
            l.Battery = (Glyph.Battery, (double)bp / 100, bc, RenderKit.Text($"{Math.Round(bp)}%", FontValue, ReadableCached(bc, pal), true));
        }
        // ④ 网速
        l.NetDown = RenderKit.Text("↓" + Core.Monitoring.MonitorSnapshot.FormatSpeedFixed(snap.DownloadSpeed), FontValue, pal.Down);
        l.NetUp = RenderKit.Text("↑" + Core.Monitoring.MonitorSnapshot.FormatSpeedFixed(snap.UploadSpeed), FontValue, pal.Up);
        // ⑤ 渐变频谱柱：CPU 历史（对齐 BrimDeck 收起形态的柱状图）
        var spec = new double[20];
        int n = _history.Series(HistoryBuffer.Kind.Cpu, spec.Length, spec, 100);
        l.Spectrum = spec; l.SpectrumCount = n;
        // ⑥ 剪贴板条数
        l.ClipCount = Clips.Count;
        // ⑦ 设备电量（收起态显示电量环）
        l.Devices = Devices;
        return l;
    }

    /// <summary>
    /// 仪表盘收起胶囊：温度组 │ 占用组（环包图标）│ 电池 │ 网速。
    /// 环 / 图标 / 数字三处同色（对齐 BrimDeck），颜色独立于主题。
    /// </summary>
    private void DrawDash(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        var L = LayoutDash(pal);
        if (Snapshot is null) return;
        // 胶囊背景由外层统一画（不透明 + 描边 + 全圆角），这里不再重复画一层
        // —— 重复画会因透明度不同（0.94 vs 1.0）让 dash/combo 比 clock 略透，风格不统一。
        double x = r.X + (r.Width - L.ContentW) / 2;      // 内容居中
        double top = cy - 7;

        void Divider()
            => dc.DrawLine(RenderKit.PenOf(pal.Hairline, 1, 0.8), new Point(x + CompactGroupGap / 2, cy - 9), new Point(x + CompactGroupGap / 2, cy + 9));
        void Gap() { x += CompactGroupGap; Divider(); x += CompactGroupGap; }

        // ⓪ 渐变频谱柱（CPU 历史，蓝→青渐变）+ 剪贴板条数 + 设备电量环
        //    对齐 BrimDeck 收起形态：左侧柱状图、中间图标环、右侧读数。
        if (L.SpectrumCount > 1)
        {
            RenderKit.Spectrum(dc, new Rect(x, cy - 11, DashLayout.SpectrumW, DashLayout.SpectrumH),
                L.Spectrum, L.SpectrumCount, ResColor(Res.Cpu, pal), ResColor(Res.Mem, pal));
            x += DashLayout.SpectrumW;
            Gap();
        }
        if (L.ClipCount > 0)
        {
            DrawIcon(dc, Glyph.ClipBox, x, cy, pal.TextDim, 0.95);
            x += 13;
            dc.DrawText(RenderKit.Text($"{L.ClipCount}", FontValue, pal.Text, true), new Point(x, top));
            x += Math.Max(RenderKit.Text($"{L.ClipCount}", FontValue, pal.Text, true).WidthIncludingTrailingWhitespace, 16);
            Gap();
        }

        // ① 温度组
        for (int i = 0; i < L.Temps.Count; i++)
        {
            var t = L.Temps[i];
            DrawIcon(dc, t.Icon, x, cy, t.Ok ? ResColor(t.Res, pal) : pal.TextTertiary, 0.9);
            x += DashIconBox + DashIconGap;
            dc.DrawText(t.Text, new Point(x, top));
            x += Math.Max(t.Text.WidthIncludingTrailingWhitespace, CompactValueMinW);   // 与指标胶囊共用最小数值槽
            if (i < L.Temps.Count - 1) x += DashTempGap;
        }
        if (L.Temps.Count > 0) Gap();

        // ② 占用组：环包图标
        for (int i = 0; i < L.Usage.Count; i++)
        {
            var u = L.Usage[i];
            var c = ResColor(u.Res, pal);
            RenderKit.Ring(dc, new Point(x + DashRing / 2, cy), DashRing, 2.2, u.Share, pal.Track, c);
            DrawGaugeCenter(dc, pal, u.Icon, u.Res, x, cy, DashRing, c);
            x += DashRing + 5;
            dc.DrawText(u.Text, new Point(x, top));
            x += Math.Max(u.Text.WidthIncludingTrailingWhitespace, 34);
            if (i < L.Usage.Count - 1) x += DashItemGap;
        }
        if (L.Usage.Count > 0) Gap();

        // ③ 电池
        if (L.Battery is { } b)
        {
            RenderKit.Ring(dc, new Point(x + DashRing / 2, cy), DashRing, 2.2, b.Share, pal.Track, b.Color);
            DrawGaugeCenter(dc, pal, b.Icon, Res.Batt, x, cy, DashRing, b.Color);
            x += DashRing + 5;
            dc.DrawText(b.Text, new Point(x, top));
            x += Math.Max(b.Text.WidthIncludingTrailingWhitespace, 34);
            Gap();
        }

        // 设备电量环（对齐 BrimDeck 收起形态：鼠标/键盘/耳机/手柄各一个电量环）
        for (int i = 0; i < L.Devices.Count; i++)
        {
            var dev = L.Devices[i];
            Color dc2 = dev.Charging ? DownC : dev.Percent <= 20 ? pal.Up : dev.Percent <= 40 ? pal.Warning : ResColor(Res.Batt, pal);
            RenderKit.Ring(dc, new Point(x + 7, cy), 14, 2, dev.Percent / 100.0, pal.Track, dc2);
            x += 14 + 6;
        }
        if (L.Devices.Count > 0) Gap();

        // ④ 网速：一行，下载在前。方向语义色保留。
        RenderKit.Baseline(dc, L.NetDown, x, Math.Round(cy + FontValue * 0.36));
        x += L.NetDown.WidthIncludingTrailingWhitespace + DashLayout.NetGap;
        RenderKit.Baseline(dc, L.NetUp, x, Math.Round(cy + FontValue * 0.36));
    }

    // ============ 胶囊监控图标（12×12 单位矢量，描边式） ============
    // 用 StreamGeometry 自绘而非 emoji：跨机型字体一致、可随数值分级配色。
    private enum Glyph { Cpu, Gpu, Mem, Down, Up, Battery, Disk, Ssd, Thermo, Ping, Spark,
                         // 天气（时钟视图用，矢量绘制不用 emoji——emoji 各系统渲染不一致）
                         Sun, Cloud, Rain, Snow, Fog, Bolt,
                         // 显示器控制页的行首图标（描边式，对齐参考界面 LunarCN 的行图标）
                         SunLine, Contrast, Speaker, Laptop,
                         // 颜色设置：红 / 绿 / 蓝增益
                         RgbRed, RgbGreen, RgbBlue,
                         // 剪贴板（收起态显示条数，对齐 BrimDeck 的收件箱图标）
                         ClipBox }

    /// <summary>在 (x,cy) 处画一枚 12×12 图标，返回其宽度（恒 12）。</summary>
    // ---- 图标预构建缓存（性能优化）----
    // 之前每次 DrawIcon 都 new brush + new pen + new StreamGeometry + Open()，时钟视图一帧 9 次。
    // 现在每个 (Glyph, Color) 只建一次：描边几何 + 填充几何都以 (0,0)-(12,12) 为基准，
    // 绘制时 PushTransform 平移/缩放。Geometry 本身不可变，缓存安全。
    private static readonly Dictionary<(Glyph, uint), (Geometry stroke, Geometry fill)> _iconCache = new();
    private static readonly Dictionary<(Color, double), ImmutablePen> _iconPens = new();


    /// <summary>把一个图标的描边段和填充形状构建成两个独立 Geometry（基准 12×12）。
    /// 填充形状用 RectangleGeometry / EllipseGeometry（原生支持圆角和椭圆），描边段用 StreamGeometry。</summary>
    private static (Geometry stroke, Geometry fill) BuildIcon(Glyph g)
    {
        var stroke = new StreamGeometry();
        var fills = new StreamGeometry();
        using (var sctx = stroke.Open())
        using (var fctx = fills.Open())
        {
            void Begin(double x, double y) => sctx.BeginFigure(new Point(x, y), false);
            void Line(double x, double y) => sctx.LineTo(new Point(x, y));
            void End() => sctx.EndFigure(false);
            void Box(double x, double y, double w, double h, double r = 0)   // 描边矩形
            {
                if (r <= 0)
                {
                    sctx.BeginFigure(new Point(x, y), false);
                    sctx.LineTo(new Point(x + w, y)); sctx.LineTo(new Point(x + w, y + h)); sctx.LineTo(new Point(x, y + h));
                    sctx.EndFigure(true);
                }
                else
                {
                    // 圆角矩形路径：四段弧
                    var rr = new Rect(x, y, w, h);
                    sctx.BeginFigure(new Point(rr.X + r, rr.Y), false);
                    sctx.LineTo(new Point(rr.Right - r, rr.Y));
                    sctx.ArcTo(new Point(rr.Right, rr.Y + r), new Size(r, r), 0, false, SweepDirection.Clockwise);
                    sctx.LineTo(new Point(rr.Right, rr.Bottom - r));
                    sctx.ArcTo(new Point(rr.Right - r, rr.Bottom), new Size(r, r), 0, false, SweepDirection.Clockwise);
                    sctx.LineTo(new Point(rr.X + r, rr.Bottom));
                    sctx.ArcTo(new Point(rr.X, rr.Bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise);
                    sctx.LineTo(new Point(rr.X, rr.Y + r));
                    sctx.ArcTo(new Point(rr.X + r, rr.Y), new Size(r, r), 0, false, SweepDirection.Clockwise);
                    sctx.EndFigure(true);
                }
            }
            // 填充形状也用 StreamGeometry 画（不用 GeometryGroup）：GeometryGroup 依赖平台实现，
            // 缓存跨次复用时可能不画出来（实测天气图标整个不显示）。统一成路径最稳。
            void FRect(double x, double y, double w, double h, double r = 0)   // 填充矩形（r>0 圆角）
            {
                if (r <= 0)
                {
                    fctx.BeginFigure(new Point(x, y), true);
                    fctx.LineTo(new Point(x + w, y)); fctx.LineTo(new Point(x + w, y + h)); fctx.LineTo(new Point(x, y + h));
                    fctx.EndFigure(true);
                }
                else
                {
                    var rr = new Rect(x, y, w, h);
                    fctx.BeginFigure(new Point(rr.X + r, rr.Y), true);
                    fctx.LineTo(new Point(rr.Right - r, rr.Y));
                    fctx.ArcTo(new Point(rr.Right, rr.Y + r), new Size(r, r), 0, false, SweepDirection.Clockwise);
                    fctx.LineTo(new Point(rr.Right, rr.Bottom - r));
                    fctx.ArcTo(new Point(rr.Right - r, rr.Bottom), new Size(r, r), 0, false, SweepDirection.Clockwise);
                    fctx.LineTo(new Point(rr.X + r, rr.Bottom));
                    fctx.ArcTo(new Point(rr.X, rr.Bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise);
                    fctx.LineTo(new Point(rr.X, rr.Y + r));
                    fctx.ArcTo(new Point(rr.X + r, rr.Y), new Size(r, r), 0, false, SweepDirection.Clockwise);
                    fctx.EndFigure(true);
                }
            }
            void FPoly(params (double x, double y)[] pts)                       // 填充多边形（不用弧，避开自相交抵消）
            {
                if (pts.Length == 0) return;
                fctx.BeginFigure(new Point(pts[0].x, pts[0].y), true);
                for (int i = 1; i < pts.Length; i++) fctx.LineTo(new Point(pts[i].x, pts[i].y));
                fctx.EndFigure(true);
            }
            void FEllipse(double cx, double cy, double rx, double ry)           // 填充椭圆（两段弧）
            {
                fctx.BeginFigure(new Point(cx + rx, cy), true);
                fctx.ArcTo(new Point(cx - rx, cy), new Size(rx, ry), 0, true, SweepDirection.Clockwise);
                fctx.ArcTo(new Point(cx + rx, cy), new Size(rx, ry), 0, true, SweepDirection.Clockwise);
                fctx.EndFigure(true);
            }

            switch (g)
            {
                case Glyph.Cpu:
                    Box(3, 3, 6, 6);
                    FEllipse(6, 6, 1.4, 1.4);
                    foreach (var (ax, ay, bx, by) in new[] { (5.0, 1, 5, 3), (7, 1, 7, 3), (5, 9, 5, 11), (7, 9, 7, 11),
                                                              (1, 5, 3, 5), (1, 7, 3, 7), (9, 5, 11, 5), (9, 7, 11, 7) })
                    { Begin(ax, ay); Line(bx, by); End(); }
                    break;
                case Glyph.Gpu:
                    // 简化成与其它资源图标同风格的**几何形**：
                    // 原来「方框 + 圆 + 三条辐射线」细节太多，缩到 11px 后糊成一团（看着像摄像头）。
                    // 现在用「双层方框 + 中心点」，与 CPU 的方框、SSD 的格子同一套语言。
                    Box(2.2, 2.2, 7.6, 7.6, 1.4);
                    FRect(3.8, 3.8, 4.4, 4.4);
                    FEllipse(6, 6, 1.1, 1.1);
                    break;
                case Glyph.Mem:
                    Box(1, 4, 10, 4);
                    for (int i = 0; i < 4; i++) { Begin(2.5 + i * 2.2, 8); Line(2.5 + i * 2.2, 10); End(); }
                    break;
                case Glyph.Down:
                    Begin(6, 1); Line(6, 10); End();
                    Begin(2.5, 6.5); Line(6, 10); Line(9.5, 6.5); End();
                    break;
                case Glyph.Up:
                    Begin(6, 11); Line(6, 2); End();
                    Begin(2.5, 5.5); Line(6, 2); Line(9.5, 5.5); End();
                    break;
                case Glyph.Battery:
                    Box(1, 3, 9, 6);
                    FRect(10, 5, 1.5, 2);
                    FRect(2.2, 4.2, 4, 3.6);
                    break;
                case Glyph.Disk:
                    Begin(1, 3); Line(11, 3); End();
                    Begin(1, 3); Line(1, 9); End();
                    Begin(11, 3); Line(11, 9); End();
                    Begin(1, 9); Line(11, 9); End();
                    Begin(1, 6); Line(11, 6); End();
                    break;
                case Glyph.Ssd:
                    Box(0.8, 3, 10.4, 6, 1);
                    FRect(2, 4.2, 2.4, 3.6);
                    FRect(5.1, 4.2, 2.4, 3.6);
                    Begin(8.9, 4.4); Line(8.9, 7.6); End();
                    Begin(10.3, 4.4); Line(10.3, 7.6); End();
                    break;
                case Glyph.Thermo:
                    Box(4.3, 0.8, 3.4, 7, 1.5);
                    FRect(5.2, 3.2, 1.6, 4.4);
                    FEllipse(6, 9, 2.7, 2.5);
                    break;
                case Glyph.Ping:
                    Begin(1, 9); Line(2.2, 9); End();
                    Begin(4, 7.5); Line(5.5, 6); End();
                    Begin(7, 5); Line(8.5, 3.5); End();
                    Begin(10, 2.5); Line(11, 1.5); End();
                    break;
                case Glyph.Spark:
                    Begin(6, 1); Line(6, 4); End();
                    Begin(6, 8); Line(6, 11); End();
                    Begin(1, 6); Line(4, 6); End();
                    Begin(8, 6); Line(11, 6); End();
                    FEllipse(6, 6, 1.4, 1.4);
                    break;
                // ---- 天气图标 ----
                case Glyph.Sun:   // 太阳：实心圆 + 8 道短光芒
                    {
                        var pts = new (double, double)[14];
                        for (int i = 0; i < 14; i++)
                        { double a = i * Math.PI * 2 / 14; pts[i] = (6 + Math.Cos(a) * 2.6, 6 + Math.Sin(a) * 2.6); }
                        FPoly(pts);
                    }
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        double c1 = Math.Cos(a), s1 = Math.Sin(a);
                        Begin(6 + c1 * 4.2, 6 + s1 * 4.2); Line(6 + c1 * 5.6, 6 + s1 * 5.6); End();
                    }
                    break;
                case Glyph.Cloud: // 云：多边形拼的云团（不用弧：弧在 NonZero 下会自相抵消）
                    FPoly((3.2, 5.2), (5.0, 5.2), (5.6, 4.4), (7.0, 4.4), (7.8, 5.3), (9.4, 5.3), (9.4, 8.4), (3.2, 8.4));
                    break;
                case Glyph.Rain:  // 云 + 三条雨线
                    FPoly((3.2, 6.0), (5.0, 6.0), (5.6, 5.2), (7.0, 5.2), (7.8, 6.1), (9.4, 6.1), (9.4, 8.6), (3.2, 8.6));
                    for (int i = 0; i < 3; i++)
                    { double rx = 4.2 + i * 2.0; Begin(rx, 9.4); Line(rx - 0.8, 11.2); End(); }
                    break;
                case Glyph.Snow: // 云 + 三个雪花点
                    FPoly((3.2, 6.0), (5.0, 6.0), (5.6, 5.2), (7.0, 5.2), (7.8, 6.1), (9.4, 6.1), (9.4, 8.6), (3.2, 8.6));
                    FPoly((3.2, 10.2), (4.0, 10.2), (4.0, 11.2), (3.2, 11.2));
                    FPoly((5.7, 10.8), (6.5, 10.8), (6.5, 11.6), (5.7, 11.6));
                    FPoly((8.2, 10.2), (9.0, 10.2), (9.0, 11.2), (8.2, 11.2));
                    break;
                case Glyph.Fog:  // 云 + 三条雾线
                    FPoly((3.2, 6.8), (5.0, 6.8), (5.6, 6.0), (7.0, 6.0), (7.8, 6.9), (9.4, 6.9), (9.4, 8.8), (3.2, 8.8));
                    for (int i = 0; i < 3; i++)
                    { double yy = 9.6 + i * 0.8; Begin(3.2, yy); Line(9.2, yy); End(); }
                    break;
                case Glyph.Bolt: // 闪电
                    sctx.BeginFigure(new Point(7.2, 1.0), false);
                    sctx.LineTo(new Point(3.4, 6.8)); sctx.LineTo(new Point(6.2, 6.8)); sctx.LineTo(new Point(4.8, 11.0));
                    sctx.LineTo(new Point(8.6, 5.2)); sctx.LineTo(new Point(5.8, 5.2)); sctx.EndFigure(true);
                    break;
                case Glyph.SunLine: // 亮度：空心圆 + 8 道光芒（描边，比实心 Sun 更轻，适合做行首图标）
                    // 圆 = 两段 180° 弧（不能写起点=终点的一段整圆，那样画不出来）
                    sctx.BeginFigure(new Point(8.4, 6), false);
                    sctx.ArcTo(new Point(3.6, 6), new Size(2.4, 2.4), 0, false, SweepDirection.Clockwise);
                    sctx.ArcTo(new Point(8.4, 6), new Size(2.4, 2.4), 0, false, SweepDirection.Clockwise);
                    sctx.EndFigure(false);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4, c1 = Math.Cos(a), s1 = Math.Sin(a);
                        Begin(6 + c1 * 3.6, 6 + s1 * 3.6); Line(6 + c1 * 4.9, 6 + s1 * 4.9); End();
                    }
                    break;
                case Glyph.Contrast: // 对比度：描边圆 + 右半实心
                    sctx.BeginFigure(new Point(9.6, 6), false);
                    sctx.ArcTo(new Point(2.4, 6), new Size(3.6, 3.6), 0, false, SweepDirection.Clockwise);
                    sctx.ArcTo(new Point(9.6, 6), new Size(3.6, 3.6), 0, false, SweepDirection.Clockwise);
                    sctx.EndFigure(false);
                    {
                        // 右半圆填充：从顶点沿右侧到 底点，再用直径闭合
                        var pts = new List<(double, double)>();
                        for (int i = 0; i <= 12; i++)
                        { double a = -Math.PI / 2 + i * Math.PI / 12; pts.Add((6 + Math.Cos(a) * 3.3, 6 + Math.Sin(a) * 3.3)); }
                        FPoly(pts.ToArray());
                    }
                    break;
                case Glyph.Speaker: // 音量：喇叭 + 一道声波
                    sctx.BeginFigure(new Point(1.6, 4.4), false);
                    sctx.LineTo(new Point(3.8, 4.4)); sctx.LineTo(new Point(6.6, 2.2));
                    sctx.LineTo(new Point(6.6, 9.8)); sctx.LineTo(new Point(3.8, 7.6));
                    sctx.LineTo(new Point(1.6, 7.6)); sctx.EndFigure(true);
                    {
                        // 声波弧：用折线近似一段圆弧（圆心 (7.2,6) 半径 2.8，-50°..50°）
                        var pts = new List<(double, double)>();
                        for (int i = 0; i <= 8; i++)
                        { double a = -50 * Math.PI / 180 + i * (100 * Math.PI / 180) / 8; pts.Add((7.2 + Math.Cos(a) * 2.8, 6 + Math.Sin(a) * 2.8)); }
                        Begin(pts[0].Item1, pts[0].Item2);
                        for (int i = 1; i < pts.Count; i++) Line(pts[i].Item1, pts[i].Item2);
                        End();
                    }
                    break;
                case Glyph.ClipBox:  // 剪贴板：盒子 + 上方小夹子
                    Box(1.6, 3.2, 8.8, 7.6, 1.2);
                    Begin(4.2, 3.2); Line(4.2, 1.8); Line(7.8, 1.8); Line(7.8, 3.2); End();
                    break;
                case Glyph.RgbRed:   // 红增益：实心圆点
                    FEllipse(6, 6, 3.6, 3.6);
                    break;
                case Glyph.RgbGreen: // 绿增益
                    FEllipse(6, 6, 3.6, 3.6);
                    break;
                case Glyph.RgbBlue:  // 蓝增益
                    FEllipse(6, 6, 3.6, 3.6);
                    break;
                case Glyph.Laptop: // 笔记本：屏幕 + 底座
                    Box(2.4, 2.2, 7.2, 5.2, 0.8);
                    Begin(1.2, 9.4); Line(10.8, 9.4); End();
                    Begin(3.6, 8.2); Line(3.6, 9.4); End();
                    Begin(8.4, 8.2); Line(8.4, 9.4); End();
                    break;
            }
        }
        return (stroke, fills);
    }

    private static ImmutablePen IconPen(Color color, double scale)
    {
        // 笔宽随 scale 变化，按 (颜色, scale) 缓存；scale 实际只有 1.0 和少数几档
        var k = (color, Math.Round(scale * 10) / 10);
        if (_iconPens.TryGetValue(k, out var p)) return p;
        p = new ImmutablePen(new ImmutableSolidColorBrush(color, 1.0, null), 1.3 * scale, null, PenLineCap.Round, PenLineJoin.Round);
        return _iconPens[k] = p;
    }

    /// <summary>一条剪贴记录在预览区实际占用的宽度（用于把「+N 行」标在文本右侧）。</summary>
    private static double TextWidth(ClipItem c, double maxW)
    {
        var t = c.Kind switch
        {
            ClipKind.Code => RenderKit.MonoTrimmed(c.Preview, 10.5, Colors.White, maxW, false),
            ClipKind.Link => RenderKit.Trimmed(c.Preview, 11, Colors.White, maxW, false),
            ClipKind.Color => RenderKit.Trimmed(c.Preview, 10.5, Colors.White, maxW - 20, false),
            ClipKind.Image => RenderKit.Trimmed(c.Preview, 10.5, Colors.White, maxW - 60, false),
            _ => RenderKit.Trimmed(c.Preview, 11, Colors.White, maxW, false),
        };
        return Math.Min(t.WidthIncludingTrailingWhitespace, maxW);
    }

    /// <summary>垃圾桶图标（描边式，12×12）：桶盖 + 提手 + 桶身。
    /// 剪贴板页「清空」按钮用（BrimDeck 位置：分类标签行右端）。
    /// 不走 Glyph 枚举 —— 那套几何都按 12×12 网格缓存，塞一个只此一处用的图标不划算。
    /// </summary>
    private static void DrawTrashIcon(DrawingContext dc, Palette pal, double x, double cy)
    {
        var pen = RenderKit.PenOf(pal.TextDim, 1.2, 0.9);
        // 桶盖：横线
        dc.DrawLine(pen, new Point(x - 5, cy - 3.5), new Point(x + 5, cy - 3.5));
        // 提手：短竖 + 顶横
        dc.DrawLine(pen, new Point(x - 2, cy - 3.5), new Point(x - 2, cy - 5));
        dc.DrawLine(pen, new Point(x - 2, cy - 5), new Point(x + 2, cy - 5));
        dc.DrawLine(pen, new Point(x + 2, cy - 5), new Point(x + 2, cy - 3.5));
        // 桶身：上宽下窄的梯形 + 两条竖纹
        dc.DrawLine(pen, new Point(x - 4, cy - 1.5), new Point(x - 3, cy + 4.5));
        dc.DrawLine(pen, new Point(x + 4, cy - 1.5), new Point(x + 3, cy + 4.5));
        dc.DrawLine(pen, new Point(x - 3, cy + 4.5), new Point(x + 3, cy + 4.5));
        dc.DrawLine(pen, new Point(x - 1.2, cy - 1), new Point(x - 1, cy + 3));
        dc.DrawLine(pen, new Point(x + 1.2, cy - 1), new Point(x + 1, cy + 3));
    }

    /// <summary>在 (x,cy) 处画一枚 12×12 图标，返回其宽度（恒 12×scale）。几何按 (Glyph,Color) 缓存。</summary>
    private static double DrawIcon(DrawingContext dc, Glyph g, double x, double cy, Color color, double scale = 1.0)    {
        var key = (g, (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B));
        if (!_iconCache.TryGetValue(key, out var geos)) _iconCache[key] = geos = BuildIcon(g);
        // ⚠ 乘法顺序：Avalonia 是行向量约定，A * B = 先 A 后 B。
        //   写成 Translation * Scale 会把平移量 x 也放大 scale 倍 —— 图标被推到右边
        //   （scale=1.05 时偏 5% 看不出来；时钟天气图标放大到 1.25 后偏了 84px，跑到城市名后面）。
        //   正确写法：Scale * Translation = 先缩放图形，再平移到位。
        using (dc.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, cy - 6 * scale)))
        {
            // 纯填充图标（云/太阳/雪/雾/闪电）没有描边段：空的 stroke 几何传给 DrawGeometry
            // 会让 Skia 端报错并中断后续绘制——必须判断后再画。
            if (geos.stroke.Bounds.Width > 0 && geos.stroke.Bounds.Height > 0) dc.DrawGeometry(null, IconPen(color, scale), geos.stroke);
            if (geos.fill.Bounds.Width > 0 && geos.fill.Bounds.Height > 0) dc.DrawGeometry(RenderKit.BrushOf(color), null, geos.fill);
        }
        return 12 * scale;
    }

    /// <summary>时间胶囊：温度 / 网络 / 系统三组合并为一行，图标 + 数值同时显示。</summary>
    /// <summary>
    /// 时钟视图 = 信息胶囊：时间（大）+ 日期 + 天气。
    /// 简约——不塞系统监控读数（那些在展开后的监控页）。布局：
    ///   [ 14:32 | 11月24日 星期二 33 | ☂ 18° 北京 ]
    /// </summary>
    // ============ 收起态字号阶梯 ============
    // 之前三个收起视图各定各的，一共用了 9 种字号（9.5 / 10 / 11 / 11.5 / 12.5 / 15 / 22 …），
    // 一个 46px 高的胶囊里塞 4 种层级，看着"奇奇怪怪"（2026-11-24 用户反馈）。
    // 统一成 4 级，**所有收起视图只用这四个**：
    private const double FontHero = 22;      // 主数值：时钟的时间
    private const double FontValue = 13;     // 数值：百分比 / 温度 / 网速 / 日期
    private const double FontBody = 11.5;    // 正文：天气、副标题
    private const double FontLabel = 10;     // 标签：城市、单位缩写

    // ============ 时钟胶囊布局 ============
    // 测量和绘制**共用**下面这套 Layout 计算量出来的数字。
    // 之前测量（MeasureClockWidth）和绘制（DrawClock）各写一遍公式，三处对不上
    // （秒间距 3 vs 6、天气图标前少算 8px），合计少算 11px，城市名被顶出胶囊外
    // （2026-11-24 用户截图："Tokyo" 出框）。
    private const double ClockPadX = 18;      // 胶囊左右内边距
    private const double ClockColGap = 14;    // 三个分区的间距（分隔线居中）

    /// <summary>时钟胶囊各段的文本（已排好版式）。测量与绘制共用，保证宽度一致。</summary>
    private sealed class ClockLayout
    {
        public FormattedText Time = null!, Date = null!, Sec = null!;
        public Glyph? Icon; public Color IconColor;
        public FormattedText? Temp, City;
        public bool HasWeather;

        // 逐段宽度（不含分区间距）
        public double TimeW => Time.WidthIncludingTrailingWhitespace;
        public double SecW => Sec.WidthIncludingTrailingWhitespace;
        public double SecGap => 3;                       // 时间与秒之间（秒与主时间基线对齐，像个整体）
        public double DateW => Date.WidthIncludingTrailingWhitespace;
        public double IconW => 13;                      // 图标宽（和正文字号匹配；之前 15 + 纯饱和蓝，比温度还抢眼）
        public double IconGap => 8;                     // 图标到温度
        public double TempGap => 8;
        public double TempW => Temp?.WidthIncludingTrailingWhitespace ?? 0;
        public double CityGap => 8;
        public double CityW => City?.WidthIncludingTrailingWhitespace ?? 0;

        public double LeftW => TimeW + SecGap + SecW;
        public double WeatherW => HasWeather ? IconW + IconGap + TempGap + TempW + CityGap + CityW : 0;
        /// <summary>内容总宽 = 三段 + 两个分区间距。</summary>
        public double ContentW => LeftW + ClockColGap + DateW + (HasWeather ? ClockColGap + WeatherW : 0);
        /// <summary>胶囊宽度 = 内容 + 左右内边距。</summary>
        public double CapsuleW => ContentW + ClockPadX * 2;
    }

    /// <summary>把颜色往基准色混（降低饱和度）：语义色在收起态胶囊里太抢眼时用它压一档。</summary>
    private static Color Mute(Color c, Color toward, double towardShare)
        => Color.FromRgb(
            (byte)Math.Round(c.R + (toward.R - c.R) * towardShare),
            (byte)Math.Round(c.G + (toward.G - c.G) * towardShare),
            (byte)Math.Round(c.B + (toward.B - c.B) * towardShare));

    /// <summary>城市名的最大宽度：超过就尾部省略。给得比以前宽（90 → 132），常见地名都能放下。</summary>
    private const double ClockCityMaxW = 132;

    /// <summary>
    /// 排出时钟胶囊的版式。城市名来自 IP 定位，长短和语言都不可控（"Tokyo" / "乌鲁木齐" /
    /// "San Francisco"），所以**胶囊宽度跟着实际内容走**，而不是把内容塞进固定宽度。
    /// </summary>
    private ClockLayout LayoutClock(Palette pal)
    {
        var now = DateTime.Now;
        var (wGlyph, wColor, weatherText, temp, city) = WeatherParts(pal);
        var l = new ClockLayout
        {
            Time = RenderKit.Text(now.ToString("HH:mm"), FontHero, pal.Text, true),
            // 日期与星期用间隔点连接："11月24日·周二"。原来用 ASCII 空格，在苹方下太宽，像断开了
            Date = RenderKit.Text(now.ToString("M月d日") + "·" + WeekdayShort(now.DayOfWeek), FontValue, pal.TextDim),
            // 秒：和主时间是一体的，用 TextDim（不是第三级灰），**基线对齐**主时间。
            // 之前是 10px + TextTertiary 悬在右下角，像个瑕疵。
            Sec = RenderKit.Text(now.ToString("ss"), FontBody, pal.TextDim, true),
            Icon = wGlyph, IconColor = wColor,
            HasWeather = weatherText is not null && wGlyph is not null,
        };
        if (l.HasWeather)
        {
            // 只显示「图标 + 温度 + 城市」。图标已经表达了天气现象，再画一个汉字"雨"是重复的，
            // 而且 13px 的图标和 11.5px 的汉字紧挨着会黏成一团（2026-11-24 用户反馈）。
            if (temp is { } tp) l.Temp = RenderKit.Text($"{Math.Round(tp)}°", FontValue, pal.Text, true);
            if (city is { Length: > 0 } cs)
            {
                // 先按原宽排版；放得下就原样显示，放不下才用带省略号的裁剪版
                var full = RenderKit.Text(cs, FontLabel, pal.TextDim);
                l.City = full.WidthIncludingTrailingWhitespace <= ClockCityMaxW
                    ? full
                    : RenderKit.Trimmed(cs, FontLabel, pal.TextDim, ClockCityMaxW, false);
            }
        }
        return l;
    }

    /// <summary>时钟胶囊所需宽度（宿主用它设置窗口大小）。</summary>
    public double MeasureClockWidth() => LayoutClock(Palette).CapsuleW;
    /// <summary>时间胶囊：大号时间 + 秒 + 日期 + 天气，整体居中于药丸内。</summary>
    private void DrawClock(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        var L = LayoutClock(pal);
        double x = r.X + (r.Width - L.ContentW) / 2;      // 内容在胶囊里水平居中
        double top = cy - 15;

        // ① 时间 + 秒：**基线对齐**（秒是主时间的一部分，不是角落里的装饰）
        double mid = cy;                                   // 胶囊垂直中线
        dc.DrawText(L.Time, new Point(x, mid - L.Time.Height / 2));
        // 基线对齐：两者的 baseline 到各自顶部的距离之差
        dc.DrawText(L.Sec, new Point(x + L.TimeW + L.SecGap, mid - L.Time.Height / 2 + (L.Time.Baseline - L.Sec.Baseline)));
        x += L.LeftW;

        void Divider(double px)
            => dc.DrawLine(RenderKit.PenOf(pal.Hairline, 1, 0.85), new Point(px, mid - 10), new Point(px, mid + 10));

        // ② 日期（垂直居中）
        Divider(x + ClockColGap / 2);
        x += ClockColGap;
        dc.DrawText(L.Date, new Point(x, mid - L.Date.Height / 2));
        x += L.DateW;

        // ③ 天气：图标 + 温度 + 城市。图标颜色降一档（和日期同级），不再比温度抢眼。
        if (L.HasWeather && L.Icon is { } g)
        {
            Divider(x + ClockColGap / 2);
            x += ClockColGap;
            DrawIcon(dc, g, x, mid, Mute(L.IconColor, pal.Text, 0.62), L.IconW / 12);
            x += L.IconW + L.IconGap;
            if (L.Temp is not null)
            {
                dc.DrawText(L.Temp, new Point(x, mid - L.Temp.Height / 2));
                x += L.TempW + L.TempGap;
            }
            if (L.City is not null)
                dc.DrawText(L.City, new Point(x, mid - L.City.Height / 2));
        }
    }

    /// <summary>星期两字缩写（周二/周三）——比"星期二"紧凑。</summary>
    private static string WeekdayShort(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "周一", DayOfWeek.Tuesday => "周二", DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四", DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六", DayOfWeek.Sunday => "周日",
        _ => "",
    };

    /// <summary>
    /// 屏幕页实际内容高度——展开面板高度按它算，不写死。
    /// 组成：顶部提示（无内建屏时 20）+ 内建屏顶栏（有内建屏时 34+10 / 已关闭 56+10）
    ///      + 每张卡片（内建 68 / 外接 150）+ 间距 10 + 底部状态行 20。
    /// </summary>
    /// <summary>
    /// 屏幕页实际内容高度——展开面板高度按它算，不写死。
    /// 必须和 DrawScreensPage 的排布**逐项一致**（常量同名同值）：
    ///   卡片头 38 + 每个控制行 34 + 状态行 30 + 折叠行 30 (+ 展开区 64) + 上下内边距。
    /// </summary>
    public double MeasureScreensHeight()
    {
        const double cardPad = 16, cardGap = 12, rowH = 34;
        double h = 0;
        bool hasBuiltin = Screens.Any(s => s.Builtin);
        if (hasBuiltin || BuiltinOff) h += 38 + 2 + (hasBuiltin ? rowH : 0) + 24 + cardPad + cardGap;   // 笔记本屏幕卡（含内建屏亮度）
        foreach (var s in Screens)
        {
            if (s.Builtin) continue;                                    // 已并入笔记本屏幕卡
            double card = 38 + 2 + rowH + rowH + (hasBuiltin ? rowH : 0) + rowH;   // 头 + 亮度 + 对比度 + (跟随) + 音量
            if (!s.Builtin) card += 24 + 3 * 30 + 38;  // 颜色设置（标题+三滑条+重置按钮）
            card += 30 + 30;                                            // 状态行 + 折叠行
            if (ScreenSettingsOpen.Contains(s.Id)) card += 64;
            h += card + cardPad - 4 + cardGap;
        }
        if (!string.IsNullOrEmpty(AutoFollowStatus)) h += 16;
        return h + 22;
    }

    /// <summary>设备页高度：每行 150 + 间距 12。</summary>
    public double MeasureDevicesHeight()
    {
        const double cardH = MonitorLayoutSpec.DeviceCardHeight, gap = MonitorLayoutSpec.DeviceCardGap;
        double w = MonitorLayoutSpec.DevicesContentWidth(MonitorLayoutSpec.ExpandedPanelWidth); // 与 DrawDevicesPage 的可用宽度一致
        int perRow = MonitorLayoutSpec.DevicesPerRow(MonitorLayoutSpec.ExpandedPanelWidth);
        int rows = (Devices.Count + perRow - 1) / perRow;
        if (rows == 0) return 40;                        // 空状态提示一行
        return rows * cardH + (rows - 1) * gap;
    }

    /// <summary>dash 胶囊所需宽度（宿主用它设置窗口大小）。</summary>
    public double MeasureDashWidth() => LayoutDash(Palette).CapsuleW;

    /// <summary>信息胶囊实际所需宽度（时间 + 4 表盘 + 温度 + 网速）。</summary>
    /// <summary>天气的绘制部分（图标/颜色/文字/温度/城市）。</summary>
    private (Glyph? glyph, Color color, string? text, double? temp, string? city) WeatherParts(Palette pal)
    {
        var w = Weather;
        if (w is null) return (null, pal.TextDim, null, null, null);
        return (WeatherGlyph(w.Condition), WeatherColor(w.Condition, pal), w.Condition, w.Temp, w.City);
    }

    /// <summary>天气现象 → 图标（矢量，不用 emoji：emoji 在各系统渲染不一致）。</summary>
    private static Glyph WeatherGlyph(string condition) => condition switch
    {
        var c when c.Contains("雷") => Glyph.Bolt,
        var c when c.Contains("雨") => Glyph.Rain,
        var c when c.Contains("雪") => Glyph.Snow,
        var c when c.Contains("雾") || c.Contains("霾") => Glyph.Fog,
        var c when c.Contains("云") || c.Contains("阴") => Glyph.Cloud,
        _ => Glyph.Sun,
    };

    /// <summary>天气的语义色：雨天偏蓝、雪偏青、晴偏暖。</summary>
    private static Color WeatherColor(string condition, Palette pal)
    {
        if (condition.Contains("雷")) return Color.FromRgb(0xC9, 0xA6, 0xFF);
        if (condition.Contains("雨")) return Color.FromRgb(0x6F, 0xB8, 0xFF);
        if (condition.Contains("雪")) return Color.FromRgb(0xA8, 0xE6, 0xF0);
        if (condition.Contains("雾") || condition.Contains("霾")) return pal.TextDim;
        if (condition.Contains("云") || condition.Contains("阴")) return Color.FromRgb(0x9F, 0xB4, 0xD0);
        return Color.FromRgb(0xFF, 0xC4, 0x6B);
    }
}
