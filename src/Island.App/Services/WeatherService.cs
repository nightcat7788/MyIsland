using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Island.App.Services;

/// <summary>一次天气读数。</summary>
public sealed record WeatherInfo(
    string City, string Condition, string Emoji,
    double Temp, double FeelsLike, double TempMin, double TempMax,
    int Humidity, double WindKmh, DateTime UpdatedAt)
{
    /// <summary>过期阈值：最近一次成功更新距今超过这么久就算过期。</summary>
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(30);

    /// <summary>距最近一次成功更新的时长。</summary>
    public TimeSpan Age => DateTime.Now - UpdatedAt;

    /// <summary>
    /// 是否已过期（最近一次成功更新超过 30 分钟）。
    /// 网络失败时旧值仍会返回，但界面无从分辨"实时"和"旧数据"——
    /// 靠这个标记区分，界面才能提示"数据可能已过期"。
    /// </summary>
    public bool IsStale => Age > StaleThreshold;
}

/// <summary>
/// 天气：Open-Meteo 免费无 Key（wisland 同款默认源）。
/// 定位：优先 Settings.City（"北京" 等中文名走 geocoding API），
/// 未配置则用 IP 定位接口（ipapi.co，免 Key）。
/// 30 分钟缓存；失败保留上次数据并进入退避重试（60 秒起，封顶 10 分钟）。
/// </summary>
public sealed class WeatherService : IDisposable
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private WeatherInfo? _last;
    private DateTime _lastFetch;
    private string? _resolvedCity;
    private (double lat, double lon)? _coords;
    private readonly object _gate = new();
    private Task? _fetching;

    /// <summary>
    /// 失败后的重试间隔：首次 60 秒，逐次翻倍，封顶 10 分钟。
    /// 为什么要有：原来失败时 _lastFetch 不更新，Get() 一直判 stale，
    /// 但宿主只有 30 分钟的定时器会再调 Get() → 一次失败可以空白半小时。
    /// </summary>
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(10);

    /// <summary>当前退避时长（成功一次后重置回 MinRetryDelay）。</summary>
    private TimeSpan _retryDelay = MinRetryDelay;

    /// <summary>下一次允许发起请求的时间；DateTime.MinValue = 不在退避期。</summary>
    private DateTime _retryAfter = DateTime.MinValue;

    /// <summary>自愈重试定时器：失败后由服务自己按退避时间再拉一次，不必等宿主的 30 分钟定时器。</summary>
    private System.Threading.Timer? _retryTimer;

    /// <summary>已释放：不再发起新的请求（Dispose 后仍可能有已排队的重试回调）。</summary>
    private bool _disposed;

    public Settings Settings { get; set; } = new();

    /// <summary>当前天气（可能为缓存；null=从未成功）。</summary>
    public WeatherInfo? Current { get { lock (_gate) return _last; } }

    /// <summary>城市变更后调用：作废缓存，下次 Get() 立即重新定位并拉取。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _lastFetch = DateTime.MinValue;
            _resolvedCity = null;
            _coords = null;
            // 换城市是用户的主动操作：清掉退避，立刻重试（别让用户等 60 秒）
            _retryAfter = DateTime.MinValue;
            _retryDelay = MinRetryDelay;
        }
    }

    /// <summary>
    /// 取天气：缓存新鲜（30 分钟）直接返回，否则后台刷新。
    /// 失败后进入退避（首次 60 秒，逐次翻倍，封顶 10 分钟）：退避期内不再发请求，
    /// 免得断网时每次取用都打一次网络。
    /// </summary>
    public WeatherInfo? Get()
    {
        lock (_gate)
        {
            if (_disposed) return _last;
            bool stale = _last is null || (DateTime.Now - _lastFetch).TotalMinutes > 30;
            if (!stale) return _last;                                  // 缓存还新鲜
            if (DateTime.Now < _retryAfter) return _last;              // 退避中：等自愈重试
            if (!(_fetching is null || _fetching.IsCompleted)) return _last;  // 已有请求在飞
            _fetching = Task.Run(FetchAsync);
            return _last;
        }
    }

    private async Task FetchAsync()
    {
        bool ok = false;
        try
        {
            var (lat, lon, city) = await ResolveLocationAsync();
            if (city is null) return;
            lock (_gate) _resolvedCity = city;

            string url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}") +
                         "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m" +
                         "&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto";
            string json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var cur = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");

            double temp = cur.GetProperty("temperature_2m").GetDouble();
            double feels = cur.GetProperty("apparent_temperature").GetDouble();
            int humidity = cur.GetProperty("relative_humidity_2m").GetInt32();
            int code = cur.GetProperty("weather_code").GetInt32();
            double wind = cur.GetProperty("wind_speed_10m").GetDouble();
            double tmax = daily.GetProperty("temperature_2m_max")[0].GetDouble();
            double tmin = daily.GetProperty("temperature_2m_min")[0].GetDouble();

            var (cond, emoji) = Describe(code);
            var info = new WeatherInfo(city, cond, emoji, temp, feels, tmin, tmax, humidity, wind, DateTime.Now);
            lock (_gate)
            {
                _last = info;
                _lastFetch = DateTime.Now;
                _coords = (lat, lon);
                // 成功一次：退避重置，下次失败重新从 60 秒开始
                _retryDelay = MinRetryDelay;
                _retryAfter = DateTime.MinValue;
            }
            ok = true;
        }
        catch
        {
            // 网络失败保留旧数据，但旧数据会带上 IsStale 标记（界面能分辨是不是实时数据）
        }
        finally
        {
            // 失败：不更新 _lastFetch（保持 stale），但排一次"退避后自动重试"，
            // 否则只有宿主 30 分钟的定时器会再调 Get()，天气最长空白半小时
            if (!ok) ScheduleRetry();
        }
    }

    /// <summary>
    /// 安排一次退避后的自动重试：按当前退避时长（60 秒起，逐次翻倍，封顶 10 分钟）重拉一次。
    /// 退避期内 Get() 不再发起请求，避免断网时每次取用都打一次网络。
    /// </summary>
    private void ScheduleRetry()
    {
        lock (_gate)
        {
            if (_disposed) return;   // 已释放：不再排重试
            TimeSpan delay = _retryDelay;
            _retryAfter = DateTime.Now + delay;
            // 逐次翻倍，封顶 10 分钟：断网时也不至于把接口打爆
            _retryDelay = TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
            _retryTimer ??= new System.Threading.Timer(_ => RetryTick(), null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _retryTimer.Change(delay, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// 退避到期：清掉退避标记后走正常取用路径重拉一次。
    /// 必须先清标记：定时器可能比计划时刻早几毫秒触发，
    /// 若此时 _retryAfter 还没到，Get() 会直接返回且不再排下一次重试 → 又要等 30 分钟。
    /// </summary>
    private void RetryTick()
    {
        try
        {
            lock (_gate) _retryAfter = DateTime.MinValue;
            Get();
        }
        catch { }
    }

    /// <summary>定位：Settings.City 中文名 → Open-Meteo geocoding；否则 IP 定位。</summary>
    private async Task<(double lat, double lon, string? city)> ResolveLocationAsync()
    {
        string? want = Settings.City;
        if (!string.IsNullOrWhiteSpace(want))
        {
            // 地理编码单独包 try：原来它的 HTTP 调用不在 try 内，
            // 端点一挂（超时/DNS 失败）整个定位直接抛出去 → 连 IP 兜底都不走
            try
            {
                string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(want)}&count=1&language=zh";
                string json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                {
                    var r = results[0];
                    return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(),
                            r.GetProperty("name").GetString() ?? want);
                }
            }
            catch { /* 地理编码端点不可用 → 继续走 IP 兜底 */ }
        }
        // IP 定位兜底：多源逐个尝试。
        // 原来只有 ipapi.co 一个源——它对匿名请求有严格限流（实测 429 RateLimited，
        // 2026-11-24），一旦限流整条天气链路就断，时钟胶囊永远没有天气。
        // 逐个试，任一成功即可；全失败才返回"无定位"（绝不伪造坐标）。
        foreach (var probe in IpLocators)
        {
            try
            {
                string json = await _http.GetStringAsync(probe.Url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                double lat = probe.Lat(root), lon = probe.Lon(root);
                if (lat is < -90 or > 90 || lon is < -180 or > 180) continue;
                string city = probe.City(root) ?? "本地";
                return (lat, lon, city);
            }
            catch { /* 这个源不可用，试下一个 */ }
        }
        // 定位失败 = 没有城市信息，返回 null 让 FetchAsync 直接放弃本轮（不发请求、不产数据）。
        // 以前这里兜底返回北京坐标 (39.9, 116.4) + "本地"：IP 定位和地理编码都挂的时候，
        // 界面会把北京的天气当成本机实时数据显示 —— 用户完全无从分辨（2026-11-23 复审 F-1 假修复）。
        // 绝不伪造：宁可空白，也不冒充。
        return (0, 0, null);
    }

    /// <summary>IP 定位备用源（依次尝试）。每个源用不同的 JSON 结构，取值方式不同。</summary>
    private sealed record IpLocator(string Url, Func<JsonElement, double> Lat, Func<JsonElement, double> Lon,
                                  Func<JsonElement, string?> City)
    {
        public static IpLocator Get(string url, string latKey, string lonKey, string cityKey)
            => new(url,
                  r => r.GetProperty(latKey).GetDouble(),
                  r => r.GetProperty(lonKey).GetDouble(),
                  r => r.TryGetProperty(cityKey, out var c) ? c.GetString() : null);

        public static IpLocator GetNested(string url, string[] path, string latKey, string lonKey, string cityKey)
            => new(url,
                  r => GetNested(r, path).GetProperty(latKey).GetDouble(),
                  r => GetNested(r, path).GetProperty(lonKey).GetDouble(),
                  r => GetNested(r, path).TryGetProperty(cityKey, out var c) ? c.GetString() : null);

        private static JsonElement GetNested(JsonElement root, string[] path)
        {
            var cur = root;
            foreach (var seg in path) cur = cur.GetProperty(seg);
            return cur;
        }
    }

    private static readonly IpLocator[] IpLocators =
    {
        // 主源：ipapi.co（字段最全，含城市名）——但匿名限流严，429 时自动让位
        IpLocator.Get("https://ipapi.co/json/", "latitude", "longitude", "city"),
        // 备用 1：ipwho.is（免费、无严格限流，实测可用）
        IpLocator.Get("https://ipwho.is/", "latitude", "longitude", "city"),
    };

    /// <summary>WMO weather code → 中文描述 + emoji。</summary>
    private static (string, string) Describe(int code) => code switch
    {
        0 => ("晴", "☀️"),
        1 or 2 => ("少云", "🌤️"),
        3 => ("阴", "☁️"),
        45 or 48 => ("雾", "🌫️"),
        >= 51 and <= 57 => ("毛毛雨", "🌦️"),
        61 or 63 or 65 => ("雨", "🌧️"),
        66 or 67 => ("冻雨", "🌧️"),
        71 or 73 or 75 => ("雪", "🌨️"),
        77 => ("米雪", "🌨️"),
        80 or 81 or 82 => ("阵雨", "🌧️"),
        85 or 86 => ("阵雪", "🌨️"),
        95 => ("雷暴", "⛈️"),
        96 or 99 => ("冰雹雷暴", "⛈️"),
        _ => ("未知", "🌡️"),
    };

    public void Dispose()
    {
        // 退避重试定时器也要停：否则窗口关了还会自己再拉一次天气
        lock (_gate)
        {
            _disposed = true;
            _retryTimer?.Dispose();
            _retryTimer = null;
        }
    }
}
