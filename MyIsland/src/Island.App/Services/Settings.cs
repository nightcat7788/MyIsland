using System.IO;
using System.Text.Json;

namespace Island.App.Services;

/// <summary>全局设置，存到 %LOCALAPPDATA%\MyIsland\settings.json。原子写入避免损坏。</summary>
public sealed class Settings
{
    public string Theme { get; set; } = "Dark";       // Dark / Light / Auto
    public string Edge { get; set; } = "top";          // top/bottom/left/right
    public double PosAlongEdge { get; set; } = 0.5;    // 0..1
    public string ExpandMode { get; set; } = "hover";  // hover / always / never
    public string CompactView { get; set; } = "dash";  // dash / clock / pet / combo / monitor / devices / clipboard
    public bool AlwaysOnTop { get; set; } = true;
    public bool HideFullscreen { get; set; } = true;
    public string PetStyle { get; set; } = "classic";   // 小形象款式：classic / mono / cat / robot / pixel
    public bool PetAutoCycle { get; set; } = true;      // 小形象 10s → 信息胶囊 30s 自动轮换（参考 BrimDeck 刘海轮播）
    public bool IndicatorOnMaximize { get; set; } = true; // 前台窗口最大化时缩成指示条（BrimDeck 同款）
    /// <summary>长期显示：全屏/最大化都不隐藏不缩条，岛体始终保持可见（覆盖 HideFullscreen/IndicatorOnMaximize）。</summary>
    public bool AlwaysVisible { get; set; } = false;
    /// <summary>dash 视图显示项（逗号分隔：cores,cputemp,gputemp,mem,gpu,disk,ssd,battery,net；空=全部）。</summary>
    public string DashItems { get; set; } = "";
    /// <summary>combo 信息胶囊显示项（rings,dials,clock,net；空=全部）。</summary>
    public string ComboItems { get; set; } = "";
    /// <summary>监控页显示项（cpu,cores,mem,gpu,disk,net,power,topproc；空=全部）。</summary>
    public string MonitorItems { get; set; } = "";
    /// <summary>鼠标穿透模式：岛体只显示不拦截点击（适合游戏；BrimDeck 兼容模式同款）。</summary>
    public bool ClickThrough { get; set; } = false;
    public int MonitorIntervalMs { get; set; } = 1000;
    public int QuotaIntervalMin { get; set; } = 10;
    public string City { get; set; } = "";             // 天气城市（空=IP 定位）

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyIsland");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (System.IO.File.Exists(FilePath))
            {
                var json = System.IO.File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
        }
        catch { /* 损坏则用默认 */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            string tmp = FilePath + ".tmp";
            System.IO.File.WriteAllText(tmp, json);
            System.IO.File.Move(tmp, FilePath, true);
        }
        catch { /* 写盘失败不影响运行 */ }
    }
}
