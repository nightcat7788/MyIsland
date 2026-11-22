using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Island.App.Services;

/// <summary>一次天气读数。</summary>
public sealed record WeatherInfo(
    string City, string Condition, string Emoji,
    double Temp, double FeelsLike, double TempMin, double TempMax,
    int Humidity, double WindKmh, DateTime UpdatedAt);

/// <summary>
/// 天气：Open-Meteo 免费无 Key（wisland 同款默认源）。
/// 定位：优先 Settings.City（"北京" 等中文名走 geocoding API），
/// 未配置则用 IP 定位接口（ipapi.co，免 Key）。
/// 30 分钟缓存；失败保留上次数据（超过 3 小时才标记过期）。
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

    public Settings Settings { get; set; } = new();

    /// <summary>当前天气（可能为缓存；null=从未成功）。</summary>
    public WeatherInfo? Current { get { lock (_gate) return _last; } }

    /// <summary>城市变更后调用：作废缓存，下次 Get() 立即重新定位并拉取。</summary>
    public void Invalidate()
    {
        lock (_gate) { _lastFetch = DateTime.MinValue; _resolvedCity = null; _coords = null; }
    }

    /// <summary>取天气：缓存新鲜（30 分钟）直接返回，否则后台刷新。</summary>
    public WeatherInfo? Get()
    {
        bool stale;
        lock (_gate) stale = _last is null || (DateTime.Now - _lastFetch).TotalMinutes > 30;
        if (stale && _fetching is null or { IsCompleted: true })
            _fetching = Task.Run(FetchAsync);
        return Current;
    }

    private async Task FetchAsync()
    {
        try
        {
            var (lat, lon, city) = await ResolveLocationAsync();
            if (city is null) return;
            lock (_gate) _resolvedCity = city;

            string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
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
            lock (_gate) { _last = info; _lastFetch = DateTime.Now; _coords = (lat, lon); }
        }
        catch { /* 网络失败保留旧数据 */ }
    }

    /// <summary>定位：Settings.City 中文名 → Open-Meteo geocoding；否则 IP 定位。</summary>
    private async Task<(double lat, double lon, string? city)> ResolveLocationAsync()
    {
        string? want = Settings.City;
        if (!string.IsNullOrWhiteSpace(want))
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
        // IP 定位兜底
        try
        {
            string json = await _http.GetStringAsync("https://ipapi.co/json/");
            using var doc = JsonDocument.Parse(json);
            return (doc.RootElement.GetProperty("latitude").GetDouble(),
                    doc.RootElement.GetProperty("longitude").GetDouble(),
                    doc.RootElement.GetProperty("city").GetString() ?? "本地");
        }
        catch { return (_coords?.lat ?? 39.9, _coords?.lon ?? 116.4, _resolvedCity ?? "本地"); }
    }

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

    public void Dispose() { }
}
