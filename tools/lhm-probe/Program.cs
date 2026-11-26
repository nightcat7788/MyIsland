using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Island.App.Services;
using Island.Native;
using LibreHardwareMonitor.Hardware;

/// <summary>
/// 硬件传感器侦察工具（开发期专用）：
///   · 默认模式：打印 LHM 看到的全部 CPU 温度传感器，再用生产规则 CpuTemperatureRule 选一个，
///     比对"选中的到底是哪个传感器"。
///   · matrix 模式（2026-11-26 task-13 新增）：按阶段 F 验证矩阵的需要，一次性把
///     CPU + GPU + 主板的传感器快照采成 JSON（stdout / 可选文件），供
///     tools\verify-matrix.ps1 逐轴做专项检查。
///
/// 用法：lhm-probe
///       lhm-probe matrix [输出文件.json]
/// ⚠️ 读 MSR 温度需要 PawnIO 驱动 + 管理员权限；没权限时会看到传感器读不到（值为空），
///    这属于"读不到"，不是缺陷 —— 生产代码此时会显示 "--"。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // matrix 模式：阶段 F 验证矩阵的传感器快照（JSON）
        if (args.Length > 0 && string.Equals(args[0], "matrix", StringComparison.OrdinalIgnoreCase))
            return RunMatrix(args.Length > 1 ? args[1] : null);

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

    // ============================================================ matrix 模式

    /// <summary>
    /// matrix 模式：把阶段 F 验证矩阵要用的传感器快照一次性采出来，输出 JSON。
    ///
    /// 为什么要有这个模式（2026-11-26 task-13）：
    ///   · 默认模式只开 CPU（IsGpuEnabled=false / IsMotherboardEnabled=false），
    ///     拿不到 F2 要的"主板 / SuperIO 温度条目"，也拿不到 F3 要的"显存 / 每块 GPU 的温度"；
    ///   · 这里按**产品 ThermalService.OpenComputer 的同一组开关**开：CPU + GPU + 主板开，
    ///     存储/内存/控制器/网络/PSU 关（存储开关在本机触发过 0x1EA 蓝屏，见 ThermalService 的注释）；
    ///   · 取值一律走产品的规则类（CpuTemperatureRule / HardwareSensorRules）；
    ///     显卡名称与占用口径直接跑 Island.Native 的 CounterSampleCache（产品那份代码）——
    ///     **不自己另发明规则**；
    ///   · 读不到的传感器**照列不误**（值为 null）："有条目但读不到"与"根本没有条目"是两回事，
    ///     报告里必须分开写。
    /// </summary>
    private static int RunMatrix(string? outFile)
    {
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

        var result = new MatrixDto
        {
            Tool = "lhm-probe matrix",
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Admin = admin,
        };

        // 与产品同一套拦停：只有核显的机器上不让库初始化 Intel GCL（否则一次 700MB–4.5GB 的提交内存尖峰）。
        try
        {
            if (IntelComputePolicy.ShouldSkipOnThisMachine()) IntelComputePolicy.Disable();
        }
        catch { /* 拦不成就退回默认行为，与产品一致 */ }

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
            IsPsuEnabled = false,
        };
        try
        {
            computer.Open();
            result.ComputerOpened = true;
        }
        catch (Exception ex)
        {
            result.ComputerOpened = false;
            result.OpenError = ex.GetType().Name + ": " + ex.Message;
        }

        if (result.ComputerOpened)
        {
            try
            {
                CollectComputer(computer, result);
            }
            catch (Exception ex)
            {
                result.CollectError = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                try { computer.Close(); } catch { }
            }
        }

        // 显卡名称与占用口径：直接跑产品那份代码（Island.Native\CounterSampleCache）。
        try
        {
            using var cache = new CounterSampleCache();
            result.GpuSelection ??= new GpuSelectionDto();
            result.GpuSelection.DisplayName = cache.Name;
            cache.ReadGpuUsage();                 // 第一轮是基线（速率计数器要两次采集才有值）
            System.Threading.Thread.Sleep(300);
            result.GpuSelection.UsagePercent = Math.Round(cache.ReadGpuUsage(), 2);
            result.GpuSelection.PerAdapter = ReadPerAdapterUsage();
            result.GpuSelection.VideoControllers = VideoControllerNames();
            result.GpuSelection.D3DAdapters = ReadD3DAdapters();
        }
        catch (Exception ex)
        {
            result.GpuSelection ??= new GpuSelectionDto();
            result.GpuSelection.Error = ex.GetType().Name + ": " + ex.Message;
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        string json = JsonSerializer.Serialize(result, options);
        if (!string.IsNullOrEmpty(outFile))
        {
            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outFile, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("matrix 输出文件写不了：" + ex.Message);
                return 3;
            }
        }
        Console.WriteLine(json);
        return 0;
    }

    /// <summary>按产品的枚举口径把一块硬件的传感器收进快照。</summary>
    private static void CollectComputer(Computer computer, MatrixDto result)
    {
        foreach (IHardware hw in computer.Hardware)
        {
            hw.Update();
            foreach (IHardware sub in hw.SubHardware) sub.Update();

            if (hw.HardwareType == HardwareType.Cpu)
            {
                result.Cpu ??= new CpuDto { HardwareName = hw.Name };
                result.Cpu.TemperatureSensors = Sensors(hw, SensorType.Temperature);
                result.Cpu.PowerSensors = Sensors(hw, SensorType.Power);
                result.Cpu.FanSensors = Sensors(hw, SensorType.Fan);

                // ① 产品运行期用的口径：HardwareSensorRules.Pick(CpuTemperature, ...)
                var productPick = HardwareSensorRules.Pick(
                    HardwareSensorKind.CpuTemperature, Collect(hw, SensorType.Temperature));
                result.Cpu.ProductPick = PickOf(productPick?.SensorName, productPick?.Value, "HardwareSensorRules");

                // ② 独立规则类（lhm-probe 一直用的那套，分层口径与产品一致）
                var candidates = new List<CpuTemperatureCandidate>();
                foreach (HardwareSensor s in Collect(hw, SensorType.Temperature))
                    candidates.Add(new CpuTemperatureCandidate(s.Name, s.Value));
                var rulePick = CpuTemperatureRule.Pick(candidates);
                result.Cpu.RulePick = PickOf(rulePick?.SensorName, rulePick?.Value,
                    rulePick is null ? null : rulePick.Source.ToString());

                // ③ CCD 传感器（F1 要的那一格）：只列名字里有 ccd 的温度条目，读不到也列出来
                foreach (SensorDto s in result.Cpu.TemperatureSensors)
                    if (s.Name.Contains("ccd", StringComparison.OrdinalIgnoreCase))
                        result.Cpu.CcdSensors.Add(s);
            }
            else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                var gpu = new GpuDto { HardwareName = hw.Name, HardwareType = hw.HardwareType.ToString() };
                gpu.TemperatureSensors = Sensors(hw, SensorType.Temperature);
                gpu.PowerSensors = Sensors(hw, SensorType.Power);
                gpu.FanSensors = Sensors(hw, SensorType.Fan);
                gpu.SmallDataSensors = Sensors(hw, SensorType.SmallData);
                gpu.ClockSensors = Sensors(hw, SensorType.Clock);

                gpu.TemperaturePick = PickOf(
                    HardwareSensorRules.Pick(HardwareSensorKind.GpuTemperature, Collect(hw, SensorType.Temperature)));
                gpu.PowerPick = PickOf(
                    HardwareSensorRules.Pick(HardwareSensorKind.GpuPower, Collect(hw, SensorType.Power)));
                gpu.MemoryUsedPick = PickOf(
                    HardwareSensorRules.Pick(HardwareSensorKind.GpuMemoryUsed, Collect(hw, SensorType.SmallData)));
                gpu.MemoryTotalPick = PickOf(
                    HardwareSensorRules.Pick(HardwareSensorKind.GpuMemoryTotal, Collect(hw, SensorType.SmallData)));
                gpu.ClockPick = PickOf(
                    HardwareSensorRules.Pick(HardwareSensorKind.GpuClock, Collect(hw, SensorType.Clock)));

                result.Gpus.Add(gpu);
            }
            else if (hw.HardwareType == HardwareType.Motherboard)
            {
                var mb = new MotherboardDto { HardwareName = hw.Name };
                mb.TemperatureSensors = Sensors(hw, SensorType.Temperature);
                var temps = new List<HardwareSensor>();
                foreach (IHardware sub in hw.SubHardware)
                {
                    var subDto = new SubHardwareDto { Name = sub.Name };
                    subDto.TemperatureSensors = Sensors(sub, SensorType.Temperature);
                    subDto.FanSensors = Sensors(sub, SensorType.Fan);
                    mb.SubHardware.Add(subDto);
                    temps.AddRange(Collect(sub, SensorType.Temperature));
                }
                mb.TemperaturePick = PickOf(HardwareSensorRules.Pick(HardwareSensorKind.MotherboardTemperature, temps));
                result.Motherboard = mb;
            }
        }

        // 多 GPU 的温度来源：产品 ThermalService 的口径 —— 优先"正在耗电"的那块（独显），
        // 其次取先遇到的那块。0W 是合法读数（空闲显卡），所以判据是 p > 0。
        result.ThermalGpuChoice = null;
        double? pickedTemp = null, pickedPower = null;
        foreach (GpuDto gpu in result.Gpus)
        {
            double? t = gpu.TemperaturePick?.Value;
            double? p = gpu.PowerPick?.Value;
            if (t is null) continue;
            bool thisActive = p is not null && p > 0;
            bool currentActive = pickedPower is not null && pickedPower > 0;
            if (pickedTemp is null || (thisActive && !currentActive))
            {
                pickedTemp = t;
                pickedPower = p;
                result.ThermalGpuChoice = new ThermalGpuChoiceDto
                {
                    HardwareName = gpu.HardwareName,
                    Temperature = t,
                    Power = p,
                    Rule = "优先选功耗>0 的那块（独显），否则取先遇到的那块 —— ThermalService.SampleCore 的口径",
                };
            }
        }
    }

    /// <summary>
    /// 一块硬件上某种类型的传感器（**读不到的照列**，值为 null）。
    /// </summary>
    private static List<SensorDto> Sensors(IHardware hw, SensorType type)
    {
        var list = new List<SensorDto>();
        foreach (ISensor s in hw.Sensors)
        {
            if (s.SensorType != type) continue;
            list.Add(new SensorDto { Name = s.Name, Value = s.Value });
        }
        return list;
    }

    /// <summary>
    /// 收"某种类型 + 读数有效"的候选（给规则类挑代表值用）——
    /// 口径与产品 ThermalService.Collect / IsValidReading 完全一致。
    /// </summary>
    private static List<HardwareSensor> Collect(IHardware hw, SensorType type)
    {
        var list = new List<HardwareSensor>();
        foreach (ISensor s in hw.Sensors)
            if (s.SensorType == type && IsValidReading(type, s.Value))
                list.Add(new HardwareSensor(s.Name, s.Value!.Value));
        return list;
    }

    /// <summary>
    /// 读数是否可信（与产品 ThermalService.IsValidReading 一致）：
    /// 风扇/功耗接受 0（停转 / 空闲是真实读数），其它必须 &gt; 0.01；
    /// 无论哪种都必须有值 —— 没有读数与读数为 0 是两回事。
    /// </summary>
    private static bool IsValidReading(SensorType type, double? value)
    {
        if (!value.HasValue) return false;
        if (type is SensorType.Fan or SensorType.Power) return value.Value >= 0;
        return value.Value > 0.01;
    }

    private static PickDto? PickOf(HardwareSensorPick? pick)
        => pick is null ? null : new PickDto { SensorName = pick.SensorName, Value = pick.Value };

    private static PickDto? PickOf(string? sensorName, double? value, string? source)
        => sensorName is null ? null : new PickDto { SensorName = sensorName, Value = value, Source = source };

    /// <summary>
    /// 每适配器的 3D 占用：按产品同一套实例名解析口径（PdhWildcardQuery.GroupKeyOf）
    /// 把同一 (适配器, 引擎) 上各进程的份额相加，多引擎之间取最大。
    /// 应用本身**只显示全体的最忙引擎值**（CounterSampleCache.ReadGpuUsage），这个是按适配器拆开的。
    /// </summary>
    private static List<AdapterUsageDto> ReadPerAdapterUsage()
    {
        var result = new List<AdapterUsageDto>();
        try
        {
            using var query = new PdhWildcardQuery();
            if (!query.Open(@"\GPU Engine(*)\Utilization Percentage")) return result;
            // 速率计数器要两次采集才有值：先采一轮当基线，隔一小段再采一轮当读数
            // （产品里是每 2 秒采一轮，间隔更长；这里 300 ms 足够拿到可用的相对值）。
            query.ReadAll();
            System.Threading.Thread.Sleep(300);
            List<(string Name, double Value)> instances = query.ReadAll();
            var perAdapter = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
            foreach ((string name, double value) in instances)
            {
                if (name.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string key = PdhWildcardQuery.GroupKeyOf(name);
                int at = key.IndexOf('@');
                if (at <= 0) continue;
                string adapter = key.Substring(0, at);
                string engine = key.Substring(at + 1);
                if (!perAdapter.TryGetValue(adapter, out Dictionary<string, double>? engines))
                {
                    engines = new Dictionary<string, double>(StringComparer.Ordinal);
                    perAdapter[adapter] = engines;
                }
                engines.TryGetValue(engine, out double sum);
                engines[engine] = sum + value;
            }
            foreach (KeyValuePair<string, Dictionary<string, double>> kv in perAdapter)
            {
                double best = 0;
                foreach (double v in kv.Value.Values) if (v > best) best = v;
                result.Add(new AdapterUsageDto
                {
                    AdapterKey = kv.Key,
                    Percent = Math.Round(Math.Clamp(best, 0, 100), 2),
                    Engines = kv.Value.Count,
                });
            }
        }
        catch { /* 读不到就当没有分适配器数据 */ }
        return result;
    }

    /// <summary>
    /// 显卡的"核显 / 独显"属性 —— 走库自己的 D3D 探测（产品 IntelComputePolicy 用的同一条路）。
    ///
    /// 为什么需要：WMI 的 Win32_VideoController 只有名字，分不出核显与独显
    ///（本机的 "Intel(R) Arc(TM) 130T GPU (16GB)" 就是核显，名字里却没有 "Graphics"），
    /// 而阶段 F 的 F3 轴（核显 + 独显）恰恰要靠这个判定。
    /// 库里的 D3DDisplayDevice 能给出驱动上报的 Integrated 标志与专用显存大小，
    /// 产品判"是不是只有核显"用的就是它（见 IntelComputePolicy 的说明）。
    ///
    /// 用反射按名字取字段：库升级后字段名对不上就只是取不到，绝不崩、绝不瞎猜。
    /// </summary>
    private static List<D3DAdapterDto> ReadD3DAdapters()
    {
        var list = new List<D3DAdapterDto>();
        try
        {
            var library = Type.GetType("LibreHardwareMonitor.Hardware.Computer, LibreHardwareMonitorLib", false)?.Assembly;
            if (library is null) return list;
            var d3d = library.GetType("LibreHardwareMonitor.Hardware.D3DDisplayDevice", false);
            var getIdentifiers = d3d?.GetMethod("GetDeviceIdentifiers",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var getInfo = d3d?.GetMethod("GetDeviceInfoByIdentifier",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (getIdentifiers is null || getInfo is null) return list;
            if (getIdentifiers.Invoke(null, null) is not string[] identifiers) return list;
            foreach (string? id in identifiers)
            {
                if (id is null) continue;
                var dto = new D3DAdapterDto { Id = id, IsSoftware = id.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase) < 0 };
                try
                {
                    var args = new object?[] { id, null };
                    dto.Ok = getInfo.Invoke(null, args) is true;
                    object? info = args[1];
                    if (info is not null)
                    {
                        // 注意：要在返回对象的**实际类型**上找字段（参数类型是按引用的）
                        var t = info.GetType();
                        dto.Name = (t.GetField("Description")?.GetValue(info) as string)
                            ?? (t.GetProperty("Description")?.GetValue(info) as string) ?? "";
                        dto.Integrated = t.GetField("Integrated")?.GetValue(info) as bool?;
                        dto.DedicatedVideoMemory = t.GetField("DedicatedVideoMemory")?.GetValue(info) as long?;
                        dto.SharedUsage = t.GetField("SharedUsage")?.GetValue(info) as long?;
                    }
                }
                catch { /* 问不出来就留空，不猜 */ }
                list.Add(dto);
            }
        }
        catch { /* D3D 探测不可用就当没有这份信息 */ }
        return list;
    }

    /// <summary>显卡清单（WMI）—— 与产品 GuessGpuName 同一个数据源，用来核对它跳过了谁。</summary>
    private static List<string> VideoControllerNames()
    {
        var names = new List<string>();
        try
        {
            using var search = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            foreach (System.Management.ManagementObject obj in search.Get())
            {
                string? n = obj["Name"]?.ToString();
                if (!string.IsNullOrEmpty(n)) names.Add(n);
            }
        }
        catch { /* WMI 不可用就没有清单 */ }
        return names;
    }

    // ---------------------------------------------------------------- DTO

    private sealed class MatrixDto
    {
        public string Tool { get; set; } = "";
        public string GeneratedAt { get; set; } = "";
        public bool Admin { get; set; }
        public bool ComputerOpened { get; set; }
        public string? OpenError { get; set; }
        public string? CollectError { get; set; }
        public CpuDto? Cpu { get; set; }
        public List<GpuDto> Gpus { get; } = new();
        public MotherboardDto? Motherboard { get; set; }
        public ThermalGpuChoiceDto? ThermalGpuChoice { get; set; }
        public GpuSelectionDto? GpuSelection { get; set; }
    }

    private sealed class SensorDto
    {
        public string Name { get; set; } = "";
        public double? Value { get; set; }
    }

    private sealed class PickDto
    {
        public string? SensorName { get; set; }
        public double? Value { get; set; }
        public string? Source { get; set; }
    }

    private sealed class CpuDto
    {
        public string HardwareName { get; set; } = "";
        public List<SensorDto> TemperatureSensors { get; set; } = new();
        public List<SensorDto> PowerSensors { get; set; } = new();
        public List<SensorDto> FanSensors { get; set; } = new();
        public PickDto? ProductPick { get; set; }
        public PickDto? RulePick { get; set; }
        public List<SensorDto> CcdSensors { get; } = new();
    }

    private sealed class GpuDto
    {
        public string HardwareName { get; set; } = "";
        public string HardwareType { get; set; } = "";
        public List<SensorDto> TemperatureSensors { get; set; } = new();
        public List<SensorDto> PowerSensors { get; set; } = new();
        public List<SensorDto> FanSensors { get; set; } = new();
        public List<SensorDto> SmallDataSensors { get; set; } = new();
        public List<SensorDto> ClockSensors { get; set; } = new();
        public PickDto? TemperaturePick { get; set; }
        public PickDto? PowerPick { get; set; }
        public PickDto? MemoryUsedPick { get; set; }
        public PickDto? MemoryTotalPick { get; set; }
        public PickDto? ClockPick { get; set; }
    }

    private sealed class MotherboardDto
    {
        public string HardwareName { get; set; } = "";
        public List<SensorDto> TemperatureSensors { get; set; } = new();
        public List<SubHardwareDto> SubHardware { get; } = new();
        public PickDto? TemperaturePick { get; set; }
    }

    private sealed class SubHardwareDto
    {
        public string Name { get; set; } = "";
        public List<SensorDto> TemperatureSensors { get; set; } = new();
        public List<SensorDto> FanSensors { get; set; } = new();
    }

    private sealed class ThermalGpuChoiceDto
    {
        public string HardwareName { get; set; } = "";
        public double? Temperature { get; set; }
        public double? Power { get; set; }
        public string Rule { get; set; } = "";
    }

    private sealed class GpuSelectionDto
    {
        public string DisplayName { get; set; } = "";
        public double UsagePercent { get; set; }
        public List<AdapterUsageDto> PerAdapter { get; set; } = new();
        public List<string> VideoControllers { get; set; } = new();
        public List<D3DAdapterDto> D3DAdapters { get; set; } = new();
        public string? Error { get; set; }
    }

    /// <summary>一块 D3D 适配器（核显/独显判定用）。</summary>
    private sealed class D3DAdapterDto
    {
        public string Id { get; set; } = "";
        public bool Ok { get; set; }
        public string Name { get; set; } = "";
        public bool? Integrated { get; set; }
        public long? DedicatedVideoMemory { get; set; }
        public long? SharedUsage { get; set; }
        /// <summary>软件适配器（虚拟显示器 / BasicRender）—— 不是真显卡，判定核显独显时必须排除。</summary>
        public bool IsSoftware { get; set; }
    }

    private sealed class AdapterUsageDto
    {
        public string AdapterKey { get; set; } = "";
        public double Percent { get; set; }
        public int Engines { get; set; }
    }
}
