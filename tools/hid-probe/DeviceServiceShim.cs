namespace Island.App.Services;

/// <summary>
/// 探针工具里的 DeviceService 替身：生产的 PeripheralBatteryService 只用到它的诊断日志入口，
/// 而真正的 DeviceService 带着 System.Management 依赖，进不了这个轻量工程。
/// </summary>
internal static class DeviceService
{
    public static void LogLine(string text) => Console.WriteLine("[log] " + text);

    public static void LogLineThrottled(string text, TimeSpan cooldown) => Console.WriteLine("[log] " + text);
}
