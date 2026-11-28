using System.IO;
using Island.App.Services;

namespace Island.Tests;

/// <summary>多屏显示偏好（DisplayPreferences）在 Settings 中的持久化往返用例。</summary>
internal static class DisplayPrefsTests
{
    public static void Run(Action<string, bool> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "Island-DispPrefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string? saved = Settings.DirectoryOverride;
        try
        {
            Settings.DirectoryOverride = dir;
            var def = Settings.Load();
            check("DisplayPreferences 默认值为空串", def.DisplayPreferences == "");

            const string payload = "{\"ext-1\":{\"Min\":10,\"Max\":90,\"FollowBuiltin\":true,\"PresetBrightness\":65}}";
            var s = new Settings { DisplayPreferences = payload };
            s.Save();

            var loaded = Settings.Load();
            check("DisplayPreferences 落盘后重读完全一致", loaded.DisplayPreferences == payload);
        }
        finally
        {
            Settings.DirectoryOverride = saved;
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
