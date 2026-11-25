using System;
using NAudio.CoreAudioApi;

namespace Island.App.Services;

/// <summary>
/// 音量监听：系统音量/静音变化（含用户按键盘/其他程序调整）时触发事件，
/// 像手机一样在岛内弹出音量指示。参考 wisland 的 on_volume 交互。
/// </summary>
public sealed class VolumeService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private System.Threading.Timer? _timer;
    private (float vol, bool mute)? _last;

    // ---------- 音量（NAudio MMDevice：读/写主音量与静音） ----------
    /// <summary>设置主音量 0..1。</summary>
    public void SetVolume(float v)
    {
        try
        {
            if (_device is not null)
                _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0, 1);
        }
        catch { }
    }

    /// <summary>设置静音。</summary>
    public void SetMute(bool mute)
    {
        try
        {
            if (_device is not null) _device.AudioEndpointVolume.Mute = mute;
        }
        catch { }
    }

    /// <summary>当前（音量0..1, 静音）读数（无设备为 null）。</summary>
    public (float vol, bool mute)? Current => _last;

    /// <summary>当前是否静音。</summary>
    public bool IsMutedNow => _last?.mute ?? false;

    // ---------- 麦克风（YASB Microphone 同款：默认采集端点静音切换） ----------

    /// <summary>麦克风静音状态；无采集设备为 null。</summary>
    public bool? MicMuted
    {
        get
        {
            try
            {
                using var ep = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                return ep.AudioEndpointVolume.Mute;
            }
            catch { return null; }
        }
    }

    /// <summary>切换麦克风静音，返回新状态（无设备返回 false）。</summary>
    public bool ToggleMicMute()
    {
        try
        {
            using var ep = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            bool next = !ep.AudioEndpointVolume.Mute;
            ep.AudioEndpointVolume.Mute = next;
            return next;
        }
        catch { return false; }
    }

    /// <summary>麦克风是否正被使用（默认采集端点存在活动会话；L4 隐私点同款数据源）。</summary>
    public bool MicInUse
    {
        get
        {
            try
            {
                using var ep = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
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

    /// <summary>(音量0..1, 是否静音)。后台线程触发。</summary>
    public event Action<float, bool>? Changed;

    public void Start()
    {
        try
        {
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _last = (_device.AudioEndpointVolume.MasterVolumeLevelScalar,
                     _device.AudioEndpointVolume.Mute);
        }
        catch { /* 无音频设备 */ }

        // 轮询而非事件订阅：AudioEndpointVolume 的回调在 COM 线程，轮询更稳更简单
        _timer = new System.Threading.Timer(_ =>
        {
            try
            {
                var d = _device;
                if (d is null)
                {
                    d = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _device = d;
                }
                var cur = (d.AudioEndpointVolume.MasterVolumeLevelScalar,
                           d.AudioEndpointVolume.Mute);
                if (_last is { } prev && cur != prev)
                    Changed?.Invoke(cur.Item1, cur.Item2);
                _last = cur;
            }
            catch { /* 设备切换瞬间忽略 */ }
        }, null, 500, 500);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _device?.Dispose();
        _enumerator.Dispose();
    }
}
