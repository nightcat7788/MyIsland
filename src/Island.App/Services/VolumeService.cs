using System;
using NAudio.CoreAudioApi;

namespace Island.App.Services;

/// <summary>
/// 音量监听：系统音量/静音变化（含默认播放设备热插拔切换）时触发事件。
/// 所有 NAudio COM 对象访问统一加锁，避免 UI 线程与轮询线程并发重入或热插拔后失效。
/// </summary>
public sealed class VolumeService : IDisposable
{
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private string? _deviceId;
    private System.Threading.Timer? _timer;
    private (float vol, bool mute)? _last;
    private bool _disposed;

    public void SetVolume(float v)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var d = EnsureRenderDeviceLocked();
                if (d is not null)
                    d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0, 1);
            }
            catch { ResetDeviceLocked(); }
        }
    }

    public void SetMute(bool mute)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var d = EnsureRenderDeviceLocked();
                if (d is not null)
                    d.AudioEndpointVolume.Mute = mute;
            }
            catch { ResetDeviceLocked(); }
        }
    }

    public (float vol, bool mute)? Current { get { lock (_gate) return _last; } }
    public bool IsMutedNow => Current?.mute ?? false;

    public bool? MicMuted
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return null;
                try
                {
                    using var ep = GetDefaultCaptureLocked();
                    return ep?.AudioEndpointVolume.Mute;
                }
                catch { return null; }
            }
        }
    }

    public bool ToggleMicMute()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            try
            {
                using var ep = GetDefaultCaptureLocked();
                if (ep is null) return false;
                bool next = !ep.AudioEndpointVolume.Mute;
                ep.AudioEndpointVolume.Mute = next;
                return next;
            }
            catch { return false; }
        }
    }

    public bool MicInUse
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return false;
                try
                {
                    using var ep = GetDefaultCaptureLocked();
                    if (ep is null) return false;
                    var sessions = ep.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var s = sessions[i];
                        if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive)
                            return true;
                    }
                }
                catch { }
                return false;
            }
        }
    }

    private MMDevice? GetDefaultCaptureLocked()
    {
        try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); }
        catch
        {
            try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
            catch { return null; }
        }
    }

    private MMDevice? EnsureRenderDeviceLocked()
    {
        try
        {
            using var currentDefault = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_device is null || !string.Equals(_deviceId, currentDefault.ID, StringComparison.Ordinal))
            {
                ResetDeviceLocked();
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _deviceId = _device.ID;
            }
            return _device;
        }
        catch
        {
            ResetDeviceLocked();
            return null;
        }
    }

    private void ResetDeviceLocked()
    {
        try { _device?.Dispose(); } catch { }
        _device = null;
        _deviceId = null;
    }

    public event Action<float, bool>? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null) return;
            try
            {
                var d = EnsureRenderDeviceLocked();
                if (d is not null)
                    _last = (d.AudioEndpointVolume.MasterVolumeLevelScalar, d.AudioEndpointVolume.Mute);
            }
            catch { ResetDeviceLocked(); }

            _timer = new System.Threading.Timer(_ => PollTick(), null, 500, 500);
        }
    }

    private void PollTick()
    {
        (float vol, bool mute)? notify = null;
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var d = EnsureRenderDeviceLocked();
                if (d is null) return;
                var cur = (d.AudioEndpointVolume.MasterVolumeLevelScalar, d.AudioEndpointVolume.Mute);
                if (_last is { } prev && (Math.Abs(cur.Item1 - prev.vol) > 0.002f || cur.Item2 != prev.mute))
                    notify = cur;
                _last = cur;
            }
            catch
            {
                ResetDeviceLocked();
            }
        }
        if (notify is { } n) Changed?.Invoke(n.vol, n.mute);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            ResetDeviceLocked();
            try { _enumerator.Dispose(); } catch { }
        }
    }
}
