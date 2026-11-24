using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Island.App.Services;

/// <summary>
/// 系统控制：亮度（WMI MonitorBrightness，外接显示器不支持则忽略）/
/// WLAN 开关（WlanFree…走 WMI MSNdis 或 netsh——不读 SSID 不触发位置）/蓝牙开关（WinRT Radio）。
///wisland 同款约束：不读 Wi-Fi 名称（netsh show interfaces 在 Win11 24H2 触发位置访问）。
/// </summary>
public static class ControlService
{
    // ---------- 亮度 ----------
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr monitor, ref uint min, ref uint cur, ref uint max);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint value);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR { public IntPtr hPhysicalMonitor; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc; }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    private static IntPtr _firstMonitor = IntPtr.Zero;

    private static void EnsureMonitor()
    {
        if (_firstMonitor != IntPtr.Zero) return;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdcMon, ref RECT rectMon, IntPtr dataMon) =>
            {
                _firstMonitor = hMon;
                return false;
            }, IntPtr.Zero);
    }

    /// <summary>当前亮度 0..100（外接显示器/驱动不支持返回 null）。</summary>
    public static uint? GetBrightness()
    {
        try
        {
            EnsureMonitor();
            if (_firstMonitor == IntPtr.Zero) return null;
            uint count = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(_firstMonitor, ref count) || count == 0) return null;
            var mons = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(_firstMonitor, count, mons)) return null;
            uint min = 0, cur = 0, max = 0;
            bool ok = GetMonitorBrightness(mons[0].hPhysicalMonitor, ref min, ref cur, ref max);
            DestroyPhysicalMonitors(count, mons);
            if (!ok || max == min) return null;
            return (cur - min) * 100 / (max - min);
        }
        catch { return null; }
    }

    public static bool SetBrightness(uint percent)
    {
        try
        {
            EnsureMonitor();
            if (_firstMonitor == IntPtr.Zero) return false;
            uint count = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(_firstMonitor, ref count)) return false;
            var mons = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(_firstMonitor, count, mons)) return false;
            uint min = 0, cur = 0, max = 0;
            bool ok = GetMonitorBrightness(mons[0].hPhysicalMonitor, ref min, ref cur, ref max);
            if (ok && max > min)
                ok = SetMonitorBrightness(mons[0].hPhysicalMonitor, min + (max - min) * percent / 100);
            DestroyPhysicalMonitors(count, mons);
            return ok;
        }
        catch { return false; }
    }

    // ---------- WLAN 开关（netsh，仅开关不读名称） ----------
    // 接口名不能硬编码：本机无线接口实测叫 "WLAN"，写死 "Wi-Fi" 会让 netsh 报"找不到接口"，
    // 磁贴点了只弹 UAC 不生效，用户反复点反复弹。所以先枚举网卡找出无线适配器的真实名字再操作。

    /// <summary>当前在用的无线适配器名（NetworkInterfaceType == Wireless80211）。
    /// 本机可能同时挂着多个无线适配器（"本地连接* 2" 这类是历史残留、状态 Down），
    /// 必须挑状态为 Up 的那个，否则 netsh 会切到没人用的残留网卡上，界面状态也不会变。
    /// 一个都没有时返回 null。</summary>
    public static string? GetWirelessAdapterName()
    {
        try
        {
            string? fallback = null;
            foreach (var nic in GetNetworkAdapters())
            {
                if (nic.Type != System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211) continue;
                if (nic.Status == System.Net.NetworkInformation.OperationalStatus.Up) return nic.Name;
                fallback ??= nic.Name;
            }
            return fallback;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 一次 netsh 查询的结果：适配器管理状态（开/关）与连接状态（连/未连）。
    /// 两者都可能为 null（这一列读不到）；外层 null 表示 netsh 整体失败/超时。
    /// </summary>
    private readonly record struct WlanSnapshot(bool? AdapterEnabled, bool? Connected);

    /// <summary>无线状态缓存：UI 每次刷新要读两个值，不缓存就是两次 netsh 进程。</summary>
    private static WlanSnapshot? _wlanCache;
    private static DateTime _wlanCacheAt = DateTime.MinValue;
    private static readonly object _wlanGate = new();
    private const int WlanCacheMs = 1000;

    /// <summary>
    /// 无线适配器开关状态（true=已启用 / false=已禁用 / null=读不到）。
    /// 失败时不再编造：netsh 超时/失败、找不到无线接口一律 null
    /// （旧代码两条失败路径一边返回 false 一边返回 true，界面随机显示开/关）。
    /// </summary>
    public static bool? WlanAdapterEnabled => GetWlanState().AdapterEnabled;

    /// <summary>
    /// 无线连接状态（true=已连接 / false=未连接 / null=读不到）。
    /// 这是 netsh 的"连接状态"列，不是"管理状态"列：
    /// Wi-Fi 开着但没连热点时这里是 false（未连接）。
    /// </summary>
    public static bool? WlanConnected => GetWlanState().Connected;

    /// <summary>一次拿到两个状态（UI 刷新用，省一次 netsh 进程）。</summary>
    public static (bool? AdapterEnabled, bool? Connected) GetWlanState()
    {
        var snap = ReadWlanState();
        return (snap?.AdapterEnabled, snap?.Connected);
    }

    /// <summary>
    /// 兼容旧调用点：无线适配器开关状态（读不到时按"关闭"处理）。
    /// 新代码请用 <see cref="WlanAdapterEnabled"/> / <see cref="WlanConnected"/>：
    /// 那两个是可空值，能区分"关闭"与"读不到"。
    /// </summary>
    public static bool WlanEnabled => WlanAdapterEnabled == true;

    /// <summary>
    /// 读无线状态（1 秒内复用上一次结果）。
    /// netsh 超时/失败/找不到无线接口 → null（未知），不再编造 true/false。
    /// </summary>
    private static WlanSnapshot? ReadWlanState()
    {
        lock (_wlanGate)
        {
            if (_wlanCache.HasValue && (DateTime.Now - _wlanCacheAt).TotalMilliseconds < WlanCacheMs)
                return _wlanCache;
        }
        var snap = QueryWlanState();
        lock (_wlanGate)
        {
            _wlanCache = snap;
            _wlanCacheAt = DateTime.Now;
        }
        return snap;
    }

    /// <summary>真正跑一次 netsh 并解析（失败返回 null = 未知）。</summary>
    private static WlanSnapshot? QueryWlanState()
    {
        try
        {
            string? outp = RunNetsh("interface show interface");
            if (outp is null) return null;
            // netsh 的列顺序随语言/版本不同（中文：接口名在最前；英文：接口名在最后），
            // 所以按"词"定位接口名，再从其余词里判状态
            string? nic = GetWirelessAdapterName();
            foreach (var raw in outp.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;
                if (nic is not null)
                {
                    int idx = Array.FindIndex(tokens,
                        t => string.Equals(t, nic, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                        return ParseState(tokens.Where((_, i) => i != idx));
                    // 接口名里带空格时按整行包含匹配（名字本身不会是状态词，混进去不影响判断）
                    if (line.Contains(nic, StringComparison.OrdinalIgnoreCase))
                        return ParseState(tokens);
                    continue;
                }
                // 兜底：按关键字找无线网卡（老办法）
                if (!line.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("WLAN", StringComparison.OrdinalIgnoreCase)) continue;
                return ParseState(tokens);
            }
            return null;   // 找不到无线接口行：未知，不编造
        }
        catch { return null; }
    }

    /// <summary>从一行 tokens 里分别取"管理状态"和"连接状态"。</summary>
    private static WlanSnapshot ParseState(IEnumerable<string> tokens)
    {
        var list = tokens.ToList();
        return new WlanSnapshot(StateEnabled(list), StateConnected(list));
    }

    /// <summary>开关 WLAN（netsh，需管理员，保留原有提权流程）。
    /// 返回 (是否成功, 提示文案)：找不到无线适配器时不做任何操作，只给提示。</summary>
    public static (bool Ok, string Message) SetWlan(bool enable)
    {
        try
        {
            string? nic = GetWirelessAdapterName();
            if (string.IsNullOrWhiteSpace(nic))
                return (false, "未找到无线适配器，无法开关无线网络");

            var psi = new System.Diagnostics.ProcessStartInfo("netsh",
                $"interface set interface name=\"{nic}\" {(enable ? "enable" : "disable")}")
            { UseShellExecute = true, Verb = "runas", CreateNoWindow = true };   // 需管理员
            System.Diagnostics.Process.Start(psi);
            return (true, enable ? $"已请求开启无线网络（{nic}）" : $"已请求关闭无线网络（{nic}）");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "需要管理员权限才能开关无线网络");   // 用户取消了 UAC
        }
        catch { return (false, "开关无线网络失败"); }
    }

    /// <summary>判断 netsh 接口的"管理状态"（已启用/已禁用、Enabled/Disabled）：
    /// 这才是 netsh set interface enable/disable 改的那个字段——
    /// 无线开着但没连上热点时，状态列是"已断开"而管理状态仍是"已启用"。
    /// 没有这一列时退回连接状态；两列都读不到 → null（未知），不再返回 false 编造"已关闭"。
    /// </summary>
    private static bool? StateEnabled(IEnumerable<string> tokens)
    {
        bool? admin = null, state = null;
        foreach (string t in tokens)
        {
            switch (t.ToLowerInvariant())
            {
                case "已启用": case "enabled": admin = true; break;
                case "已禁用": case "disabled": admin = false; break;
                case "已连接": case "connected": case "up": state = true; break;
                case "已断开": case "已断开连接": case "disconnected": case "down": state = false; break;
            }
        }
        if (admin.HasValue) return admin;
        return state;
    }

    /// <summary>
    /// 判断 netsh 接口的"连接状态"列（已连接 / 已断开连接；Connected / Disconnected）。
    /// ⚠️ 必须整词匹配："disconnected" 里也含 "connected"，
    /// 用 Contains 会把"未连接"判成"已连接"——这正是旧代码把管理状态当连接状态显示的根因。
    /// 读不到这一列 → null（未知）。
    /// </summary>
    private static bool? StateConnected(IEnumerable<string> tokens)
    {
        bool? conn = null;
        foreach (string t in tokens)
        {
            switch (t.ToLowerInvariant())
            {
                case "已断开连接": case "已断开": case "disconnected": case "down": conn = false; break;
                case "已连接": case "connected": case "up": conn = true; break;
            }
        }
        return conn;
    }

    /// <summary>跑一条 netsh 命令并取回输出（带超时）；超时/失败返回 null。
    /// 先启动异步读取再 WaitForExit：同步 ReadToEnd 会把超时架空，管道写满时还会互相死等。</summary>
    private static string? RunNetsh(string args, int timeoutMs = 3000)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.WaitForExit(1000);
                return null;    // 超时：当作没拿到输出
            }
            string outp = outTask.Wait(1000) ? outTask.Result : "";
            _ = errTask.Wait(500) ? errTask.Result : "";   // stderr 只为及时收干管道
            return outp.Length == 0 ? null : outp;
        }
        catch { return null; }
    }

    // ---------- 蓝牙服务（服务级：bthserv） ----------
    /// <summary>
    /// 蓝牙服务（bthserv）是否在运行。
    /// ⚠️ 这不是 Windows 11 的"蓝牙无线电开关"：无线电关掉时 bthserv 照样在跑。
    /// 工程没引用 WinRT 投影，读不到真正的无线电状态，所以这里只报服务状态，
    /// 界面文案也按"蓝牙服务"说，别宣称是无线电开关。
    /// </summary>
    public static bool BluetoothEnabled
    {
        get
        {
            try
            {
                using var svc = new System.ServiceProcess.ServiceController("bthserv");
                return svc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 开关蓝牙服务（bthserv）。返回 (是否成功, 提示文案)。
    /// 以前失败被静默吞掉，用户以为按钮坏了；现在明确回报成功/失败原因。
    /// ⚠️ 停服务要管理员权限，普通权限下会 Access Denied——
    /// 这时引导用户用管理员模式运行，或点"打开蓝牙设置"交给系统设置处理。
    /// </summary>
    public static (bool Ok, string Message) SetBluetooth(bool enable)
    {
        try
        {
            using var svc = new System.ServiceProcess.ServiceController("bthserv");
            bool running = svc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
            if (enable && !running)
            {
                svc.Start();
                return (true, "蓝牙服务已启动");
            }
            if (!enable && running)
            {
                svc.Stop();
                return (true, "蓝牙服务已停止");
            }
            return (true, enable ? "蓝牙服务已在运行" : "蓝牙服务已停止");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "需要管理员权限才能开关蓝牙服务（可改用系统设置）");
        }
        catch (Exception ex)
        {
            return (false, "开关蓝牙服务失败：" + ex.Message);
        }
    }

    public static void OpenBluetoothSettings() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        { FileName = "ms-settings:bluetooth", UseShellExecute = true });

    // ---------- 静音 ----------
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
    private const int WM_APPCOMMAND = 0x0319;
    private const int APPCOMMAND_VOLUME_MUTE = 0x800;
    public static void ToggleMute() =>
        SendMessageW(GetDesktopWindow(), WM_APPCOMMAND, IntPtr.Zero, (IntPtr)(APPCOMMAND_VOLUME_MUTE << 16));
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    // ---------- 锁屏 / 睡眠 ----------
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    /// <summary>立即锁屏。</summary>
    public static void Lock()
    {
        try { LockWorkStation(); } catch { }
    }

    /// <summary>立即睡眠。</summary>
    public static void Sleep()
    {
        try { SetSuspendState(false, true, false); } catch { }
    }

    // ---------- 系统深色模式（HKCU 个人化，免管理员） ----------
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>系统应用是否为浅色模式。</summary>
    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey, false);
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>设置系统应用深/浅色模式（即时生效，无需重启）。</summary>
    public static void SetSystemLightTheme(bool light)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey, true);
            key?.SetValue("AppsUseLightTheme", light ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch { }
    }

    // ---------- 夜灯（夜间模式）：CloudStore 二进制状态位，免管理员 ----------
    // 社区验证布局：REG_BINARY "Data"，偏移 18 = 开关（1 开 / 0 关）
    private const string NightLightKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate";

    /// <summary>夜灯当前状态（null = 读不到/不支持）。</summary>
    public static bool? GetNightLightOn()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NightLightKeyPath, false);
            if (key?.GetValue("Data") is byte[] data && data.Length > 18)
                return data[18] != 0;
        }
        catch { }
        return null;
    }

    /// <summary>设置夜灯开关；返回是否写入成功（不支持返回 false）。</summary>
    public static bool SetNightLight(bool on)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NightLightKeyPath, true);
            if (key?.GetValue("Data") is byte[] data && data.Length > 18)
            {
                byte want = (byte)(on ? 1 : 0);
                if (data[18] != want)
                {
                    data[18] = want;
                    key.SetValue("Data", data, Microsoft.Win32.RegistryValueKind.Binary);
                }
                return true;
            }
        }
        catch { }
        return false;
    }

    // ---------- 系统设置快捷链接 ----------
    /// <summary>打开指定系统设置页（ms-settings: 协议）。</summary>
    // ---------- 电源计划（YASB PowerPlan 同款：powercfg 枚举/切换，免管理员） ----------

    // ---------- 控制台输出编码（powercfg 的中文输出按本机代码页，不是 UTF-8） ----------

    /// <summary>
    /// 本机控制台输出编码（ANSI/OEM 代码页，如 936=GBK）。
    /// powercfg /LIST 走管道时输出的是本机代码页字节，.NET 默认按 UTF-8 解码
    /// → 中文电源计划名（"平衡"）全变乱码。
    /// .NET 8 不内置 GBK：Encoding.GetEncoding(936) 会抛 ArgumentException，
    /// 必须先注册 CodePagesEncodingProvider（见 csproj 里的 System.Text.Encoding.CodePages）。
    /// </summary>
    private static readonly Encoding ConsoleEncoding = ResolveConsoleEncoding();

    /// <summary>取本机 ANSI 代码页：注册表 ACP → 当前 UI 语言的 ANSICodePage → Encoding.Default。</summary>
    private static Encoding ResolveConsoleEncoding()
    {
        // 只注册一次：重复注册无副作用，但放在这里保证后面 GetEncoding(936) 能用
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* 已注册或本机不支持 */ }

        // ① 注册表里的 ANSI 代码页（HKLM\SYSTEM\CurrentControlSet\Control\Nls\CodePage\ACP）
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Nls\CodePage", writable: false);
            if (key?.GetValue("ACP") is string s && int.TryParse(s, out int cp) && cp > 0)
                return Encoding.GetEncoding(cp);
        }
        catch { /* 取不到就退下一档 */ }

        // ② 当前 UI 语言的 ANSI 代码页
        try
        {
            int cp = System.Globalization.CultureInfo.InstalledUICulture.TextInfo.ANSICodePage;
            if (cp > 0) return Encoding.GetEncoding(cp);
        }
        catch { }

        // ③ 兜底
        try { return Encoding.Default; } catch { return Encoding.UTF8; }
    }

    /// <summary>所有电源计划（Guid + 名称）+ 当前激活项索引。</summary>
    public static (List<(string Id, string Name)> Plans, int ActiveIndex) GetPowerPlans()
    {
        var plans = new List<(string Id, string Name)>();
        int active = -1;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powercfg.exe", "/LIST")
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true,
                // 按本机代码页解码，否则"平衡"变成乱码
                StandardOutputEncoding = ConsoleEncoding,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return (plans, active);
            // 先启动异步读取再等退出：同步 ReadToEnd 会把 WaitForExit 的超时架空，
            // 管道被写满时还会互相死等（同 RunNetsh 的做法）。
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.WaitForExit(1000);
            }
            string output = outTask.Wait(1000) ? outTask.Result : "";
            // 行格式：电源方案 GUID: xxxx  (平衡) *   —— 中英文系统都兼容
            var re = new System.Text.RegularExpressions.Regex(
                @"GUID:\s*([0-9a-fA-F-]{36})\s*\(([^)]*)\)\s*(\*)?");
            foreach (System.Text.RegularExpressions.Match m in re.Matches(output))
            {
                plans.Add((m.Groups[1].Value, m.Groups[2].Value.Trim()));
                if (m.Groups[3].Success && m.Groups[3].Value == "*")
                    active = plans.Count - 1;
            }
        }
        catch { }
        return (plans, active);
    }

    public static bool SetPowerPlan(string guid)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powercfg.exe", $"/SETACTIVE {guid}")
            { CreateNoWindow = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    // ---------- 内存清理（LiteMonitor 同款：本进程 GC + 尽量清空各进程工作集） ----------

    /// <summary>
    /// 清理结果"未知"的哨兵值：超时（后台还没跑完）或任务失败时返回它。
    /// UI 必须把它和 0 区分开：0 = "清完了但内存压力本来就不高"，
    /// CleanMemoryUnknown = "还在进行/结果未知"，不能报成"已清理（内存压力本就不高）"。
    /// </summary>
    public const long CleanMemoryUnknown = long.MinValue;

    /// <summary>
    /// 立即清理内存（同步兼容版，签名与旧调用点一致）。
    /// 重的部分（扫全系统进程 + EmptyWorkingSet）搬到后台线程执行，UI 线程最多等 waitMs 毫秒取结果；
    /// 超时不算失败，后台继续跑完，结果经 onDone 回调回传。
    /// 返回值：&gt;= 0 = 实际释放的可用内存增量（MB）；<see cref="CleanMemoryUnknown"/> = 结果未知（超时/失败）。
    /// 建议宿主改调 <see cref="CleanMemoryAsync()"/>（不阻塞 UI）。
    /// </summary>
    public static long CleanMemory(Action<long>? onDone = null, int waitMs = 1500)
    {
        Task<long> task = CleanMemoryAsync();
        if (onDone is not null)
        {
            // 给了回调就完全不等：结果回来再通知（回调在后台线程，调用方需自行切回 UI 线程）
            _ = task.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion) onDone(t.Result);
            }, TaskScheduler.Default);
            return CleanMemoryUnknown;   // 结果还没出来：未知，不是 0
        }
        try
        {
            // 超时不再返回 0 冒充"没清到"：后台其实清完了，返回 0 会让界面误报"内存压力本就不高"
            if (!task.Wait(waitMs)) return CleanMemoryUnknown;
            return task.Result;
        }
        catch { return CleanMemoryUnknown; }
    }

    /// <summary>立即清理内存（async 版，不阻塞 UI）：返回释放的可用内存增量（MB，可能为 0）。
    /// 宿主可改调 async 版本。</summary>
    public static Task<long> CleanMemoryAsync() => Task.Run(RunMemoryCleanup);

    /// <summary>
    /// 立即清理内存（async + 回调版）：跑完后在完成线程回调实际释放的 MB（不会返回哨兵值）。
    /// 回调在后台线程，调用方需自行切回 UI 线程再更新界面。
    /// </summary>
    public static async Task CleanMemoryAsync(Action<long> onDone)
    {
        long freed = await CleanMemoryAsync().ConfigureAwait(false);
        onDone?.Invoke(freed);
    }

    /// <summary>真正的清理动作（跑在后台线程）：本进程 GC + 尽量清空各进程工作集。</summary>
    private static long RunMemoryCleanup()
    {
        long before = AvailableMB();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try { EmptyWorkingSet(p.Handle); } catch { /* 系统进程无权限：跳过 */ }
            finally { p.Dispose(); }
        }
        return Math.Max(0, AvailableMB() - before);
    }

    private static long AvailableMB()
    {
        try
        {
            using var c = new System.Diagnostics.PerformanceCounter("Memory", "Available MBytes", true);
            return (long)c.NextValue();
        }
        catch { return 0; }
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    // ---------- 网卡枚举（设置中心手动选网卡用） ----------

    /// <summary>本机物理网卡列表（含接口类型与状态：WLAN 开关靠它认出当前在用的无线适配器）。</summary>
    public static List<(string Id, string Name, System.Net.NetworkInformation.NetworkInterfaceType Type, System.Net.NetworkInformation.OperationalStatus Status)> GetNetworkAdapters()
    {
        var list = new List<(string, string, System.Net.NetworkInformation.NetworkInterfaceType, System.Net.NetworkInformation.OperationalStatus)>
        {
            ("", "自动（聚合所有物理网卡）",
                System.Net.NetworkInformation.NetworkInterfaceType.Unknown,
                System.Net.NetworkInformation.OperationalStatus.Unknown)
        };
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                list.Add((nic.Id, nic.Name, nic.NetworkInterfaceType, nic.OperationalStatus));
            }
        }
        catch { }
        return list;
    }

    public static void OpenSettings(string page)
    {
        try
        {
            string uri = page switch
            {
                "network" => "ms-settings:network",
                "sound" => "ms-settings:sound",
                "display" => "ms-settings:display",
                "power" => "ms-settings:powersleep",
                "nightlight" => "ms-settings:nightlight",
                _ => "ms-settings:",
            };
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = uri, UseShellExecute = true });
        }
        catch { }
    }
}
