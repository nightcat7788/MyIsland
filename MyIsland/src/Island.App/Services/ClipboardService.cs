using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Island.App.Services;

/// <summary>一条剪贴板记录。</summary>
public sealed record ClipboardItem(DateTime At, string Kind, string Preview, string Full);

/// <summary>
/// 剪贴板历史（纯本地，不落盘，最多 50 条）。
/// AddClipboardFormatListener 接收 WM_CLIPBOARDUPDATE；文本直接取，
/// 图片存缩略预览说明（"图片 800x600"）。无隐私外泄：一切只在内存。
/// </summary>
public static class ClipboardService
{
    private static readonly object _gate = new();
    private static readonly List<ClipboardItem> _items = new();
    private static HwndSourceHook? _hook;
    private static IntPtr _hwnd;
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int Max = 50;

    /// <summary>最近一条预览（收起态显示）。</summary>
    public static string LastPreview { get { lock (_gate) return _items.Count == 0 ? "" : _items[0].Preview; } }

    /// <summary>记录条数。</summary>
    public static int Count { get { lock (_gate) return _items.Count; } }

    /// <summary>历史副本（新→旧）。</summary>
    public static List<ClipboardItem> Items { get { lock (_gate) return new List<ClipboardItem>(_items); } }

    private static HwndSource? _source;

    /// <summary>在主窗口句柄上挂剪贴板监听（窗口加载后调用）。</summary>
    public static void Attach(IntPtr hwnd)
    {
        if (_source is not null) return;
        _hwnd = hwnd;
        if (!AddClipboardFormatListener(hwnd)) return;
        var src = HwndSource.FromHwnd(hwnd);
        if (src is null) return;
        _source = src;
        _hook = WndProc;
        src.AddHook(_hook);
    }

    public static void Detach()
    {
        if (_source is not null && _hook is not null) _source.RemoveHook(_hook);
        if (_hwnd != IntPtr.Zero) RemoveClipboardFormatListener(_hwnd);
        _source = null;
        _hook = null;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            try { Record(); } catch { }
            handled = false;
        }
        return IntPtr.Zero;
    }

    private static void Record()
    {
        if (!Clipboard.ContainsText() && !Clipboard.ContainsImage()) return;

        string kind, preview, full;
        if (Clipboard.ContainsText())
        {
            full = Clipboard.GetText() ?? "";
            if (string.IsNullOrWhiteSpace(full)) return;
            kind = "text";
            preview = full.Length > 40 ? full.Substring(0, 40) + "…" : full;
            preview = preview.Replace("\r", "").Replace("\n", " ");
        }
        else
        {
            var img = Clipboard.GetImage();
            kind = "image";
            full = "";
            preview = $"图片 {img.Width:F0}x{img.Height:F0}";
        }

        lock (_gate)
        {
            // 去重：与最近一条相同则跳过
            if (_items.Count > 0 && _items[0].Full == full && _items[0].Kind == kind) return;
            _items.Insert(0, new ClipboardItem(DateTime.Now, kind, preview, full));
            if (_items.Count > Max) _items.RemoveAt(_items.Count - 1);
        }
    }

    /// <summary>清空历史（托盘菜单入口）。</summary>
    public static void Clear() { lock (_gate) _items.Clear(); }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
