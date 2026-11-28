using Island.App.Services;

namespace Island.Tests;

/// <summary>天气请求代次测试。HTTP 全由内存替身完成，测试不访问网络。</summary>
internal static class WeatherTests
{
    private const string OldGeo = """{"results":[{"latitude":31.0,"longitude":121.0,"name":"Old"}]}""";
    private const string NewGeo = """{"results":[{"latitude":22.0,"longitude":114.0,"name":"New"}]}""";
    private const string OldWeather = """
        {"current":{"temperature_2m":99.0,"apparent_temperature":99.0,"relative_humidity_2m":50,"weather_code":0,"wind_speed_10m":1.0},"daily":{"temperature_2m_max":[100.0],"temperature_2m_min":[90.0]}}
        """;
    private const string NewWeather = """
        {"current":{"temperature_2m":22.0,"apparent_temperature":23.0,"relative_humidity_2m":60,"weather_code":1,"wind_speed_10m":2.0},"daily":{"temperature_2m_max":[25.0],"temperature_2m_min":[18.0]}}
        """;

    public static void Run(Action<string, bool> check)
        => StaleResponseCannotOverwrite(check).GetAwaiter().GetResult();

    private static async Task StaleResponseCannotOverwrite(Action<string, bool> check)
    {
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var service = new WeatherService { Settings = new Settings { City = "Old" } };
        service.HttpGetForTest = (url, cancellationToken) =>
        {
            // 旧请求故意忽略取消，模拟已经抵达传输层、无法及时撤回的响应。
            if (url.Contains("name=Old", StringComparison.Ordinal)) return Task.FromResult(OldGeo);
            if (url.Contains("name=New", StringComparison.Ordinal)) return Task.FromResult(NewGeo);
            if (url.Contains("latitude=31", StringComparison.Ordinal))
            {
                oldStarted.TrySetResult();
                return oldResponse.Task;
            }
            if (url.Contains("latitude=22", StringComparison.Ordinal)) return Task.FromResult(NewWeather);
            throw new InvalidOperationException("unexpected test URL: " + url);
        };

        service.Get();
        check("旧城市天气请求已进入在途状态", oldStarted.Task.Wait(2000));
        Task? oldFetch = service.FetchingForTest;

        service.Settings = new Settings { City = "New" };
        service.Invalidate();
        service.Get();

        bool newCommitted = await WaitUntil(() => service.Current?.City == "New", 2000);
        check("旧请求未阻塞新城市立即刷新", newCommitted);
        check("新城市结果先提交且数值正确",
            service.Current is { City: "New", Temp: 22.0 });

        oldResponse.TrySetResult(OldWeather);
        if (oldFetch is not null) await oldFetch.WaitAsync(TimeSpan.FromSeconds(2));
        check("旧请求完全结束后也不能覆盖新城市",
            service.Current is { City: "New", Temp: 22.0 });
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
