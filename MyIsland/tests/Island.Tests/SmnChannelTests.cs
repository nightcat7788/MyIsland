using Island.App.Services;

namespace Island.Tests;

/// <summary>
/// SMN 高风险通道的回归用例（2026-11-23 三次 0x1EA THREAD_STUCK_IN_DEVICE_DRIVER 蓝屏）。
///
/// 事故链：以管理员运行 → 首次温度采样 → 加载 inpoutx64.dll（它会自动装未签名内核驱动）
///        → MapPhysToLin 映射 PCIe ECAM 物理内存 → 读挂总线 → 蓝屏整机重启。
///
/// 关键点：以前这个判断写在 ThermalService 内部，而测试工程**没有链接** ThermalService.cs
///        ⇒ 门禁根本走不到那段代码 ⇒ 把 "AllowSmnDirectRead &&" 这个条件去掉，
///        测试仍然全绿（假绿）。
///        现在决策收拢到 SmnChannel（无外部依赖，可被测试链接），这里把它钉死。
///
/// 安全说明：本用例**绝不调用** TryReadCpuTemperature —— 那才是真正加载驱动、
///        映射物理内存的动作。仅构造 InpOutSmnService 是惰性的（加载发生在首次读取时），
///        因此本用例在任何机器上都安全。
/// </summary>
internal static class SmnChannelTests
{
    public static void Run(Action<string, bool> check)
    {
        // ---------- ① 默认关闭：绝不构造服务 ----------
        using (var ch = new SmnChannel())
        {
            check("SMN 通道默认关闭", !ch.AllowSmnDirectRead);
            check("未开启时 Ensure() 返回 null（不构造服务）", ch.Ensure() is null);
            check("未开启时不创建任何服务实例", !ch.IsCreated);
            check("未开启时状态文本说明是设置关掉的",
                ch.Status.Contains("disabled", StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < 5; i++) ch.Ensure();
            check("未开启时反复 Ensure() 仍然不构造服务", !ch.IsCreated);
        }

        // ---------- ② 显式开启后才创建 ----------
        var ch2 = new SmnChannel();
        ch2.AllowSmnDirectRead = true;
        var svc = ch2.Ensure();
        check("显式开启后 Ensure() 才返回服务", svc is not null);
        check("显式开启后服务已创建", ch2.IsCreated);
        check("显式开启后 Ensure() 复用同一实例（不重复创建）", ReferenceEquals(svc, ch2.Ensure()));

        // ---------- ③ 关闭开关 ⇒ 立刻释放（映射的物理区/已加载的 dll 不留在进程里） ----------
        ch2.AllowSmnDirectRead = false;
        check("关闭开关后服务立即释放", !ch2.IsCreated);
        check("关闭开关后 Ensure() 再次返回 null", ch2.Ensure() is null);
        check("关闭开关后状态回到未启用",
            ch2.Status.Contains("disabled", StringComparison.OrdinalIgnoreCase));

        // ---------- ④ Dispose 也要回收 ----------
        var ch3 = new SmnChannel { AllowSmnDirectRead = true };
        ch3.Ensure();
        check("Dispose 前服务已创建", ch3.IsCreated);
        ch3.Dispose();
        check("Dispose 后服务已释放", !ch3.IsCreated);
        check("Dispose 后开关回到关闭", !ch3.AllowSmnDirectRead);
    }
}
