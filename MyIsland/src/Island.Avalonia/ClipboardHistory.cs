using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Island.Avalonia;

/// <summary>一条剪贴板记录。</summary>
public sealed record ClipItem(DateTime At, string Preview);

/// <summary>
/// 剪贴板历史（跨平台）：轮询 TopLevel.Clipboard，1.5s 一次，纯内存最多 20 条。
/// Avalonia 的 Clipboard API 在 macOS/Windows/Linux 都可用；只读，不写不持久化。
/// </summary>
public sealed class ClipboardHistory : IDisposable
{
    private readonly List<ClipItem> _items = new();
    private readonly object _gate = new();
    private DispatcherTimer? _timer;
    private string? _last;

    public IReadOnlyList<ClipItem> Items { get { lock (_gate) return _items.ToList(); } }

    public void Start(TopLevel top)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += async (_, _) =>
        {
            try
            {
                var clip = top.Clipboard;
                if (clip is null) return;
                string? text = await clip.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text) || text == _last) return;
                _last = text;
                string preview = text.Replace("\r", "").Replace("\n", " ");
                if (preview.Length > 40) preview = preview[..40] + "…";
                lock (_gate)
                {
                    _items.Insert(0, new ClipItem(DateTime.Now, preview));
                    if (_items.Count > 20) _items.RemoveAt(_items.Count - 1);
                }
            }
            catch { /* 剪贴板被占用等：跳过本轮 */ }
        };
        _timer.Start();
    }

    public void Dispose() => _timer?.Stop();
}
