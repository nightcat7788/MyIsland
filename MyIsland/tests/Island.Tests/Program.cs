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

try
{
    SettingsTests.Run(Check);
    AnimationGuardTests.Run(Check);
    ThermalChannelTests.Run(Check);
    SmnChannelTests.Run(Check);
    ThemeTests.Run(Check);
    DesignSystemTests.Run(Check);   // 2026-11-24 视觉重做：等宽数字 / 对比度 / 坏数值防线
    MonitorModelTests.Run(Check);
    GpuPdhTests.Run(Check);
    GlassBackdropTests.Run(Check);
    MemoryReclaimTests.Run(Check);
    IntelComputePolicyTests.Run(Check);
    PawnIoInstallerTests.Run(Check);
}
catch (Exception ex)
{
    failures.Add("harness: " + ex.Message);
}

if (failures.Count > 0)
{
    Console.WriteLine($"总计 {passed + failures.Count} 条：通过 {passed} 条，失败 {failures.Count} 条");
    foreach (var f in failures) Console.WriteLine("  × " + f);
    Environment.ExitCode = 1;
    return;
}

Console.WriteLine($"全部通过：{passed} 条");
