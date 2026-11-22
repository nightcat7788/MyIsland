using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Island.App.Services;
using Island.Core.Monitoring;
using Island.Core.Quota;

namespace Island.App.Controls;

/// <summary>展开面板的 ViewModel：持有可绑定的监控与配额数据。</summary>
public sealed class PanelData : INotifyPropertyChanged
{
    public ObservableCollection<QuotaCard> Quotas { get; } = new();

    private string _cpu = "0%", _memory = "0%", _gpu = "0%", _net = "0";
    private string _memoryDetail = "", _gpuDetail = "", _netUp = "0";

    public string Cpu { get => _cpu; set => Set(ref _cpu, value); }
    public string Memory { get => _memory; set => Set(ref _memory, value); }
    public string Gpu { get => _gpu; set => Set(ref _gpu, value); }
    public string NetDown { get => _net; set => Set(ref _net, value); }
    public string NetUp { get => _netUp; set => Set(ref _netUp, value); }
    public string MemoryDetail { get => _memoryDetail; set => Set(ref _memoryDetail, value); }
    public string GpuDetail { get => _gpuDetail; set => Set(ref _gpuDetail, value); }

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

    public System.Windows.Input.ICommand BrightnessUp { get; private set; } = null!;
    public System.Windows.Input.ICommand BrightnessDown { get; private set; } = null!;
    public System.Windows.Input.ICommand ToggleWlan { get; private set; } = null!;
    public System.Windows.Input.ICommand ToggleBt { get; private set; } = null!;
    public System.Windows.Input.ICommand OpenBtSettings { get; private set; } = null!;

    /// <summary>接控制命令（App 侧注入，避免 PanelData 直接依赖 ControlService 不可测）。</summary>
    public void BindControls(
        System.Windows.Input.ICommand brightUp, System.Windows.Input.ICommand brightDown,
        System.Windows.Input.ICommand wlan, System.Windows.Input.ICommand bt,
        System.Windows.Input.ICommand btSettings)
    {
        BrightnessUp = brightUp; BrightnessDown = brightDown;
        ToggleWlan = wlan; ToggleBt = bt; OpenBtSettings = btSettings;
    }

    public void UpdateControls()
    {
        var b = Services.ControlService.GetBrightness();
        Brightness = b is null ? "不支持" : $"{b}%";
        if (b is { } v) BrightnessValue = v / 100.0;
        WlanState = Services.ControlService.WlanEnabled ? "已连接" : "已关闭";
        BtState = Services.ControlService.BluetoothEnabled ? "开启" : "关闭";
    }

    // 迷你走势图点集（PolyLine 数据，0..1）
    private System.Windows.Point[] _cpuPts = Array.Empty<System.Windows.Point>();
    private System.Windows.Point[] _netPts = Array.Empty<System.Windows.Point>();
    public System.Windows.Point[] CpuSpark { get => _cpuPts; set => Set(ref _cpuPts, value); }
    public System.Windows.Point[] NetSpark { get => _netPts; set => Set(ref _netPts, value); }

    /// <summary>宿主把时间正序的归一化序列转成 Polyline 点（w×h 画布内）。</summary>
    public static System.Windows.Point[] ToSpark(double[] seq, double w, double h)
    {
        var pts = new System.Windows.Point[seq.Length];
        for (int i = 0; i < seq.Length; i++)
            pts[i] = new System.Windows.Point(w * i / (seq.Length - 1), h - 2 - seq[i] * (h - 4));
        return pts;
    }

    // 温度/风扇（LibreHardwareMonitor；读不到显示 "--"，不伪造）
    private string _cpuTemp = "--", _gpuTemp = "--", _cpuPower = "--", _fan = "--";
    private System.Windows.Media.Brush _cpuTempBrush = System.Windows.Media.Brushes.Gray;
    private System.Windows.Media.Brush _gpuTempBrush = System.Windows.Media.Brushes.Gray;
    private string _session = "", _link = "";
    public string CpuTemp { get => _cpuTemp; set => Set(ref _cpuTemp, value); }
    public string GpuTemp { get => _gpuTemp; set => Set(ref _gpuTemp, value); }
    public string CpuPower { get => _cpuPower; set => Set(ref _cpuPower, value); }
    public string Fan { get => _fan; set => Set(ref _fan, value); }
    public System.Windows.Media.Brush CpuTempBrush { get => _cpuTempBrush; set => Set(ref _cpuTempBrush, value); }
    public System.Windows.Media.Brush GpuTempBrush { get => _gpuTempBrush; set => Set(ref _gpuTempBrush, value); }
    public string Session { get => _session; set => Set(ref _session, value); }
    public string Link { get => _link; set => Set(ref _link, value); }

    private static System.Windows.Media.Brush TempBrush(double? t) => t switch
    {
        null => System.Windows.Media.Brushes.Gray,
        < 60 => System.Windows.Media.Brushes.MediumSeaGreen,
        < 75 => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 162, 74)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 74, 74)),
    };

    private Services.ThermalSnapshot? _lastThermal;
    private double? _lastCpuT, _lastGpuT;
    private readonly System.Collections.Generic.List<double> _cpuHist = new();
    private readonly System.Collections.Generic.List<double> _gpuHist = new();
    private System.Windows.Point[] _tempPts = System.Array.Empty<System.Windows.Point>();

    /// <summary>温度历史曲线（CPU/GPU 双线，60 点 ≈ 2 分钟）。</summary>
    public System.Windows.Point[] TempSpark { get => _tempPts; set => Set(ref _tempPts, value); }

    public void UpdateThermal(Services.ThermalSnapshot t)
    {
        _lastThermal = t;
        _lastCpuT = t.CpuTemp; _lastGpuT = t.GpuTemp;
        CpuTemp = t.CpuTemp is { } c ? $"{Math.Round(c)}°" : "--";
        GpuTemp = t.GpuTemp is { } g ? $"{Math.Round(g)}°" : "--";
        CpuTempBrush = TempBrush(t.CpuTemp);
        GpuTempBrush = TempBrush(t.GpuTemp);
        CpuPower = t.CpuPower is { } p && p > 0.01 ? $"{Math.Round(p)}W" :
                   (t.GpuPower is { } gp && gp > 0.01 ? $"GPU {Math.Round(gp)}W" : "--");
        Fan = (t.CpuFanRpm ?? t.GpuFanRpm) is { } f && f > 0.01 ? $"{Math.Round(f)} rpm" : "--";

        // 历史曲线（缺失数据用 NaN 断线；面板 90pt 宽、18pt 高）
        if (_cpuHist.Count == 0 || (DateTime.Now - _lastHistAt).TotalSeconds >= 2)
        {
            _lastHistAt = DateTime.Now;
            _cpuHist.Add(t.CpuTemp ?? double.NaN);
            _gpuHist.Add(t.GpuTemp ?? double.NaN);
            if (_cpuHist.Count > 60) { _cpuHist.RemoveAt(0); _gpuHist.RemoveAt(0); }
            RebuildTempSpark();
        }
    }

    private DateTime _lastHistAt;

    private void RebuildTempSpark()
    {
        var pts = new System.Collections.Generic.List<System.Windows.Point>(_cpuHist.Count * 2);
        const double w = 88, h = 18;
        for (int i = 0; i < _cpuHist.Count; i++)
        {
            double x = w * i / Math.Max(1, _cpuHist.Count - 1);
            double cy = Y(_cpuHist[i]), gy = Y(_gpuHist[i]);
            if (!double.IsNaN(cy)) pts.Add(new System.Windows.Point(x, cy));
            if (!double.IsNaN(gy)) pts.Add(new System.Windows.Point(x, gy));
        }
        TempSpark = pts.ToArray();

        double Y(double t)
        {
            if (double.IsNaN(t)) return double.NaN;
            double k = System.Math.Clamp((t - 30) / 70, 0, 1);   // 30-100°C 映射
            return h - k * h;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Update(MonitorSnapshot s)
    {
        Cpu = s.CpuFrequencyGHz > 0.1
            ? $"{s.CpuUsage:F0}% · {s.CpuFrequencyGHz:F1}GHz"
            : $"{s.CpuUsage:F0}%";
        Memory = $"{s.MemoryUsagePercent:F0}%";
        Gpu = s.GpuUsage > 0 ? $"{s.GpuUsage:F0}%" : "N/A";
        NetDown = MonitorSnapshot.FormatSpeed(s.DownloadSpeed);
        NetUp = MonitorSnapshot.FormatSpeed(s.UploadSpeed);
        MemoryDetail = $"{s.MemoryUsedGB:F1} / {s.MemoryTotalGB:F1} GB";
        GpuDetail = s.GpuName;
        Session = s.SessionText;
        Link = s.LinkType == "wifi" ? "WiFi" : s.LinkType == "ethernet" ? "有线" : "";
    }

    public void UpdateQuota(QuotaSnapshot snap)
    {
        Quotas.Clear();
        foreach (var p in snap.Providers)
        {
            double? used = p.UsedPercent;
            Quotas.Add(new QuotaCard
            {
                Name = p.DisplayName,
                StatusText = p.StatusText,
                TokensToday = p.TokensToday > 0 ? FormatTokens(p.TokensToday) : "—",
                CostToday = p.CostToday > 0 ? $"${p.CostToday:F2}（估算）" : "—",
                UsedPercent = used,
                Detail = p.Detail,
                // BrimDeck 用量列：大百分比 + 进度条，颜色随级别（≥70 橙 / ≥90 红）
                PercentText = used is { } u ? $"{Math.Round(u)}%" : "—",
                UsedFraction = (used ?? 0) / 100.0,
                PercentBrush = used is { } u2
                    ? u2 >= 90 ? BrushOf(0xE8, 0x4A, 0x4A)
                    : u2 >= 70 ? BrushOf(0xE8, 0xA2, 0x4A)
                    : ProviderBrush(p.Id)
                    : ProviderBrush(p.Id),
            });
        }
    }

    /// <summary>工具品牌色（与收起态配额环一致）：Claude 橙 / Codex 绿 / Cursor 蓝。</summary>
    private static System.Windows.Media.Brush ProviderBrush(string id) => id switch
    {
        "claude" => BrushOf(0xD9, 0x77, 0x57),
        "codex" => BrushOf(0x10, 0xA3, 0x7F),
        _ => BrushOf(0x5A, 0x96, 0xFF),
    };

    private static System.Windows.Media.Brush BrushOf(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static string FormatTokens(long n) =>
        n >= 1_000_000 ? $"{n / 1e6:F1}M" : n >= 1000 ? $"{n / 1000:F0}K" : n.ToString();
}

public sealed class QuotaCard
{
    public string Name { get; set; } = "";
    public string StatusText { get; set; } = "";
    public string TokensToday { get; set; } = "";
    public string CostToday { get; set; } = "";
    public double? UsedPercent { get; set; }
    public string Detail { get; set; } = "";
    /// <summary>大百分比文本（"34%" / 无数据 "—"）。</summary>
    public string PercentText { get; set; } = "—";
    /// <summary>进度条值 0..1。</summary>
    public double UsedFraction { get; set; }
    /// <summary>百分比/进度条颜色（品牌色，超阈值变预警/告警色）。</summary>
    public System.Windows.Media.Brush PercentBrush { get; set; } = System.Windows.Media.Brushes.Gray;
}
