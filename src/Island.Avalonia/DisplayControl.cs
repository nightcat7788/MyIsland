using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Island.Avalonia;

/// <summary>
/// 一块屏幕的可读状态（对齐参考版 ScreenState）。
///
/// ValuesLoaded=false：屏幕已发现但 DDC 读数还在后台补读（两阶段加载：先 58ms 列出屏幕，再补读数）。
/// BrightnessOk/ContrastOk/VolumeOk：该项是否**真读到了**。false = 读不到或不支持，
///   面板显示"不支持"，而不是画一个假的 0%。
/// Message：这张卡的状态文字（"已读取当前设置" / "亮度：读取失败…" / "亮度已实时调整并读取确认"）。
/// </summary>
public sealed record DdcScreen(
    uint Id, string Uuid, string Name, bool Builtin,
    int Brightness, int Contrast, int Volume, int? Mute,
    bool ValuesLoaded = true,
    bool BrightnessOk = true, bool ContrastOk = true, bool VolumeOk = true,
    string Message = "",
    // RGB 硬件增益（0-100）。null = 该显示器不支持（读不到）
    int? Red = null, int? Green = null, int? Blue = null,
    // 当前色温预设档位（VCP 20）。null = 显示器不支持色温预设
    int? ColorTempPreset = null);

/// <summary>
/// 屏幕控制（Mac）——内置月光 LunarCN 0.6.1 的控制层，**三个工具均随包从源码编译**
/// （tools/display-tools/build.sh），不依赖用户另装月光：
/// - `island-screens`：枚举屏幕 → `ID|UUID|名称|builtin/external`
/// - `ddc-helper`：外接屏 DDC/CI（VCP 16 亮度 / 18 对比度 / 98 音量 / 141 静音）
/// - `screen-guardian`：内建屏关闭的独立守护进程（主进程崩溃也能恢复）
/// 内建屏亮度走 DisplayServices 私有 API（无 root；CoreDisplay 在本机恒返回 1.0，不可用）。
/// </summary>
public static class DisplayControl
{
    private const string HelperName = "ddc-helper";
    private const string ListName = "island-screens";
    private const string GuardianName = "screen-guardian";
    public const int VcpBrightness = 16, VcpContrast = 18, VcpVolume = 98, VcpMute = 141;
    /// <summary>RGB 硬件增益（颜色设置）——对齐 LunarCN 功能表 M08。实测 DELL U3225QE 支持且可写。</summary>
    public const int VcpRed = 22, VcpGreen = 24, VcpBlue = 26;
    /// <summary>
    /// 色温预设选择（Select Color Preset）。**实测可写**的色温控制项。
    /// DELL U3225QE 上有效索引为 1/4/5/6/8/9/11/12（2/3/7/10 会被显示器拒绝）。
    /// </summary>
    public const int VcpColorTempPreset = 20;
    /// <summary>「用户自定义」档 —— 选它之后显示器不再接管白点，由我们用 RGB 增益微调。</summary>
    public const int ColorTempUserPreset = 12;

    // DDC/CI 是串行慢速总线（I2C），并发读写会互相干扰导致"未返回有效数据"。
    private static readonly SemaphoreSlim _ddcGate = new(1, 1);

    /// <summary>
    /// 「整轮刷新」锁：DDC/CI over I2C 是**单总线**，一次读事务会打断写事务。
    /// 原来每次 Read 各自持 _ddcGate → 读 7 个 VCP 之间锁会释放 → 写入插进来就被显示器拒绝
    /// （实测：屏幕页 3 秒轮询时写红增益 40，显示器回"未确认目标值"，实际停在 100）。
    /// 这把锁让「读完一整轮」和「写一次」互斥。
    /// </summary>
    private static readonly SemaphoreSlim _refreshGate = new(1, 1);
    /// <summary>上一次 DDC 事务结束的单调时间戳（Environment.TickCount64，避免系统改时导致负值抛异常）。</summary>
    private static long _lastDdcTick;

    // 写入"未确认"的 (uuid, vcp)：跨 UI 线程与后台 DDC 线程并发读写，必须使用 ConcurrentDictionary。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string uuid, int vcp), byte> _failedWrites = new();

    private static void Log(string what, Exception ex)
        => Console.Error.WriteLine($"[display] {what}: {ex.GetType().Name}: {ex.Message}");

    // ---------- 枚举 + 全量读取 ----------
    /// <summary>只枚举内建屏的 id/uuid，不读 DDC（毫秒级）。关闭内建屏前用这个，避免 DDC 慢拖 20+ 秒。
    /// 同时返回是否有可见外接屏（C-2 校验用），一次枚举两用。</summary>
    public static ((uint id, string uuid) builtin, bool hasExternal) FindBuiltinFast()
    {
        if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("仅 macOS 支持");
        string? lister = Locate(ListName) ?? throw new InvalidOperationException("随包工具缺失（island-screens）");
        (uint, string)? builtin = null; bool hasExternal = false;
        foreach (var line in Run(lister, 2500).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 4 || !uint.TryParse(parts[0], out uint id)) continue;
            // 第 5 列（island-screens 输出）：physical=实体屏，virtual=虚拟/AirPlay/读不到信息字典。
            // 关闭内建屏前的校验**只认可见的实体外接屏**（对齐参考版 visibleExternalIDs）——
            // 旧实现把任何 external 行都算上，接着 AirPlay 或虚拟屏时校验会放行 → 整机黑屏。
            bool physical = parts.Length >= 5 && parts[4].Trim() == "physical";
            if (parts[3].Trim() == "builtin") builtin = (id, parts[1].Trim());
            else if (parts[3].Trim() == "external" && physical) hasExternal = true;
        }
        if (builtin is null) throw new InvalidOperationException("未检测到内建屏");
        return (builtin.Value, hasExternal);
    }

    /// <summary>
    /// 只枚举屏幕（不读 DDC）——58ms 返回。
    /// 用于两阶段加载的第一阶段：用户切到屏幕页立刻能看到屏幕卡片，
    /// DDC 读数由 <see cref="ReadValues"/> 在后台补。
    /// </summary>
    public static List<DdcScreen> ListNames()
    {
        var list = new List<DdcScreen>();
        if (!OperatingSystem.IsMacOS()) return list;
        try
        {
            string? lister = Locate(ListName);
            if (lister is null) return list;
            string outp = Run(lister, 2500);
            foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|');
                if (parts.Length < 4 || !uint.TryParse(parts[0], out uint id)) continue;
                string uuid = parts[1].Trim();
                bool builtin = parts[3].Trim() == "builtin";
                int builtinBri = builtin && BuiltinBrightness.Read(id) is { Ok: true, Level: { } bl } ? bl.PercentRounded : 0;
                list.Add(new DdcScreen(id, uuid, parts[2].Trim(), builtin,
                    builtinBri, 0, 0, null, ValuesLoaded: builtin));
            }
        }
        catch (Exception ex) { Log("list screens", ex); }
        return list;
    }

    /// <summary>
    /// 补读外接屏的 DDC 值（亮度/对比度/音量/静音），返回带值的新列表。
    ///
    /// ⚠ DDC/CI over I2C **必须串行**：同一块屏上并发多个 I2C 事务会互相冲突
    /// （实测并发读 4 个 VCP → 全部返回空；串行 → 全部成功）。
    /// 提速靠"已确认不支持的 VCP 直接跳过"，不靠并发。
    ///
    /// 每项保留三态（对齐参考版 load()）：读到 → 值；不支持/失败 → 记入 Failures，
    /// 面板显示具体哪项读不到，而不是画一个假的 0。
    /// </summary>
    public static List<DdcScreen> ReadValues(List<DdcScreen> screens)
    {
        var result = new List<DdcScreen>(screens.Count);
        _refreshGate.Wait();      // 整轮刷新期间不让写事务插进来
        try
        {
        foreach (var s in screens)
        {
            if (s.Builtin || s.ValuesLoaded) { result.Add(s); continue; }
            var b = ReadLevel(s.Uuid, s.Id, VcpBrightness);
            var c = ReadLevel(s.Uuid, s.Id, VcpContrast);
            var v = ReadLevel(s.Uuid, s.Id, VcpVolume);
            var m = ReadMuteState(s.Uuid, s.Id);
            // RGB 增益：读不到就置 null（该显示器不支持），面板据此隐藏"颜色"区
            var r = ReadLevel(s.Uuid, s.Id, VcpRed);
            var g = ReadLevel(s.Uuid, s.Id, VcpGreen);
            var bl = ReadLevel(s.Uuid, s.Id, VcpBlue);
            var ct = ReadLevel(s.Uuid, s.Id, VcpColorTempPreset);

            // 读不到的项目：亮度/对比度是主要项，读不到要明确报出来；音量/静音是可选项，静默跳过
            var failures = new List<string>();
            if (!b.Ok) failures.Add("亮度：" + (b.Message ?? "读取失败"));
            if (!c.Ok && c.Status != DdcStatus.Unsupported) failures.Add("对比度：" + (c.Message ?? "读取失败"));
            result.Add(s with
            {
                Brightness = b.Ok ? b.Level!.Value.PercentRounded : 0,
                Contrast = c.Ok ? c.Level!.Value.PercentRounded : 0,
                Volume = v.Ok ? v.Level!.Value.PercentRounded : 0,
                Mute = m.Ok ? m.Level!.Value.Current : null,   // 1=静音 2=有声（参考版 MuteState 枚举）
                // 读失败时**保留旧值**而不是置 null：DDC 总线忙（外部命令正在读）会导致
                // 单次读取失败，若置 null 颜色区就会"时有时无"地消失。
                // 只有"这块屏确实不支持 RGB"（连续失败）才隐藏颜色区。
                Red = r.Ok ? r.Level!.Value.PercentRounded : s.Red,
                Green = g.Ok ? g.Level!.Value.PercentRounded : s.Green,
                Blue = bl.Ok ? bl.Level!.Value.PercentRounded : s.Blue,
                // 色温预设档位（读不到就保持旧值，别让颜色区忽隐忽现）
                ColorTempPreset = ct.Ok ? ct.Level!.Value.Current : s.ColorTempPreset,
                BrightnessOk = b.Ok, ContrastOk = c.Ok, VolumeOk = v.Ok,
                Message = failures.Count == 0 ? "已读取当前设置" : string.Join("\n", failures),
                ValuesLoaded = true,
            });
        }
        }
        finally { _refreshGate.Release(); Volatile.Write(ref _lastDdcTick, Environment.TickCount64); }
        return result;
    }

    private static int Pct((int current, int max)? v)
        => v is { } x && x.max > 0 ? (int)Math.Round(x.current * 100.0 / x.max) : 0;

    /// <summary>
    /// 一次显示器控制操作的结果。对齐参考版 ControlError 的三类：
    /// Unsupported（显示器不支持此项）/ WriteUnconfirmed（已发出但设备没确认）/ 其它失败。
    /// 旧实现把所有失败压成一个 bool，导致"写入未确认"和"线松了"无法区别处理。
    /// </summary>
    public enum DdcStatus { Ok, Unsupported, WriteUnconfirmed, Failed, Paused }

    public readonly record struct DdcLevel(int Current, int Maximum)
    {
        /// <summary>百分比。对齐参考版 ControlLevel.percent（保留小数，避免 0-1 映射成整数后丢精度）。</summary>
        public double Percent => Maximum > 0 ? Current * 100.0 / Maximum : 0;
        public int PercentRounded => (int)Math.Round(Percent);
    }

    public readonly record struct DdcResult(DdcStatus Status, DdcLevel? Level, string? Message = null)
    {
        public bool Ok => Status == DdcStatus.Ok;
        public static DdcResult Success(DdcLevel l) => new(DdcStatus.Ok, l);
    }

    /// <summary>
    /// 已确认"显示器不支持"的 (uuid, vcp)。只有 ddc-helper **退出码 3** 才记入。
    /// 退出码 1（连接/总线忙/暂时无响应）是临时失败，重试有意义，绝不记入——
    /// 否则后续所有读写都被跳过，亮度永远调不动（2026-11-24 用户反馈，已修）。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string uuid, int code), bool> _unsupportedVcp = new();
    private static bool IsUnsupported(string uuid, int code) => _unsupportedVcp.ContainsKey((uuid, code));
    private static void MarkUnsupported(string uuid, int code) => _unsupportedVcp[(uuid, code)] = true;

    // 每屏亮度范围（min,max）。对齐参考版 BrightnessRange：约束"本程序发出的亮度目标"。
    // 旧实现只把范围写进 plist，从不参与写入 —— 菜单里的三档范围是摆设。
    private static System.Collections.Concurrent.ConcurrentDictionary<string, (int min, int max)> _ranges = new();
    public static void SetBrightnessRange(string uuid, int min, int max) => _ranges[uuid] = (min, max);
    public static (int min, int max) GetBrightnessRange(string uuid)
        => _ranges.TryGetValue(uuid, out var r) ? r : (0, 100);

    /// <summary>取回某屏某项的写入暂停状态：true=已暂停（未确认过）。</summary>
    public static bool IsWritePaused(string uuid, int vcp) => _failedWrites.ContainsKey((uuid, vcp));
    /// <summary>允许重试（对齐参考版 allowRetry）：清除该屏所有项的写入暂停与不支持缓存。</summary>
    public static void AllowRetry(string uuid)
    {
        foreach (var k in _failedWrites.Keys)
            if (k.uuid == uuid) _failedWrites.TryRemove(k, out _);
        foreach (var k in _unsupportedVcp.Keys)
            if (k.uuid == uuid) _unsupportedVcp.TryRemove(k, out _);
    }
    /// <summary>该屏被暂停的 VCP 集合（面板显示用）。</summary>
    public static IReadOnlyCollection<int> PausedControls(string uuid)
        => _failedWrites.Keys.Where(k => k.uuid == uuid).Select(k => k.vcp).ToList();

    // ---------- 读取 ----------
    /// <summary>
    /// 带重试的读取（串行总线锁内）。外接屏走 ddc-helper，内建屏走 DisplayServices。
    /// 已知不支持的 VCP 直接返回 Unsupported，不浪费 2~4 秒等它超时。
    /// </summary>
    public static DdcResult ReadLevel(string uuid, uint id, int code)
    {
        if (code == VcpMute) { var mr = ReadMuteState(uuid, id); return mr; }
        if (IsUnsupported(uuid, code)) return new DdcResult(DdcStatus.Unsupported, null, "显示器不支持此项");
        if (IsUnsupported(uuid, VcpBrightness) && code == VcpBrightness) return new(DdcStatus.Unsupported, null, "显示器不支持此项");
        _ddcGate.Wait();
        try
        {
            // 最多试 3 次：DDC/CI over I2C 有总线占用问题，紧邻的读写会互相撞
            // （实测"写完立刻读"会报"未找到可用的显示器控制通道"，间隔 300ms 就正常）。
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var r = ReadOnce(uuid, id, code);
                if (r.Ok || r.Status == DdcStatus.Unsupported) return r;
                if (attempt < 2) Thread.Sleep(attempt == 0 ? 200 : 400);
            }
            return new DdcResult(DdcStatus.Failed, null, "显示器响应超时，请检查连接后刷新。");
        }
        finally { _ddcGate.Release(); }
    }

    /// <summary>兼容旧调用：读 (current,max)，失败 null。</summary>
    public static (int current, int max)? Read(string uuid, uint id, int code)
        => ReadLevel(uuid, id, code).Level is { } l ? (l.Current, l.Maximum) : null;

    private static DdcResult ReadOnce(string uuid, uint id, int code)
    {
        string? helper = Locate(HelperName);
        if (helper is null) return new(DdcStatus.Failed, null, "未找到 ddc-helper（随包工具缺失）");
        string outp = RunCore(helper, 5500, out bool cleanExit, out int exitCode, "read", id.ToString(), uuid, code.ToString());
        if (string.IsNullOrWhiteSpace(outp))
        {
            // 只有退出码 3（"显示器不支持此 VCP"）才永久跳过；1 是临时失败，可重试
            if (exitCode == 3) { MarkUnsupported(uuid, code); return new(DdcStatus.Unsupported, null, "显示器不支持此项"); }
            return new(DdcStatus.Failed, null, cleanExit ? "显示器控制失败，请刷新后重试。" : "显示器响应超时，请检查连接后刷新。");
        }
        try
        {
            using var doc = JsonDocument.Parse(outp.Trim());
            var r = doc.RootElement;
            if (!r.TryGetProperty("current", out var cur) || !r.TryGetProperty("maximum", out var max)) return new(DdcStatus.Failed, null, "显示器返回格式无效");
            var lvl = new DdcLevel(cur.GetInt32(), max.GetInt32());
            if (lvl.Maximum is < 1 or > 65535 || lvl.Current < 0 || lvl.Current > lvl.Maximum)
                return new(DdcStatus.Failed, null, "显示器返回的数值无效。");
            return DdcResult.Success(lvl);
        }
        catch (JsonException) { return new(DdcStatus.Failed, null, "显示器返回格式无法解析"); }
    }

    /// <summary>静音状态（VCP 141 枚举：1=静音 2=有声）。对齐参考版 MuteState。</summary>
    public static DdcResult ReadMuteState(string uuid, uint id)
    {
        if (IsUnsupported(uuid, VcpMute)) return new(DdcStatus.Unsupported, null, "显示器不支持此项");
        _ddcGate.Wait();
        try
        {
            var r = ReadOnce(uuid, id, VcpMute);
            if (!r.Ok || r.Level is not { } lvl) return r;
            return lvl.Current is 1 or 2 ? r : new(DdcStatus.Unsupported, null, "显示器返回的静音状态无效。");
        }
        finally { _ddcGate.Release(); }
    }

    // ---------- 写入 ----------
    /// <summary>
    /// 写百分比（串行总线锁内）。外接屏用 ddc-helper 的百分比模式（helper 内部读上限并换算，
    /// 写后回读确认）；内建屏走 DisplayServices 并回读。
    /// 亮度(VCP 16) 会被该屏亮度范围钳制。返回结构化结果：
    ///   WriteUnconfirmed = 已发出但设备没确认 → 记入暂停名单，直到用户"允许重试"。
    /// </summary>
    public static DdcResult SetPercentEx(string uuid, uint id, int code, int percent, bool builtin = false)
    {
        if (IsWritePaused(uuid, code)) return new(DdcStatus.Paused, null, "此项写入已暂停，请检查连接后点\"允许重试\"。");
        _refreshGate.Wait();      // 与"整轮刷新"互斥：写事务不能被读事务打断
        try
        {
            long last = Volatile.Read(ref _lastDdcTick);
            if (last != 0)
            {
                long elapsed = Environment.TickCount64 - last;
                if (elapsed >= 0 && elapsed < 400) Thread.Sleep((int)(400 - elapsed));
            }
            var r = SetPercentExCore(uuid, id, code, percent, builtin);
            Volatile.Write(ref _lastDdcTick, Environment.TickCount64);
            return r;
        }
        finally { _refreshGate.Release(); }
    }

    private static DdcResult SetPercentExCore(string uuid, uint id, int code, int percent, bool builtin)
    {
        if (code == VcpBrightness)
        {
            var (min, max) = GetBrightnessRange(uuid);
            percent = (int)Math.Clamp(percent, min, max);
        }
        DdcResult r;
        _ddcGate.Wait();
        try
        {
            if (builtin)
            {
                r = BuiltinBrightness.Set(percent, id);
                if (r.Ok) _failedWrites.TryRemove((uuid, code), out _);
                else if (r.Status == DdcStatus.WriteUnconfirmed) _failedWrites[(uuid, code)] = 1;
            }
            else
            {
                r = SetPercentUnlocked(uuid, id, code, percent);
                // 写后冷却：ddc-helper 的 set 内部做 3 次确认读取，刚结束时总线仍忙（实测 300ms 才稳）
                if (r.Ok || r.Status == DdcStatus.WriteUnconfirmed) Thread.Sleep(320);
                if (r.Ok) _failedWrites.TryRemove((uuid, code), out _);
                else if (r.Status == DdcStatus.WriteUnconfirmed) _failedWrites[(uuid, code)] = 1;
            }
        }
        finally { _ddcGate.Release(); }
        return r;
    }

    /// <summary>兼容旧调用：写百分比，成功 true。</summary>
    public static bool SetPercent(string uuid, uint id, int code, int percent, bool builtin = false)
        => SetPercentEx(uuid, id, code, percent, builtin).Ok;

    public static bool SetMute(string uuid, uint id, bool muted)
        => SetPercentEx(uuid, id, VcpMute, muted ? 1 : 2).Ok;

    private static DdcResult SetPercentUnlocked(string uuid, uint id, int code, int percent)
    {
        string? helper = Locate(HelperName);
        if (helper is null) return new(DdcStatus.Failed, null, "未找到 ddc-helper（随包工具缺失）");
        // 静音是 1/2 枚举不是百分比 → 用绝对值模式；其它用百分比模式（helper 内部换算）
        int exitCode;
        string outp = code == VcpMute
            ? RunCore(helper, 5500, out _, out exitCode, "set", id.ToString(), uuid, code.ToString(), percent.ToString())
            : RunCore(helper, 5500, out _, out exitCode, "set", id.ToString(), uuid, code.ToString(), "p", percent.ToString());
        DdcLevel? Parse()
        {
            try
            {
                using var doc = JsonDocument.Parse(outp.Trim());
                var r = doc.RootElement;
                if (r.TryGetProperty("current", out var c) && r.TryGetProperty("maximum", out var m))
                    return new DdcLevel(c.GetInt32(), m.GetInt32());
            }
            catch { /* 设置读取失败 → 用默认 */ }
            return null;
        }
        if (!string.IsNullOrWhiteSpace(outp) && Parse() is { } ok) return DdcResult.Success(ok);
        if (exitCode == 3) { MarkUnsupported(uuid, code); return new(DdcStatus.Unsupported, null, "显示器不支持此项"); }
        if (exitCode == 2) return new(DdcStatus.WriteUnconfirmed, null, "显示器未确认目标值。请检查显示器设置或连接后重试。");
        return new(DdcStatus.Failed, null, "显示器控制失败，请刷新后重试。");
    }

    // ---------- 内建屏亮度（DisplayServices 私有 API，无 root） ----------
    // ⚠ 不能用 CoreDisplay：本机实测 CoreDisplay_Display_GetUserBrightness **恒返回 1.0**，
    //   DisplayServices 返回真实值（0.389）。参考版 0.2.1 修过同一个 bug，移植时漏了 →
    //   内建屏亮度永远显示 100%、写完回读也"成功"，整套是假的。
    [DllImport("/System/Library/PrivateFrameworks/DisplayServices.framework/DisplayServices", EntryPoint = "DisplayServicesGetBrightness")]
    private static extern int DisplayServices_GetBrightness(uint display, out float brightness);
    [DllImport("/System/Library/PrivateFrameworks/DisplayServices.framework/DisplayServices", EntryPoint = "DisplayServicesSetBrightness")]
    private static extern int DisplayServices_SetBrightness(uint display, float brightness);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", EntryPoint = "CGDisplayIsBuiltin")]
    private static extern int CGDisplayIsBuiltin(uint display);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", EntryPoint = "CGDisplayIsActive")]
    private static extern int CGDisplayIsActive(uint display);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", EntryPoint = "CGDisplayIsAsleep")]
    private static extern int CGDisplayIsAsleep(uint display);

    /// <summary>内建屏亮度的读/写（DisplayServices）。display 用真实内建屏 id，不再用 CGMainDisplayID()。</summary>
    internal static class BuiltinBrightness
    {
        public static DdcResult Read(uint display)
        {
            if (!OperatingSystem.IsMacOS()) return new(DdcStatus.Failed, null, "仅 macOS 支持");
            try
            {
                int status = DisplayServices_GetBrightness(display, out float value);
                if (status != 0 || float.IsNaN(value) || float.IsInfinity(value) || value < 0 || value > 1)
                    return new(DdcStatus.Failed, null, $"无法读取内置屏幕亮度（{status}）。");
                // 对齐参考版：current = round(value*1000)，maximum = 1000
                return DdcResult.Success(new DdcLevel((int)Math.Round(value * 1000), 1000));
            }
            catch (Exception ex) { Log("builtin brightness read", ex); return new(DdcStatus.Failed, null, ex.Message); }
        }

        public static DdcResult Set(int percent, uint display)
        {
            if (!OperatingSystem.IsMacOS()) return new(DdcStatus.Failed, null, "仅 macOS 支持");
            try
            {
                int raw = (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * 1000);
                int status = DisplayServices_SetBrightness(display, raw / 1000f);
                if (status != 0) return new(DdcStatus.Failed, null, $"系统拒绝设置内置屏幕亮度（{status}）。");
                var back = Read(display);
                if (!back.Ok) return back;
                // 对齐参考版：回读误差 ≤ 5/1000 才算确认
                if (Math.Abs(back.Level!.Value.Current - raw) > 5)
                    return new(DdcStatus.WriteUnconfirmed, back.Level, "系统未确认目标亮度，请刷新后检查。");
                return back;
            }
            catch (Exception ex) { Log("builtin brightness set", ex); return new(DdcStatus.Failed, null, ex.Message); }
        }
    }

    /// <summary>找内建屏 id（经 SkyLight，关闭态也能找到）；没有返回 0。</summary>
    private static uint FindBuiltinViaSkyLight()
    {
        uint count = 0;
        int r0 = SLSGetDisplayListCount(0, IntPtr.Zero, ref count);
        if (r0 != 0 || count == 0 || count > 128) return 0;
        var ids = new uint[count];
        uint cap = count;
        int r1 = SLSGetDisplayList(cap, ids, ref count);
        if (r1 != 0) return 0;
        for (int i = 0; i < count; i++) if (CGDisplayIsBuiltin(ids[i]) != 0) return ids[i];
        return 0;
    }

    /// <summary>内建屏 id（在线列表里找；找不到返回 0）。不读 DDC，纯 CG 调用。</summary>
    public static uint FindBuiltinId()
    {
        if (!OperatingSystem.IsMacOS()) return 0;
        try
        {
            var (b, _) = FindBuiltinFast();
            return b.id;
        }
        catch { return 0; }
    }

    public static int GetBuiltinBrightness()
    {
        uint id = FindBuiltinId();
        if (id == 0) return 0;
        var r = BuiltinBrightness.Read(id);
        return r.Ok ? r.Level!.Value.PercentRounded : 0;
    }

    /// <summary>源屏是否可用于自动跟随（对齐参考版 checkSource：内建 + active + 未休眠）。</summary>
    public static bool IsBuiltinUsableAsSource(uint id)
        => id != 0 && CGDisplayIsBuiltin(id) != 0 && CGDisplayIsActive(id) != 0 && CGDisplayIsAsleep(id) == 0;

    // ---------- 内建屏关闭 / 恢复（经独立守护进程） ----------
    private static readonly object _guardianGate = new();
    private static Process? _guardian;

    public static bool IsBuiltinOff
    {
        get
        {
            lock (_guardianGate)
            {
                if (_guardian is null) return false;
                try { return !_guardian.HasExited; }
                catch { return false; }
            }
        }
    }

    /// <summary>内建屏被关闭的时刻（用于 15 秒倒计时）；未关闭为 null。</summary>
    public static DateTime? BuiltinOffAt { get; private set; }

    /// <summary>距离守护进程 15 秒自动恢复还剩几秒；已确认保持或未关闭返回 null。</summary>
    public static int? BuiltinOffCountdown
    {
        get
        {
            lock (_guardianGate)
            {
                if (_guardian is null || BuiltinOffKept || BuiltinOffAt is not { } at) return null;
                try { if (_guardian.HasExited) return null; } catch { return null; }
                int left = 15 - (int)(DateTime.Now - at).TotalSeconds;
                return left > 0 ? left : 0;
            }
        }
    }

    /// <summary>用户是否已点"保持关闭"。</summary>
    public static bool BuiltinOffKept { get; private set; }

    public static string? TurnOffBuiltin(uint id, string uuid, bool hasExternal)
    {
        string? guardian = Locate(GuardianName);
        if (guardian is null) return "未找到 screen-guardian（随包工具缺失，请重新安装）";
        if (!hasExternal) return "请先连接并开启一台实体外接显示器，再关闭内建屏。";

        lock (_guardianGate)
        {
            if (_guardian is not null)
            {
                try { if (!_guardian.HasExited) return null; } catch { }
                TryKillAndDispose(_guardian);
                _guardian = null;
            }
            try
            {
                var psi = new ProcessStartInfo(guardian)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                psi.ArgumentList.Add(id.ToString());
                psi.ArgumentList.Add(uuid);
                var p = Process.Start(psi);
                if (p is null) return "无法启动守护进程";
                var readyTask = p.StandardOutput.ReadLineAsync();
                if (!readyTask.Wait(8000))
                {
                    TryKillAndDispose(p);
                    return "屏幕恢复保护程序无响应，已取消关闭内建屏。";
                }
                string? first = readyTask.Result;
                if (first != "READY")
                {
                    TryKillAndDispose(p);
                    return first?.StartsWith("ERROR:") == true ? first[6..] : "内建屏关闭失败";
                }
                _guardian = p;
                BuiltinOffAt = DateTime.Now;
                BuiltinOffKept = false;
                _ = Task.Run(async () =>
                {
                    try { await p.StandardOutput.ReadToEndAsync(); await p.StandardError.ReadToEndAsync(); }
                    catch { /* 探针退出时读流中断 */ }
                });
                return null;
            }
            catch (Exception ex) { Log("turn off builtin", ex); return ex.Message; }
        }
    }

    /// <summary>确认保持关闭（重置 15 秒倒计时）。</summary>
    public static void KeepBuiltinOff()
    {
        lock (_guardianGate)
        {
            if (_guardian is null) return;
            try
            {
                if (_guardian.HasExited) return;
                _guardian.StandardInput.WriteLine("keep");
                _guardian.StandardInput.Flush();
                BuiltinOffKept = true;
            }
            catch (Exception ex) { Log("keep builtin off", ex); }
        }
    }

    /// <summary>恢复内建屏（阻塞，调用方须在后台线程）。守护进程不在时也兜底。</summary>
    public static void RestoreBuiltin()
    {
        Process? g;
        lock (_guardianGate)
        {
            g = _guardian;
            _guardian = null;
            BuiltinOffAt = null;
            BuiltinOffKept = false;
        }
        if (g is not null)
        {
            try
            {
                if (!g.HasExited)
                {
                    g.StandardInput.WriteLine("restore");
                    g.StandardInput.Flush();
                    if (!g.WaitForExit(6000)) g.WaitForExit(3000);
                }
            }
            catch (Exception ex) { Log("restore via guardian", ex); }
        }

        if (!OperatingSystem.IsMacOS())
        {
            TryKillAndDispose(g);
            return;
        }
        try
        {
            uint builtinId = FindBuiltinViaSkyLight();
            if (builtinId == 0) return;       // 这台机器没有内建屏（台式 Mac）

            string? lister = Locate(ListName);
            bool builtinVisible = lister is not null && Run(lister, 2500).Contains("|builtin", StringComparison.Ordinal);
            if (!builtinVisible)
            {
                RestoreBuiltinDirect(builtinId);
            }
        }
        catch (Exception ex) { Log("restore fallback", ex); }
        finally
        {
            TryKillAndDispose(g);
        }
    }

    /// <summary>不经守护进程直接恢复内建屏（等价于原版 `LunarCN --restore-display`）。</summary>
    private static void RestoreBuiltinDirect(uint id)
    {
        try
        {
            var cfg = IntPtr.Zero;
            if (CGBeginDisplayConfiguration(ref cfg) != 0) { Log("restore direct begin", new InvalidOperationException("CGBeginDisplayConfiguration failed")); return; }
            int r = SLSConfigureDisplayEnabled(cfg, id, true);
            if (r != 0) { CGCancelDisplayConfiguration(cfg); Log("restore direct configure", new InvalidOperationException($"code {r}")); return; }
            // 注意：提交之后**不要再碰 cfg**，成败都不碰。
            // CGCompleteDisplayConfiguration 一经调用，配置的所有权就交给了系统；
            // 失败时再调 CGCancelDisplayConfiguration 是对已释放对象的操作（use-after-free）。
            // 随包的 LunarCN 原版（BuiltinDisplayEngine.swift）也是提交失败后直接抛错、不取消。
            int c = CGCompleteDisplayConfiguration(cfg, 1 /* kCGConfigureForSession */);
            if (c != 0) Log("restore direct commit", new InvalidOperationException($"code {c}"));
        }
        catch (Exception ex) { Log("restore direct", ex); }
    }

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", EntryPoint = "CGBeginDisplayConfiguration")]
    private static extern int CGBeginDisplayConfiguration(ref IntPtr config);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", EntryPoint = "CGCancelDisplayConfiguration")]
    private static extern int CGCancelDisplayConfiguration(IntPtr config);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", EntryPoint = "CGCompleteDisplayConfiguration")]
    private static extern int CGCompleteDisplayConfiguration(IntPtr config, int option);
    // SkyLight 私有 API（原版 BuiltinDisplayEngine 同款）。
    // SLSGetDisplayList 第一次调用 ids 传 NULL 只取数量——用 IntPtr 而非数组，避免 .NET 编组 null 数组的歧义。
    [DllImport("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", EntryPoint = "SLSGetDisplayList")]
    private static extern int SLSGetDisplayListCount(uint maxCount, IntPtr ids, ref uint count);
    [DllImport("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", EntryPoint = "SLSGetDisplayList")]
    private static extern int SLSGetDisplayList(uint maxCount, [Out] uint[] ids, ref uint count);
    // bool 必须按 1 字节编组（Swift/ObjC 的 Bool 是 1 字节）；.NET 默认 4 字节 Win32 BOOL 会把参数槽填错，
    // 导致 configure 返回 0 但实际传入的是"禁用"而非"启用"。
    [DllImport("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", EntryPoint = "SLSConfigureDisplayEnabled")]
    private static extern int SLSConfigureDisplayEnabled(IntPtr config, uint display, [MarshalAs(UnmanagedType.I1)] bool enabled);

    private static void TryKillAndDispose(Process? p)
    {
        if (p is null) return;
        try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(1000); } }
        catch (Exception ex) { Log("kill guardian", ex); }
        finally { try { p.Dispose(); } catch { } }
    }

    // ---------- 工具定位：只用随包编译的三个工具（在 BaseDirectory 下，与可执行文件同级） ----------
    private static string? Locate(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>起子进程并取 stdout；stdout/stderr 异步排空防管道满阻塞（见审查 M-1）。</summary>
    private static string Run(string file, int timeoutMs, params string[] args)
        => RunCore(file, timeoutMs, out _, args);

    /// <summary>
    /// 同 Run，但额外报告子进程是否"正常退出"（没被超时 kill）。
    /// 用于区分"显示器不支持该 VCP"（正常退出，可永久记住）和"这次没响应"（超时，只该重试）。
    /// </summary>
    private static string RunCore(string file, int timeoutMs, out bool exitedCleanly, params string[] args)
    {
        exitedCleanly = false;
        return RunCore(file, timeoutMs, out exitedCleanly, out _, args);
    }

    /// <summary>
    /// 同 RunCore，但额外报告子进程的**退出码**。
    /// ddc-helper 的退出码约定：0=成功，3=显示器明确不支持该 VCP，1=其它失败（连接问题等）。
    /// 只有 3 才允许永久跳过该 VCP；1 是临时失败，重试有意义。
    /// </summary>
    private static string RunCore(string file, int timeoutMs, out bool exitedCleanly, out int exitCode, params string[] args)
    {
        exitedCleanly = false;
        exitCode = -1;
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动 {file}");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } p.WaitForExit(500); }
        else
        {
            exitedCleanly = true;   // 内自行退出 = 不是超时
            try { exitCode = p.ExitCode; } catch { }
        }
        string o = outTask.Wait(500) ? outTask.Result : "";
        errTask.Wait(100);
        return o;
    }
}
