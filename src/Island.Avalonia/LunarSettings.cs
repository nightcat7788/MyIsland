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
    private static readonly object Gate = new();
    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Preferences", Domain + ".plist");

    public sealed record ScreenSetting(
        string Uuid, bool FollowBuiltin, int Min, int Max,
        double? Brightness, double? Contrast, double? Volume);

    /// <summary>
    /// 读取 screenSettings.v1 的 JSON Data。优先走 defaults export 从 cfprefsd 读取实时缓存，
    /// 避免 defaults write 后 plist 尚未刷盘导致读回旧值；再用 XDocument 安全解析字典键值。
    /// </summary>
    private static JsonObject? LoadDocUnlocked()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            string xml = "";
            try { xml = Run("defaults", "export", Domain, "-"); }
            catch
            {
                if (File.Exists(PlistPath))
                    xml = Run("plutil", "-convert", "xml1", "-o", "-", PlistPath);
            }
            if (string.IsNullOrWhiteSpace(xml)) return null;
            using var sr = new StringReader(xml);
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore };
            using var reader = System.Xml.XmlReader.Create(sr, settings);
            var doc = System.Xml.Linq.XDocument.Load(reader);
            var dict = doc.Root?.Element("dict");
            if (dict is null) return null;
            var elements = dict.Elements().ToList();
            for (int i = 0; i + 1 < elements.Count; i++)
            {
                if (elements[i].Name.LocalName != "key") continue;
                string k = elements[i].Value;
                var valElem = elements[++i];
                if (k == Key && valElem.Name.LocalName == "data")
                {
                    string b64 = valElem.Value.Replace("\n", "").Replace("\r", "").Replace(" ", "").Replace("\t", "");
                    if (b64.Length == 0) return null;
                    byte[] bytes = Convert.FromBase64String(b64);
                    return JsonNode.Parse(Encoding.UTF8.GetString(bytes)) as JsonObject;
                }
            }
        }
        catch (Exception ex)
        {
            if (File.Exists(PlistPath)) Console.Error.WriteLine($"[lunar-settings] load: {ex.Message}");
        }
        return null;
    }

    /// <summary>读取所有屏幕设置；解析失败返回空（绝不猜测/伪造）。</summary>
    public static List<ScreenSetting> Read()
    {
        var result = new List<ScreenSetting>();
        if (!OperatingSystem.IsMacOS()) return result;
        lock (Gate)
        {
            var doc = LoadDocUnlocked();
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
        if (!OperatingSystem.IsMacOS()) return false;
        lock (Gate)
        {
            try
            {
                var doc = LoadDocUnlocked() ?? new JsonObject();
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
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(3000))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"{file} 执行超时");
        }
        string err = errTask.Wait(200) ? errTask.Result.Trim() : "";
        string output = outTask.Wait(500) ? outTask.Result : "";
        if (p.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrEmpty(err) ? $"{file} 退出码 {p.ExitCode}" : err);
        return output;
    }
}
