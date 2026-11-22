using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Island.App.Controls;

/// <summary>静音按钮文字（true→取消静音 / false→静音）。</summary>
public sealed class MuteTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "取消静音" : "静音";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true→Collapsed / false→Visible（"无数据"提示用）。</summary>
public sealed class InverseBoolToVis : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 展开面板（BrimDeck 式页面结构）：顶栏分段胶囊切页 + 中央时间 + 右侧收起，
/// 页面 = 监控 / 剪贴板·文件 / 设备电量 / 控制，一次只显示一页。
/// </summary>
public partial class ExpandedPanel : UserControl
{
    private Button[] _pageBtns = null!;
    private UIElement[] _pages = null!;
    private readonly DispatcherTimer _clock;

    /// <summary>监控页（宿主直接注入快照数据，不走绑定）。</summary>
    public MonitorPage Monitor => monitorPage;

    /// <summary>请求收起面板（顶栏 ✕）。</summary>
    public event Action? CloseRequested;

    public ExpandedPanel()
    {
        InitializeComponent();
        _pageBtns = new[] { btnMonitor, btnShelf, btnDevices, btnControl };
        _pages = new UIElement[] { monitorPage, pageShelf, pageDevices, pageControl };
        SelectPage(0);

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _clock.Tick += (_, _) => TickClock();
        _clock.Start();
        TickClock();
    }

    private void TickClock() => clockText.Text = DateTime.Now.ToString("HH:mm");

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    // 音量滑杆 / 静音 / 系统浅色模式：走 PanelData 回调（面板不直接持有服务引用）
    private void OnVolumeSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DataContext is PanelData pd && Math.Abs(e.NewValue - pd.VolumeValue) > 0.001)
            pd.ApplyVolume(e.NewValue);
    }

    private void OnMuteToggle(object sender, RoutedEventArgs e)
    {
        if (DataContext is PanelData pd) pd.ApplyToggleMute();
    }

    private void OnLightThemeToggle(object sender, RoutedEventArgs e)
    {
        if (DataContext is PanelData pd && sender is CheckBox cb)
            pd.ApplyLightTheme(cb.IsChecked == true);
    }

    private void OnNightLightToggle(object sender, RoutedEventArgs e)
    {
        if (DataContext is PanelData pd && sender is CheckBox cb)
            pd.ApplyNightLight(cb.IsChecked == true);
    }

    private void OnPage(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && int.TryParse(b.Tag?.ToString(), out int i)) SelectPage(i);
    }

    /// <summary>切页：显示目标页，分段胶囊中对应按钮亮起（BrimDeck 同款选中态）。</summary>
    public void SelectPage(int idx)
    {
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;
        var accent = TryFindResource("Accent") as SolidColorBrush;
        for (int i = 0; i < _pageBtns.Length; i++)
        {
            bool on = i == idx;
            _pageBtns[i].Background = on && accent is not null
                ? new SolidColorBrush(accent.Color) { Opacity = 0.28 }
                : System.Windows.Media.Brushes.Transparent;
            if (_pageBtns[i].Content is TextBlock t)
                t.Foreground = on
                    ? TryFindResource("Fg") as Brush ?? System.Windows.Media.Brushes.White
                    : TryFindResource("FgDim") as Brush ?? System.Windows.Media.Brushes.Gray;
        }
    }
}
