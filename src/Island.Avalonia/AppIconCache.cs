using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Island.Avalonia;

/// <summary>
/// 应用图标缓存：用随包探针 <c>app-icon</c> 取 .app 包的图标（NSWorkspace），
/// 供应用榜显示真实图标（对齐 BrimDeck）。
///
/// 设计要点：
/// - **一次性批量取**：攒够一批路径再起一次子进程（每起一次 ~100ms，不能每个进程起一次）。
/// - **结果缓存**：路径 → PNG 字节；进程生命周期内不再问。
/// - **拿不到就空**：非 .app（命令行工具等）返回空，UI 用首字母兜底，不显示假图标。
/// </summary>
public sealed class AppIconCache
{
    private readonly ConcurrentDictionary<string, byte[]> _cache = new();
    private readonly ConcurrentDictionary<string, bool> _known = new();
    private readonly List<string> _pending = new();
    private readonly object _gate = new();
    private readonly string? _toolPath;

    public AppIconCache(string? toolPath) => _toolPath = toolPath;

    /// <summary>已缓存的图标数量（诊断用）。</summary>
    
    /// <summary>
    /// 取图标。命中缓存直接返回；未命中则登记到待取批次（由 <see cref="Flush"/> 批量取回）。
    /// 返回 null 表示还没取到 / 取不到，调用方应显示首字母兜底。
    /// </summary>
    public byte[]? Get(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        if (_cache.TryGetValue(exePath, out var hit)) return hit.Length > 0 ? hit : null;
        if (_known.ContainsKey(exePath)) return null;      // 已确认取不到，不再排队
        lock (_gate)
        {
            if (!_known.ContainsKey(exePath) && !_pending.Contains(exePath)) _pending.Add(exePath);
        }
        return null;
    }

    /// <summary>把待取的路径批量问一次探针（后台线程调用）。</summary>
    public void Flush()
    {
        string[] batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToArray();
            _pending.Clear();
        }
        if (_toolPath is null || !File.Exists(_toolPath)) { MarkAllUnknown(batch); return; }

        try
        {
            var psi = new ProcessStartInfo(_toolPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) { MarkAllUnknown(batch); return; }
            // 写路径（探针读到 EOF 结束）
            foreach (var path in batch)
            {
                p.StandardInput.WriteLine(path);
                p.StandardInput.Flush();
            }
            p.StandardInput.Close();

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { /* 失败降级：保留默认值/上次值 */ } }
            string text = outTask.Wait(500) ? outTask.Result : "";
            errTask.Wait(100);

            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                string path = line[..tab].Trim();
                string b64 = line[(tab + 1)..].Trim();
                if (b64.Length == 0) { _known[path] = true; continue; }
                try { _cache[path] = Convert.FromBase64String(b64); }
                catch { _known[path] = true; }
            }
            // 探针没回的（超时/崩溃）标记为已知取不到，避免每帧重复排队
            foreach (var path in batch) _known.TryAdd(path, true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[app-icon] {ex.GetType().Name}: {ex.Message}");
            MarkAllUnknown(batch);
        }
    }

    private void MarkAllUnknown(IEnumerable<string> paths)
    {
        foreach (var p in paths) _known[p] = true;
    }
}
