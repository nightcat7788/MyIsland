using System.IO;
using System.Text.Json.Nodes;
using Island.App.Services;

namespace Island.Tests;

/// <summary>
/// 设置持久化用例：全部落在 %TEMP% 下的临时目录（Settings.DirectoryOverride），
/// 绝不读写真实用户配置 %LOCALAPPDATA%\MyIsland\settings.json。
/// </summary>
internal static class SettingsTests
{
    /// <summary>临时根目录（本轮用例共用）。</summary>
    private static string _root = "";

    public static void Run(Action<string, bool> check)
    {
        _root = Path.Combine(Path.GetTempPath(), "Island-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        string? saved = Settings.DirectoryOverride;
        try
        {
            Defaults(check);
            check("高温告警不被音量或低电量覆盖", !Settings.CanReplaceActivity("temp", false, "volume")
                && !Settings.CanReplaceActivity("temp", false, "battery"));
            check("高温可以抢占音量和低电量", Settings.CanReplaceActivity("volume", false, "temp")
                && Settings.CanReplaceActivity("battery", false, "temp"));
            check("已过期活动和同类活动可以替换", Settings.CanReplaceActivity("temp", true, "volume")
                && Settings.CanReplaceActivity("volume", false, "volume"));
            RoundTrip(check);
            UnreadableIsolated(check);
            UnknownFieldsKept(check);
            MissingFieldsFallBack(check);
            DebouncedSave(check);
            ImmediateSave(check);
            AtomicWrite(check);
            ConcurrentSave(check);
        }
        finally
        {
            Settings.DirectoryOverride = saved;
            try { Directory.Delete(_root, true); } catch { /* 临时目录残留不影响运行 */ }
        }
    }

    /// <summary>切到一个新的临时子目录（每个场景互不干扰）。</summary>
    private static string NewDir()
    {
        string dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Settings.DirectoryOverride = dir;
        return dir;
    }

    private static JsonObject ReadJson() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Settings.FilePath))!;

    private static void Defaults(Action<string, bool> check)
    {
        var s = new Settings();
        check("默认设置的主题/边缘/视图是出厂值",
            s.Theme == "Dark" && s.Edge == "top" && s.CompactView == "dash"
            && s.ExpandMode == "hover" && s.PetStyle == "classic");
        // combo 默认组合：时间 + 读数表盘 + CPU/GPU 温度 + 硬盘占用 + 网速
        // （环组已经画了 C/M/G 占用率，所以 cpu/gpu/mem 三个数字项默认不勾）
        var combo = s.ComboItems.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        check("默认 combo 显示项含时间/表盘/温度/硬盘/网速",
            combo.Contains("clock") && combo.Contains("dials") && combo.Contains("cputemp")
            && combo.Contains("gputemp") && combo.Contains("disk") && combo.Contains("net"));
        check("默认 combo 显示项不与表盘组重复摆 CPU/GPU/内存数字",
            !combo.Contains("cpu") && !combo.Contains("gpu") && !combo.Contains("mem"));
        // AMD SMN 直读会安装未签名内核驱动 + 映射 PCIe 物理内存（2026-11-23 三次 0x1EA 蓝屏），默认必须关
        check("默认不启用 AMD SMN 直读（高风险通道默认关闭）", !s.EnableSmnDirectRead);
        check("默认设置的开关与阈值是出厂值",
            s.AlwaysOnTop && s.HideFullscreen && s.PetAutoCycle && s.IndicatorOnMaximize
            && !s.AlwaysVisible && !s.ClickThrough && s.TempAlertEnabled
            && s.TempAlertThreshold == 88 && s.MonitorIntervalMs == 1000);
    }

    private static void RoundTrip(Action<string, bool> check)
    {
        NewDir();
        var s = new Settings
        {
            Theme = "Dracula", Edge = "left", PosAlongEdge = 0.25, ExpandMode = "always",
            CompactView = "monitor", PetStyle = "cat", AlwaysOnTop = false, HideFullscreen = false,
            ClickThrough = true, MonitorIntervalMs = 500, TempAlertThreshold = 95,
            City = "杭州", PreferredNetwork = "nic-42", DashItems = "cores,mem", ComboItems = "rings",
        };
        s.Save();
        check("保存后文件存在且是合法 JSON 对象",
            File.Exists(Settings.FilePath) && ReadJson() is not null);
        var loaded = Settings.Load();
        check("设置保存后可原样读回",
            loaded.Theme == "Dracula" && loaded.Edge == "left" && Math.Abs(loaded.PosAlongEdge - 0.25) < 1e-9
            && loaded.ExpandMode == "always" && loaded.CompactView == "monitor" && loaded.PetStyle == "cat"
            && !loaded.AlwaysOnTop && !loaded.HideFullscreen && loaded.ClickThrough
            && loaded.MonitorIntervalMs == 500 && loaded.TempAlertThreshold == 95
            && loaded.City == "杭州" && loaded.PreferredNetwork == "nic-42"
            && loaded.DashItems == "cores,mem" && loaded.ComboItems == "rings");

        // 新增设置必须覆盖完整的保存-加载闭环：低电量提醒与 SMN 首启标记不能重启失忆。
        var safety = new Settings
        {
            BatteryAlertEnabled = false,
            BatteryAlertThreshold = 37,
            SmnAutoDone = true,
            EnableSmnDirectRead = false,
        };
        safety.Save();
        var safetyLoaded = Settings.Load();
        check("新增安全设置保存后可原样读回",
            !safetyLoaded.BatteryAlertEnabled
            && safetyLoaded.BatteryAlertThreshold == 37
            && safetyLoaded.SmnAutoDone
            && !safetyLoaded.EnableSmnDirectRead);
    }

    private static void UnreadableIsolated(Action<string, bool> check)
    {
        string dir = NewDir();
        string broken = "{ \"Theme\": \"Dark\", ";   // 被截断的 JSON
        File.WriteAllText(Settings.FilePath, broken);
        var loaded = Settings.Load();
        var copies = Directory.GetFiles(dir, "settings.unreadable-*.json");
        check("损坏的设置文件被另存为 unreadable 副本",
            copies.Length == 1 && File.ReadAllText(copies[0]) == broken);
        check("读失败时原文件不被覆盖也不被删除",
            File.Exists(Settings.FilePath) && File.ReadAllText(Settings.FilePath) == broken);
        check("读失败时退回默认值而不是丢数据",
            loaded.Theme == "Dark" && loaded.Edge == "top" && loaded.MonitorIntervalMs == 1000);
        loaded.Theme = "Light";
        loaded.Save();
        check("后续保存不会改写 unreadable 副本里的原始内容",
            File.ReadAllText(copies[0]) == broken);
    }

    private static void UnknownFieldsKept(Action<string, bool> check)
    {
        NewDir();
        File.WriteAllText(Settings.FilePath, """
        {
          "Theme": "Light",
          "Edge": "bottom",
          "FutureField": 42,
          "FutureObject": { "a": 1, "b": [2, 3] }
        }
        """);
        var loaded = Settings.Load();
        check("未知字段不影响已知字段的读取",
            loaded.Theme == "Light" && loaded.Edge == "bottom");
        loaded.Save();
        var root = ReadJson();
        check("未知字段在保存后仍然保留",
            root["FutureField"] != null && root["FutureField"]!.GetValue<int>() == 42
            && root["FutureObject"] != null);
        check("未知字段的嵌套结构原样写回",
            root["FutureObject"]!["b"]!.AsArray().Count == 2
            && root["FutureObject"]!["a"]!.GetValue<int>() == 1);
        check("已知字段不被未知字段覆盖",
            root["Theme"]!.GetValue<string>() == "Light" && root["Edge"]!.GetValue<string>() == "bottom");
    }

    private static void MissingFieldsFallBack(Action<string, bool> check)
    {
        NewDir();
        File.WriteAllText(Settings.FilePath, "{ \"Theme\": \"Pixel\" }");
        var loaded = Settings.Load();
        check("缺少的字段回落到默认值",
            loaded.Theme == "Pixel" && loaded.Edge == "top" && loaded.CompactView == "dash"
            && loaded.MonitorIntervalMs == 1000 && loaded.TempAlertThreshold == 88
            && loaded.PetAutoCycle && !loaded.ClickThrough);
        // 旧版本留下的、本版本不认识的键同样要留住
        File.WriteAllText(Settings.FilePath, "{ \"Theme\": \"Pixel\", \"LegacyFlag\": true }");
        var loaded2 = Settings.Load();
        loaded2.Save();
        var root = ReadJson();
        check("旧版本遗留的未知键保存后仍在",
            root["LegacyFlag"] != null && root["LegacyFlag"]!.GetValue<bool>()
            && root["Theme"]!.GetValue<string>() == "Pixel");
    }

    private static void DebouncedSave(Action<string, bool> check)
    {
        NewDir();
        var s = new Settings { Theme = "Cyberpunk" };
        int before = Settings.SaveWriteCount;
        s.RequestSave();
        s.RequestSave();
        s.RequestSave();
        Thread.Sleep(120);
        check("防抖窗口内还没落盘",
            !File.Exists(Settings.FilePath) && Settings.SaveWriteCount == before);
        // 等防抖窗口真正走完并落盘（最多 5 秒）—— **不能固定 sleep**：
        // 防抖定时器是 400ms，但满载机器上线程调度一慢，写盘就落在 820ms 之后 ⇒ 旧写法随机变红
        // （2026-11-26 在发布流水线并行时实测挂过一次，拦下了整个发布）。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && Settings.SaveWriteCount == before)
            Thread.Sleep(50);
        check("连续 RequestSave 只落一次盘",
            Settings.SaveWriteCount == before + 1 && File.Exists(Settings.FilePath));
        check("防抖写入的内容正确",
            ReadJson()["Theme"]!.GetValue<string>() == "Cyberpunk");
        s.CancelPendingSave();
    }

    private static void ImmediateSave(Action<string, bool> check)
    {
        NewDir();
        var s = new Settings { Theme = "NightOwl" };
        s.Save();
        check("Save 立即落盘（不等防抖窗口）",
            File.Exists(Settings.FilePath)
            && ReadJson()["Theme"]!.GetValue<string>() == "NightOwl");
    }

    private static void AtomicWrite(Action<string, bool> check)
    {
        string dir = NewDir();
        new Settings { Theme = "Island" }.Save();
        check("原子写入不留 .tmp 残留",
            !File.Exists(Settings.FilePath + ".tmp") && Directory.GetFiles(dir).Length == 1);
    }

    private static void ConcurrentSave(Action<string, bool> check)
    {
        NewDir();
        var s = new Settings { Theme = "Dark" };
        var tasks = new Task[8];
        for (int i = 0; i < tasks.Length; i++)
        {
            int n = i;
            tasks[i] = Task.Run(() => { s.Theme = n % 2 == 0 ? "Light" : "Dark"; s.Save(); });
        }
        Task.WaitAll(tasks);
        check("并发写盘后文件仍是合法 JSON（写盘串行化）",
            ReadJson() is not null
            && (ReadJson()["Theme"]!.GetValue<string>() == "Light" || ReadJson()["Theme"]!.GetValue<string>() == "Dark"));
    }
}
