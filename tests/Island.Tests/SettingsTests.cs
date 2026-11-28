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
            IoFailureNeverOverwrites(check);
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
            s.Theme == "Dark" && s.Edge == "top" && s.CompactView == "combo"
            && s.ExpandMode == "hover" && s.PetStyle == "classic");
        // combo 默认组合：时间 + 读数表盘 + CPU/GPU 温度 + 硬盘占用 + 网速
        // （环组已经画了 C/M/G 占用率，所以 cpu/gpu/mem 三个数字项默认不勾）
        var combo = s.ComboItems.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        check("默认 combo 显示项含时间/表盘/温度/硬盘/网速",
            combo.Contains("clock") && combo.Contains("dials") && combo.Contains("cputemp")
            && combo.Contains("gputemp") && combo.Contains("disk") && combo.Contains("net"));
        check("默认 combo 显示项不与表盘组重复摆 CPU/GPU/内存数字",
            !combo.Contains("cpu") && !combo.Contains("gpu") && !combo.Contains("mem"));
        check("默认设置的开关与阈值是出厂值",
            s.AlwaysOnTop && s.HideFullscreen && s.PetAutoCycle && s.AutoCycleViews && s.IndicatorOnMaximize
            && !s.AlwaysVisible && !s.ClickThrough && s.TempAlertEnabled
            && s.TempAlertThreshold == 90 && s.BatteryAlertEnabled && s.BatteryAlertThreshold == 20
            && s.MonitorIntervalMs == 1000);
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
            // Windows 默认 DecorNotch=false、DecorLetters=true，这里取相反值。
            DecorNotch = true, DecorLetters = false,
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
            && loaded.DecorNotch && !loaded.DecorLetters
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

    private static void IoFailureNeverOverwrites(Action<string, bool> check)
    {
        NewDir();
        const string original = "{ \"Theme\": \"Light\", \"Edge\": \"left\", \"FutureField\": 42 }";
        File.WriteAllText(Settings.FilePath, original);

        Settings loaded;
        // FileShare.None 让 Load 遇到真实的临时 I/O 占用；解除占用后旧实现会覆盖原文件。
        using (new FileStream(Settings.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            loaded = Settings.Load();

        loaded.Theme = "Dark";   // 即使用户改过设置，I/O 失败会话也不能重建未读出的配置。
        loaded.Save();
        check("I/O 读取失败后即使解除锁定也不覆盖原设置",
            File.ReadAllText(Settings.FilePath) == original);
        check("I/O 读取失败后的跳过写盘有可见错误",
            !string.IsNullOrEmpty(Settings.LastSaveError));
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
            loaded.Theme == "Pixel" && loaded.Edge == "top" && loaded.CompactView == "combo"
            && loaded.MonitorIntervalMs == 1000 && loaded.TempAlertThreshold == 90
            && loaded.PetAutoCycle && loaded.AutoCycleViews && !loaded.ClickThrough);
        // 旧键 PetAutoCycle 兼容迁移到统一的 AutoCycleViews
        File.WriteAllText(Settings.FilePath, "{ \"PetAutoCycle\": false, \"CapsuleDefaultV3\": true }");
        var legacyCycle = Settings.Load();
        check("旧字段 PetAutoCycle 兼容映射到统一的 AutoCycleViews",
            !legacyCycle.AutoCycleViews && !legacyCycle.PetAutoCycle);
        // 双端默认悬浮圆角信息胶囊（DecorNotch=false）且字母环开启（DecorLetters=true）
        check("装饰默认值：悬浮胶囊且字母环开",
            !loaded.DecorNotch && loaded.DecorLetters);
        // 旧版配置迁移：无 CapsuleDefaultV3 且未固定视图时自动迁到 combo 信息胶囊与悬浮圆角造型
        File.WriteAllText(Settings.FilePath, "{ \"CompactView\": \"clock\", \"CompactViewPinned\": false, \"DecorNotch\": true }");
        var migrated = Settings.Load();
        check("旧版未固定 clock/刘海配置首次升级自动迁移为悬浮信息胶囊",
            migrated.CompactView == "combo" && !migrated.DecorNotch && migrated.MigratedOnLoad);
        migrated.CompactView = "clock";
        migrated.DecorNotch = true;
        migrated.Save();
        var afterUserPick = Settings.Load();
        check("迁移落盘后用户手动切换的视图与刘海造型正常保留",
            afterUserPick.CompactView == "clock" && afterUserPick.DecorNotch && !afterUserPick.MigratedOnLoad);
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
        File.WriteAllText(Settings.FilePath, "{ \"Edge\": \"diagonal\", \"ExpandMode\": \"teleport\", \"CompactView\": \"invalid-view\" }");
        var badEnums = Settings.Load();
        check("非法的边缘/展开方式/收起视图枚举值退回出厂默认",
            badEnums.Edge == "top" && badEnums.ExpandMode == "hover" && badEnums.CompactView == "combo");
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
        string boundDir = NewDir();
        using var s = new Settings { Theme = "Cyberpunk" };
        int before = Settings.SaveWriteCount;
        s.RequestSave();
        s.RequestSave();
        s.RequestSave();
        // 即使后续 DirectoryOverride 切到别的目录，已创建的实例也必须写回其构造时绑定的目录（绝不污染真实用户目录）
        string otherDir = NewDir();
        Thread.Sleep(120);
        check("防抖窗口内还没落盘",
            !File.Exists(Path.Combine(boundDir, "settings.json")) && Settings.SaveWriteCount == before);
        Thread.Sleep(700);
        string boundFile = Path.Combine(boundDir, "settings.json");
        check("连续 RequestSave 只落一次盘且绑定到实例原目录",
            Settings.SaveWriteCount == before + 1 && File.Exists(boundFile) && !File.Exists(Path.Combine(otherDir, "settings.json")));
        check("防抖写入的内容正确",
            ((JsonObject)JsonNode.Parse(File.ReadAllText(boundFile))!)["Theme"]!.GetValue<string>() == "Cyberpunk");
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
