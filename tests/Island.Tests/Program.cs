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

    // 一个测试类里抛异常时，**不能**连累后面的门禁一起不跑。
    // 2026-11-27 实测：一条用例的表达式抛异常 => 其后 65 条门禁被静默跳过，
    // 只看"全部通过：N 条"会以为门禁变少了，而不是变红了。所以每个测试类单独 try/catch。
    void Suite(string name, Action<Action<string, bool>> run)
    {
        try { run(Check); }
        catch (Exception ex)
        {
            string msg = name + "：抛出异常 " + ex.GetType().Name + ": " + ex.Message;
            failures.Add(msg);
            Console.WriteLine("FAIL: " + msg);
        }
    }
    Suite("ClipboardTests", ClipboardTests.Run);
    Suite("SettingsTests", SettingsTests.Run);
    Suite("AnimationGuardTests", AnimationGuardTests.Run);
    Suite("ThermalChannelTests", ThermalChannelTests.Run);
    Suite("SmnChannelTests", SmnChannelTests.Run);
    Suite("DeviceBatteryTests", DeviceBatteryTests.Run);  // 2026-11-25 设备电量增强：档位语义 / 提醒设置
    Suite("PeripheralBatteryTests", PeripheralBatteryTests.Run); // 2026-11-26 Windows 外设电量：协议解析三层校验 / 协议表匹配 / 标准 HID 换算 / 识别兜底
    Suite("PeripheralProtocolTableTests", PeripheralProtocolTableTests.Run); // 2026-11-26 task2：外设协议表自检（表合法性校验 / 覆盖规则 / 字段级 JSON 报错）
    Suite("CpuTemperatureTests", CpuTemperatureTests.Run); // 2026-11-26 CPU 温度取值：分层匹配 + 排除 Distance to TjMax 等干扰项
    Suite("HardwareSensorRulesTests", HardwareSensorRulesTests.Run);
    Suite("HardwareSensorListTests", HardwareSensorListTests.Run); // 2026-11-26 task4 硬件传感器列表：单位 / 格式 / 状态词（读不到≠0）+ 名字→语义映射
    Suite("HardwareSensorsViewTests", HardwareSensorsViewTests.Run); // 2026-11-26 task4 硬件详情面板：离屏渲染 + 读数位 + 刷新不重建 // 2026-11-26 硬件传感器规则：名字→语义 + 分层裁决 + 干扰项排除
    Suite("ExpandedPanelSpecTests", ExpandedPanelSpecTests.Run); // 2026-11-26 阶段 2：展开面板规格（以 mac 现有实现为基准）
    Suite("HidDescriptorTests", HidDescriptorTests.Run); // 2026-11-26 mac 对齐：HID 报告描述符解析（mac 没有 HidP_GetUsageValue） // 2026-11-26 CPU 温度取值：分层匹配 + 排除 Distance to TjMax 等干扰项
    Suite("ThemeTests", ThemeTests.Run);
    Suite("DesignSystemTests", DesignSystemTests.Run);   // 2026-11-24 视觉重做：等宽数字 / 对比度 / 坏数值防线
    Suite("MonitorModelTests", MonitorModelTests.Run);
    Suite("GpuPdhTests", GpuPdhTests.Run);
    Suite("GlassBackdropTests", GlassBackdropTests.Run);
    Suite("MemoryReclaimTests", MemoryReclaimTests.Run);
    Suite("IntelComputePolicyTests", IntelComputePolicyTests.Run);
    Suite("PawnIoInstallerTests", PawnIoInstallerTests.Run);
    Suite("CompactSpecTests", CompactSpecTests.Run);   // 2026-11-25 收起态对齐：规格常量与宽度公式锁死
    Suite("CompactLayoutTests", CompactLayoutTests.Run); // 2026-11-25 验收：收起态尺寸表不得超出宿主窗口宽 640
    Suite("CapabilityManifestTests", CapabilityManifestTests.Run); // 2026-11-26 阶段 4/5：能力清单漂移检查（清单改了、两端没跟着改 ⇒ 红）
    Suite("MonitorStatusLineTests", MonitorStatusLineTests.Run); // 2026-11-26 E2：底部状态行 —— Windows 端字段位/渲染/「读不到⇒未知」
    Suite("MonitorStatusLineMacTests", MonitorStatusLineMacTests.Run); // 2026-11-26 E2：底部状态行 —— macOS 端字段位与格式化规则
    Suite("GpuMemoryKindTests", GpuMemoryKindTests.Run); // 2026-11-26 task15：GPU 显存读数口径（厂商/专用/共享/未知）与显示形状
    Suite("MonitorPageLayoutTests", MonitorPageLayoutTests.Run); // 2026-11-26 task16：监控页卡片分组（两端共享规格 MonitorPageLayoutSpec）
    Suite("MonitorPageLayoutMacTests", MonitorPageLayoutMacTests.Run); // 2026-11-26 task16：macOS 端监控页分组（规格过滤 + 源码绘制顺序）
    Suite("ThemeTokenTests", ThemeTokenTests.Run); // 2026-11-26 task9：主题 token 语义清单漂移检查（两端同名 token 必须指同一语义角色）
    Suite("ThemeTokenUsageTests", ThemeTokenUsageTests.Run); // 2026-11-26 task14：跨端语义 token 用法漂移检查（同一个界面元素在两端必须用同一个语义 token）
    Suite("ThemeContrastTests", ThemeContrastTests.Run); // 2026-11-26：主题可读性门禁（macOS 端此前一条对比度断言都没有）+ 两端主题名集合一致
    Suite("ClockViewTests", ClockViewTests.Run); // 2026-11-26：时钟视图内容对齐（两端都画 秒 + 日期，格式同一份 ClockTextSpec）
    Suite("ResourceColorTests", ResourceColorTests.Run); // 2026-11-27：按资源配色（色值锁定 ResourceColorSpec + 两端画布必须取同一份规格 + 旧色值不得复活 + 展开面板本轮不接规格）
    Suite("VisualRenderTests", VisualRenderTests.Run); // 2026-11-26 视觉回归探针：离屏渲染 + 像素不变量（防全黑 / 栅格几何 / 文本越界）
    Suite("AvaloniaVisualTests", AvaloniaVisualTests.Run); // 2026-11-26 macOS 端离屏渲染视觉回归：跨端几何一致 + 判定逻辑有牙齿
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
