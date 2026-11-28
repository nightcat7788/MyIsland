using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Island.App.Controls;
using NAudio.Wave;
using Windows.Media.Control;

namespace Island.App.Services;

/// <summary>系统当前媒体会话快照（SMTC + 封面主色）。</summary>
public sealed record MediaSnapshot
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string SourceApp { get; init; } = "";
    public bool IsPlaying { get; init; }
    public double PositionSec { get; init; }
    public double DurationSec { get; init; }
    public BitmapSource? Cover { get; init; }
    public Color? AccentColor { get; init; }
    public bool HasMedia => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>
/// Windows 系统媒体传输控制（SMTC）+ 按需 WASAPI Loopback 音频律动采样。
/// - 全部 WinRT / COM / 音频设备访问均在后台线程隔离捕获异常（N 版系统或无声卡机器静默降级）；
/// - 封面按「标题|歌手」缓存解码结果与 DominantColor 主色，避免每轮重复解码流；
/// - WasapiLoopbackCapture 仅在「界面可见且正在播放」时启动，暂停或切走立即停表释放。
/// </summary>
public sealed class MediaService : IDisposable
{
    private readonly object _gate = new();
    private Timer? _pollTimer;
    private int _polling;
    private bool _disposed;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private string _cachedCoverKey = "";
    private BitmapSource? _cachedCover;
    private Color? _cachedAccent;

    private MediaSnapshot _current = new();
    public MediaSnapshot Current { get { lock (_gate) return _current; } }

    public event Action<MediaSnapshot>? Updated;

    // ---------- 按需音频律动频谱（6 柱，0..1） ----------
    private readonly double[] _bars = new double[16];
    private WasapiLoopbackCapture? _loopback;
    private bool _spectrumWanted;

    /// <summary>读取当前 16 段音频律动柱副本（0..1）。</summary>
    public void CopyBars(Span<double> dest)
    {
        lock (_gate)
        {
            int n = Math.Min(dest.Length, _bars.Length);
            for (int i = 0; i < n; i++) dest[i] = _bars[i];
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _pollTimer is not null) return;
            _pollTimer = new Timer(_ => _ = PollOnceAsync(), null, 300, 1500);
        }
    }

    public void SetSpectrumWanted(bool wanted)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _spectrumWanted = wanted;
            SyncLoopbackLocked();
        }
    }

    private async Task PollOnceAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _polling, 1) != 0) return;
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_manager is null) return;

            var session = _manager.GetCurrentSession();
            if (session is null)
            {
                Publish(new MediaSnapshot());
                return;
            }

            var info = session.GetPlaybackInfo();
            bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var props = await session.TryGetMediaPropertiesAsync();
            string title = props?.Title?.Trim() ?? "";
            string artist = props?.Artist?.Trim() ?? "";
            string album = props?.AlbumTitle?.Trim() ?? "";
            string app = CleanAppName(session.SourceAppUserModelId);

            double pos = 0, dur = 0;
            try
            {
                var tl = session.GetTimelineProperties();
                if (tl is not null)
                {
                    dur = Math.Max(0, tl.EndTime.TotalSeconds - tl.StartTime.TotalSeconds);
                    pos = Math.Max(0, tl.Position.TotalSeconds - tl.StartTime.TotalSeconds);
                    if (playing && tl.LastUpdatedTime != default)
                    {
                        double elapsed = (DateTimeOffset.UtcNow - tl.LastUpdatedTime).TotalSeconds;
                        if (elapsed is > 0 and < 300) pos += elapsed;
                    }
                    if (dur > 0) pos = Math.Min(pos, dur);
                }
            }
            catch { }

            BitmapSource? cover = null;
            Color? accent = null;
            string coverKey = title + "|" + artist + "|" + album;
            if (!string.IsNullOrEmpty(title))
            {
                if (coverKey == _cachedCoverKey && _cachedCover is not null)
                {
                    cover = _cachedCover;
                    accent = _cachedAccent;
                }
                else if (props?.Thumbnail is not null)
                {
                    try
                    {
                        using var winrtStream = await props.Thumbnail.OpenReadAsync();
                        using var netStream = winrtStream.AsStreamForRead();
                        using var ms = new MemoryStream();
                        await netStream.CopyToAsync(ms);
                        ms.Position = 0;
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 160;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();
                        cover = bmp;
                        accent = RenderKit.DominantColor(bmp, dark: true);
                        _cachedCoverKey = coverKey;
                        _cachedCover = cover;
                        _cachedAccent = accent;
                    }
                    catch { }
                }
            }

            Publish(new MediaSnapshot
            {
                Title = title,
                Artist = artist,
                Album = album,
                SourceApp = app,
                IsPlaying = playing,
                PositionSec = pos,
                DurationSec = dur,
                Cover = cover,
                AccentColor = accent,
            });
        }
        catch
        {
            // SMTC 偶发 RPC/COM 异常时保持静默，下一拍自动重试
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void Publish(MediaSnapshot next)
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed) return;
            changed = _current.Title != next.Title
                   || _current.Artist != next.Artist
                   || _current.IsPlaying != next.IsPlaying
                   || Math.Abs(_current.PositionSec - next.PositionSec) >= 0.8
                   || !ReferenceEquals(_current.Cover, next.Cover);
            _current = next;
            SyncLoopbackLocked();
        }
        if (changed) Updated?.Invoke(next);
    }

    private static string CleanAppName(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "";
        string s = aumid.Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            s = Path.GetFileNameWithoutExtension(s);
        int bang = s.LastIndexOf('!');
        if (bang >= 0 && bang + 1 < s.Length) s = s[(bang + 1)..];
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < s.Length && s.Length > 18) s = s[(dot + 1)..];
        return s switch
        {
            "cloudmusic" => "网易云音乐",
            "QQMusic" => "QQ音乐",
            "Spotify" => "Spotify",
            "msedge" or "MSEdge" => "Edge",
            "chrome" or "Chrome" => "Chrome",
            "firefox" => "Firefox",
            "PotPlayerMini64" => "PotPlayer",
            _ => s,
        };
    }

    // ---------- 播放控制 ----------
    public void TogglePlayPause() => RunSessionAction(static s => s.TryTogglePlayPauseAsync().AsTask());
    public void NextTrack() => RunSessionAction(static s => s.TrySkipNextAsync().AsTask());
    public void PreviousTrack() => RunSessionAction(static s => s.TrySkipPreviousAsync().AsTask());

    private void RunSessionAction(Func<GlobalSystemMediaTransportControlsSession, Task> act)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var mgr = _manager ?? await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var s = mgr?.GetCurrentSession();
                if (s is not null)
                {
                    await act(s);
                    await Task.Delay(180);
                    await PollOnceAsync();
                }
            }
            catch { }
        });
    }

    // ---------- WASAPI Loopback 频谱提取 ----------
    private void SyncLoopbackLocked()
    {
        bool shouldRun = !_disposed && _spectrumWanted && _current.IsPlaying;
        if (shouldRun && _loopback is null)
        {
            try
            {
                var cap = new WasapiLoopbackCapture();
                cap.DataAvailable += OnAudioData;
                cap.StartRecording();
                _loopback = cap;
            }
            catch
            {
                _loopback = null;
            }
        }
        else if (!shouldRun && _loopback is not null)
        {
            StopLoopbackLocked();
        }
    }

    private void StopLoopbackLocked()
    {
        var cap = _loopback;
        _loopback = null;
        for (int i = 0; i < _bars.Length; i++) _bars[i] = 0;
        if (cap is not null)
        {
            _ = Task.Run(() =>
            {
                try { cap.DataAvailable -= OnAudioData; cap.StopRecording(); } catch { }
                try { cap.Dispose(); } catch { }
            });
        }
    }

    private void OnAudioData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded < 64 || sender is not WasapiLoopbackCapture cap) return;
        try
        {
            int bytesPerSample = cap.WaveFormat.BitsPerSample / 8;
            if (bytesPerSample != 4 && bytesPerSample != 2) return;
            int totalSamples = e.BytesRecorded / bytesPerSample;
            int bands = _bars.Length;
            int perBand = Math.Max(1, totalSamples / bands);

            Span<double> next = stackalloc double[16];
            for (int b = 0; b < bands; b++)
            {
                int start = b * perBand;
                int end = Math.Min(totalSamples, start + perBand);
                double sumSq = 0;
                int count = 0;
                for (int i = start; i < end; i += Math.Max(1, cap.WaveFormat.Channels))
                {
                    double sample = bytesPerSample == 4
                        ? BitConverter.ToSingle(e.Buffer, i * 4)
                        : BitConverter.ToInt16(e.Buffer, i * 2) / 32768.0;
                    if (double.IsFinite(sample))
                    {
                        sumSq += sample * sample;
                        count++;
                    }
                }
                double rms = count > 0 ? Math.Sqrt(sumSq / count) : 0;
                // 对数增益映射：日常音量（RMS 0.02~0.25）也能呈现饱满律动
                double level = rms <= 0.0005 ? 0 : Math.Clamp(Math.Log10(1 + rms * 28) / Math.Log10(9), 0, 1);
                next[b] = level;
            }

            lock (_gate)
            {
                for (int b = 0; b < bands; b++)
                {
                    double cur = _bars[b];
                    double target = next[b];
                    _bars[b] = target > cur ? cur * 0.35 + target * 0.65 : cur * 0.78 + target * 0.22;
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        Timer? t;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            t = _pollTimer;
            _pollTimer = null;
            StopLoopbackLocked();
        }
        t?.Dispose();
    }
}
