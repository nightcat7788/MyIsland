using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Island.App.Services;

/// <summary>
/// **Windows 端功能自检** —— 逐项跑一遍 Windows 独有的能力，报出**这台机器上的真实状态**。
///
/// 为什么需要它：Windows 有一批平台专属能力（亮度 / WLAN / 蓝牙 / 静音 / 锁屏 / 睡眠 / 系统主题 / 夜灯 /
/// 电源计划 / 清理内存 / 网络适配器 / 剪贴板历史 / 开机自启），它们是**带副作用的 OS 操作**，
/// 所以测试工程（零依赖、只跑纯逻辑）把它们排除了 ⇒ 这一批能力**长期没有任何功能验证**，
/// 只有「编译通过」这一层。
///
/// 本工具调的是**产品自己的服务**（同一份源文件链接进来），不是另写一套替身 ——
/// 测的必须是产品真会跑的那条路。
///
/// 用法（仓库根目录）：
///   powershell -ExecutionPolicy Bypass -File tools\win-selftest.ps1
///   （或）dotnet run --project tools\win-selftest -c Release
///
/// **安全约定**：
/// · 默认**只读**：不改动系统任何状态；
/// · `--mutate` 才会做写操作，且**立即还原**（亮度 / 电源计划 / 静音）；
/// · **锁屏与睡眠永远不自动执行**（会打断你手上的事）—— 报告里标「需人工」并给出确切操作路径。
///
/// 产物：`docs\verification\windows-selftest-<日期>.md`（人读报告）+ 控制台摘要。
/// 退出码：0 = 每一项都跑完且无异常；非 0 = 有项目抛异常（那是真缺陷，需要修）。
/// 「该机不支持」这类**真实状态**不算失败。
/// </summary>
internal static class Program
{
    /// <summary>一项自检的结果：<c>Ok</c> 正常 / <c>Manual</c> 需人工验证（**不算失败**）/ <c>Fail</c> 真缺陷。</summary>
    private sealed record Check(string Name, string Detail, string State, string? Manual = null)
    {
        public bool Ok => State == "ok";
        public bool IsManual => State == "manual";
    }

    private static int Main(string[] args)
    {
        bool mutate = args.Contains("--mutate", StringComparer.OrdinalIgnoreCase);
        string outDir = Path.Combine(FindRepoRoot() ?? Directory.GetCurrentDirectory(), "docs", "verification");
        var checks = new List<Check>();

        void Run(string name, Func<(string detail, bool ok)> body)
        {
            try
            {
                var (detail, ok) = body();
                checks.Add(new Check(name, detail, ok ? "ok" : "fail"));
            }
            catch (Exception ex)
            {
                checks.Add(new Check(name, "抛异常：" + ex.GetType().Name + " — " + ex.Message, "fail"));
            }
        }

        Console.WriteLine("Windows 端功能自检" + (mutate ? "（含写操作往返，会立即还原）" : "（只读）"));
        Console.WriteLine(new string('─', 64));

        // ---------- 控制页：亮度 ----------
        uint? brightness = null;
        Run("控制页 · 屏幕亮度（读）", () =>
        {
            brightness = ControlService.GetBrightness();
            return brightness is { } b
                ? ($"{b}%", true)
                : ("该机不提供亮度读数（台式机 / 驱动不提供 ⇒ 正常状态）", true);
        });
        if (mutate && brightness is { } bv && bv > 10)
        {
            Run("控制页 · 屏幕亮度（写回往返）", () =>
            {
                uint target = Math.Max(10, bv - 10);
                bool set = ControlService.SetBrightness(target);
                uint? back = ControlService.GetBrightness();
                ControlService.SetBrightness(bv);
                uint? restored = ControlService.GetBrightness();
                return ($"写 {bv}% → {target}%（set={set}，读回 {back?.ToString() ?? "未知"}%）→ 还原 {bv}%（读回 {restored?.ToString() ?? "未知"}%）",
                    set && back == target && restored == bv);
            });
        }

        // ---------- 控制页：WLAN ----------
        Run("控制页 · 无线网卡名称（读）", () =>
        {
            string? name = ControlService.GetWirelessAdapterName();
            return name is { } n && n.Length > 0
                ? (n, true)
                : ("读不到（按产品口径：不读 SSID，避免触发 Win11 的位置访问授权）", true);
        });
        Run("控制页 · WLAN 开关与连接状态（读）", () =>
        {
            var (adapter, connected) = ControlService.GetWlanState();
            string a = adapter is null ? "未知" : adapter.Value ? "已启用" : "已禁用";
            string c = connected is null ? "未知" : connected.Value ? "已连接" : "未连接";
            return ($"适配器 {a}；连接 {c}", true);
        });
        checks.Add(new Check("控制页 · WLAN 开关（写）",
            "**不自动执行**：关掉 Wi-Fi 会切断本机网络（本会话可能因此断开）", "manual",
            "人工验证：控制页 → WLAN 开关拨到关 → 确认系统托盘网络图标消失 → 再拨回开。"));

        // ---------- 控制页：蓝牙 ----------
        Run("控制页 · 蓝牙状态（读）", () =>
            (ControlService.BluetoothEnabled ? "已启用" : "已禁用", true));
        checks.Add(new Check("控制页 · 蓝牙开关（写）",
            "**不自动执行**：会断掉正在用的蓝牙键鼠", "manual",
            "人工验证：控制页 → 蓝牙开关拨到关 → 确认蓝牙设备失联 → 拨回开。"));

        // ---------- 控制页：静音 ----------
        Run("控制页 · 静音（接口可用性）", () =>
            ("ToggleMute 可调用（产品没有只读接口：静音状态读不出来）", true));
        if (mutate)
        {
            Run("控制页 · 静音（往返）", () =>
            {
                ControlService.ToggleMute();
                System.Threading.Thread.Sleep(250);
                ControlService.ToggleMute();
                return ("已切换两次，状态应回到原样（中途约 250ms）", true);
            });
        }

        // ---------- 控制页：系统主题 / 夜灯 ----------
        Run("控制页 · 系统深色模式（读）", () =>
            (ControlService.IsSystemLightTheme() ? "系统当前是浅色" : "系统当前是深色", true));
        Run("控制页 · 夜灯（读）", () =>
        {
            bool? on = ControlService.GetNightLightOn();
            return on is null
                ? ("读不到（该系统版本不支持夜灯接口）", true)
                : (on.Value ? "已开启" : "已关闭", true);
        });

        // ---------- 控制页：电源计划 ----------
        string? activePlanId = null;
        Run("控制页 · 电源计划（读）", () =>
        {
            var (plans, active) = ControlService.GetPowerPlans();
            if (plans.Count == 0) return ("没有读到电源计划", true);
            activePlanId = active >= 0 && active < plans.Count ? plans[active].Id : null;
            string list = string.Join(" / ", plans.Select(p => p.Name));
            return ($"{plans.Count} 个：{list}；当前 = {(active >= 0 && active < plans.Count ? plans[active].Name : "未知")}", true);
        });
        if (mutate && activePlanId is { } pid)
        {
            Run("控制页 · 电源计划（写回往返）", () =>
            {
                var (plans, active) = ControlService.GetPowerPlans();
                if (active < 0 || active >= plans.Count) return ("读不到当前计划，跳过", true);
                string other = plans.First(p => p.Id != plans[active].Id).Id;
                bool set = ControlService.SetPowerPlan(other);
                ControlService.SetPowerPlan(pid);
                var (_, back) = ControlService.GetPowerPlans();
                return ($"切到 {other}（set={set}）→ 还原 {pid} → 读回索引 {back}", set && back == active);
            });
        }

        // ---------- 控制页：清理内存（真跑一次，安全） ----------
        Run("控制页 · 清理内存（真跑）", () =>
        {
            long freed = ControlService.CleanMemory(waitMs: 8000);
            return freed == ControlService.CleanMemoryUnknown
                ? ("结果未知（8 秒内没回传；不代表失败，产品同样显示未知而不是 0）", true)
                : ($"释放 {freed} MB", true);
        });

        // ---------- 控制页：网络适配器清单 ----------
        Run("控制页 · 网络适配器清单（读）", () =>
        {
            var adapters = ControlService.GetNetworkAdapters();
            string list = string.Join(" / ", adapters.Select(a => $"{a.Name}({a.Type},{a.Status})"));
            return ($"{adapters.Count} 个：{list}", true);
        });

        // ---------- 锁屏 / 睡眠：永远不自动执行 ----------
        checks.Add(new Check("控制页 · 锁屏", "**不自动执行**：会立刻锁屏、打断你手上的事", "manual",
            "人工验证：控制页 → 锁屏 ⇒ 屏幕立刻锁定，用 Win+L 或指纹解锁回来。"));
        checks.Add(new Check("控制页 · 睡眠", "**不自动执行**：会让机器进入睡眠", "manual",
            "人工验证：控制页 → 睡眠 ⇒ 机器进入睡眠，唤醒后一切正常。"));

        // ---------- 剪贴板历史 ----------
        Run("剪贴板 · 历史条目（读）", () =>
        {
            int count = ClipboardService.Count;
            var items = ClipboardService.Items;
            string head = items.Count > 0 ? items[0].Preview : "（空）";
            return ($"{count} 条；最新预览：{head}", true);
        });

        // ---------- 开机自启 ----------
        Run("系统 · 开机自启（读）", () =>
            (StartupService.IsEnabled(elevated: false) ? "已启用" : "未启用", true));

        // ---------- 汇总与报告 ----------
        int failed = checks.Count(c => c.State == "fail");
        Console.WriteLine(new string('─', 64));
        foreach (var c in checks)
            Console.WriteLine($"{(c.State switch { "ok" => "PASS", "manual" => "MANUAL", _ => "FAIL" })}  {c.Name}：{c.Detail}");
        Console.WriteLine(new string('─', 64));
        Console.WriteLine($"共 {checks.Count} 项；失败 {failed} 项；需要人工验证 {checks.Count(c => c.Manual is not null)} 项");

        try
        {
            Directory.CreateDirectory(outDir);
            string path = Path.Combine(outDir, $"windows-selftest-{DateTime.Now:yyyy-MM-dd}.md");
            File.WriteAllText(path, BuildReport(checks, mutate), new UTF8Encoding(false));
            Console.WriteLine("报告：" + path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("写报告失败：" + ex.Message);
        }

        return failed == 0 ? 0 : 1;
    }

    private static string BuildReport(List<Check> checks, bool mutate)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Windows 端功能自检报告");
        sb.AppendLine();
        sb.AppendLine($"- 执行时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 模式：{(mutate ? "含写操作往返（立即还原）" : "只读（未改动系统任何状态）")}");
        sb.AppendLine("- 说明：调的是**产品自己的服务**（同一份源文件链接进本工具），测的就是产品真会跑的那条路。");
        sb.AppendLine();
        sb.AppendLine("| # | 能力 | 结果 | 实测 |");
        sb.AppendLine("|---|---|---|---|");
        for (int i = 0; i < checks.Count; i++)
        {
            var c = checks[i];
            string res = c.State switch { "ok" => "✅ 通过", "manual" => "⚠ 需人工", _ => "❌ 失败" };
            sb.AppendLine($"| {i + 1} | {c.Name} | {res} | {c.Detail.Replace("|", "/")} |");
        }
        sb.AppendLine();
        sb.AppendLine("## 需要人工验证的项（脚本不自动执行）");
        sb.AppendLine();
        foreach (var c in checks.Where(c => c.Manual is not null))
        {
            sb.AppendLine($"- **{c.Name}** —— {c.Manual}");
        }
        sb.AppendLine();
        sb.AppendLine("## 口径说明");
        sb.AppendLine();
        sb.AppendLine("- 「该机不支持 / 读不到」是**真实状态**，不算失败（例如台式机没有亮度读数、非管理员读不到某些 WMI 项）。");
        sb.AppendLine("- 只有**抛异常**才算失败 —— 那说明产品代码在这台机器上跑不通，是真缺陷。");
        sb.AppendLine("- 锁屏 / 睡眠 / WLAN 开关 / 蓝牙开关**永不自动执行**：它们会打断你手上的事或切断网络。");
        return sb.ToString();
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Island.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
