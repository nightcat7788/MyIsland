namespace Island.Core.Capabilities;

/// <summary>一个能力在某个平台上的可用性。</summary>
public enum CapabilityAvailability
{
    /// <summary>平台不支持 ⇒ 该页签/项**直接不出现**（不置灰、不留空位，总纲 §1 的呈现规则）。</summary>
    Unsupported = 0,

    /// <summary>平台专属：两端实现不同源，不互相搬运，只在能力清单里公开差异。</summary>
    PlatformSpecific = 1,

    /// <summary>两端共享同一份实现（同一批源文件或同一份规格常量）。</summary>
    Shared = 2,
}

/// <summary>平台专属项归属哪一端。</summary>
public enum CapabilityOwner
{
    /// <summary>两端都有。</summary>
    Both = 0,

    /// <summary>只有 Windows 有。</summary>
    WindowsOnly = 1,

    /// <summary>只有 macOS 有。</summary>
    MacOnly = 2,
}

/// <summary>一个能力项（收起视图 / 展开页签 / 数据源）。</summary>
/// <param name="Key">稳定标识（用于测试与文档引用，不随显示名变化）。</param>
/// <param name="Title">显示名。</param>
/// <param name="Group">分组：collapsed / expanded / data / pet。</param>
/// <param name="Availability">可用性。</param>
/// <param name="Owner">平台专属时归属哪一端。</param>
/// <param name="Note">差异说明（写清"为什么不同"，避免下次又靠猜）。</param>
public sealed record CapabilityEntry(
    string Key,
    string Title,
    string Group,
    CapabilityAvailability Availability,
    CapabilityOwner Owner = CapabilityOwner.Both,
    string? Note = null);

/// <summary>
/// **能力清单 —— 两端唯一的真相源**（总纲 §3 的矩阵从"文档里的表格"变成"代码里的数据"）。
///
/// 为什么要有它：这轮实测发现总纲里三条记录是错的（收起视图集、主题 token、小形象实现位置），
/// 而且几份计划文档的复选框大面积漂移（工作做完了框没勾）。根因是"两端各写一份、靠人记"。
/// 清单 + 漂移检查（CapabilityManifestTests）就是阶段 4 + 阶段 5 的最小可用形态：
/// **清单改了、两端代码没跟着改 ⇒ 门禁红**。
///
/// 用法：
///  · 两端的收起视图列表 / 展开页签顺序都**从本类取**（不再各写一份）；
///  · 小形象款式列表同理（8 款，两端一致）；
///  · 测试断言清单自身的完整性，并断言两端代码引用的就是清单里的值。
/// </summary>
public static class CapabilityManifest
{
    // ================= 收起视图（岛体收起时的视图集） =================

    /// <summary>
    /// 收起视图。**共享部分的顺序以 mac 为准**（dash / clock / pet / combo，总纲 §1），
    /// Windows 独有的四个追加在后面。**Windows 比 mac 多 4 个**（monitor / devices / clipboard / window）。
    /// 这四个要么依赖 Windows 专属 API，要么是展开页能力在收起态的精简投影；差异公开在这里。
    /// </summary>
    public static readonly IReadOnlyList<CapabilityEntry> CollapsedViews = new CapabilityEntry[]
    {
        new CapabilityEntry("dash", "读数带", "collapsed", CapabilityAvailability.Shared),
        new CapabilityEntry("clock", "时钟", "collapsed", CapabilityAvailability.Shared),
        new CapabilityEntry("pet", "小形象", "collapsed", CapabilityAvailability.Shared),
        new CapabilityEntry("combo", "组合", "collapsed", CapabilityAvailability.Shared),
        new CapabilityEntry("monitor", "高密度读数带", "collapsed", CapabilityAvailability.PlatformSpecific,
            CapabilityOwner.WindowsOnly, "Windows 独有：每核细柱 + C/M/G/D 表盘"),
        new CapabilityEntry("devices", "设备电量行", "collapsed", CapabilityAvailability.PlatformSpecific,
            CapabilityOwner.WindowsOnly, "Windows 独有：收起态显示各设备电量图标与读数"),
        new CapabilityEntry("clipboard", "剪贴板提示行", "collapsed", CapabilityAvailability.PlatformSpecific,
            CapabilityOwner.WindowsOnly, "Windows 独有：收起态显示最近一条剪贴板预览"),
        new CapabilityEntry("window", "活动窗口", "collapsed", CapabilityAvailability.PlatformSpecific,
            CapabilityOwner.WindowsOnly, "依赖 user32 前台窗口 API，mac 无等价实现"),
    };

    // ================= 展开页签 =================

    /// <summary>
    /// 展开面板页签。**共享页签在前、平台专属页签在后**（总纲 §1 的呈现规则统一）。
    /// </summary>
    public static readonly IReadOnlyList<CapabilityEntry> ExpandedTabs = new CapabilityEntry[]
    {
        new CapabilityEntry("monitor", "监控", "expanded", CapabilityAvailability.Shared),
        new CapabilityEntry("clips", "剪贴板", "expanded", CapabilityAvailability.Shared,
            CapabilityOwner.Both, "Windows 的页面内另有「文件」列（Windows 专属内容）"),
        new CapabilityEntry("devices", "设备", "expanded", CapabilityAvailability.Shared),
        new CapabilityEntry("control", "控制", "expanded", CapabilityAvailability.PlatformSpecific,
            CapabilityOwner.WindowsOnly, "亮度/WLAN/蓝牙/麦克风/锁屏/睡眠/清理内存"),
        new CapabilityEntry("screens", "屏幕", "expanded", CapabilityAvailability.PlatformSpecific,
            CapabilityOwner.MacOnly, "显示器控制（DDC）"),
    };

    // ================= 数据源 =================

    /// <summary>数据源（读数从哪来）。</summary>
    public static readonly IReadOnlyList<CapabilityEntry> DataSources = new CapabilityEntry[]
    {
        new CapabilityEntry("cpu-usage", "CPU 占用", "data", CapabilityAvailability.Shared,
            CapabilityOwner.Both, "口径已对齐：% Processor Utility（任务管理器口径），2026-11-26 修正"),
        new CapabilityEntry("cpu-frequency", "CPU 频率", "data", CapabilityAvailability.Shared,
            CapabilityOwner.Both, "口径已对齐：Actual Frequency 真值优先"),
        new CapabilityEntry("cpu-temp", "CPU 温度", "data", CapabilityAvailability.Shared,
            CapabilityOwner.Both, "口径已对齐：Package 优先、排除 Distance to TjMax 等干扰项"),
        new CapabilityEntry("gpu", "GPU 占用/温度/显存/频率", "data", CapabilityAvailability.Shared),
        new CapabilityEntry("memory", "内存占用", "data", CapabilityAvailability.Shared),
        new CapabilityEntry("disk", "磁盘活动/读写", "data", CapabilityAvailability.Shared),
        new CapabilityEntry("network", "网速", "data", CapabilityAvailability.Shared,
            CapabilityOwner.Both, "口径已对齐：排除虚拟网卡（Hyper-V/WSL/容器等）"),
        new CapabilityEntry("runtime", "运行时长", "data", CapabilityAvailability.Shared,
            CapabilityOwner.Both, "口径已对齐：PDH System Up Time（不含休眠）"),
        new CapabilityEntry("battery-bt", "蓝牙设备电量", "data", CapabilityAvailability.PlatformSpecific,
            CapabilityOwner.Both, "Windows 走 BLE GATT 0x180F/0x2A19；mac 走 IOKit 电池属性"),
        new CapabilityEntry("battery-hid", "2.4G 接收器电量", "data", CapabilityAvailability.Shared,
            CapabilityOwner.Both, "两端共用同一份协议表与三层校验；mac 侧原生探针待实机验证"),
        new CapabilityEntry("battery-internal", "本机内置电池", "data", CapabilityAvailability.PlatformSpecific,
            CapabilityOwner.Both, "Windows 走 WMI Win32_Battery；mac 走 AppleSmartBattery"),
        new CapabilityEntry("storage-temp", "SSD 温度", "data", CapabilityAvailability.PlatformSpecific,
            CapabilityOwner.MacOnly, "Windows 不可用：LHM 的 NVMe SMART IOCTL 在本机触发 0x1EA 蓝屏两次；mac 走 IOHID NAND 传感器"),
    };

    // ================= 小形象款式 =================

    /// <summary>小形象款式（两端一致，2026-11-26 已补齐 Windows 侧缺失的 3 款）。</summary>
    public static readonly IReadOnlyList<string> PetStyles =
        new string[] { "classic", "mono", "cat", "robot", "pixel", "alien", "ghost", "frog" };

    // ================= 查询 =================

    /// <summary>某平台的收起视图键列表（按清单顺序）。</summary>
    public static IReadOnlyList<string> CollapsedViewsFor(bool macOS) =>
        CollapsedViews.Where(e => Available(e, macOS)).Select(e => e.Key).ToList();

    /// <summary>某平台的展开页签键列表（共享页签在前，平台专属在后）。</summary>
    public static IReadOnlyList<string> ExpandedTabsFor(bool macOS) =>
        ExpandedTabs
            .Where(e => e.Availability == CapabilityAvailability.Shared)
            .Concat(ExpandedTabs.Where(e =>
                e.Availability == CapabilityAvailability.PlatformSpecific && Available(e, macOS)))
            .Select(e => e.Key)
            .ToList();

    /// <summary>某个能力在指定平台是否可用。</summary>
    public static bool Available(CapabilityEntry entry, bool macOS) =>
        entry.Availability switch
        {
            CapabilityAvailability.Shared => true,
            CapabilityAvailability.PlatformSpecific => entry.Owner switch
            {
                CapabilityOwner.WindowsOnly => !macOS,
                CapabilityOwner.MacOnly => macOS,
                _ => true,
            },
            _ => false,
        };

    /// <summary>清单里全部条目（供文档与测试遍历）。</summary>
    public static IEnumerable<CapabilityEntry> All =>
        CollapsedViews.Concat(ExpandedTabs).Concat(DataSources);
}
