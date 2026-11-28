using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Island.Avalonia;

/// <summary>一条暂存文件记录（对齐 Windows 版 FileTrayService）。</summary>
public sealed record StagedFile(string FullPath, string Name, string SizeText);

/// <summary>
/// macOS 拖拽文件中转架（对齐 Windows 版 FileTrayService）：
/// 拖入灵动岛即可暂存路径，点击在 Finder 中定位，支持单条移除与一键清空。
/// </summary>
public sealed class MacFileTray
{
    private readonly object _gate = new();
    private readonly List<StagedFile> _files = new();
    private const int MaxFiles = 20;

    public event Action? Changed;

    public IReadOnlyList<StagedFile> Items
    {
        get { lock (_gate) return _files.ToList(); }
    }

    public void Add(IEnumerable<string> paths)
    {
        bool dirty = false;
        lock (_gate)
        {
            foreach (string raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string full;
                try { full = Path.GetFullPath(raw); }
                catch { continue; }
                if (!File.Exists(full) && !Directory.Exists(full)) continue;
                _files.RemoveAll(f => f.FullPath.Equals(full, StringComparison.OrdinalIgnoreCase));
                string name = Path.GetFileName(full);
                if (string.IsNullOrEmpty(name)) name = full;
                string sz = Directory.Exists(full) ? "文件夹" : FormatFileSize(full);
                _files.Insert(0, new StagedFile(full, name, sz));
                dirty = true;
            }
            while (_files.Count > MaxFiles) _files.RemoveAt(_files.Count - 1);
        }
        if (dirty) Changed?.Invoke();
    }

    public void Remove(string fullPath)
    {
        bool dirty;
        lock (_gate) dirty = _files.RemoveAll(f => f.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)) > 0;
        if (dirty) Changed?.Invoke();
    }

    public void Clear()
    {
        bool dirty;
        lock (_gate) { dirty = _files.Count > 0; _files.Clear(); }
        if (dirty) Changed?.Invoke();
    }

    public static void Reveal(string fullPath)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(fullPath);
                Process.Start(psi);
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = true });
            }
        }
        catch { }
    }

    private static string FormatFileSize(string path)
    {
        try
        {
            long len = new FileInfo(path).Length;
            if (len < 1024) return $"{len} B";
            if (len < 1024 * 1024) return $"{len / 1024.0:F1} KB";
            if (len < 1024L * 1024 * 1024) return $"{len / (1024.0 * 1024):F1} MB";
            return $"{len / (1024.0 * 1024 * 1024):F2} GB";
        }
        catch { return ""; }
    }
}
