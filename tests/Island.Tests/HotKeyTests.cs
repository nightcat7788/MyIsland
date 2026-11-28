using Island.Avalonia;

namespace Island.Tests;

/// <summary>热键回调调度测试：不启动探针，不依赖 Avalonia 运行时。</summary>
internal static class HotKeyTests
{
    public static void Run(Action<string, bool> check)
    {
        var queued = new Queue<Action>();
        using var client = new HotKeyClient(queued.Enqueue);
        bool ran = false;

        client.DispatchCallback(() => ran = true);
        check("热键回调不会在当前后台线程直接执行", !ran && queued.Count == 1);

        queued.Dequeue()();
        check("热键回调由注入的 UI 调度器执行", ran && queued.Count == 0);

        // 回调自身失败不能沿 OutputDataReceived 线程冒出，影响其他热键。
        client.DispatchCallback(() => throw new InvalidOperationException("test"));
        check("异常热键回调仍进入调度队列", queued.Count == 1);
        bool escaped = false;
        try { queued.Dequeue()(); } catch { escaped = true; }
        check("异常回调被隔离", !escaped);
    }
}
