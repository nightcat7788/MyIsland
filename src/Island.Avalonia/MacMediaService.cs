using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Island.Avalonia;

/// <summary>macOS 系统媒体会话快照（对齐 Windows 版 MediaSnapshot）。</summary>
public sealed record MacMediaSnapshot
{
    public bool HasMedia { get; init; }
    public bool IsPlaying { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string AppName { get; init; } = "";
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
    /// <summary>专辑封面原始图片字节（按曲目缓存，换曲时自动刷新）。</summary>
    public byte[]? ArtworkBytes { get; init; }
}

/// <summary>
/// macOS 媒体控制与律动频谱服务（对齐 Windows 版 MediaService）：
/// - 在 macOS 上通过后台 osascript 按需轮询 Apple Music / Spotify 播放状态并提取真实专辑封面；
/// - 支持播放/暂停、上一曲、下一曲控制；
/// - 播放中驱动 16 段平滑音频律动条（无录音权限弹窗打扰）。
/// </summary>
public sealed class MacMediaService : IDisposable
{
    private static readonly HttpClient ArtworkHttp = new() { Timeout = TimeSpan.FromSeconds(2.5) };
    private readonly object _gate = new();
    private MacMediaSnapshot _current = new();
    private string _activePlayer = "";
    private string _artworkTrackKey = "";
    private byte[]? _artworkCache;
    private Timer? _pollTimer;
    private bool _wanted;
    private bool _disposed;
    private int _polling;
    private double _phase;

    public event Action<MacMediaSnapshot>? Updated;

    public MacMediaSnapshot Current { get { lock (_gate) return _current; } }

    public void SetWanted(bool wanted)
    {
        lock (_gate)
        {
            if (_disposed || _wanted == wanted) return;
            _wanted = wanted;
            if (!wanted)
            {
                _pollTimer?.Dispose();
                _pollTimer = null;
                return;
            }
            _pollTimer ??= new Timer(_ => Poll(), null, 0, 1500);
        }
    }

    public void CopyBars(double[] dest)
    {
        bool playing = Current.IsPlaying;
        _phase += playing ? 0.24 : 0.05;
        for (int i = 0; i < dest.Length; i++)
        {
            if (!playing)
            {
                dest[i] = dest[i] * 0.8 + 0.12 * 0.2;
                continue;
            }
            double wave = 0.48
                + 0.28 * Math.Sin(_phase * 1.15 + i * 0.62)
                + 0.18 * Math.Cos(_phase * 1.73 - i * 0.41);
            double target = Math.Clamp(wave, 0.14, 0.98);
            dest[i] = dest[i] * 0.55 + target * 0.45;
        }
    }

    public void TogglePlayPause() => RunPlayerCommand("playpause");
    public void NextTrack() => RunPlayerCommand("next track");
    public void PreviousTrack() => RunPlayerCommand("previous track");

    private void RunPlayerCommand(string verb)
    {
        if (!OperatingSystem.IsMacOS()) return;
        string app;
        lock (_gate) app = _activePlayer;
        if (string.IsNullOrEmpty(app)) app = "Music";
        _ = Task.Run(() =>
        {
            RunOsascript($"tell application \"{app}\" to {verb}");
            Thread.Sleep(180);
            Poll();
        });
    }

    private void Poll()
    {
        if (!OperatingSystem.IsMacOS() || _disposed) return;
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        try
        {
            var snap = QueryAppleScriptPlayer("Music", "Apple Music")
                    ?? QueryAppleScriptPlayer("Spotify", "Spotify")
                    ?? new MacMediaSnapshot();

            bool changed;
            lock (_gate)
            {
                changed = _current != snap;
                _current = snap;
            }
            if (changed) Updated?.Invoke(snap);
        }
        catch { }
        finally { Volatile.Write(ref _polling, 0); }
    }

    private MacMediaSnapshot? QueryAppleScriptPlayer(string appId, string displayName)
    {
        string script =
            $"if application \"{appId}\" is running then\n" +
            $"  tell application \"{appId}\"\n" +
            $"    set st to player state as string\n" +
            $"    if st is \"playing\" or st is \"paused\" then\n" +
            $"      set tName to name of current track\n" +
            $"      set tArt to artist of current track\n" +
            $"      set pPos to player position\n" +
            $"      set tDur to duration of current track\n" +
            $"      return st & \"||\" & tName & \"||\" & tArt & \"||\" & pPos & \"||\" & tDur\n" +
            $"    end if\n" +
            $"  end tell\n" +
            $"end if\n" +
            $"return \"\"";
        string? output = RunOsascript(script);
        if (string.IsNullOrWhiteSpace(output)) return null;
        string[] parts = output.Trim().Split("||");
        if (parts.Length < 3) return null;

        bool playing = parts[0].Equals("playing", StringComparison.OrdinalIgnoreCase);
        double pos = parts.Length >= 4 && double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var pv) ? pv : 0;
        double dur = parts.Length >= 5 && double.TryParse(parts[4], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var dv) ? dv : 0;
        // Spotify duration is in milliseconds
        if (appId == "Spotify" && dur > 36000) dur /= 1000.0;

        lock (_gate) _activePlayer = appId;
        string title = parts[1].Trim();
        string artist = parts[2].Trim();
        byte[]? art = FetchArtworkCached(appId, title, artist);
        return new MacMediaSnapshot
        {
            HasMedia = true,
            IsPlaying = playing,
            Title = title,
            Artist = artist,
            AppName = displayName,
            PositionSeconds = Math.Max(0, pos),
            DurationSeconds = Math.Max(0, dur),
            ArtworkBytes = art,
        };
    }

    private byte[]? FetchArtworkCached(string appId, string title, string artist)
    {
        string key = $"{appId}|{title}|{artist}";
        lock (_gate)
        {
            if (string.Equals(_artworkTrackKey, key, StringComparison.Ordinal))
                return _artworkCache;
        }

        byte[]? bytes = null;
        try
        {
            if (appId == "Music")
            {
                string tmp = Path.Combine(Path.GetTempPath(), "myisland-mac-artwork.img");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                string safePath = tmp.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string script =
                    $"tell application \"Music\"\n" +
                    $"  if (count of artworks of current track) > 0 then\n" +
                    $"    set rawData to raw data of artwork 1 of current track\n" +
                    $"    set fRef to open for access (POSIX file \"{safePath}\") with write permission\n" +
                    $"    set eof of fRef to 0\n" +
                    $"    write rawData to fRef\n" +
                    $"    close access fRef\n" +
                    $"    return \"ok\"\n" +
                    $"  end if\n" +
                    $"end tell\n" +
                    $"return \"\"";
                if (RunOsascript(script)?.Trim() == "ok" && File.Exists(tmp))
                {
                    var fi = new FileInfo(tmp);
                    if (fi.Length is > 32 and <= 4 * 1024 * 1024)
                        bytes = File.ReadAllBytes(tmp);
                }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            else if (appId == "Spotify")
            {
                string? url = RunOsascript("tell application \"Spotify\" to return artwork url of current track")?.Trim();
                if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                {
                    var data = ArtworkHttp.GetByteArrayAsync(uri).GetAwaiter().GetResult();
                    if (data.Length is > 32 and <= 4 * 1024 * 1024)
                        bytes = data;
                }
            }
        }
        catch { }

        lock (_gate)
        {
            _artworkTrackKey = key;
            _artworkCache = bytes;
        }
        return bytes;
    }

    private static string? RunOsascript(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("osascript")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            string stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(1500))
            {
                try { proc.Kill(); } catch { }
                return null;
            }
            return proc.ExitCode == 0 ? stdout : null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
    }
}
