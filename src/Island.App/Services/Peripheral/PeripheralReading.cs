namespace Island.App.Services.Peripheral;

/// <summary>
/// 发命令的传输方式。同一条命令可能只认其中一种端点，三种都要试
/// （参考 2.4G 外设电量的通用做法：控制端点回 True 但设备不理、只有中断 OUT 才回）。
/// </summary>
public enum PeripheralTransport
{
    /// <summary>控制端点：HidD_SetOutputReport。</summary>
    Control = 0,

    /// <summary>中断 OUT 端点：WriteFile 整包。</summary>
    Interrupt = 1,

    /// <summary>Feature 报告：HidD_SetFeature。</summary>
    Feature = 2,
}

/// <summary>
/// 协议表解析出来的电量读数（纯数据，单元测试可以直接构造）。
/// </summary>
/// <param name="Percent">电量百分比，0..100。</param>
/// <param name="Charging">是否在充电。系统没给明确充电位时为 false（不猜）。</param>
/// <param name="MilliVolts">电池电压（毫伏）；协议没报就是 null。</param>
public sealed record PeripheralReading(int Percent, bool Charging, int? MilliVolts = null);
