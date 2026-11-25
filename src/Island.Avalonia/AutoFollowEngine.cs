namespace Island.Avalonia;

/// <summary>
/// 自动亮度 · 跟随内建屏。逐条对齐参考版（LunarCN 0.6.1）：
///
/// AutomaticBrightnessPolicy.target：
///   目标 = 内建屏百分比钳到该屏范围；差值 &lt; 1.5% 不动（死区）；每轮最多走 ±5%。
/// AutomaticBrightnessEngine.checkSource：
///   源屏必须是**内建 + active + 未休眠**的内建屏，否则暂停，外接屏保持当前亮度。
/// AutomaticBrightnessEngine.sample：
///   只对"开启了跟随、亮度读得到、且亮度项没被暂停"的外接屏调整；**只动亮度**（VCP 16）；
///   写完回读；某一屏失败只暂停那一屏，不影响其它屏。
///
/// 手动拖动优先：用户正在拖 / 有待写目标的屏本轮跳过（旧实现靠 IsDragging 回调，现由宿主注入）。
/// </summary>
internal sealed class AutoFollowEngine : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private volatile bool _enabled;

    /// <summary>某屏当前是否被用户占着（正在拖 / 有待写目标）。只读内存标志，不起子进程。</summary>
    public Func<string, bool> IsBusy { get; set; } = _ => false;
    /// <summary>状态文本（面板显示）。</summary>
    public string Status { get; private set; } = "自动亮度已暂停";

    public AutoFollowEngine()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>开始自动跟随（幂等）。</summary>
    public void Start()
    {
        if (_enabled) return;
        _enabled = true;
        _timer.Change(0, 2000);
    }

    /// <summary>暂停自动跟随（幂等）。</summary>
    public void Stop()
    {
        if (!_enabled) return;
        _enabled = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        Status = "自动亮度已暂停";
    }

    /// <summary>立刻跑一轮（用户刚点了"允许重试"或刚开启跟随时用）。</summary>
    public void PollNow()
    {
        if (_enabled) _timer.Change(0, 2000);
    }

    /// <summary>
    /// 移植 AutomaticBrightnessPolicy.target：返回下一步目标百分比，null=无需调整。
    /// 与参考版逐位对齐：死区 1.5、步长 ±5、按范围钳制；换算成 raw 后与当前相同则不动。
    /// </summary>
    public static double? NextTarget(double source, double current, (int min, int max) range, int currentRaw = -1, int maximum = 100)
    {
        if (double.IsNaN(source) || double.IsInfinity(source) || source < 0 || source > 100) return null;
        double desired = Math.Clamp(source, range.min, range.max);
        bool inRange = current >= range.min && current <= range.max;
        if (inRange && Math.Abs(desired - current) < 1.5) return null;
        double step = Math.Clamp(desired - current, -5, 5);
        double next = Math.Clamp(current + step, range.min, range.max);
        // 参考版：raw 值与当前相同就不写（避免硬件步进不够时反复发同一个值）
        if (currentRaw >= 0 && maximum > 0 && (int)Math.Round(next / 100 * maximum) == currentRaw) return null;
        return next;
    }

    private void Tick()
    {
        if (!_enabled) return;
        lock (_gate)
        {
            try
            {
                // 没有任何屏开启跟随时，不碰 DDC 总线也不读 plist（空闲零开销）
                var settings = LunarSettings.Read();
                if (!settings.Any(s => s.FollowBuiltin))
                {
                    Status = "自动亮度未开启";
                    return;
                }

                // ---- 源屏校验（对齐 checkSource）----
                var screens = DisplayControl.ListNames();
                var builtin = screens.FirstOrDefault(s => s.Builtin);
                if (builtin is null)
                {
                    Status = "未找到活动的内建屏，自动亮度已暂停；外接屏保持当前亮度。";
                    return;
                }
                if (!DisplayControl.IsBuiltinUsableAsSource(builtin.Id))
                {
                    Status = "内建屏已关闭或休眠，自动亮度已暂停；外接屏保持当前亮度。";
                    return;
                }
                // 源亮度用 DisplayServices 读真实值（旧实现用 CoreDisplay，本机恒为 1.0 → 源永远 100%）
                var src = DisplayControl.BuiltinBrightness.Read(builtin.Id);
                if (!src.Ok || src.Level is not { } srcLevel)
                {
                    Status = "无法读取内建屏，已暂停跟随：" + (src.Message ?? "");
                    return;
                }
                double source = srcLevel.Percent;

                int moved = 0; var failures = new List<string>();
                foreach (var s in screens.Where(s => !s.Builtin))
                {
                    var st = settings.FirstOrDefault(k => k.Uuid == s.Uuid);
                    if (st?.FollowBuiltin != true) continue;
                    if (IsBusy(s.Uuid)) continue;                                                      // 用户正在手动调
                    if (DisplayControl.IsWritePaused(s.Uuid, DisplayControl.VcpBrightness)) continue;  // 该屏亮度项已暂停
                    if (!s.BrightnessOk) { failures.Add($"{s.Name}：亮度读不到"); continue; }

                    // 目标屏当前亮度：回读设备（不信任可能过期的缓存）
                    var cur = DisplayControl.ReadLevel(s.Uuid, s.Id, DisplayControl.VcpBrightness);
                    if (!cur.Ok || cur.Level is not { } curLevel) { failures.Add($"{s.Name}：亮度读取失败"); continue; }

                    var next = NextTarget(source, curLevel.Percent, (st.Min, st.Max), curLevel.Current, curLevel.Maximum);
                    if (next is not { } target) continue;

                    var res = DisplayControl.SetPercentEx(s.Uuid, s.Id, DisplayControl.VcpBrightness, (int)Math.Round(target));
                    if (res.Ok) moved++;
                    else failures.Add($"{s.Name}：{(res.Status == DisplayControl.DdcStatus.WriteUnconfirmed ? "写入未确认，已暂停" : res.Message ?? "写入失败")}");
                }

                Status = failures.Count > 0
                    ? "自动亮度有屏暂停：" + string.Join("；", failures)
                    : $"正在跟随内建屏：{source:F0}% · 已调 {moved} 块";
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[auto-follow] {ex.Message}");
                Status = "自动亮度读取失败，下轮重试";
            }
        }
    }

    public void Dispose() => _timer.Dispose();
}
