using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Island.App.Services;

namespace Island.App.Controls;

/// <summary>展开面板的 ViewModel：剪贴板/文件/控制（监控页、设备页均自绘直取服务，不走绑定）。</summary>
public sealed class PanelData : INotifyPropertyChanged
{
    // ---------- 剪贴板历史 ----------
    private System.Collections.ObjectModel.ObservableCollection<ClipboardRow> _clipboard = new();
    public System.Collections.ObjectModel.ObservableCollection<ClipboardRow> Clipboard
    { get => _clipboard; set => Set(ref _clipboard, value); }

    public sealed record ClipboardRow(string Time, string Preview, string Full, bool IsText)
    {
        public System.Windows.Media.Imaging.BitmapSource? Image { get; init; }
        public ClipboardItem? Item { get; init; }
    }

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
            Clipboard.Add(new ClipboardRow(i.At.ToString("HH:mm"), i.Preview, i.Full, i.Kind == "text") { Image = i.Image, Item = i });
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
    private string _brightness = "--", _wlan = "--", _wlanAdapter = "--", _bt = "--";
    public string Brightness { get => _brightness; set => Set(ref _brightness, value); }
    /// <summary>Wi-Fi 连接状态：已连接 / 未连接 / 状态未知（三态）。</summary>
    public string WlanState { get => _wlan; set => Set(ref _wlan, value); }
    /// <summary>Wi-Fi 适配器开关状态：Wi-Fi 已开启 / Wi-Fi 已关闭 / Wi-Fi 状态未知（三态）。</summary>
    public string WlanAdapterState { get => _wlanAdapter; set => Set(ref _wlanAdapter, value); }
    public string BtState { get => _bt; set => Set(ref _bt, value); }
    /// <summary>亮度进度条绑定值：必须走 Set() 发通知，否则面板上的进度条不会跟着「＋/－」刷新
    /// （OneWay 绑定只在建立时取一次值，不实现 INPC 的属性不会再通知）。</summary>
    private double _brightnessValue;
    public double BrightnessValue { get => _brightnessValue; set => Set(ref _brightnessValue, value); }

    // 音量 / 深色模式（宿主回调，面板不直接持有服务引用）
    private double _volumeValue;
    public double VolumeValue { get => _volumeValue; set => Set(ref _volumeValue, value); }
    private bool _isMuted;
    public bool IsMuted { get => _isMuted; set => Set(ref _isMuted, value); }
    public Action<double>? OnSetVolume;
    public Action? OnToggleMute;
    public void ApplyVolume(double v) { VolumeValue = v; OnSetVolume?.Invoke(v); }
    public void ApplyToggleMute() { IsMuted = !IsMuted; OnToggleMute?.Invoke(); }

    // 麦克风静音（YASB Microphone 同款）
    private bool _micMuted;
    public bool MicMuted { get => _micMuted; set => Set(ref _micMuted, value); }
    public Action? OnToggleMicMute;
    public void ApplyToggleMicMute() => OnToggleMicMute?.Invoke();

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

    // 鼠标穿透
    private bool _clickThrough;
    public bool ClickThrough { get => _clickThrough; set => Set(ref _clickThrough, value); }
    public Action<bool>? OnSetClickThrough;
    public void ApplyClickThrough(bool on) { ClickThrough = on; OnSetClickThrough?.Invoke(on); }

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
        UpdateWlan();
        // 这里读的是 bthserv 服务状态，不是 Win11 的蓝牙无线电开关，文案按"蓝牙服务"说
        BtState = Services.ControlService.BluetoothEnabled ? "蓝牙服务运行中" : "蓝牙服务未运行";
    }

    /// <summary>
    /// Wi-Fi 状态：连接状态与适配器开关分开显示。
    /// 以前直接渲染 WlanEnabled（那是适配器的"管理状态"），
    /// "Wi-Fi 开着但没连热点"会被显示成"已连接"。
    /// </summary>
    private void UpdateWlan()
    {
        var (adapter, connected) = Services.ControlService.GetWlanState();
        WlanState = connected switch { true => "已连接", false => "未连接", _ => "状态未知" };
        WlanAdapterState = adapter switch
        {
            true => "Wi-Fi 已开启",
            false => "Wi-Fi 已关闭",
            _ => "Wi-Fi 状态未知",
        };
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
