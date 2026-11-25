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
            CompactViewPinned = true, LastPage = "clips", AutoColorByScreen = "UUID-1:1,UUID-2:0",
            // 装饰开关逐字段读取验证：每个字段都取「与默认值相反」的值，
            // 读不到该字段时会退回默认值 → 断言失败，确保逐字段读取真的生效。
            // DecorNotch / DecorLetters 默认 true，这里设 false。
            DecorNotch = false, DecorLetters = false,
            // 胶囊样式默认 true（数字主角），这里设 false 验证逐字段读取真的生效
            ComboNumericHero = false,
            // 剪贴板保留默认 session（退出即清），这里设 7d
            ClipboardRetention = "7d",
            // 自动循环默认 true，这里设 false。
            // （小形象款式由上方第 80 行的 PetStyle = "cat" 覆盖验证，别重复写 ——
            //   重复赋值是 CS1912 编译错误，会让整个测试工程跑不起来。）
            AutoCycleViews = false,
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
            && loaded.DashItems == "cores,mem" && loaded.ComboItems == "rings"
            && loaded.CompactViewPinned && loaded.LastPage == "clips"
            && loaded.AutoColorByScreen == "UUID-1:1,UUID-2:0"
            && !loaded.DecorNotch && !loaded.DecorLetters
            && !loaded.ComboNumericHero
            && loaded.ClipboardRetention == "7d"
            && !loaded.AutoCycleViews);
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
        // 第五十七轮起：只剩刘海造型与字母标识环两个装饰开关，默认都开。
        check("装饰默认值：刘海与字母环开",
            loaded.DecorNotch && loaded.DecorLetters);
        // 剪贴板保留策略默认 session（退出即清，隐私最稳）
        check("剪贴板保留默认值：退出时清除",
            loaded.ClipboardRetention == "session");
        // 非白名单的保留策略 → 退回默认，不进 UI 循环
        File.WriteAllText(Settings.FilePath, "{ \"ClipboardRetention\": \"999y\" }");
        var badRet = Settings.Load();
        check("非法的保留策略退回默认",
            badRet.ClipboardRetention == "session");
        // 第六十轮：小形象款式默认 classic、自动循环默认开
        check("小形象款式默认值：经典双眼",
            new Settings().PetStyle == "classic");
        check("自动循环默认值：开",
            new Settings().AutoCycleViews);
        // 非白名单的款式 → 退回 classic（不能让未知 id 画不出东西）
        File.WriteAllText(Settings.FilePath, "{ \"PetStyle\": \"not-a-pet\" }");
        var badPet = Settings.Load();
        check("非法的小形象款式退回 classic",
            badPet.PetStyle == "classic");
        // 第六十四轮：托盘「启动时关闭内建屏」开关依赖这个字段往返。
        // 该功能 2026-11-25 前只有「用户在屏幕页关过屏」这一条隐式设置路径，
        // 字段从未有过往返断言 —— 开关加上后这个字段开始被显式读写，必须有门禁。
        File.WriteAllText(Settings.FilePath, "{ \"AutoCloseBuiltin\": true }");
        var acOn = Settings.Load();
        acOn.Save();
        var acRound = Settings.Load();
        check("启动自动关内建屏：读取与往返",
            acOn.AutoCloseBuiltin && acRound.AutoCloseBuiltin
            && new Settings().AutoCloseBuiltin == false);   // 默认关（新装机不自动动屏幕）
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
        Thread.Sleep(700);
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
