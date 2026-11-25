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
    ClipboardTests.Run(Check);
    SettingsTests.Run(Check);
    AnimationGuardTests.Run(Check);
    ThermalChannelTests.Run(Check);
    SmnChannelTests.Run(Check);
    DeviceBatteryTests.Run(Check);  // 2026-11-25 设备电量增强：档位语义 / 提醒设置
    PeripheralBatteryTests.Run(Check); // 2026-11-26 Windows 外设电量：协议解析三层校验 / 协议表匹配 / 标准 HID 换算 / 识别兜底
    CpuTemperatureTests.Run(Check); // 2026-11-26 CPU 温度取值：分层匹配 + 排除 Distance to TjMax 等干扰项
    HardwareSensorRulesTests.Run(Check); // 2026-11-26 硬件传感器规则：名字→语义 + 分层裁决 + 干扰项排除
    ExpandedPanelSpecTests.Run(Check); // 2026-11-26 阶段 2：展开面板规格（以 mac 现有实现为基准）
    HidDescriptorTests.Run(Check); // 2026-11-26 mac 对齐：HID 报告描述符解析（mac 没有 HidP_GetUsageValue） // 2026-11-26 CPU 温度取值：分层匹配 + 排除 Distance to TjMax 等干扰项
    ThemeTests.Run(Check);
    DesignSystemTests.Run(Check);   // 2026-11-24 视觉重做：等宽数字 / 对比度 / 坏数值防线
    MonitorModelTests.Run(Check);
    GpuPdhTests.Run(Check);
    GlassBackdropTests.Run(Check);
    MemoryReclaimTests.Run(Check);
    IntelComputePolicyTests.Run(Check);
    PawnIoInstallerTests.Run(Check);
    CompactSpecTests.Run(Check);   // 2026-11-25 收起态对齐：规格常量与宽度公式锁死
    CompactLayoutTests.Run(Check); // 2026-11-25 验收：收起态尺寸表不得超出宿主窗口宽 640
    CapabilityManifestTests.Run(Check); // 2026-11-26 阶段 4/5：能力清单漂移检查（清单改了、两端没跟着改 ⇒ 红）
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
