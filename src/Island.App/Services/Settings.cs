using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;

namespace Island.App.Services;

/// <summary>
/// 全局设置，存到 %LOCALAPPDATA%\MyIsland\settings.json。原子写入避免损坏。
/// </summary>
public sealed class Settings : IDisposable
{
    public string Theme { get; set; } = "Dark";       // Dark / Light / Auto / 预设名
    public string Edge { get; set; } = "top";          // top/bottom/left/right
    public double PosAlongEdge { get; set; } = 0.5;    // 0..1
    public string ExpandMode { get; set; } = "hover";  // hover / always / never
    public string CompactView { get; set; } = "combo"; // 默认信息胶囊（与 Mac 实机截图一致）；combo / clock / dash / media / pet / monitor / devices / clipboard / window
    public bool CompactViewPinned { get; set; } = false;
    public string LastPage { get; set; } = "monitor";
    public bool AutoCloseBuiltin { get; set; } = false;
    public string AutoColorByScreen { get; set; } = "";
    /// <summary>多显示器偏好（范围/跟随内建屏/预设值，JSON 字符串持久化）。</summary>
    public string DisplayPreferences { get; set; } = "";
    public bool AlwaysOnTop { get; set; } = true;
    public bool HideFullscreen { get; set; } = true;
    public string PetStyle { get; set; } = "classic";
    /// <summary>向后兼容别名：与 <see cref="AutoCycleViews"/> 共享同一份收起态自动轮换状态。</summary>
    [JsonIgnore]
    public bool PetAutoCycle { get => AutoCycleViews; set => AutoCycleViews = value; }
    public bool IndicatorOnMaximize { get; set; } = true;
    public bool AlwaysVisible { get; set; } = false;
    public string DashItems { get; set; } = "";
    public string ComboItems { get; set; } = "clock,dials,disk,cputemp,gputemp,ssdtemp,battery,net";
    public string MonitorItems { get; set; } = "";
    public bool ClickThrough { get; set; } = false;
    public int MonitorIntervalMs { get; set; } = 1000;
    public bool TempAlertEnabled { get; set; } = true;
    public int TempAlertThreshold { get; set; } = 90;
    public bool BatteryAlertEnabled { get; set; } = true;
    public int BatteryAlertThreshold { get; set; } = 20;
    public string City { get; set; } = "";
    public string PreferredNetwork { get; set; } = "";
    public string ClipboardExcludedProcesses { get; set; } = "";

    public bool DecorNotch { get; set; } = false; // 双端默认纯圆角悬浮胶囊（与 Mac 实机截图一致）
    public bool DecorLetters { get; set; } = true;
    public bool ComboNumericHero { get; set; } = true;
    public string ClipboardRetention { get; set; } = "session";
    public bool AutoCycleViews { get; set; } = true;
    public bool CapsuleDefaultV3 { get; set; } = true;

    /// <summary>当前应用语义化版本号（从程序集 InformationalVersion 读取，避免手写版本号漂移）。</summary>
    public static string AppVersion { get; } = ReadAppVersion();

    private static string ReadAppVersion()
    {
        try
        {
            var attr = typeof(Settings).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(attr))
            {
                int plus = attr.IndexOf('+');
                return plus > 0 ? attr[..plus] : attr;
            }
            var v = typeof(Settings).Assembly.GetName().Version;
            if (v is not null) return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
        catch { }
        return "1.1.8";
    }

    /// <summary>标记旧版 settings.json 在首次升级加载时是否执行了默认时间胶囊造型迁移。</summary>
    [JsonIgnore]
    internal bool MigratedOnLoad { get; private set; }

    public static readonly string[] PetStyleValues =
        { "classic", "mono", "cat", "robot", "pixel", "alien", "ghost", "frog" };

    public static readonly string[] ClipboardHistoryRetentionValues = { "session", "1d", "7d", "30d" };

    public static readonly string[] EdgeValues = { "top", "bottom", "left", "right" };
    public static readonly string[] ExpandModeValues = { "hover", "always", "never" };
    public static readonly string[] CompactViewValues =
        { "clock", "combo", "dash", "media", "pet", "monitor", "devices", "clipboard", "window" };

    private const int MonitorIntervalMinMs = 250;
    private const int MonitorIntervalMaxMs = 10000;
    private const int TempAlertMinC = 30;
    private const int TempAlertMaxC = 120;

    /// <summary>把数值与枚举字符串夹到合法范围（就地修正）。</summary>
    public void ClampValues()
    {
        MonitorIntervalMs = Math.Clamp(MonitorIntervalMs, MonitorIntervalMinMs, MonitorIntervalMaxMs);
        TempAlertThreshold = Math.Clamp(TempAlertThreshold, TempAlertMinC, TempAlertMaxC);
        BatteryAlertThreshold = Math.Clamp(BatteryAlertThreshold, 5, 60);
        if (double.IsNaN(PosAlongEdge) || double.IsInfinity(PosAlongEdge)) PosAlongEdge = 0.5;
        else PosAlongEdge = Math.Clamp(PosAlongEdge, 0.0, 1.0);
        if (!EdgeValues.Contains(Edge)) Edge = "top";
        if (!ExpandModeValues.Contains(ExpandMode)) ExpandMode = "hover";
        if (!CompactViewValues.Contains(CompactView)) CompactView = "combo";
    }

    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(400);
    private static readonly object WriteGate = new();
    internal static readonly object CrashLogGate = new();

    private static readonly HashSet<string> KnownKeys = new(
        JsonSerializer.SerializeToNode(new Settings(skipDirCapture: true))!.AsObject().Select(p => p.Key),
        StringComparer.Ordinal);

    private static readonly HashSet<string> DeprecatedKeys = new(StringComparer.Ordinal)
    {
        "DecorRings",
        "DecorBars",
        "PetAutoCycle",
    };

    private JsonObject _unknown = new();
    private bool _readFailed;
    private bool _allowOverwriteAfterReadFailure;
    private string? _readError;
    private Timer? _saveTimer;
    private bool _disposed;
    private readonly object _timerGate = new();

    /// <summary>实例创建时绑定的目标目录（防止测试清理还原 DirectoryOverride 后后台防抖计时器误写真实用户目录）。</summary>
    private readonly string _instanceDir;

    public Settings()
    {
        _instanceDir = Dir;
    }

    private Settings(bool skipDirCapture)
    {
        _instanceDir = skipDirCapture ? "" : Dir;
    }

    /// <summary>测试钩子：把设置目录指向别处（测试用；null = 真实用户目录）。</summary>
    internal static string? DirectoryOverride { get; set; }

    /// <summary>已完成的写盘次数（测试用：验证防抖只落一次盘）。</summary>
    internal static int SaveWriteCount;

    private static string Dir => DirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyIsland");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    private string TargetDir => string.IsNullOrEmpty(_instanceDir) ? Dir : _instanceDir;
    private string TargetFilePath => Path.Combine(TargetDir, "settings.json");

    public static Settings Load()
    {
        var settings = new Settings();
        string filePath = settings.TargetFilePath;
        string dir = settings.TargetDir;
        try
        {
            if (!File.Exists(filePath)) return settings;

            string json;
            try
            {
                json = File.ReadAllText(filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogToDir(dir, $"读取设置文件失败（{ex.GetType().Name}: {ex.Message}）；本次会话使用默认值且不覆盖原文件");
                settings._readFailed = true;
                settings._readError = "设置文件无法读取（可能被占用或无权限），本次会话使用默认值且不会覆盖原文件";
                return settings;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException ex)
            {
                LogToDir(dir, $"设置文件 JSON 解析失败：{ex.Message}");
                settings._allowOverwriteAfterReadFailure = KeepUnreadable(dir, filePath);
                settings._readFailed = true;
                settings._readError = settings._allowOverwriteAfterReadFailure
                    ? "设置文件内容损坏，已另存为 unreadable 副本，本次会话使用默认值"
                    : "设置文件内容损坏且备份失败，本次会话使用默认值且不会覆盖原文件";
                return settings;
            }

            if (node is not JsonObject root)
            {
                settings._allowOverwriteAfterReadFailure = KeepUnreadable(dir, filePath);
                LogToDir(dir, settings._allowOverwriteAfterReadFailure
                    ? "设置文件根节点不是对象，已另存为 unreadable 副本"
                    : "设置文件根节点不是对象，且 unreadable 副本创建失败");
                settings._readFailed = true;
                settings._readError = settings._allowOverwriteAfterReadFailure
                    ? "设置文件内容损坏，已另存为 unreadable 副本，本次会话使用默认值"
                    : "设置文件内容损坏且备份失败，本次会话使用默认值且不会覆盖原文件";
                return settings;
            }

            settings.ApplyFields(root);
            settings.ClampValues();
            foreach (var pair in root)
                if (!KnownKeys.Contains(pair.Key) && !DeprecatedKeys.Contains(pair.Key))
                    settings._unknown[pair.Key] = pair.Value?.DeepClone();
            return settings;
        }
        catch (Exception ex)
        {
            LogToDir(dir, $"读取设置文件失败：{ex}");
            settings._allowOverwriteAfterReadFailure = KeepUnreadable(dir, filePath);
            settings._readFailed = true;
            settings._readError = settings._allowOverwriteAfterReadFailure
                ? "设置文件读取失败，已另存 unreadable 副本，本次会话使用默认值"
                : "设置文件读取失败且无法备份，本次会话使用默认值且不会覆盖原文件";
            return settings;
        }
    }

    private void ApplyFields(JsonObject root)
    {
        if (TryReadField(root, nameof(Theme), out string theme) && !string.IsNullOrWhiteSpace(theme)) Theme = theme;
        if (TryReadField(root, nameof(Edge), out string edge) && EdgeValues.Contains(edge)) Edge = edge;
        if (TryReadField(root, nameof(PosAlongEdge), out double pos)) PosAlongEdge = pos;
        if (TryReadField(root, nameof(ExpandMode), out string expandMode) && ExpandModeValues.Contains(expandMode)) ExpandMode = expandMode;
        if (TryReadField(root, nameof(CompactView), out string compactView) && CompactViewValues.Contains(compactView)) CompactView = compactView;
        if (TryReadField(root, nameof(CompactViewPinned), out bool cvPinned)) CompactViewPinned = cvPinned;
        if (TryReadField(root, nameof(LastPage), out string lastPage)) LastPage = lastPage;
        if (TryReadField(root, nameof(AutoCloseBuiltin), out bool autoClose)) AutoCloseBuiltin = autoClose;
        if (TryReadField(root, nameof(AutoColorByScreen), out string autoColor)) AutoColorByScreen = autoColor;
        if (TryReadField(root, nameof(DisplayPreferences), out string dispPref)) DisplayPreferences = dispPref;
        if (TryReadField(root, nameof(AlwaysOnTop), out bool alwaysOnTop)) AlwaysOnTop = alwaysOnTop;
        if (TryReadField(root, nameof(HideFullscreen), out bool hideFullscreen)) HideFullscreen = hideFullscreen;
        if (TryReadField(root, nameof(PetStyle), out string petStyle)
            && PetStyleValues.Contains(petStyle)) PetStyle = petStyle;
        if (TryReadField(root, nameof(AutoCycleViews), out bool autoCycle)) AutoCycleViews = autoCycle;
        else if (TryReadField(root, nameof(PetAutoCycle), out bool petAutoCycle)) AutoCycleViews = petAutoCycle;
        if (TryReadField(root, nameof(IndicatorOnMaximize), out bool indicator)) IndicatorOnMaximize = indicator;
        if (TryReadField(root, nameof(AlwaysVisible), out bool alwaysVisible)) AlwaysVisible = alwaysVisible;
        if (TryReadField(root, nameof(DashItems), out string dashItems)) DashItems = dashItems;
        if (TryReadField(root, nameof(ComboItems), out string comboItems)) ComboItems = comboItems;
        if (TryReadField(root, nameof(MonitorItems), out string monitorItems)) MonitorItems = monitorItems;
        if (TryReadField(root, nameof(ClickThrough), out bool clickThrough)) ClickThrough = clickThrough;
        if (TryReadField(root, nameof(MonitorIntervalMs), out int interval)) MonitorIntervalMs = interval;
        if (TryReadField(root, nameof(TempAlertEnabled), out bool tempAlert)) TempAlertEnabled = tempAlert;
        if (TryReadField(root, nameof(TempAlertThreshold), out int threshold)) TempAlertThreshold = threshold;
        if (TryReadField(root, nameof(BatteryAlertEnabled), out bool battAlert)) BatteryAlertEnabled = battAlert;
        if (TryReadField(root, nameof(BatteryAlertThreshold), out int battThreshold)) BatteryAlertThreshold = battThreshold;
        if (TryReadField(root, nameof(City), out string city)) City = city;
        if (TryReadField(root, nameof(PreferredNetwork), out string nic)) PreferredNetwork = nic;
        if (TryReadField(root, nameof(ClipboardExcludedProcesses), out string clip)) ClipboardExcludedProcesses = clip;
        if (TryReadField(root, nameof(DecorNotch), out bool decorNotch)) DecorNotch = decorNotch;
        if (TryReadField(root, nameof(DecorLetters), out bool decorLetters)) DecorLetters = decorLetters;
        if (TryReadField(root, nameof(ComboNumericHero), out bool comboNumericHero)) ComboNumericHero = comboNumericHero;
        if (TryReadField(root, nameof(ClipboardRetention), out string clipRetention)
            && ClipboardHistoryRetentionValues.Contains(clipRetention)) ClipboardRetention = clipRetention;
        if (TryReadField(root, nameof(CapsuleDefaultV3), out bool capV3)) CapsuleDefaultV3 = capV3;

        // 一次性平滑迁移：对齐悬浮圆角信息胶囊（03:08 [C]% [M]% [G]% [D]% │ [C]° [G]° [B]% │ ↓ ↑），
        // 仅在未手动固定视图时迁移，避免覆盖用户明确锁定的视图选择。
        if (!root.ContainsKey(nameof(CapsuleDefaultV3)))
        {
            if (!CompactViewPinned) CompactView = "combo";
            DecorNotch = false;
            CapsuleDefaultV3 = true;
            MigratedOnLoad = true;
        }
        // 旧版默认 ComboItems 自动补入硬盘温度与电池状态环（仅匹配历史默认串，不干扰用户自定义）
        if (ComboItems is "clock,dials,cputemp,gputemp,disk,net"
            or "clock,dials,cputemp,gputemp,disk,battery,net"
            or "clock,dials,disk,cputemp,gputemp,battery,net")
        {
            ComboItems = "clock,dials,disk,cputemp,gputemp,ssdtemp,battery,net";
        }
    }

    internal static bool TryReadField<T>(JsonObject root, string name, out T value)
    {
        value = default!;
        if (root is null || string.IsNullOrEmpty(name)) return false;
        if (!root.TryGetPropertyValue(name, out JsonNode? node) || node is null) return false;
        try
        {
            if (node.GetValueKind() == JsonValueKind.Null) return false;
            T? parsed = node.Deserialize<T>();
            if (parsed is null) return false;
            value = parsed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private bool DiffersFromDefaults()
    {
        var mine = JsonSerializer.SerializeToNode(this)?.AsObject();
        var def = JsonSerializer.SerializeToNode(new Settings(skipDirCapture: true))?.AsObject();
        if (mine is null || def is null) return true;
        foreach (var pair in def)
        {
            if (!mine.TryGetPropertyValue(pair.Key, out JsonNode? v)) return true;
            if (!JsonNode.DeepEquals(v, pair.Value)) return true;
        }
        return false;
    }

    public static string? LastSaveError { get; private set; }

    public void Save()
    {
        string dir = TargetDir;
        string filePath = TargetFilePath;
        try
        {
            CancelPendingSave();
            lock (WriteGate)
            {
                ClampValues();
                if (_readFailed && (!_allowOverwriteAfterReadFailure || !DiffersFromDefaults()))
                {
                    LastSaveError = _readError ?? "设置文件读取失败，已跳过写盘以保留原文件";
                    LogToDir(dir, "读盘失败且未满足安全重建条件：跳过写盘，避免覆盖用户原文件");
                    return;
                }
                Directory.CreateDirectory(dir);
                var own = JsonSerializer.SerializeToNode(this)!.AsObject();
                foreach (var pair in _unknown)
                    if (!own.ContainsKey(pair.Key))
                        own[pair.Key] = pair.Value?.DeepClone();
                string json = own.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                string tmp = filePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, filePath, true);
            }
            LastSaveError = null;
            Interlocked.Increment(ref SaveWriteCount);
        }
        catch (Exception ex)
        {
            LastSaveError = $"保存设置失败：{ex.Message}";
            LogToDir(dir, $"保存设置失败：{ex}");
        }
    }

    public void RequestSave()
    {
        lock (_timerGate)
        {
            if (_disposed) return;
            _saveTimer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    internal void CancelPendingSave()
    {
        lock (_timerGate)
        {
            if (_saveTimer is not null && !_disposed)
            {
                try { _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    public void Dispose()
    {
        lock (_timerGate)
        {
            if (_disposed) return;
            _disposed = true;
            _saveTimer?.Dispose();
            _saveTimer = null;
        }
    }

    private const long LogLimit = 1_048_576;

    private static void LogToDir(string dir, string text)
    {
        try
        {
            lock (CrashLogGate)
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "crash.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > LogLimit) File.Delete(path);
                File.AppendAllText(path, $"[{DateTime.Now}] {text}\n");
            }
        }
        catch { }
    }

    private static bool KeepUnreadable(string dir, string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return false;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string name = $"settings.unreadable-{stamp}.json";
            string target = Path.Combine(dir, name);
            for (int i = 1; File.Exists(target); i++)
            {
                name = $"settings.unreadable-{stamp}-{i}.json";
                target = Path.Combine(dir, name);
            }
            File.Copy(filePath, target);
            return File.Exists(target);
        }
        catch { return false; }
    }
}
