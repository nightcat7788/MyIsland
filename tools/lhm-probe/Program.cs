using System.Security.Principal;
using Island.App.Services;
using LibreHardwareMonitor.Hardware;

/// <summary>
/// CPU 温度侦察工具（开发期专用）：打印 LHM 看到的全部 CPU 温度传感器，
/// 再用生产规则 CpuTemperatureRule 选一个，比对"选中的到底是哪个传感器"。
///
/// 用法：lhm-probe
/// ⚠️ 读 MSR 温度需要 PawnIO 驱动 + 管理员权限；没权限时会看到传感器读不到（值为空），
///    这属于"读不到"，不是缺陷 —— 生产代码此时会显示 "--"。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // 可选：把输出写进文件（提权启动时用得上 —— 提权进程的 stdout 不好接回来）
        if (args.Length > 0)
        {
            try
            {
                var writer = new StreamWriter(args[0], false, new System.Text.UTF8Encoding(false))
                {
                    AutoFlush = true,   // 进程退出时 finalizer 不保证落盘，必须自动 flush
                };
                Console.SetOut(writer);
                Console.SetError(writer);
            }
            catch (Exception ex)
            {
                Console.WriteLine("无法写入输出文件：" + ex.Message);
                return 2;
            }
        }
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
        Console.WriteLine($"管理员权限：{(admin ? "有" : "无（读 MSR 温度多半读不到）")}");

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = false,
            IsMotherboardEnabled = false,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
            IsPsuEnabled = false,
        };
        try { computer.Open(); } catch (Exception ex)
        {
            Console.WriteLine("打开失败：" + ex.Message);
            return 1;
        }

        try
        {
            foreach (var hw in computer.Hardware)
            {
                if (hw.HardwareType != HardwareType.Cpu) continue;
                hw.Update();
                Console.WriteLine($"CPU 硬件：{hw.Name}");
                var candidates = new List<CpuTemperatureCandidate>();
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Temperature) continue;
                    string value = s.Value.HasValue ? s.Value.Value.ToString("F1") : "(空)";
                    Console.WriteLine($"  {s.Name,-34} = {value}");
                    if (s.Value.HasValue) candidates.Add(new CpuTemperatureCandidate(s.Name, s.Value.Value));
                }
                var pick = CpuTemperatureRule.Pick(candidates);
                Console.WriteLine(pick is { } p
                    ? $"=> 选中：{p.SensorName} = {p.Value:F1}（来源 {p.Source}）"
                    : "=> 选中：无（读数未知，界面显示 --）");
            }
        }
        finally
        {
            try { computer.Close(); } catch { }
        }
        return 0;
    }
}
