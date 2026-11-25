namespace Island.Core.Monitoring;

/// <summary>
/// 采样需求位标志（借鉴 BrimDeck ExpandedDemand）：UI 当前实际画了什么，采样器才读什么。
/// 收起态胶囊只有 CPU/内存/网速/磁盘/温度/电量，就不读进程榜、GPU；屏幕页/剪贴板页
/// 看不到监控页，也不读进程榜。"没选中的不读"省掉的是子进程、IOKit 查询和 DDC 总线占用。
/// </summary>
[Flags]
public enum SamplerDemand : int
{
    None = 0,
    Cpu = 1,
    Memory = 2,
    Network = 4,
    Disk = 8,
    Temperatures = 16,
    Battery = 32,
    Gpu = 64,
    Processes = 128,
    CpuFrequency = 256,
    Uptime = 512,

    /// <summary>收起态胶囊显示的全部项。</summary>
    Collapsed = Cpu | Memory | Network | Disk | Temperatures | Battery,
    /// <summary>信息胶囊（combo）额外需要 GPU——它要画 GPU 表盘。
    /// GPU 采样要起 ioreg 子进程，所以只在用户真的切到 combo 视图时才读。</summary>
    Combo = Cpu | Memory | Network | Disk | Temperatures | Battery | Gpu,
    /// <summary>仪表盘（dash）：占用组里有 GPU 占用率，所以也需要 GPU 采样。
    /// 之前 dash 走的是 Collapsed（不含 Gpu）—— 画了一个从不采样的值，永远显示不出来。</summary>
    Dash = Cpu | Memory | Network | Disk | Temperatures | Battery | Gpu,
    /// <summary>展开到监控页时的全部项。</summary>
    MonitorPage = Collapsed | Gpu | Processes | CpuFrequency | Uptime,
}
