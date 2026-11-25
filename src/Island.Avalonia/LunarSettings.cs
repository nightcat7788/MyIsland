using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Island.Avalonia;

/// <summary>
/// 月光 LunarCN 设置模块：读取/修改它的屏幕偏好（screenSettings.v1，plist 里的 JSON Data）。
/// 字段与 Preferences.swift 的 ScreenSettings 对齐：range / preset / followBuiltin。
/// 用系统 defaults 命令读写 plist Data（无第三方依赖、保留 LunarCN 其它键）。
/// </summary>
public static class LunarSettings
{
    private const string Domain = "local.lunarcn.displaycontrol";
    private const string Key = "screenSettings.v1";
    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Preferences", Domain + ".plist");

    public sealed record ScreenSetting(
        string Uuid, bool FollowBuiltin, int Min, int Max,
        // 静音**不持久化**：显示器自己保存音量/静音状态（DDC 写入即生效），
        // 本地再存一份反而可能和显示器实际状态不一致。
        double? Brightness, double? Contrast, double? Volume);

    /// <summary>
    /// 读取 screenSettings.v1 的 JSON Data。注意 defaults read 对 Data 会截断成 "..."，
    /// 必须走 plutil 转 XML 拿完整 base64（系统自带工具，无 python 依赖）。
    /// </summary>
    private static JsonObject? LoadDoc()
    {
        try
        {
            if (!File.Exists(PlistPath)) return null;
            string xml = Run("plutil", "-convert", "xml1", "-o", "-", PlistPath);
            using var sr = new StringReader(xml);
            // plist XML 带 DOCTYPE，.NET XmlReader 默认禁止 DTD 会抛异常 → 必须 Ignore
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore };
            using var reader = System.Xml.XmlReader.Create(sr, settings);
            bool inKey = false;
            while (reader.Read())
            {
                if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.Name == "key")
                    inKey = reader.ReadElementContentAsString() == Key;
                else if (inKey && reader.NodeType == System.Xml.XmlNodeType.Element && reader.Name == "data")
                {
                    string b64 = reader.ReadElementContentAsString();
                    byte[] bytes = Convert.FromBase64String(b64.Replace("\n", "").Replace("\r", "").Replace(" ", ""));
                    return JsonNode.Parse(Encoding.UTF8.GetString(bytes)) as JsonObject;
                }
            }
        }
        catch (Exception ex)
        {
            // plist 不存在/损坏：返回 null 让 UI 显示"未检测到"，但留日志便于排查
            if (File.Exists(PlistPath)) Console.Error.WriteLine($"[lunar-settings] load: {ex.Message}");
        }
        return null;
    }

    /// <summary>读取所有屏幕设置；解析失败返回空（绝不猜测/伪造）。</summary>
    public static List<ScreenSetting> Read()
    {
        var result = new List<ScreenSetting>();
        var doc = LoadDoc();
        if (doc is null) return result;
        try
        {
            foreach (var kv in doc)
            {
                if (kv.Value is not JsonObject o) continue;
                var range = o["range"] as JsonObject;
                var preset = o["preset"] as JsonObject;
                result.Add(new ScreenSetting(
                    kv.Key,
                    o["followBuiltin"]?.GetValue<bool>() ?? false,
                    range?["minimum"]?.GetValue<int>() ?? 0,
                    range?["maximum"]?.GetValue<int>() ?? 100,
                    preset?["brightness"]?.GetValue<double>(),
                    preset?["contrast"]?.GetValue<double>(),
                    preset?["volume"]?.GetValue<double>()));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[lunar-settings] parse entry: {ex.Message}"); }
        return result;
    }

    /// <summary>切换某块屏幕的"自动亮度·跟随内建屏"。</summary>
    public static bool SetFollow(string uuid, bool enabled)
        => Mutate(uuid, o => o["followBuiltin"] = enabled);

    /// <summary>设置某块屏幕的亮度上下限（0-99 / 1-100，且下限 &lt; 上限）。</summary>
    public static bool SetRange(string uuid, int min, int max)
    {
        if (min is < 0 or > 99 || max is < 1 or > 100 || min >= max) return false;
        return Mutate(uuid, o =>
        {
            var r = o["range"] as JsonObject ?? new JsonObject();
            r["minimum"] = min; r["maximum"] = max;
            o["range"] = r;
        });
    }

    /// <summary>保存预设（亮度/对比度/音量；与月光 ScreenPreset 对齐）。</summary>
    public static bool SavePreset(string uuid, int brightness, int? contrast, int? volume)
    {
        if (brightness is < 0 or > 100) return false;
        if (contrast is < 0 or > 100 || volume is < 0 or > 100) return false;
        return Mutate(uuid, o =>
        {
            var p = new JsonObject { ["brightness"] = brightness };
            if (contrast is { } c) p["contrast"] = c;
            if (volume is { } v) p["volume"] = v;
            o["preset"] = p;
        });
    }

    private static bool Mutate(string uuid, Action<JsonObject> change)
    {
        try
        {
            var doc = LoadDoc() ?? new JsonObject();
            var target = doc[uuid] as JsonObject ?? new JsonObject();
            change(target);
            doc[uuid] = target;
            string json = doc.ToJsonString();
            Run("defaults", "write", Domain, Key, "-data",
                Convert.ToHexString(Encoding.UTF8.GetBytes(json)));
            return true;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[lunar-settings] mutate {uuid}: {ex.Message}"); return false; }
    }

    private static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动 {file}");
        // stdout/stderr 必须同时异步排空：plutil 若向 stderr 写超过管道缓冲(~4KB)会阻塞，
        // 同步串行读会导致子进程卡死、线程池线程泄漏（审查 M-1）
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(3000)) { try { p.Kill(true); } catch { /* 失败降级：保留默认值/上次值 */ } }
        errTask.Wait(200);
        return outTask.Wait(500) ? outTask.Result : "";
    }
}
