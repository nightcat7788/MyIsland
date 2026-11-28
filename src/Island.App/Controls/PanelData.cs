using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Island.App.Services;

namespace Island.App.Controls;

/// <summary>展开面板的 ViewModel：剪贴板工作台 / 文件中转 / 控制页。</summary>
public sealed class PanelData : INotifyPropertyChanged
{
    // ---------- 剪贴板工作台（对齐 macOS 版分类筛选 / 缩略图 / 色块 / 常驻 / 单条删除） ----------
    private ObservableCollection<ClipboardRow> _clipboard = new();
    public ObservableCollection<ClipboardRow> Clipboard
    { get => _clipboard; set => Set(ref _clipboard, value); }

    private string _clipFilter = "all"; // all / common / link / code
    public string ClipFilter
    {
        get => _clipFilter;
        set
        {
            if (_clipFilter == value) return;
            _clipFilter = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClipFilter)));
            UpdateClipboard(force: true);
        }
    }

    public sealed class ClipboardRow
    {
        public required ClipboardItem Raw { get; init; }
        public required string RelTime { get; init; }
        public required string Preview { get; init; }
        public required string Full { get; init; }
        public required string Category { get; init; }
        public required Brush DotBrush { get; init; }
        public required FontFamily Font { get; init; }
        public string LinesBadge { get; init; } = "";
        public Visibility HasLinesBadge => string.IsNullOrEmpty(LinesBadge) ? Visibility.Collapsed : Visibility.Visible;
        public bool Pinned { get; init; }
        public string PinGlyph => Pinned ? "★" : "☆";
        public double PinOpacity => Pinned ? 1.0 : 0.48;
        public bool IsImage => Raw.Kind == "image";
        public BitmapSource? Thumb { get; init; }
        public Visibility HasThumb => Thumb is not null ? Visibility.Visible : Visibility.Collapsed;
        public Brush? ColorSwatch { get; init; }
        public Visibility HasSwatch => ColorSwatch is not null ? Visibility.Visible : Visibility.Collapsed;
        public string ToolTipText => Raw.Kind == "image" ? Preview : (Full.Length > 600 ? Full[..600] + "\n…" : Full);
    }

    private static readonly FontFamily UiFont = new("Segoe UI, Microsoft YaHei UI");
    private static readonly FontFamily MonoFont = new("Consolas, Microsoft YaHei UI");
    private static readonly SolidColorBrush CommonDot = FrozenBrush(0x4F, 0xE3, 0xD7);
    private static readonly SolidColorBrush LinkDot = FrozenBrush(0x30, 0xD1, 0x58);
    private static readonly SolidColorBrush CodeDot = FrozenBrush(0xBD, 0x93, 0xF9);
    private static readonly SolidColorBrush ImageDot = FrozenBrush(0x2E, 0x9B, 0xFF);

    private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private DateTime _lastClipboardAt;
    private string _clipboardSig = "";

    public void UpdateClipboard(bool force = false)
    {
        if (!force && (DateTime.Now - _lastClipboardAt).TotalSeconds < 2) return;
        _lastClipboardAt = DateTime.Now;
        var items = ClipboardService.Items;
        string sig = _clipFilter + "|" + items.Count + "|"
            + string.Join(';', items.Take(8).Select(x => $"{x.Sequence}:{x.Pinned}:{x.At.Ticks}"));
        if (!force && sig == _clipboardSig) return;
        _clipboardSig = sig;

        // 常驻置顶排在前面，其余按时间倒序
        var filtered = items
            .Where(it => _clipFilter switch
            {
                "common" => it.Category is "common" or "color" or "image",
                "link" => it.Category == "link",
                "code" => it.Category == "code",
                _ => true,
            })
            .OrderByDescending(it => it.Pinned)
            .ThenByDescending(it => it.At)
            .ToList();

        Clipboard.Clear();
        foreach (var i in filtered)
        {
            Brush? swatch = null;
            if (i.Category == "color" && !string.IsNullOrEmpty(i.HexColor))
            {
                try
                {
                    if (ColorConverter.ConvertFromString(i.HexColor) is Color hc)
                    {
                        var sb = new SolidColorBrush(hc);
                        sb.Freeze();
                        swatch = sb;
                    }
                }
                catch { }
            }

            BitmapSource? thumb = null;
            if (i.Kind == "image" && i.ImagePng is { Length: > 0 } png)
            {
                try
                {
                    using var ms = new MemoryStream(png);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 96;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    thumb = bmp;
                }
                catch { }
            }

            Brush dot = i.Category switch
            {
                "link" => LinkDot,
                "code" => CodeDot,
                "image" => ImageDot,
                "color" => swatch ?? CommonDot,
                _ => CommonDot,
            };

            Clipboard.Add(new ClipboardRow
            {
                Raw = i,
                RelTime = FormatRelTime(i.At),
                Preview = i.Preview,
                Full = i.Full,
                Category = i.Category,
                DotBrush = dot,
                Font = i.Category == "code" ? MonoFont : UiFont,
                LinesBadge = i.Lines > 1 && i.Kind == "text" ? $"+{i.Lines - 1} 行" : "",
                Pinned = i.Pinned,
                Thumb = thumb,
                ColorSwatch = swatch,
            });
        }
        HasClipboard = items.Count > 0;
    }

    private static string FormatRelTime(DateTime at)
    {
        var d = DateTime.Now - at;
        if (d.TotalSeconds < 60) return "刚刚";
        if (d.TotalMinutes < 60) return $"{Math.Max(1, (int)d.TotalMinutes)}分钟前";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}小时前";
        return $"{(int)d.TotalDays}天前";
    }

    private bool _hasClipboard;
    public bool HasClipboard { get => _hasClipboard; set => Set(ref _hasClipboard, value); }

    // ---------- 文件中转 ----------
    private ObservableCollection<FileRow> _files = new();
    public ObservableCollection<FileRow> Files
    { get => _files; set => Set(ref _files, value); }

    public sealed record FileRow(string Name, string Size, string Full);

    private DateTime _lastFileAt;
    private string _filesSig = "";

    public void UpdateFiles(bool force = false)
    {
        if (!force && (DateTime.Now - _lastFileAt).TotalSeconds < 2) return;
        _lastFileAt = DateTime.Now;
        var items = FileTrayService.Items;
        if (!force && items.Count == 0 && Files.Count == 0) return;
        string sig = string.Join('|', items.Select(f => f.Path + f.Bytes));
        if (!force && sig == _filesSig) return;
        _filesSig = sig;
        Files.Clear();
        foreach (var f in items)
            Files.Add(new FileRow(f.Name, FileTrayService.FormatBytes(f.Bytes), f.Path));
        HasFiles = items.Count > 0;
    }

    public void ForceRefreshShelf()
    {
        UpdateClipboard(force: true);
        UpdateFiles(force: true);
    }

    private bool _hasFiles;
    public bool HasFiles { get => _hasFiles; set => Set(ref _hasFiles, value); }

    // ---------- 控制页 ----------
    private string _brightness = "--", _wlan = "--", _wlanAdapter = "--", _bt = "--";
    public string Brightness { get => _brightness; set => Set(ref _brightness, value); }
    public string WlanState { get => _wlan; set => Set(ref _wlan, value); }
    public string WlanAdapterState { get => _wlanAdapter; set => Set(ref _wlanAdapter, value); }
    public string BtState { get => _bt; set => Set(ref _bt, value); }

    private double _brightnessValue;
    public double BrightnessValue { get => _brightnessValue; set => Set(ref _brightnessValue, value); }

    private double _volumeValue;
    public double VolumeValue { get => _volumeValue; set => Set(ref _volumeValue, value); }
    private bool _isMuted;
    public bool IsMuted { get => _isMuted; set => Set(ref _isMuted, value); }
    public Action<double>? OnSetVolume;
    public Action? OnToggleMute;
    public void ApplyVolume(double v) { VolumeValue = v; OnSetVolume?.Invoke(v); }
    public void ApplyToggleMute() { IsMuted = !IsMuted; OnToggleMute?.Invoke(); }

    private bool _micMuted;
    public bool MicMuted { get => _micMuted; set => Set(ref _micMuted, value); }
    public Action? OnToggleMicMute;
    public void ApplyToggleMicMute() => OnToggleMicMute?.Invoke();

    private bool _isLightTheme;
    public bool IsLightTheme { get => _isLightTheme; set => Set(ref _isLightTheme, value); }
    public Action<bool>? OnSetLightTheme;
    public void ApplyLightTheme(bool light) { IsLightTheme = light; OnSetLightTheme?.Invoke(light); }

    private bool _nightLightOn, _hasNightLight;
    public bool NightLightOn { get => _nightLightOn; set => Set(ref _nightLightOn, value); }
    public bool HasNightLight { get => _hasNightLight; set => Set(ref _hasNightLight, value); }
    public Action<bool>? OnSetNightLight;
    public void ApplyNightLight(bool on) { NightLightOn = on; OnSetNightLight?.Invoke(on); }

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
        var b = ControlService.GetBrightness();
        Brightness = b is null ? "不支持" : $"{b}%";
        if (b is { } v) BrightnessValue = v / 100.0;
        UpdateWlan();
        BtState = ControlService.BluetoothEnabled ? "蓝牙服务运行中" : "蓝牙服务未运行";
    }

    private void UpdateWlan()
    {
        var (adapter, connected) = ControlService.GetWlanState();
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
}
