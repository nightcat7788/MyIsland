using System;
using System.Collections.Generic;
using System.IO;

namespace Island.App.Services;

/// <summary>一条暂存文件。</summary>
public sealed record FileTrayItem(DateTime At, string Name, string Path, long Bytes);

/// <summary>
/// 文件中转：记录拖入文件的路径（不复制文件本身），供"再拖出/打开所在目录"。
/// 纯本地、退出即清（可选保留）。最多 20 条。
/// </summary>
public static class FileTrayService
{
    private static readonly object _gate = new();
    private static readonly List<FileTrayItem> _items = new();
    private const int Max = 20;

    public static List<FileTrayItem> Items { get { lock (_gate) return new List<FileTrayItem>(_items); } }
    public static int Count { get { lock (_gate) return _items.Count; } }

    /// <summary>暂存一批文件路径（去重，保持顺序）。</summary>
    public static void Add(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
                if (_items.Exists(i => i.Path == p)) continue;
                var fi = new FileInfo(p);
                _items.Insert(0, new FileTrayItem(DateTime.Now, fi.Name, p, fi.Length));
            }
            while (_items.Count > Max) _items.RemoveAt(_items.Count - 1);
        }
    }

    public static void Remove(string path) { lock (_gate) _items.RemoveAll(i => i.Path == path); }
    public static void Clear() { lock (_gate) _items.Clear(); }

    public static string FormatBytes(long b) =>
        b < 1024 ? $"{b} B" : b < 1 << 20 ? $"{b / 1024.0:F1} KB" :
        b < 1 << 30 ? $"{b / 1048576.0:F1} MB" : $"{b / 1073741824.0:F2} GB";
}
