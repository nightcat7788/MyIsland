using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Island.App.Services;

namespace Island.App.Controls;

/// <summary>展开面板的 ViewModel：配额/设备/剪贴板/文件/控制（监控页由 MonitorPage 自绘直取快照，不走绑定）。</summary>
public sealed class PanelData : INotifyPropertyChanged
{
    // ---------- 设备电量 ----------
    private System.Collections.ObjectModel.ObservableCollection<DeviceRow> _devices = new();
    public System.Collections.ObjectModel.ObservableCollection<DeviceRow> Devices
    { get => _devices; set => Set(ref _devices, value); }

    public sealed record DeviceRow(string Name, string Kind, string PercentText, string Color, double Fraction);

    private DateTime _lastDeviceAt;
    private string _devicesSig = "";

    public void UpdateDevices()
    {
        if ((DateTime.Now - _lastDeviceAt).TotalSeconds < 5) return;
        _lastDeviceAt = DateTime.Now;
        var list = Services.DeviceService.Snapshot;
        // 签名相同就跳过重建：避免每秒清空集合造成列表闪烁/滚动位置丢失
        string sig = string.Join('|', list.Select(d => d.Name + d.Percent));
        if (sig == _devicesSig) return;
        _devicesSig = sig;
        Devices.Clear();
        foreach (var d in list)
        {
            string color = d.Percent <= 20 ? "#E84A4A" : d.Percent <= 40 ? "#E8A24A" : "#3CBE78";
            Devices.Add(new DeviceRow(d.Name, d.Kind, $"{d.Percent}%", color, d.Percent / 100.0));
        }
        HasDevices = list.Count > 0;
    }

    private bool _hasDevices;
    public bool HasDevices { get => _hasDevices; set => Set(ref _hasDevices, value); }

    // ---------- 剪贴板历史 ----------
    private System.Collections.ObjectModel.ObservableCollection<ClipboardRow> _clipboard = new();
    public System.Collections.ObjectModel.ObservableCollection<ClipboardRow> Clipboard
    { get => _clipboard; set => Set(ref _clipboard, value); }

    public sealed record ClipboardRow(string Time, string Preview, string Full, bool IsText);

    private DateTime _lastClipboardAt;
    private string _clipboardSig = "";

    public void UpdateClipboard()
    {
        if ((DateTime.Now - _lastClipboardAt).TotalSeconds < 2) return;
        _lastClipboardAt = DateTime.Now;
        var items = Services.ClipboardService.Items;
        if (items.Count == 0 && Clipboard.Count == 0) return;
        // 签名（条数+最新一条）：无变化不重建集合，防闪烁
        string sig = items.Count + "|" + (items.Count > 0 ? items[0].Preview + items[0].At.Ticks : "");
        if (sig == _clipboardSig) return;
        _clipboardSig = sig;
        Clipboard.Clear();
        foreach (var i in items)
            Clipboard.Add(new ClipboardRow(i.At.ToString("HH:mm"), i.Preview, i.Full, i.Kind == "text"));
        HasClipboard = items.Count > 0;
    }

    private bool _hasClipboard;
    public bool HasClipboard { get => _hasClipboard; set => Set(ref _hasClipboard, value); }

    // ---------- 文件中转 ----------
    private System.Collections.ObjectModel.ObservableCollection<FileRow> _files = new();
    public System.Collections.ObjectModel.ObservableCollection<FileRow> Files
    { get => _files; set => Set(ref _files, value); }

    public sealed record FileRow(string Name, string Size, string Full);

    private DateTime _lastFileAt;
    private string _filesSig = "";

    public void UpdateFiles()
    {
        if ((DateTime.Now - _lastFileAt).TotalSeconds < 2) return;
        _lastFileAt = DateTime.Now;
        var items = Services.FileTrayService.Items;
        if (items.Count == 0 && Files.Count == 0) return;
        string sig = string.Join('|', items.Select(f => f.Path + f.Bytes));
        if (sig == _filesSig) return;
        _filesSig = sig;
        Files.Clear();
        foreach (var f in items)
            Files.Add(new FileRow(f.Name, Services.FileTrayService.FormatBytes(f.Bytes), f.Path));
        HasFiles = items.Count > 0;
    }

    private bool _hasFiles;
    public bool HasFiles { get => _hasFiles; set => Set(ref _hasFiles, value); }

    // ---------- 控制页 ----------
    private string _brightness = "--", _wlan = "--", _bt = "--";
    public string Brightness { get => _brightness; set => Set(ref _brightness, value); }
    public string WlanState { get => _wlan; set => Set(ref _wlan, value); }
    public string BtState { get => _bt; set => Set(ref _bt, value); }
    public double BrightnessValue { get; set; }

    // 音量 / 深色模式（宿主回调，面板不直接持有服务引用）
    private double _volumeValue;
    public double VolumeValue { get => _volumeValue; set => Set(ref _volumeValue, value); }
    private bool _isMuted;
    public bool IsMuted { get => _isMuted; set => Set(ref _isMuted, value); }
    public Action<double>? OnSetVolume;
    public Action? OnToggleMute;
    public void ApplyVolume(double v) { VolumeValue = v; OnSetVolume?.Invoke(v); }
    public void ApplyToggleMute() { IsMuted = !IsMuted; OnToggleMute?.Invoke(); }

    private bool _isLightTheme;
    public bool IsLightTheme { get => _isLightTheme; set => Set(ref _isLightTheme, value); }
    public Action<bool>? OnSetLightTheme;
    public void ApplyLightTheme(bool light) { IsLightTheme = light; OnSetLightTheme?.Invoke(light); }

    // 夜灯（不支持时卡片隐藏）
    private bool _nightLightOn, _hasNightLight;
    public bool NightLightOn { get => _nightLightOn; set => Set(ref _nightLightOn, value); }
    public bool HasNightLight { get => _hasNightLight; set => Set(ref _hasNightLight, value); }
    public Action<bool>? OnSetNightLight;
    public void ApplyNightLight(bool on) { NightLightOn = on; OnSetNightLight?.Invoke(on); }

    public System.Windows.Input.ICommand BrightnessUp { get; private set; } = null!;
    public System.Windows.Input.ICommand BrightnessDown { get; private set; } = null!;
    public System.Windows.Input.ICommand ToggleWlan { get; private set; } = null!;
    public System.Windows.Input.ICommand ToggleBt { get; private set; } = null!;
    public System.Windows.Input.ICommand OpenBtSettings { get; private set; } = null!;
    public System.Windows.Input.ICommand LockCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand SleepCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand OpenSettingsCommand { get; private set; } = null!;

    /// <summary>接控制命令（App 侧注入，避免 PanelData 直接依赖 ControlService 不可测）。</summary>
    public void BindControls(
        System.Windows.Input.ICommand brightUp, System.Windows.Input.ICommand brightDown,
        System.Windows.Input.ICommand wlan, System.Windows.Input.ICommand bt,
        System.Windows.Input.ICommand btSettings,
        System.Windows.Input.ICommand lockCmd, System.Windows.Input.ICommand sleepCmd,
        System.Windows.Input.ICommand openSettings)
    {
        BrightnessUp = brightUp; BrightnessDown = brightDown;
        ToggleWlan = wlan; ToggleBt = bt; OpenBtSettings = btSettings;
        LockCommand = lockCmd; SleepCommand = sleepCmd; OpenSettingsCommand = openSettings;
    }

    public void UpdateControls()
    {
        var b = Services.ControlService.GetBrightness();
        Brightness = b is null ? "不支持" : $"{b}%";
        if (b is { } v) BrightnessValue = v / 100.0;
        WlanState = Services.ControlService.WlanEnabled ? "已连接" : "已关闭";
        BtState = Services.ControlService.BluetoothEnabled ? "开启" : "关闭";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static string FormatTokens(long n) =>
        n >= 1_000_000 ? $"{n / 1e6:F1}M" : n >= 1000 ? $"{n / 1000:F0}K" : n.ToString();
}
