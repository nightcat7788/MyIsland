using System.Diagnostics;

namespace Island.Avalonia;

/// <summary>
/// 全局快捷键客户端：通过随包探针 <c>island-hotkey</c> 注册系统级热键。
///
/// 为什么用 Swift 探针而不是 C# 直接 P/Invoke Carbon：
/// Carbon 的 opaque 引用（EventTargetRef / EventHandlerRef / EventHandlerCallRef）都是
/// **4 字节 UInt32**，C# P/Invoke 里极易声明错导致 ABI 错位 —— InstallEventHandler
/// 里直接段错误、进程被杀（连 .NET 异常都来不及抛，只留一行日志）。实测卡了三轮：
///   ① EventHandlerRef 声明成 IntPtr（8 字节）→ 段错误
///   ② inCallback（EventListenerUPP）传 NULL → 段错误
///   ③ 事件类型传 0（该类所有事件）→ 段错误
/// Swift 侧由编译器保证签名正确，且注册已验证成功（输出 OK &lt;id&gt;）。
///
/// 探针协议（stdin 逐行）：
///   register &lt;id&gt; &lt;keyChar&gt; &lt;mods&gt;    mods: c=cmd a=alt s=shift k=ctrl
///   unregister &lt;id&gt;
/// 探针输出：
///   READY            启动就绪
///   OK &lt;id&gt; / ERR &lt;id&gt; &lt;code&gt;    注册结果
///   FIRED &lt;id&gt;      热键被触发
/// </summary>
public sealed class HotKeyClient : IDisposable
{
    private Process? _proc;
    private uint _nextId = 1;
    private readonly Dictionary<uint, Action> _actions = new();
    private readonly object _gate = new();

    /// <summary>探针是否已就绪（READY 已收到）。</summary>
    public bool Ready { get; private set; }

    /// <summary>注册失败时的说明（供界面提示）。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>
    /// 启动探针并注册一组热键。
    /// </summary>
    /// <param name="toolPath">island-hotkey 可执行文件路径</param>
    /// <param name="bindings">要注册的快捷键：(键字符, cmd, shift, alt, 回调)</param>
    public void Start(string toolPath, IEnumerable<(char Key, bool Cmd, bool Shift, bool Alt, Action Act)> bindings)
    {
        if (!File.Exists(toolPath))
        {
            LastError = "未找到 island-hotkey（随包工具缺失）";
            Console.Error.WriteLine($"[hotkey] {LastError}");
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(toolPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            _proc = Process.Start(psi);
            if (_proc is null) { LastError = "探针启动失败"; return; }
            _proc.OutputDataReceived += (_, e) => OnLine(e.Data);
            _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.Error.WriteLine($"[hotkey] {e.Data}"); };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            foreach (var (key, cmd, shift, alt, act) in bindings)
            {
                uint id = _nextId++;
                lock (_gate) _actions[id] = act;
                string mods = (cmd ? "c" : "") + (alt ? "a" : "") + (shift ? "s" : "");
                try { _proc.StandardInput.WriteLine($"register {id} {key} {mods}"); _proc.StandardInput.Flush(); }
                catch (Exception ex) { Console.Error.WriteLine($"[hotkey] register {key}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine($"[hotkey] {LastError}");
        }
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (line == "READY") { Ready = true; Console.Error.WriteLine("[hotkey] 探针就绪"); return; }
        if (line.StartsWith("FIRED "))
        {
            if (uint.TryParse(line.AsSpan(6), out uint id))
            {
                Action? act;
                lock (_gate) _actions.TryGetValue(id, out act);
                if (act is not null)
                {
                    try { act(); }
                    catch (Exception ex) { Console.Error.WriteLine($"[hotkey] 回调异常: {ex.Message}"); }
                }
            }
            return;
        }
        if (line.StartsWith("ERR "))
        {
            // 探针的 RegisterEventHotKey 失败（-9878 = 已被占用）
            LastError = line.Length > 8 ? $"快捷键注册失败：{line.Substring(8)}（-9878 表示已被其他应用占用）" : "快捷键注册失败";
            Console.Error.WriteLine($"[hotkey] {LastError}");
        }
    }

    public void Dispose()
    {
        var proc = _proc;
        _proc = null;
        if (proc is null) return;
        try { proc.StandardInput.WriteLine("quit"); proc.StandardInput.Flush(); } catch { /* 探针已退出 */ }
        try { if (!proc.HasExited) proc.WaitForExit(500); } catch { /* 探针已退出 */ }
        try { proc.Kill(true); } catch { }
        proc.Dispose();
    }
}
