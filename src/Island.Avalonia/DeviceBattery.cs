using System.Diagnostics;

namespace Island.Avalonia;

/// <summary>
/// 设备电量（对齐 BrimDeck 的「设备电量」页）：蓝牙 / USB 设备 + 内置电池。
/// 数据来自随包探针 <c>island-devices</c>（IOKit 读 AppleDeviceManagementHIDEventService /
/// AppleSmartBattery）。读不到就没有，绝不编造数值。
/// </summary>
public static class DeviceBattery
{
    /// <summary>一个设备的电量。</summary>
    public sealed record Device(string Kind, string Name, int Percent, bool Charging, string Id)
    {
        /// <summary>电量档位：正常 / 偏低 / 低（对齐 BrimDeck 的两档阈值）。</summary>
        public string Level => Percent <= 20 ? "low" : Percent <= 40 ? "warn" : "ok";
    }

    /// <summary>读一次设备电量。探针缺失或出错返回空列表。</summary>
    public static List<Device> Read()
    {
        var list = new List<Device>();
        try
        {
            string? tool = Locate("island-devices");
            if (tool is null) return list;
            var psi = new ProcessStartInfo(tool)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return list;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { /* 失败降级：保留默认值/上次值 */ } }
            string text = outTask.Wait(500) ? outTask.Result : "";
            errTask.Wait(100);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split('\t');
                if (f.Length < 3) continue;
                if (!int.TryParse(f[2], out int pct)) continue;
                if (pct < 0 || pct > 100) continue;
                list.Add(new Device(
                    Kind: f[0].Trim(),
                    Name: f[1].Trim(),
                    Percent: pct,
                    Charging: f.Length > 3 && f[3].Trim() == "1",
                    Id: f.Length > 4 ? f[4].Trim() : f[1].Trim()));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[devices] {ex.GetType().Name}: {ex.Message}");
        }
        return list;
    }

    private static string? Locate(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }
}
