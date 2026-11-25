namespace Island.App.Services;

/// <summary>
/// 探针用的 DeviceService 替身：生产的 DeviceService 带 System.Management / WinRT 依赖，
/// 进不了这个零包依赖的轻量工程（同 tools/hid-probe 的 DeviceServiceShim 做法）。
///
/// 这里给的是**合成的设备列表**：探针的渲染结果必须确定、可做回归基线，
/// 读真硬件会让"这台机器上有几台蓝牙设备"变成 PNG 的输入。
/// </summary>
internal static class DeviceService
{
    /// <summary>合成设备列表（鼠标 / 键盘 / 耳机 / 手柄 / 本机电池）。</summary>
    public static List<DeviceInfo> Snapshot { get; } = new()
    {
        new DeviceInfo("ThinkBook 蓝牙鼠标", "mouse", 59, false),
        new DeviceInfo("罗技 K380", "keyboard", 88, false),
        new DeviceInfo("WH-1000XM5", "headset", 42, true),
        new DeviceInfo("Xbox 手柄", "gamepad", 0, false, "中"),
        new DeviceInfo("本机电池", "battery", 100, false),
    };
}
