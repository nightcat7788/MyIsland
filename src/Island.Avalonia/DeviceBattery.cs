using System.Diagnostics;
using System.Globalization;
using Island.App.Services;
using Island.App.Services.Peripheral;

namespace Island.Avalonia;

/// <summary>
/// 设备电量（对齐 BrimDeck 的「设备电量」页）：蓝牙 / USB HID / 2.4G 接收器 / 本机内置电池。
///
/// 数据来自随包探针 <c>island-devices</c>（IOKit）：两种来源 ——
///   1) 探针直接读到的电池设备（AppleDeviceManagementHIDEventService / IOBluetoothHIDDevice /
///      AppleSmartBattery）—— 蓝牙 HID 与本机内置电池；
///   2) 探针搬运过来的 **HID 原始信息**（HIDDEV / HIDFTR 行）—— USB / 2.4G 接收器：
///      这里只按**与 Windows 端同一份**规则判定（共享层：协议表 + 标准 usage + 识别兜底），
///      读不到就是「未知」，绝不显示 0%。
///
/// 探针只负责"搬运原始字节"，判定逻辑全在共享层 —— 两端口径因此不会漂移。
/// </summary>
public static class DeviceBattery
{
    /// <summary>一个设备的电量。Kind 用共享词表（battery/ble/mouse/keyboard/headset/gamepad/other）。</summary>
    public sealed record Device(string Kind, string Name, int Percent, bool Charging, string Id)
    {
        /// <summary>
        /// 档位或「未知」；null = 显示百分比。
        /// 只识别到设备但读不到电量时是"未知"（与 Windows 端同口径：不拿 0% 充数）。
        /// </summary>
        public string? LevelText { get; init; }
    }

    /// <summary>读一次设备电量。探针缺失或出错返回空列表。</summary>
    public static List<Device> Read()
    {
        var devices = new List<Device>();
        string[] lines = RunProbe();
        if (lines.Length == 0) return devices;

        // 用户自带的协议表（两端同一个文件名、同一份语义）
        PeripheralProtocols.ReloadUserTableFile(Path.Combine(Settings.DataDir, "peripheral-protocols.json"));

        var hids = new List<HidDevice>();
        foreach (string line in lines)
        {
            var f = line.Split('\t');
            if (f.Length == 0) continue;

            if (f[0] == "HIDDEV" && f.Length >= 8)
            {
                hids.Add(new HidDevice
                {
                    Vid = ParseHex(f[1]),
                    Pid = ParseHex(f[2]),
                    UsagePage = (ushort)ParseHex(f[3]),
                    Usage = (ushort)ParseHex(f[4]),
                    Product = f[5],
                    Serial = f[6],
                    Descriptor = ParseHexBytes(f[7]),
                });
                continue;
            }
            if (f[0] == "HIDFTR" && f.Length >= 5)
            {
                int vid = ParseHex(f[1]), pid = ParseHex(f[2]);
                int reportId = int.TryParse(f[3], out int rid) ? rid : 0;
                byte[] bytes = ParseHexBytes(f[4]);
                var target = hids.FirstOrDefault(h => h.Vid == vid && h.Pid == pid);
                if (target is not null) target.Reports[reportId] = bytes;
                continue;
            }

            // 旧格式行：<类型>\t<名称>\t<电量%>\t<充电中>\t<设备ID>
            if (f.Length < 5) continue;
            if (!int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct)) continue;
            if (pct < 0 || pct > 100) continue;       // 探针不该输出非法值，输出也不认
            bool charging = f[3].Trim() == "1";
            // 类型词表与 Windows 端统一（mouse/keyboard/headset/gamepad/battery/ble/other）：
            // 名字认得出具体类型就用具体类型，认不出才回退到"ble"/"other"。
            string kind = f[0] switch
            {
                "内置" => "battery",
                "蓝牙" => KindOr(f[1], "ble"),
                "HID" => KindOr(f[1], "other"),
                _ => "other",
            };
            devices.Add(new Device(kind, f[1].Trim(), pct, charging, f[4].Trim()));
        }

        devices.AddRange(ResolveHidDevices(hids));
        return devices;
    }

    /// <summary>
    /// 对探针搬来的 HID 设备应用**共享规则**（顺序与 Windows 端一致）：
    /// L1 标准 HID 电量 usage → L3 厂商协议表 → L4 只识别不读数（显示「未知」）。
    /// </summary>
    private static IEnumerable<Device> ResolveHidDevices(List<HidDevice> hids)
    {
        foreach (var hid in hids)
        {
            string kind = PeripheralClassifier.KindOf(hid.Product, hid.Product, hid.UsagePage, hid.Usage);
            string fallbackName = hid.Product.Length > 0 ? hid.Product : "HID 设备";

            // ---- L1：标准 HID 电量 usage（报告描述符解析 + 按位取值） ----
            int? standard = ReadStandardBattery(hid);
            if (standard is { } pct1)
            {
                yield return new Device(kind, fallbackName, pct1, false, HidId(hid)) { LevelText = null };
                continue;
            }

            // ---- L3：厂商私有协议（协议表与 Windows 端共用同一份） ----
            var protocol = PeripheralProtocols.Match(hid.Vid, hid.Pid, hid.UsagePage, hid.Usage,
                hid.MaxReportLength);
            if (protocol is not null)
            {
                string name = PeripheralProtocols.DisplayName(protocol, fallbackName);
                if (hid.Reports.TryGetValue(protocol.FeatureReportId, out byte[]? raw)
                    && PeripheralProtocolReader.TryParse(protocol, raw, out var reading) && reading is not null)
                {
                    yield return new Device(kind, name, reading.Percent, reading.Charging, HidId(hid));
                    continue;
                }
                // 协议对上了但这次没读到：如实显示"未知"（不假装没有这台设备）
                yield return new Device(kind, name, 0, false, HidId(hid)) { LevelText = "未知" };
                continue;
            }

            // ---- L4：只识别不读数 ----
            if (!PeripheralClassifier.LooksWireless(hid.Product)) continue;
            yield return new Device(kind, fallbackName, 0, false, HidId(hid)) { LevelText = "未知" };
        }
    }

    /// <summary>
    /// L1：标准 HID 电量 usage。macOS 没有 HidP_GetUsageValue，
    /// 所以按报告描述符自己解析出电量字段，再从 Feature 报告里按位取值。
    /// </summary>
    private static int? ReadStandardBattery(HidDevice hid)
    {
        if (hid.Descriptor.Length == 0) return null;
        var fields = HidReportDescriptor.Parse(hid.Descriptor);
        if (fields.Count == 0) return null;
        foreach (var (page, usage) in StandardHidBattery.PercentUsages)
        {
            var field = HidReportDescriptor.FindFeatureField(fields, page, usage);
            if (field is null) continue;
            if (!hid.Reports.TryGetValue(field.ReportId, out byte[]? report)) continue;
            if (HidReportDescriptor.ReadValue(report, field) is not { } raw) continue;
            int? percent = StandardHidBattery.ToPercent(raw, field.LogicalMin, field.LogicalMax);
            if (percent is null) continue;
            return percent;
        }
        return null;
    }

    private static string HidId(HidDevice hid)
    {
        if (hid.Serial.Length > 0) return $"{hid.Vid:X4}:{hid.Pid:X4}:{hid.Serial}";
        return $"{hid.Vid:X4}:{hid.Pid:X4}:{hid.UsagePage:X4}:{hid.Usage:X4}";
    }

    /// <summary>用共享的分类器猜类型；猜不出具体类型（返回 other）就用 fallback。</summary>
    private static string KindOr(string name, string fallback)
    {
        string kind = PeripheralClassifier.KindOf(name, name, 0, 0);
        return kind == "other" ? fallback : kind;
    }

    private static int ParseHex(string text)
    {
        text = text.Trim();
        return text.Length > 0
            && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? v : 0;
    }

    /// <summary>十六进制字符串 → 字节数组（容忍空格与分隔符）。</summary>
    private static byte[] ParseHexBytes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<byte>();
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
            if (Uri.IsHexDigit(c)) sb.Append(c);
        if (sb.Length % 2 != 0) return Array.Empty<byte>();
        var bytes = new byte[sb.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(sb.ToString(i * 2, 2), 16);
        return bytes;
    }

    /// <summary>探针搬运过来的一台 HID 设备（原始信息，判定在共享层）。</summary>
    private sealed class HidDevice
    {
        public int Vid, Pid;
        public ushort UsagePage, Usage;
        public string Product = "", Serial = "";
        public byte[] Descriptor = Array.Empty<byte>();
        public Dictionary<int, byte[]> Reports { get; } = new();

        /// <summary>Feature 报告的最大长度（协议表按报告长度做匹配，用它近似）。</summary>
        public int MaxReportLength
        {
            get
            {
                int max = 0;
                foreach (var r in Reports.Values) if (r.Length > max) max = r.Length;
                return max;
            }
        }
    }

    /// <summary>跑探针，拿到它的输出行。探针缺失/出错返回空数组。</summary>
    private static string[] RunProbe()
    {
        string? tool = Locate("island-devices");
        if (tool is null) return Array.Empty<string>();
        var psi = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return Array.Empty<string>();
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { } }
        string text = outTask.Wait(500) ? outTask.Result : "";
        errTask.Wait(100);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? Locate(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }
}
