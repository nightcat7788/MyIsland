namespace Island.App.Services.Peripheral;

/// <summary>
/// L1：标准 HID 电量。按 HID Usage Tables 的电量 usage 读，**不猜字节偏移**
/// （用 HidP_GetUsageValue 让系统按报告描述符解析）。
///
/// 只认这几组作为百分比来源：
///  · 0x06:0x20  Generic Device Controls / Battery Strength
///  · 0x0D:0x3B  Digitizer / Battery Strength（部分触控笔）
///  · 0x85:0x0065 Battery / Battery Percentage
/// 明确**不**当百分比用的：
///  · 0x85:0x0001 Battery System Status（位域，不是百分比）
///  · 0x85:0x0066 Battery Time Remaining（时间，不是百分比）
/// </summary>
public static class StandardHidBattery
{
    /// <summary>可作为百分比来源的 usage 组合。</summary>
    public static readonly IReadOnlyList<(ushort Page, ushort Usage)> PercentUsages =
        new (ushort, ushort)[]
        {
            (0x0006, 0x0020),
            (0x000D, 0x003B),
            (0x0085, 0x0065),
        };

    /// <summary>在集合的能力表里找覆盖目标 usage 的 value caps。</summary>
    public static HidUsageRange? FindUsage(HidInterface it, ushort page, ushort usage)
    {
        if (it is null) return null;
        foreach (var u in it.FeatureUsages) if (u.Covers(page, usage)) return u;
        foreach (var u in it.InputUsages) if (u.Covers(page, usage)) return u;
        return null;
    }

    /// <summary>
    /// 按逻辑范围换算百分比。越界或范围非法一律返回 null（未知），
    /// 绝不用 0 或默认值充数。
    /// </summary>
    public static int? ToPercent(int raw, int logicalMin, int logicalMax)
    {
        if (logicalMax <= logicalMin) return null;
        if (raw < logicalMin || raw > logicalMax) return null;
        long span = logicalMax - logicalMin;
        int percent = (int)((raw - logicalMin) * 100L / span);
        if (percent < 0 || percent > 100) return null;
        return percent;
    }
}
