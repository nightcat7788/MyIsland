using Island.Core.Monitoring;

namespace Island.Tests;

/// <summary>macOS 采样防重入门禁：慢采样期间的其他计时器回调必须被跳过。</summary>
internal static class SamplerGuardTests
{
    public static void Run(Action<string, bool> check)
    {
        var guard = new SamplerReentrancyGuard();
        check("首个采样可以进入", guard.TryEnter());

        var contenders = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => guard.TryEnter()))
            .ToArray();
        Task.WaitAll(contenders);
        check("在途采样期间所有并发回调都被拒绝", contenders.All(t => !t.Result));

        guard.Exit();
        check("采样结束后下一轮可以进入", guard.TryEnter());
        guard.Exit();
    }
}
