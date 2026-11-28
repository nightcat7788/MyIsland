using System.IO;
using System.Text;
using Island.Tests;

// 控制台用 UTF-8 输出（中文用例名在非 UTF-8 终端上也能正确显示）
try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 某些精简终端不支持切换编码 */ }

// 自研 harness：逐条打印 PASS/FAIL
// ⚠️ 失败不抛异常：抛出会把后面的用例全跳过，门禁只报首个失败、掩盖真实缺陷面。
// 这里记录失败并继续跑完，最后统一以非 0 退出码收口。
var passed = 0;
var failures = new List<string>();

void Check(string name, bool condition)
{
    if (!condition)
    {
        failures.Add(name);
        Console.WriteLine("FAIL: " + name);
        return;
    }
    Console.WriteLine("PASS: " + name);
    passed++;
}

void RunSuite(string suiteName, Action run)
{
    try
    {
        run();
    }
    catch (Exception ex)
    {
        string msg = $"{suiteName} 抛出未捕获异常: {ex.GetType().Name}: {ex.Message}";
        failures.Add(msg);
        Console.WriteLine("FAIL: " + msg);
    }
}

RunSuite(nameof(SettingsTests), () => SettingsTests.Run(Check));
RunSuite(nameof(WeatherTests), () => WeatherTests.Run(Check));
RunSuite(nameof(ClipboardTests), () => ClipboardTests.Run(Check));
RunSuite(nameof(HotKeyTests), () => HotKeyTests.Run(Check));
RunSuite(nameof(SamplerGuardTests), () => SamplerGuardTests.Run(Check));
RunSuite(nameof(AnimationGuardTests), () => AnimationGuardTests.Run(Check));
RunSuite(nameof(PawnIoTests), () => PawnIoTests.Run(Check));
RunSuite(nameof(ThermalSensorGuardTests), () => ThermalSensorGuardTests.Run(Check));
RunSuite(nameof(HidPeripheralProtocolTests), () => HidPeripheralProtocolTests.Run(Check));
RunSuite(nameof(ThemeTests), () => ThemeTests.Run(Check));
RunSuite(nameof(DisplayPrefsTests), () => DisplayPrefsTests.Run(Check));
RunSuite(nameof(MonitorModelTests), () => MonitorModelTests.Run(Check));
RunSuite(nameof(GpuPdhTests), () => GpuPdhTests.Run(Check));
RunSuite(nameof(ScreenContextTests), () => ScreenContextTests.Run(Check));

if (failures.Count > 0)
{
    Console.WriteLine($"总计 {passed + failures.Count} 条：通过 {passed} 条，失败 {failures.Count} 条");
    foreach (var f in failures) Console.WriteLine("  × " + f);
    Environment.ExitCode = 1;
    return;
}

Console.WriteLine($"全部通过：{passed} 条");
