namespace Island.App.Services.Peripheral;

/// <summary>
/// HID 报告描述符里的一条 value caps（usage 范围 + 逻辑取值范围 + 报告 ID）。
/// 逻辑范围是"标准 HID 电量"的换算依据：LogicalMin/LogicalMax 通常就是 0..100。
/// </summary>
public sealed record HidUsageRange(
    ushort UsagePage,
    ushort UsageMin,
    ushort UsageMax,
    byte ReportId,
    bool IsRange,
    int LogicalMin,
    int LogicalMax)
{
    /// <summary>这个 usage 范围里是否覆盖了目标 usage。</summary>
    public bool Covers(ushort page, ushort usage)
        => UsagePage == page && (usage >= UsageMin && usage <= UsageMax);
}

/// <summary>
/// 一个 HID 顶层集合（一条接口路径）的描述。
/// 一台 2.4G 接收器通常对应多个集合（键鼠主集合 + 厂商控制集合），它们共享 VID/PID。
/// </summary>
public sealed class HidInterface
{
    /// <summary>设备接口路径（\\?\hid#...），打开设备用。</summary>
    public string Path { get; init; } = "";

    /// <summary>实例 ID（USB\\VID_xxxx&PID_xxxx\...），用于查注册表与父设备。</summary>
    public string InstanceId { get; init; } = "";

    /// <summary>设备描述（注册表 DeviceDesc / FriendlyName）。</summary>
    public string DeviceDesc { get; init; } = "";

    /// <summary>产品名（HID 属性 Product String）。</summary>
    public string Product { get; init; } = "";

    /// <summary>厂商名（HID 属性 Manufacturer String）。</summary>
    public string Manufacturer { get; init; } = "";

    /// <summary>序列号（部分接收器是占位值，如联想的 0123456789ABCDEF）。</summary>
    public string Serial { get; init; } = "";

    public int Vid { get; init; }
    public int Pid { get; init; }

    /// <summary>顶层 usage page（厂商自定义集合通常是 0xFFxx）。</summary>
    public ushort UsagePage { get; init; }

    /// <summary>顶层 usage。</summary>
    public ushort Usage { get; init; }

    /// <summary>Input 报告总长（含报告 ID 字节）。</summary>
    public int InputReportLength { get; init; }

    /// <summary>Output 报告总长（含报告 ID 字节）。</summary>
    public int OutputReportLength { get; init; }

    /// <summary>Feature 报告总长（含报告 ID 字节）。</summary>
    public int FeatureReportLength { get; init; }

    /// <summary>Feature 报告里的 value caps（标准电量的 usage 一般在这里）。</summary>
    public List<HidUsageRange> FeatureUsages { get; } = new();

    /// <summary>Input 报告里的 value caps（有些设备周期上报电量）。</summary>
    public List<HidUsageRange> InputUsages { get; } = new();

    /// <summary>是否是厂商自定义集合（Usage page ≥ 0xFF00）—— 私有协议通常只在这里。</summary>
    public bool IsVendorDefined => UsagePage >= 0xFF00;

    /// <summary>能不能发命令（有 Output 或 Feature 报告长度才谈得上）。</summary>
    public bool CanSend => OutputReportLength > 0 || FeatureReportLength > 0;

    /// <summary>显示名：产品名优先，其次设备描述。</summary>
    public string DisplayName =>
        Product.Length > 0 ? Product : DeviceDesc.Length > 0 ? DeviceDesc : "HID 设备";

    /// <summary>集合标识（用于日志与协议表匹配）：VID:PID / usage page:usage。</summary>
    public string Key => $"{Vid:X4}:{Pid:X4}/{UsagePage:X4}:{Usage:X4}";

    /// <summary>value caps 里出现过的报告 ID（去重排序）。</summary>
    public IReadOnlyList<byte> ReportIds
    {
        get
        {
            var ids = new SortedSet<byte>();
            foreach (var u in FeatureUsages) ids.Add(u.ReportId);
            foreach (var u in InputUsages) ids.Add(u.ReportId);
            return new List<byte>(ids);
        }
    }
}
