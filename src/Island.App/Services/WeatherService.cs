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
    private static readonly HttpClient _http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MyIsland/1.1.6");
        return client;
    }
    private WeatherInfo? _last;
    private DateTime _lastFetch;
    private string? _resolvedCity;
    private (double lat, double lon)? _coords;
    private readonly object _gate = new();
    private Task? _fetching;
    private CancellationTokenSource? _fetchCts;
    private int _generation;

    /// <summary>测试用 HTTP 替身；生产环境保持 null，仍由共享 HttpClient 发请求。</summary>
    internal Func<string, CancellationToken, Task<string>>? HttpGetForTest { get; set; }

    /// <summary>测试用：捕获当前代次任务，以便验证旧任务完全结束后也不能提交结果。</summary>
    internal Task? FetchingForTest { get { lock (_gate) return _fetching; } }

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

    /// <summary>
    /// 城市变更后调用：作废旧缓存与旧请求。旧请求即使无视取消并稍后完成，
    /// 也会因为代次不匹配而被丢弃，不能覆盖新城市的结果或重试状态。
    /// </summary>
    public void Invalidate()
    {
        CancellationTokenSource? old;
        lock (_gate)
        {
            _generation++;
            _last = null;
            _lastFetch = DateTime.MinValue;
            _resolvedCity = null;
            _coords = null;
            // 换城市是用户的主动操作：清掉退避，立刻重试（别让用户等 60 秒）
            _retryAfter = DateTime.MinValue;
            _retryDelay = MinRetryDelay;
            old = _fetchCts;
            _fetchCts = null;
            // 允许新城市立即发起请求，不被一个可能忽略取消的旧请求挡住。
            _fetching = null;
        }
        if (old is not null)
        {
            try { old.Cancel(); } catch (ObjectDisposedException) { }
            old.Dispose();
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
            if (!(_fetching is null || _fetching.IsCompleted)) return _last;

            _fetchCts?.Dispose();
            var cts = new CancellationTokenSource();
            _fetchCts = cts;
            int generation = _generation;
            _fetching = Task.Run(() => FetchAsync(generation, cts.Token));
            return _last;
        }
    }

    private async Task FetchAsync(int generation, CancellationToken cancellationToken)
    {
        bool ok = false;
        try
        {
            // 每次请求固定使用开始时的城市配置；后续 Settings 替换不会改变本轮语义。
            string? wantedCity = Settings.City;
            var (lat, lon, city) = await ResolveLocationAsync(wantedCity, cancellationToken);
            if (city is null) return;
            lock (_gate)
            {
                if (generation == _generation && !_disposed) _resolvedCity = city;
            }

            string url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}") +
                         "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m" +
                         "&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto";
            string json = await GetStringAsync(url, cancellationToken);
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
                // 只有当前代次可以提交。取消/换城市后完成的旧响应到这里直接丢弃。
                if (generation != _generation || _disposed || cancellationToken.IsCancellationRequested) return;
                _last = info;
                _lastFetch = DateTime.Now;
                _coords = (lat, lon);
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
            // 旧代次失败不能给新城市排退避，也不能清掉新城市的请求状态。
            if (!ok) ScheduleRetry(generation);
        }
    }

    private Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
        => HttpGetForTest is { } testGet ? testGet(url, cancellationToken) : _http.GetStringAsync(url, cancellationToken);

    /// <summary>
    /// 安排一次退避后的自动重试：按当前退避时长（60 秒起，逐次翻倍，封顶 10 分钟）重拉一次。
    /// 退避期内 Get() 不再发起请求，避免断网时每次取用都打一次网络。
    /// </summary>
    private void ScheduleRetry(int generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) return;
            TimeSpan delay = _retryDelay;
            _retryAfter = DateTime.Now + delay;
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

    /// <summary>定位：指定城市名 → Open-Meteo geocoding；否则 IP 定位。</summary>
    private async Task<(double lat, double lon, string? city)> ResolveLocationAsync(
        string? want, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(want))
        {
            try
            {
                string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(want)}&count=1&language=zh";
                string json = await GetStringAsync(url, cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                {
                    var r = results[0];
                    return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(),
                            r.GetProperty("name").GetString() ?? want);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* 地理编码端点不可用 → 继续走 IP 兜底 */ }
        }
        // IP 定位兜底：多源逐个尝试，任一成功即可；全失败返回"无定位"，绝不伪造坐标。
        foreach (var probe in IpLocators)
        {
            try
            {
                string json = await GetStringAsync(probe.Url, cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                double lat = probe.Lat(root), lon = probe.Lon(root);
                if (lat is < -90 or > 90 || lon is < -180 or > 180) continue;
                string city = probe.City(root) ?? "本地";
                // 若 IP 定位返回英文拼音城市名（如 Shanghai / Beijing），尝试经 Open-Meteo 中文地理库转为中文名（与 macOS 时间胶囊对齐）
                if (HttpGetForTest is null && !string.IsNullOrWhiteSpace(city) && IsAsciiLetters(city))
                {
                    string? zhCity = await TryLocalizeCityNameAsync(city, lat, lon, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(zhCity)) city = zhCity;
                }
                return (lat, lon, city);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* 这个源不可用，试下一个 */ }
        }
        return (0, 0, null);
    }

    private static bool IsAsciiLetters(string s)
    {
        foreach (char ch in s)
            if (ch > 127) return false;
        return true;
    }

    private async Task<string?> TryLocalizeCityNameAsync(string englishCity, double lat, double lon, CancellationToken cancellationToken)
    {
        try
        {
            string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(englishCity)}&count=3&language=zh";
            string json = await GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results)) return null;
            foreach (var r in results.EnumerateArray())
            {
                double rLat = r.GetProperty("latitude").GetDouble();
                double rLon = r.GetProperty("longitude").GetDouble();
                if (Math.Abs(rLat - lat) <= 2.5 && Math.Abs(rLon - lon) <= 2.5)
                    return r.GetProperty("name").GetString();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { }
        return null;
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
        IpLocator.Get("https://ipwho.is/", "latitude", "longitude", "city"),
        IpLocator.Get("https://ipapi.co/json/", "latitude", "longitude", "city"),
        IpLocator.Get("https://freeipapi.com/api/json", "latitude", "longitude", "cityName"),
        new("https://ipinfo.io/json",
            r => double.Parse(r.GetProperty("loc").GetString()!.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture),
            r => double.Parse(r.GetProperty("loc").GetString()!.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture),
            r => r.TryGetProperty("city", out var c) ? c.GetString() : null),
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
        CancellationTokenSource? fetching;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _retryTimer?.Dispose();
            _retryTimer = null;
            fetching = _fetchCts;
            _fetchCts = null;
        }
        if (fetching is not null)
        {
            try { fetching.Cancel(); } catch (ObjectDisposedException) { }
            fetching.Dispose();
        }
    }
}
