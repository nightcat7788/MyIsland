using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Island.App.Services;

/// <summary>
/// 全局设置，存到 %LOCALAPPDATA%\MyIsland\settings.json。原子写入避免损坏。
/// 持久化加固（参考 BrimDeck SettingsStore）：
/// - 逐字段反序列化：单个字段类型不对只丢该字段，其余字段照常读（整份反序列化时一个手滑的引号能抹掉全部配置）；
/// - 损坏隔离：读不出来就把原文件"另存为" settings.unreadable-<时间戳>.json，原文件绝不覆盖/删除；
/// - IO 失败（文件被占用/无权限）不当成"内容坏了"：保留原文件、记日志，本次会话用默认值且禁止回写；
/// - 未知字段保留：文件里本版本不认识的键原样写回，旧版本不会抹掉新版本写的键；
/// - 写入防抖 + 串行：RequestSave() 合并 400ms 内的连续改动只落一次盘；Save() 保持立即写（退出/显式保存用）。
/// </summary>
public sealed class Settings
{
    public string Theme { get; set; } = "Dark";       // Dark / Light / Auto
    public string Edge { get; set; } = "top";          // top/bottom/left/right
    public double PosAlongEdge { get; set; } = 0.5;    // 0..1
    public string ExpandMode { get; set; } = "hover";  // hover / always / never
    public string CompactView { get; set; } = "dash";  // dash / clock / pet / combo / monitor / devices / clipboard
    public bool AlwaysOnTop { get; set; } = true;
    public bool HideFullscreen { get; set; } = true;
    public string PetStyle { get; set; } = "classic";   // 小形象款式：classic / mono / cat / robot / pixel
    public bool PetAutoCycle { get; set; } = true;      // 小形象 10s → 信息胶囊 30s 自动轮换（参考 BrimDeck 刘海轮播）
    public bool IndicatorOnMaximize { get; set; } = true; // 前台窗口最大化时缩成指示条（BrimDeck 同款）
    /// <summary>长期显示：全屏/最大化都不隐藏不缩条，岛体始终保持可见（覆盖 HideFullscreen/IndicatorOnMaximize）。</summary>
    public bool AlwaysVisible { get; set; } = false;
    /// <summary>dash 视图显示项（逗号分隔：cores,cputemp,gputemp,mem,gpu,disk,ssd,battery,net；空=全部）。</summary>
    public string DashItems { get; set; } = "";
    /// <summary>
    /// combo 信息胶囊显示项（逗号分隔：clock,dials,cputemp,gputemp,disk,net；
    /// 空=默认组合 clock,dials,cputemp,gputemp,disk,net）。
    /// dials = C/M/G 三个占用率环；cputemp/gputemp = C/G 温度环；disk = D 硬盘环。
    /// 全部是同一套环式表盘，胶囊里不会出现两种视觉风格。
    /// </summary>
    public string ComboItems { get; set; } = "clock,dials,cputemp,gputemp,disk,net";
    /// <summary>监控页显示项（cpu,cores,mem,gpu,disk,net,power,topproc；空=全部）。</summary>
    public string MonitorItems { get; set; } = "";
    /// <summary>鼠标穿透模式：岛体只显示不拦截点击（适合游戏；BrimDeck 兼容模式同款）。</summary>
    public bool ClickThrough { get; set; } = false;
    public int MonitorIntervalMs { get; set; } = 1000;
    /// <summary>高温告警开关（收起态超阈值弹浮条提醒）。</summary>
    public bool TempAlertEnabled { get; set; } = true;
    /// <summary>高温告警阈值（°C）。</summary>
    public int TempAlertThreshold { get; set; } = 88;
    /// <summary>
    /// 低电量提醒（2026-11-25）：外设/电池电量低于阈值时弹一次浮条（同一台设备 60 分钟最多一次）。
    /// 只对有百分比的设备生效；XInput 手柄只报档位（空/低/中/满），没有百分比，不参与 —— 换算就是伪装。
    /// 提醒开着时设备采样退到 5 分钟一轮的慢轮询兜底（DeviceService.BackgroundWatch）。
    /// </summary>
    public bool BatteryAlertEnabled { get; set; } = true;
    /// <summary>低电量提醒阈值（%），夹到 5..50。</summary>
    public int BatteryAlertThreshold { get; set; } = 20;
    public string City { get; set; } = "";             // 天气城市（空=IP 定位）
    /// <summary>
    /// 允许 AMD SMN 直读 CPU 温度。默认关闭；2026-11-25 起在 **AMD 机器上首次启动自动置为开**
    ///（只做一次，见 <see cref="SmnAutoDone"/>）—— AMD 上 LHM 读不到温度的机型（Family 0x10–0x16）
    /// 只有这条路能出读数。安全边界不变：真正的闸门在 SmnChannel.Ensure（未开启 ⇒ 不创建服务）
    /// 与 InpOutSmnService 的厂商门控（非 AMD ⇒ 加载驱动之前就返回），而且温度服务只在
    /// 常规路径读不到时才尝试它。
    /// ⚠️ 这条通道会加载未签名的 inpoutx64 内核驱动并映射 PCIe 物理内存，
    /// 2026-11-23 在本机引发过三次 0x1EA 蓝屏。
    /// </summary>
    public bool EnableSmnDirectRead { get; set; } = false;
    /// <summary>
    /// "AMD 机器首启自动开启 SMN 直读"只做一次的标记（2026-11-25）。首次启动检测 CPU 厂商并
    /// 按结果落定 <see cref="EnableSmnDirectRead"/>，此后**不再自动改** —— 用户在设置里的选择
    /// （包括在 AMD 机器上关掉）一律尊重。「全部重置」也不重置它：重置后再自动打开，等于替用户
    /// 做一次他可能刚刚拒绝过的决定。
    /// </summary>
    public bool SmnAutoDone { get; set; } = false;
    /// <summary>手动选择网卡 Id（空=自动聚合；LiteMonitor PreferredNetwork 同款）。</summary>
    public string PreferredNetwork { get; set; } = "";
    /// <summary>剪贴板"排除来源进程"列表（逗号分隔的进程名，如 "keepass,1password"；空=全部记录）。</summary>
    public string ClipboardExcludedProcesses { get; set; } = "";

    // ---------- macOS 版设置（与 Windows 版共用同一个 Settings 文件）----------
    /// <summary>
    /// 用户是否**手动**固定了收起视图。true = 不再自动轮换 pet⇄combo（"我选过了，别自己变"）。
    ///  macOS 版：滚轮 / 托盘菜单 / 快捷键手动切视图时置 true；首次启动（没选过）才自动轮换。
    /// </summary>
    public bool CompactViewPinned { get; set; } = false;
    /// <summary>上次展开的页（monitor / clips / screens / devices），重启后回到同一页。</summary>
    public string LastPage { get; set; } = "monitor";
    /// <summary>
    /// 记住「关闭内建屏」选择：下次启动若有外接屏且内建屏在线，自动关闭。
    /// 无外接屏时安全跳过（避免整机黑屏）。用户主动恢复时清掉。
    /// </summary>
    public bool AutoCloseBuiltin { get; set; } = false;
    /// <summary>
    /// 每屏「自动色温」开关（显示器自己不存这个逻辑状态，重启必丢）。
    /// 格式 "UUID:0/1,UUID:0/1"，按显示器 UUID 记（屏幕 Id 重启后会变）。
    /// </summary>
    public string AutoColorByScreen { get; set; } = "";

    // ---------- 收起态装饰（参考 Vibe Island 刘海岛造型，用户可逐项开关）----------
    /// <summary>刘海融合造型：收起态画成两端上扬、中间带凹槽的胶囊，露出后方刘海。
    /// 仅刘海屏机型效果明显；非刘海屏也能正常显示（凹槽露出桌面）。</summary>
    public bool DecorNotch { get; set; } = true;
    /// <summary>字母标识环：资源环内画 C/M/G/D 字母代替图标（参考图同款）。</summary>
    public bool DecorLetters { get; set; } = true;

    // ---------- 信息胶囊样式 ----------
    /// <summary>
    /// 信息胶囊样式：true = **数字主角**（占用百分比 19px 粗体成视觉焦点，时间降为次级）；
    /// false = **经典版**（时间与读数同级，占用百分比沿用资源色）。
    /// </summary>
    public bool ComboNumericHero { get; set; } = true;

    // ---------- 剪贴板保留策略（BrimDeck 同款思路）----------
    /// <summary>
    /// 剪贴板记录保留多久："session" = 只在内存、退出即清（默认，隐私最稳）；
    /// "1d" / "7d" / "30d" = 落盘保留到本机，超过期限自动清除。
    /// 只在本机保存，不加密、不联网。
    /// </summary>
    public string ClipboardRetention { get; set; } = "session";

    // ---------- 小形象款式与自动循环 ----------
    /// <summary>
    /// 收起视图自动循环：true = 小形象 ⇄ 信息胶囊 自动轮换（10 秒 / 30 秒），
    /// false = 停在「收起样式」里手选的那个视图。与 CompactViewPinned 解耦。
    /// </summary>
    public bool AutoCycleViews { get; set; } = true;

    /// <summary>小形象款式的合法取值（读盘白名单，坏值退回默认）。Windows 侧支持前 5 款，macOS 侧 8 款全支持。</summary>
    public static readonly string[] PetStyleValues =
        { "classic", "mono", "cat", "robot", "pixel", "alien", "ghost", "frog" };

    /// <summary>剪贴板保留期的合法取值（读盘时白名单校验，坏值退回默认）。
    /// 与 Avalonia 侧 <c>ClipboardHistory.RetentionOptions</c> 保持一致。</summary>
    public static readonly string[] ClipboardHistoryRetentionValues = { "session", "1d", "7d", "30d" };

    // ---------- 数值字段范围校正 ----------
    // 坏值（手改 JSON、旧版本遗留）会让岛体窗口整个起不来：
    // 例如 MonitorIntervalMs=0 → new System.Timers.Timer(0) 抛 ArgumentOutOfRangeException
    // → 构造 IslandWindow 抛异常 → 被 App.Step 吞掉 → 只剩托盘图标，岛体窗口永不再出现。
    // 载入后与保存时都夹一次，坏值不会一直留在文件里。

    /// <summary>监控刷新间隔下限（毫秒）。</summary>
    private const int MonitorIntervalMinMs = 250;
    /// <summary>监控刷新间隔上限（毫秒）。</summary>
    private const int MonitorIntervalMaxMs = 10000;
    /// <summary>高温告警阈值下限（°C）。</summary>
    private const int TempAlertMinC = 30;
    /// <summary>高温告警阈值上限（°C）。</summary>
    private const int TempAlertMaxC = 120;
    /// <summary>低电量提醒阈值的合法范围（%）。</summary>
    private const int BatteryAlertMinPct = 5, BatteryAlertMaxPct = 50;

    /// <summary>把数值字段夹到合法范围（就地修正）。</summary>
    public void ClampValues()
    {
        MonitorIntervalMs = Math.Clamp(MonitorIntervalMs, MonitorIntervalMinMs, MonitorIntervalMaxMs);
        TempAlertThreshold = Math.Clamp(TempAlertThreshold, TempAlertMinC, TempAlertMaxC);
        BatteryAlertThreshold = Math.Clamp(BatteryAlertThreshold, BatteryAlertMinPct, BatteryAlertMaxPct);
        // PosAlongEdge 是 0..1 的比例；NaN/Infinity 要单独处理（Math.Clamp 对 NaN 不生效）
        if (double.IsNaN(PosAlongEdge) || double.IsInfinity(PosAlongEdge)) PosAlongEdge = 0.5;
        else PosAlongEdge = Math.Clamp(PosAlongEdge, 0.0, 1.0);
    }

    /// <summary>写盘防抖窗口：RequestSave() 合并这段时间内的连续改动。</summary>
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>写盘串行锁：同一时刻只允许一个线程写 settings.json（后写覆盖先写，不会写坏）。</summary>
    private static readonly object WriteGate = new();

    /// <summary>已知字段名集合（用于识别文件里的未知键）。</summary>
    private static readonly HashSet<string> KnownKeys = new(
        JsonSerializer.SerializeToNode(new Settings())!.AsObject().Select(p => p.Key),
        StringComparer.Ordinal);

    /// <summary>
    /// 已删除功能的遗留键：读到时**直接丢弃**（不进 _unknown），保存时不会再写回。
    /// 与「未知键保留」的区别：未知键是"新版本加的字段，旧版本不该抹掉"，
    /// 废弃键是"这个功能已经删了，别再留在用户配置里"。
    /// </summary>
    private static readonly HashSet<string> DeprecatedKeys = new(StringComparer.Ordinal)
    {
        "DecorRings",   // 左侧彩色状态环 —— 已移除
        "DecorBars",    // 右侧彩色柱状指示 —— 已移除
    };

    /// <summary>本实例从文件里读到的未知键：保存时原样写回，旧版本不删新版本的键。</summary>
    private JsonObject _unknown = new();

    /// <summary>读盘失败标记：本次会话读不到原文件，禁止用默认值把它覆盖掉。</summary>
    private bool _readFailed;

    /// <summary>读盘失败原因（供 UI 提示"为什么没保存"）。</summary>
    private string? _readError;

    /// <summary>本实例的防抖计时器（合并连续改动，_timerGate 只保护计时器本身）。</summary>
    private Timer? _saveTimer;
    private readonly object _timerGate = new();

    /// <summary>测试钩子：把设置目录指向别处（测试用；null = 真实用户目录）。</summary>
    internal static string? DirectoryOverride { get; set; }

    /// <summary>已完成的写盘次数（测试用：验证防抖只落一次盘）。</summary>
    internal static int SaveWriteCount;

    private static string Dir => DirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyIsland");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        var settings = new Settings();
        try
        {
            if (!File.Exists(FilePath)) return settings;

            string json;
            try
            {
                json = File.ReadAllText(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // IO 失败（文件被占用/无权限）≠ 内容坏了。
                // 这时退回默认再回写 = 一次 Save 就把用户原文件覆盖成默认配置，必须避免。
                // 保留原文件、记日志，本次会话用默认值，并且禁止静默覆盖。
                Log($"读取设置文件失败（{ex.GetType().Name}: {ex.Message}）；本次会话使用默认值且不覆盖原文件");
                settings._readFailed = true;
                settings._readError = "设置文件无法读取（可能被占用或无权限），本次会话使用默认值且不会覆盖原文件";
                return settings;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException ex)
            {
                // JSON 坏了：另存一份副本，原文件留在原地（绝不覆盖/删除）
                Log($"设置文件 JSON 解析失败：{ex.Message}");
                KeepUnreadable();
                settings._readFailed = true;
                settings._readError = "设置文件内容损坏，已另存为 unreadable 副本，本次会话使用默认值";
                return settings;
            }

            if (node is not JsonObject root)
            {
                Log("设置文件根节点不是对象，已另存为 unreadable 副本");
                KeepUnreadable();
                settings._readFailed = true;
                settings._readError = "设置文件内容损坏，已另存为 unreadable 副本，本次会话使用默认值";
                return settings;
            }

            // 逐字段读取：单个字段类型不对只丢该字段，其余字段照常读
            settings.ApplyFields(root);
            // 坏值校正：数值字段越界会让岛体窗口起不来，载入时就夹回合法范围
            settings.ClampValues();
            // 不认识的键原样留着，保存时写回去（新版本加的字段不会被旧版本抹掉）
            foreach (var pair in root)
                if (!KnownKeys.Contains(pair.Key) && !DeprecatedKeys.Contains(pair.Key))
                    settings._unknown[pair.Key] = pair.Value?.DeepClone();
            return settings;
        }
        catch (Exception ex)
        {
            // 兜底：其它异常一律按"读不出来"处理，原文件保留不动
            Log($"读取设置文件失败：{ex}");
            KeepUnreadable();
            settings._readFailed = true;
            settings._readError = "设置文件读取失败，已保留原文件，本次会话使用默认值";
            return settings;
        }
    }

    /// <summary>
    /// 逐字段反序列化：单个字段类型不对只丢该字段，其余字段照常读。
    /// 以前是整份 root.Deserialize&lt;Settings&gt;()：任意一个字段类型写错就抛 JsonException
    /// → 整套配置退默认 → 随后任意一次 Save 用默认值覆盖用户原文件。
    /// </summary>
    private void ApplyFields(JsonObject root)
    {
        if (TryReadField(root, nameof(Theme), out string theme)) Theme = theme;
        if (TryReadField(root, nameof(Edge), out string edge)) Edge = edge;
        if (TryReadField(root, nameof(PosAlongEdge), out double pos)) PosAlongEdge = pos;
        if (TryReadField(root, nameof(ExpandMode), out string expandMode)) ExpandMode = expandMode;
        if (TryReadField(root, nameof(CompactView), out string compactView)) CompactView = compactView;
        if (TryReadField(root, nameof(AlwaysOnTop), out bool alwaysOnTop)) AlwaysOnTop = alwaysOnTop;
        if (TryReadField(root, nameof(HideFullscreen), out bool hideFullscreen)) HideFullscreen = hideFullscreen;
        if (TryReadField(root, nameof(PetStyle), out string petStyle)
            && PetStyleValues.Contains(petStyle)) PetStyle = petStyle;
        if (TryReadField(root, nameof(PetAutoCycle), out bool petAutoCycle)) PetAutoCycle = petAutoCycle;
        if (TryReadField(root, nameof(IndicatorOnMaximize), out bool indicator)) IndicatorOnMaximize = indicator;
        if (TryReadField(root, nameof(AlwaysVisible), out bool alwaysVisible)) AlwaysVisible = alwaysVisible;
        if (TryReadField(root, nameof(DashItems), out string dashItems)) DashItems = dashItems;
        if (TryReadField(root, nameof(ComboItems), out string comboItems)) ComboItems = comboItems;
        if (TryReadField(root, nameof(MonitorItems), out string monitorItems)) MonitorItems = monitorItems;
        if (TryReadField(root, nameof(ClickThrough), out bool clickThrough)) ClickThrough = clickThrough;
        if (TryReadField(root, nameof(MonitorIntervalMs), out int interval)) MonitorIntervalMs = interval;
        if (TryReadField(root, nameof(TempAlertEnabled), out bool tempAlert)) TempAlertEnabled = tempAlert;
        if (TryReadField(root, nameof(TempAlertThreshold), out int threshold)) TempAlertThreshold = threshold;
        if (TryReadField(root, nameof(BatteryAlertEnabled), out bool batteryAlert)) BatteryAlertEnabled = batteryAlert;
        if (TryReadField(root, nameof(BatteryAlertThreshold), out int batteryThreshold)) BatteryAlertThreshold = batteryThreshold;
        if (TryReadField(root, nameof(City), out string city)) City = city;
        if (TryReadField(root, nameof(EnableSmnDirectRead), out bool smn)) EnableSmnDirectRead = smn;
        if (TryReadField(root, nameof(SmnAutoDone), out bool smnAutoDone)) SmnAutoDone = smnAutoDone;
        if (TryReadField(root, nameof(PreferredNetwork), out string nic)) PreferredNetwork = nic;
        if (TryReadField(root, nameof(ClipboardExcludedProcesses), out string clip)) ClipboardExcludedProcesses = clip;
        if (TryReadField(root, nameof(CompactViewPinned), out bool cvPinned)) CompactViewPinned = cvPinned;
        if (TryReadField(root, nameof(LastPage), out string lastPage)) LastPage = lastPage;
        if (TryReadField(root, nameof(AutoCloseBuiltin), out bool autoClose)) AutoCloseBuiltin = autoClose;
        if (TryReadField(root, nameof(AutoColorByScreen), out string autoColor)) AutoColorByScreen = autoColor;
        if (TryReadField(root, nameof(DecorNotch), out bool decorNotch)) DecorNotch = decorNotch;
        if (TryReadField(root, nameof(DecorLetters), out bool decorLetters)) DecorLetters = decorLetters;
        if (TryReadField(root, nameof(ComboNumericHero), out bool comboNumericHero)) ComboNumericHero = comboNumericHero;
        if (TryReadField(root, nameof(ClipboardRetention), out string clipRetention)
            && ClipboardHistoryRetentionValues.Contains(clipRetention)) ClipboardRetention = clipRetention;
        if (TryReadField(root, nameof(AutoCycleViews), out bool autoCycle)) AutoCycleViews = autoCycle;
    }

    /// <summary>
    /// 从 JSON 对象里取一个字段并做类型校验（纯函数，可单测覆盖）。
    /// true = 取到且类型正确；false = 字段缺失 / 为 null / 类型不对（调用方保留该字段的默认值）。
    /// 类型不对只丢这一个字段，不牵连其它字段。
    /// </summary>
    internal static bool TryReadField<T>(JsonObject root, string name, out T value)
    {
        value = default!;
        if (root is null || string.IsNullOrEmpty(name)) return false;
        if (!root.TryGetPropertyValue(name, out JsonNode? node) || node is null) return false;
        try
        {
            if (node.GetValueKind() == JsonValueKind.Null) return false;
            T? parsed = node.Deserialize<T>();
            if (parsed is null) return false;
            value = parsed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // 类型不匹配（例如把数字写进 string 字段、把字符串写进 int 字段）：只丢这个字段
            return false;
        }
    }

    /// <summary>
    /// 当前值是否与出厂默认不同。
    /// 读盘失败时用它判断"用户是不是真的改过设置"：没改过就不许用默认值覆盖用户原文件。
    /// </summary>
    private bool DiffersFromDefaults()
    {
        var mine = JsonSerializer.SerializeToNode(this)?.AsObject();
        var def = JsonSerializer.SerializeToNode(new Settings())?.AsObject();
        if (mine is null || def is null) return true;
        foreach (var pair in def)
        {
            if (!mine.TryGetPropertyValue(pair.Key, out JsonNode? v)) return true;
            if (!JsonNode.DeepEquals(v, pair.Value)) return true;
        }
        return false;
    }

    /// <summary>
    /// 最近一次保存失败的原因（null = 上次保存成功，或还没保存过）。
    /// UI 层可以据此提示"保存失败"，不用再自己猜。
    /// </summary>
    public static string? LastSaveError { get; private set; }

    /// <summary>
    /// 立即写盘（签名保持 void：App.xaml.cs 等处按 void 调用）。
    /// 写失败不抛：记日志并把原因放进 <see cref="LastSaveError"/>，UI 层可提示"保存失败"。
    /// </summary>
    public void Save()
    {
        try
        {
            // 数值字段越界时一并夹回合法范围，坏值不会一直留在文件里
            ClampValues();
            // 立即写：防抖里待落盘的那次不必再写（写的是同一份状态）
            CancelPendingSave();
            lock (WriteGate)
            {
                // 读盘失败过：原文件（可能是用户唯一的一份）不能被默认值盖掉。
                // 只有用户确实改了设置（与出厂默认不同）才写 —— unreadable 副本也已经留着了。
                if (_readFailed && !DiffersFromDefaults())
                {
                    LastSaveError = _readError ?? "设置文件读取失败，已跳过写盘以保留原文件";
                    Log("读盘失败过且用户未修改设置：跳过写盘，避免用默认值覆盖用户原文件");
                    return;
                }
                Directory.CreateDirectory(Dir);
                var own = JsonSerializer.SerializeToNode(this)!.AsObject();
                foreach (var pair in _unknown)
                    if (!own.ContainsKey(pair.Key))
                        own[pair.Key] = pair.Value?.DeepClone();
                string json = own.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                string tmp = FilePath + ".tmp";
                System.IO.File.WriteAllText(tmp, json);
                System.IO.File.Move(tmp, FilePath, true);
            }
            LastSaveError = null;
            Interlocked.Increment(ref SaveWriteCount);
        }
        catch (Exception ex)
        {
            // 写盘失败不能一声不吭：用户以为设置存上了，其实下次启动就没了
            LastSaveError = $"保存设置失败：{ex.Message}";
            Log($"保存设置失败：{ex}");
        }
    }

    /// <summary>
    /// 防抖写入：连续调用合并成一次落盘（UI 频繁改动用这个，避免每点一下就写盘）。
    /// 显式保存点（退出/确定/重置）仍走 Save()（立即写）。
    /// </summary>
    public void RequestSave()
    {
        lock (_timerGate)
        {
            _saveTimer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>取消待落盘的防抖写入（应用退出前/测试用）。</summary>
    internal void CancelPendingSave()
    {
        lock (_timerGate)
        {
            if (_saveTimer is not null) _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>日志上限（约 1MB）：超过就删掉旧文件重开一份，免得日志无限膨胀（同 App.WriteLog 的做法）。</summary>
    private const long LogLimit = 1_048_576;

    /// <summary>
    /// 写诊断日志（%LOCALAPPDATA%\MyIsland\crash.log，与 App.WriteLog 同风格）。
    /// Settings.cs 不依赖 App（测试工程只链接本文件），所以自己写一份；写失败只吞掉。
    /// </summary>
    private static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, "crash.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > LogLimit) File.Delete(path);
            File.AppendAllText(path, $"[{DateTime.Now}] {text}\n");
        }
        catch { /* 日志写失败不影响运行 */ }
    }

    /// <summary>
    /// 读失败时把原文件"另存为" settings.unreadable-&lt;yyyyMMdd-HHmmss&gt;.json。
    /// 复制而非改名/删除：用户的原文件既不被覆盖也不被删，数据永远找得回来。
    /// </summary>
    internal static bool CanReplaceActivity(string? currentKind, bool expired, string incomingKind)
    {
        static int Priority(string? kind) => kind == "temp" ? 3 : kind == "battery" ? 2 : 1;
        return currentKind is null || expired || Priority(incomingKind) >= Priority(currentKind);
    }

    private static void KeepUnreadable()
    {
        try
        {
            if (!System.IO.File.Exists(FilePath)) return;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string name = $"settings.unreadable-{stamp}.json";
            string target = Path.Combine(Dir, name);
            for (int i = 1; System.IO.File.Exists(target); i++)
            {
                name = $"settings.unreadable-{stamp}-{i}.json";
                target = Path.Combine(Dir, name);
            }
            System.IO.File.Copy(FilePath, target);
        }
        catch { /* 副本失败也不影响启动 */ }
    }
}
