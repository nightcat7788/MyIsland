using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Island.App.Services;

namespace Island.App.Controls;

/// <summary>静音按钮文字（true→取消静音 / false→静音）。</summary>
public sealed class MuteTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "取消静音" : "静音";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true→Collapsed / false→Visible。</summary>
public sealed class InverseBoolToVis : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 展开面板（BrimDeck + LunarCN 融合页面结构）：
/// 0=系统监控  1=媒体播放  2=剪贴板·文件  3=设备电量  4=显示器·控制
/// </summary>
public partial class ExpandedPanel : UserControl
{
    public const int MonitorPageIndex = 0;
    public const int MediaPageIndex = 1;
    public const int ShelfPageIndex = 2;
    public const int DevicesPageIndex = 3;
    public const int ControlPageIndex = 4;

    private readonly Button[] _pageBtns;
    private readonly Path[] _pageIcons;
    private readonly TextBlock[] _pageLabels;
    private readonly UIElement[] _pages;
    private readonly Button[] _filterBtns;
    private readonly DispatcherTimer _clock;

    public int CurrentPageIndex { get; private set; }

    public MonitorPage Monitor => monitorPage;
    public MediaPage MediaView => mediaPage;
    public DevicesPage DevicesView => devicesPage;

    public event Action? CloseRequested;
    public event Action? SettingsRequested;
    public event Action<int>? PageSelected;
    public event Action<string>? DisplayPreferencesChanged;

    private Dictionary<string, ControlService.DisplayPref> _displayPrefs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedDisplayColor = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _builtinRevertTimer;
    private int _builtinRevertLeft;
    private bool _builtinTurnedOff;

    public void LoadDisplayPreferences(string? json)
    {
        _displayPrefs = ControlService.ParseDisplayPrefs(json);
        ControlService.SyncDisplayRanges(_displayPrefs);
    }

    public IReadOnlyDictionary<string, ControlService.DisplayPref> CurrentDisplayPreferences => _displayPrefs;

    private bool _suppressToggle;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.Property == DataContextProperty)
        {
            _suppressToggle = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _suppressToggle = false));
        }
        base.OnPropertyChanged(e);
    }

    public ExpandedPanel()
    {
        InitializeComponent();
        _pageBtns = new[] { btnMonitor, btnMedia, btnShelf, btnDevices, btnControl };
        _pageIcons = new[] { icoMonitor, icoMedia, icoShelf, icoDevices, icoControl };
        _pageLabels = new[] { lblMonitor, lblMedia, lblShelf, lblDevices, lblControl };
        _pages = new UIElement[] { monitorPage, mediaPage, pageShelf, pageDevices, pageControl };
        _filterBtns = new[] { fltAll, fltCommon, fltLink, fltCode };
        SelectPage(0);
        SyncFilterButtons("all");

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _clock.Tick += (_, _) => TickClock();
        _clock.Start();
        TickClock();

        _builtinRevertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _builtinRevertTimer.Tick += (_, _) =>
        {
            if (_builtinRevertLeft <= 1)
            {
                _builtinRevertTimer.Stop();
                _builtinRevertLeft = 0;
                _builtinTurnedOff = false;
                Task.Run(() => ControlService.SwitchBuiltinDisplay(turnOff: false));
                RefreshDisplayCards();
                return;
            }
            _builtinRevertLeft--;
            RefreshDisplayCards();
        };
    }

    private void TickClock() => clockText.Text = DateTime.Now.ToString("HH:mm");

    public void StopTimers()
    {
        _clock.Stop();
        _builtinRevertTimer.Stop();
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    public void CycleNextPage() => SelectPage((CurrentPageIndex + 1) % _pages.Length);

    // ---------- 剪贴板工作台与文件中转交互 ----------
    private Point _fileDragOrigin;
    private bool _fileDragPending;

    private void OnClipFilter(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string flt }) return;
        SyncFilterButtons(flt);
        if (DataContext is PanelData pd) pd.ClipFilter = flt;
    }

    private void SyncFilterButtons(string active)
    {
        var accent = TryFindResource("Accent") as SolidColorBrush;
        var fg = TryFindResource("Fg") as Brush ?? Brushes.White;
        var dim = TryFindResource("FgDim") as Brush ?? Brushes.Gray;
        foreach (var b in _filterBtns)
        {
            bool on = (b.Tag as string) == active;
            b.Background = on && accent is not null
                ? new SolidColorBrush(accent.Color) { Opacity = 0.25 }
                : Brushes.Transparent;
            b.Foreground = on ? fg : dim;
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void OnClipboardItemClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PanelData.ClipboardRow row }) return;
        // 对齐 macOS 版 ClipPicked：图片条目点击直接弹出大图预览窗（支持存下载与复制），文本条目写回剪贴板
        if (row.IsImage && row.Raw.ImagePng is { Length: > 0 } png)
        {
            try
            {
                var preview = new ImagePreviewWindow(png, row.Preview);
                preview.Show();
                shelfHint.Text = "已打开图片大图预览（按 Esc 关闭）";
            }
            catch
            {
                ClipboardService.CopyBack(row.Raw);
            }
            e.Handled = true;
            return;
        }
        if (ClipboardService.CopyBack(row.Raw))
            shelfHint.Text = $"✓ 已写回剪贴板（{DateTime.Now:HH:mm:ss}）";
        else
            shelfHint.Text = "剪贴板正被其它程序占用，请稍后重试";
        e.Handled = true;
    }

    private void OnClipboardItemRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PanelData.ClipboardRow row } fe) return;
        // 对齐 macOS 版剪贴板右键菜单：复制 / 查看详情(或查看大图) / 标记常驻 / 删除
        var cm = new ContextMenu();
        var miCopy = new MenuItem { Header = "复制回剪贴板" };
        miCopy.Click += (_, _) =>
        {
            shelfHint.Text = ClipboardService.CopyBack(row.Raw)
                ? $"✓ 已写回剪贴板（{DateTime.Now:HH:mm:ss}）"
                : "剪贴板正被其它程序占用，请稍后重试";
        };
        cm.Items.Add(miCopy);

        var miDetail = new MenuItem { Header = row.IsImage ? "查看大图预览" : "查看完整详情" };
        miDetail.Click += (_, _) =>
        {
            try
            {
                if (row.IsImage && row.Raw.ImagePng is { Length: > 0 } png)
                    new ImagePreviewWindow(png, row.Preview).Show();
                else
                    new ClipDetailWindow(row.Raw).Show();
            }
            catch { }
        };
        cm.Items.Add(miDetail);

        var miPin = new MenuItem { Header = row.Pinned ? "取消常驻置顶" : "★ 标记为常驻（不淘汰）" };
        miPin.Click += (_, _) =>
        {
            ClipboardService.TogglePin(row.Raw.At, row.Raw.Sequence, row.Raw.Full);
            if (DataContext is PanelData pd) pd.UpdateClipboard(force: true);
            shelfHint.Text = row.Pinned ? "已取消常驻" : "★ 已标记为常驻（不随上限淘汰）";
        };
        cm.Items.Add(miPin);
        cm.Items.Add(new Separator());

        var miDel = new MenuItem { Header = "删除该条" };
        miDel.Click += (_, _) =>
        {
            ClipboardService.Remove(row.Raw.At, row.Raw.Sequence, row.Raw.Full);
            if (DataContext is PanelData pd) pd.UpdateClipboard(force: true);
            shelfHint.Text = "已删除该条剪贴板记录";
        };
        cm.Items.Add(miDel);

        cm.PlacementTarget = fe;
        cm.IsOpen = true;
        e.Handled = true;
    }

    private void OnClipboardPinClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PanelData.ClipboardRow row }) return;
        ClipboardService.TogglePin(row.Raw.At, row.Raw.Sequence, row.Raw.Full);
        if (DataContext is PanelData pd) pd.UpdateClipboard(force: true);
        e.Handled = true;
    }

    private void OnClipboardDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PanelData.ClipboardRow row }) return;
        ClipboardService.Remove(row.Raw.At, row.Raw.Sequence, row.Raw.Full);
        if (DataContext is PanelData pd) pd.UpdateClipboard(force: true);
        shelfHint.Text = "已删除该条剪贴板记录";
        e.Handled = true;
    }

    private void OnClipboardClear(object sender, RoutedEventArgs e)
    {
        ClipboardService.Clear(keepPinned: true);
        if (DataContext is PanelData pd) pd.UpdateClipboard(force: true);
        shelfHint.Text = "已清空非常驻记录（常驻项已保留）";
    }

    private void OnClipboardExport(object sender, RoutedEventArgs e)
    {
        string? dir = ClipboardService.ExportToDownloads();
        shelfHint.Text = dir is not null ? "✓ 已导出到下载目录并打开" : "暂无可导出的剪贴板条目";
    }

    private void OnFileItemMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _fileDragOrigin = e.GetPosition(this);
        _fileDragPending = true;
    }

    private void OnFileItemMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_fileDragPending || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            _fileDragPending = false;
            return;
        }
        var cur = e.GetPosition(this);
        if (Math.Abs(cur.X - _fileDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(cur.Y - _fileDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _fileDragPending = false;
        if (sender is FrameworkElement { DataContext: PanelData.FileRow row } fe && System.IO.File.Exists(row.Full))
        {
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { row.Full });
                DragDrop.DoDragDrop(fe, data, DragDropEffects.Copy);
            }
            catch { }
        }
    }

    private void OnFileItemClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _fileDragPending = false;
        if (sender is not FrameworkElement { DataContext: PanelData.FileRow row }) return;
        if (!System.IO.File.Exists(row.Full))
        {
            FileTrayService.Remove(row.Full);
            if (DataContext is PanelData pd) pd.ForceRefreshShelf();
            shelfHint.Text = "原文件已移动或删除，已从列表移除";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{row.Full}\"",
                UseShellExecute = true,
            });
        }
        catch { }
        e.Handled = true;
    }

    private void OnFileItemRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _fileDragPending = false;
        if (sender is not FrameworkElement { DataContext: PanelData.FileRow row }) return;
        FileTrayService.Remove(row.Full);
        if (DataContext is PanelData pd) pd.ForceRefreshShelf();
        e.Handled = true;
    }

    // ---------- 多显示器无级 DDC/CI + WMI 控制卡片 ----------
    private int _displayGen;

    private void RefreshDisplayCards()
    {
        int gen = ++_displayGen;
        if (displayCardsHost.Children.Count == 0)
        {
            displayCardsHost.Children.Add(new Border
            {
                Background = TryFindResource("CardBg") as Brush,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = new TextBlock
                {
                    Text = "正在检测显示器控制通道（WMI / DDC-CI）…",
                    FontSize = 11,
                    Foreground = TryFindResource("FgDim") as Brush,
                },
            });
        }

        Task.Run(ControlService.ListDisplays).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (gen != _displayGen) return;
                displayCardsHost.Children.Clear();
                if (t.Status != TaskStatus.RanToCompletion || t.Result.Count == 0)
                {
                    displayCardsHost.Children.Add(BuildUnsupportedDisplayCard());
                    return;
                }
                bool hasBuiltin = t.Result.Any(s => s.Builtin);
                bool hasExternal = t.Result.Any(s => !s.Builtin);
                if (_builtinTurnedOff && !hasBuiltin)
                    displayCardsHost.Children.Add(BuildBuiltinRestorerCard());
                foreach (var scr in t.Result)
                    displayCardsHost.Children.Add(BuildDisplayCard(scr, hasBuiltin, hasExternal));
            });
        });
    }

    private void SavePref(string key, Func<ControlService.DisplayPref, ControlService.DisplayPref> mutate)
    {
        var cur = _displayPrefs.TryGetValue(key, out var p) ? p : new ControlService.DisplayPref();
        var next = mutate(cur);
        _displayPrefs[key] = next;
        ControlService.SetBrightnessRange(key, next.Min, next.Max);
        DisplayPreferencesChanged?.Invoke(ControlService.SerializeDisplayPrefs(_displayPrefs));
    }

    private UIElement BuildBuiltinRestorerCard()
    {
        var fg = TryFindResource("Fg") as Brush ?? Brushes.White;
        var dim = TryFindResource("FgDim") as Brush ?? Brushes.Gray;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "内置笔记本屏幕（已关闭）", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = fg,
        });
        stack.Children.Add(new TextBlock
        {
            Text = _builtinRevertLeft > 0
                ? $"{_builtinRevertLeft} 秒后自动恢复 · 点「保持关闭」确认仅用外接屏"
                : "已保持关闭 · 可随时点击下方恢复内置屏幕",
            FontSize = 10, Foreground = dim, Margin = new Thickness(0, 3, 0, 8),
        });
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        var restoreBtn = new Button
        {
            Content = "恢复内置屏幕", Height = 24, Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 8, 0), FontSize = 10.5, Foreground = fg,
            Background = TryFindResource("Accent") as Brush, Style = (Style)FindResource("FlatBtn"),
        };
        restoreBtn.Click += (_, _) =>
        {
            _builtinRevertTimer.Stop();
            _builtinRevertLeft = 0;
            _builtinTurnedOff = false;
            Task.Run(() => ControlService.SwitchBuiltinDisplay(turnOff: false));
            RefreshDisplayCards();
        };
        btns.Children.Add(restoreBtn);
        if (_builtinRevertLeft > 0)
        {
            var keepBtn = new Button
            {
                Content = "保持关闭", Height = 24, Padding = new Thickness(10, 0, 10, 0),
                FontSize = 10.5, Foreground = fg, Style = (Style)FindResource("FlatBtn"),
            };
            keepBtn.Click += (_, _) =>
            {
                _builtinRevertTimer.Stop();
                _builtinRevertLeft = 0;
                RefreshDisplayCards();
            };
            btns.Children.Add(keepBtn);
        }
        stack.Children.Add(btns);
        return new Border
        {
            Background = TryFindResource("CardBg") as Brush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack,
        };
    }

    private UIElement BuildUnsupportedDisplayCard()
    {
        return new Border
        {
            Background = TryFindResource("CardBg") as Brush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock
            {
                Text = "当前显示器未开放 WMI / DDC-CI 硬件调光接口（外接显示器可在 OSD 菜单中开启 DDC/CI）",
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TryFindResource("FgDim") as Brush,
            },
        };
    }

    private UIElement BuildDisplayCard(ControlService.DisplayScreenInfo scr, bool hasBuiltin, bool hasExternal)
    {
        var fg = TryFindResource("Fg") as Brush ?? Brushes.White;
        var dim = TryFindResource("FgDim") as Brush ?? Brushes.Gray;
        var accent = TryFindResource("Accent") as SolidColorBrush;
        var pref = _displayPrefs.TryGetValue(scr.Key, out var existingPref) ? existingPref : new ControlService.DisplayPref();
        var stack = new StackPanel();

        // 卡片标题行：屏幕名称 + 类型副标题
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var tag = new TextBlock
        {
            Text = scr.Builtin ? "内置屏幕" : "DDC/CI 外接屏",
            FontSize = 9.5,
            Foreground = (Brush?)accent ?? fg,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(tag, Dock.Right);
        head.Children.Add(tag);
        head.Children.Add(new TextBlock
        {
            Text = scr.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(head);

        Slider? briSlider = null;
        Slider? conSlider = null;
        Slider? volSlider = null;
        int liveBri = scr.Brightness ?? 50;
        int liveCon = scr.Contrast ?? 50;
        int liveVol = scr.Volume ?? 50;

        if (scr.Brightness is { } bri)
        {
            stack.Children.Add(BuildDisplaySliderRow("亮度", bri, fg, dim, out briSlider,
                pct =>
                {
                    liveBri = pct;
                    ControlService.QueueDisplayWrite(scr.Key, ControlService.DisplayControlKind.Brightness, pct);
                    if (scr.Builtin)
                        Task.Run(() => ControlService.SyncExternalFollowBrightness(_displayPrefs, force: true));
                }));

            // 护眼快捷档：15% / 45% / 75% / 100%
            var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(58, 2, 0, 4) };
            presets.Children.Add(new TextBlock
            {
                Text = "快捷：", FontSize = 9.5, Foreground = dim,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
            });
            foreach (var p in new[] { 15, 45, 75, 100 })
            {
                int target = p;
                var pb = new Button
                {
                    Content = $"{target}%", Height = 20, Padding = new Thickness(7, 0, 7, 0),
                    Margin = new Thickness(0, 0, 4, 0), FontSize = 9.5, Foreground = dim,
                    Style = (Style)FindResource("FlatBtn"),
                };
                var targetSlider = briSlider;
                pb.Click += (_, _) => { if (targetSlider is not null) targetSlider.Value = Math.Clamp(target, pref.Min, pref.Max); };
                presets.Children.Add(pb);
            }
            stack.Children.Add(presets);
        }

        // 内置笔记本屏 + 存在外接屏：支持一键关闭内置屏（带 15 秒未确认自动恢复护栏，对齐 macOS 版）
        if (scr.Builtin && hasExternal)
        {
            var offBtn = new Button
            {
                Content = "关闭内置屏幕（仅用外接屏 · 15秒安全确认）",
                Height = 24, Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 4, 0, 2), HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = 10, Foreground = dim, Style = (Style)FindResource("FlatBtn"),
            };
            offBtn.Click += (_, _) =>
            {
                _builtinTurnedOff = true;
                _builtinRevertLeft = 15;
                _builtinRevertTimer.Start();
                Task.Run(() => ControlService.SwitchBuiltinDisplay(turnOff: true));
                RefreshDisplayCards();
            };
            stack.Children.Add(offBtn);
        }

        // 外接屏专属：三档亮度范围（对齐 macOS 屏幕页：全范围 0–100 / 护眼 10–90 / 窄范围 20–80）
        if (!scr.Builtin && scr.Brightness is not null)
        {
            var rangeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 4) };
            rangeRow.Children.Add(new TextBlock
            {
                Text = "亮度范围", Width = 58, FontSize = 10, Foreground = dim,
                VerticalAlignment = VerticalAlignment.Center,
            });
            foreach (var (rLabel, rMin, rMax) in new[] { ("0–100", 0, 100), ("护眼 10–90", 10, 90), ("窄 20–80", 20, 80) })
            {
                bool active = pref.Min == rMin && pref.Max == rMax;
                int lo = rMin, hi = rMax;
                var rb = new Button
                {
                    Content = rLabel, Height = 21, Padding = new Thickness(7, 0, 7, 0),
                    Margin = new Thickness(0, 0, 4, 0), FontSize = 9.5,
                    Foreground = active ? fg : dim,
                    Background = active && accent is not null
                        ? new SolidColorBrush(accent.Color) { Opacity = 0.25 }
                        : Brushes.Transparent,
                    Style = (Style)FindResource("FlatBtn"),
                };
                rb.Click += (_, _) =>
                {
                    SavePref(scr.Key, p => p with { Min = lo, Max = hi });
                    RefreshDisplayCards();
                };
                rangeRow.Children.Add(rb);
            }
            stack.Children.Add(rangeRow);
        }

        if (!scr.Builtin && scr.Contrast is { } con)
        {
            stack.Children.Add(BuildDisplaySliderRow("对比度", con, fg, dim, out conSlider,
                pct => { liveCon = pct; ControlService.QueueDisplayWrite(scr.Key, ControlService.DisplayControlKind.Contrast, pct); }));
        }

        // 外接屏自动跟随内建屏亮度开关（仅存在内建屏时出现）
        if (!scr.Builtin && hasBuiltin && scr.Brightness is not null)
        {
            var followRow = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var cb = new CheckBox
            {
                IsChecked = pref.FollowBuiltin,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cb.Click += (_, _) =>
            {
                bool on = cb.IsChecked == true;
                SavePref(scr.Key, p => p with { FollowBuiltin = on });
                if (on) Task.Run(() => ControlService.SyncExternalFollowBrightness(_displayPrefs, force: true));
            };
            DockPanel.SetDock(cb, Dock.Right);
            followRow.Children.Add(cb);
            followRow.Children.Add(new TextBlock
            {
                Text = "自动亮度 · 跟随内建屏", FontSize = 10.5, Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(followRow);
        }

        if (!scr.Builtin && scr.Volume is { } vol)
        {
            stack.Children.Add(BuildDisplaySliderRow("屏幕音量", vol, fg, dim, out volSlider,
                pct => { liveVol = pct; ControlService.QueueDisplayWrite(scr.Key, ControlService.DisplayControlKind.Volume, pct); }));
        }

        // 外接屏静音开关（仅显示器支持 VCP 0x8D 时展示）
        if (!scr.Builtin && scr.Mute is { } mv)
        {
            var muteRow = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            bool isMuted = mv == 1;
            var mcb = new CheckBox { IsChecked = isMuted, VerticalAlignment = VerticalAlignment.Center };
            mcb.Click += (_, _) =>
            {
                bool nextMute = mcb.IsChecked == true;
                ControlService.QueueDisplayWrite(scr.Key, ControlService.DisplayControlKind.Mute, nextMute ? 1 : 2);
            };
            DockPanel.SetDock(mcb, Dock.Right);
            muteRow.Children.Add(mcb);
            muteRow.Children.Add(new TextBlock { Text = "屏幕静音", FontSize = 10.5, Foreground = dim, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(muteRow);
        }

        // 外接屏折叠区：RGB 三原色硬件增益 + 颜色重置 + 预设保存/恢复（对齐 macOS 屏幕页 Lunar 功能）
        if (!scr.Builtin && (scr.Brightness is not null || scr.Red is not null))
        {
            bool open = _expandedDisplayColor.Contains(scr.Key);
            var toggleBtn = new Button
            {
                Content = open ? "⌄ 收起颜色与预设" : "› 颜色增益（RGB）与预设",
                Height = 22, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 4, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left, FontSize = 10,
                Foreground = (Brush?)accent ?? dim, Style = (Style)FindResource("FlatBtn"),
            };
            toggleBtn.Click += (_, _) =>
            {
                if (!_expandedDisplayColor.Remove(scr.Key)) _expandedDisplayColor.Add(scr.Key);
                RefreshDisplayCards();
            };
            stack.Children.Add(toggleBtn);

            if (open)
            {
                Slider? rSl = null, gSl = null, bSl = null;
                stack.Children.Add(BuildDisplaySliderRow("红增益", scr.Red ?? 100, new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)), dim, out rSl,
                    pct => ControlService.QueueDisplayWrite(scr.Key, ControlService.DisplayControlKind.Red, pct)));
                stack.Children.Add(BuildDisplaySliderRow("绿增益", scr.Green ?? 100, new SolidColorBrush(Color.FromRgb(0x5A, 0xD1, 0x5A)), dim, out gSl,
                    pct => ControlService.QueueDisplayWrite(scr.Key, ControlService.DisplayControlKind.Green, pct)));
                stack.Children.Add(BuildDisplaySliderRow("蓝增益", scr.Blue ?? 100, new SolidColorBrush(Color.FromRgb(0x6B, 0x9C, 0xFF)), dim, out bSl,
                    pct => ControlService.QueueDisplayWrite(scr.Key, ControlService.DisplayControlKind.Blue, pct)));

                var actionRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
                var resetColor = new Button
                {
                    Content = "重置颜色", Height = 22, Padding = new Thickness(8, 0, 8, 0),
                    Margin = new Thickness(0, 0, 6, 2), FontSize = 10, Foreground = fg,
                    Style = (Style)FindResource("FlatBtn"),
                };
                resetColor.Click += (_, _) =>
                {
                    ControlService.ResetDisplayColor(scr.Key);
                    if (rSl is not null) rSl.Value = 100;
                    if (gSl is not null) gSl.Value = 100;
                    if (bSl is not null) bSl.Value = 100;
                };
                actionRow.Children.Add(resetColor);

                var savePreset = new Button
                {
                    Content = pref.PresetBrightness is null ? "保存为预设" : "更新预设",
                    Height = 22, Padding = new Thickness(8, 0, 8, 0),
                    Margin = new Thickness(0, 0, 6, 2), FontSize = 10, Foreground = fg,
                    Style = (Style)FindResource("FlatBtn"),
                };
                savePreset.Click += (_, _) =>
                {
                    SavePref(scr.Key, p => p with
                    {
                        PresetBrightness = liveBri,
                        PresetContrast = scr.Contrast is not null ? liveCon : p.PresetContrast,
                        PresetVolume = scr.Volume is not null ? liveVol : p.PresetVolume,
                    });
                    RefreshDisplayCards();
                };
                actionRow.Children.Add(savePreset);

                if (pref.PresetBrightness is { } pbVal)
                {
                    var restorePreset = new Button
                    {
                        Content = $"恢复预设 ({pbVal}%)", Height = 22, Padding = new Thickness(8, 0, 8, 0),
                        Margin = new Thickness(0, 0, 6, 2), FontSize = 10, Foreground = (Brush?)accent ?? fg,
                        Style = (Style)FindResource("FlatBtn"),
                    };
                    restorePreset.Click += (_, _) =>
                    {
                        if (briSlider is not null) briSlider.Value = Math.Clamp(pbVal, pref.Min, pref.Max);
                        if (pref.PresetContrast is { } pc && conSlider is not null) conSlider.Value = pc;
                        if (pref.PresetVolume is { } pv && volSlider is not null) volSlider.Value = pv;
                    };
                    actionRow.Children.Add(restorePreset);
                }
                stack.Children.Add(actionRow);
            }
        }

        if (scr.Brightness is null && scr.Contrast is null && scr.Volume is null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "该显示器未响应 DDC/CI 控制指令",
                FontSize = 10,
                Foreground = dim,
            });
        }

        return new Border
        {
            Background = TryFindResource("CardBg") as Brush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack,
        };
    }

    private static UIElement BuildDisplaySliderRow(string label, int initial, Brush fg, Brush dim,
        out Slider sliderOut, Action<int> onChanged)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock
        {
            Text = label, Width = 58, FontSize = 11,
            Foreground = dim, VerticalAlignment = VerticalAlignment.Center,
        });
        var valTb = new TextBlock
        {
            Text = $"{initial}%", Width = 42, FontSize = 11.5,
            FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right,
            Foreground = fg, VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(valTb, Dock.Right);
        row.Children.Add(valTb);

        var sl = new Slider
        {
            Minimum = 0, Maximum = 100, Value = Math.Clamp(initial, 0, 100),
            TickFrequency = 1, IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        sl.ValueChanged += (_, e) =>
        {
            int pct = (int)Math.Round(e.NewValue);
            valTb.Text = $"{pct}%";
            onChanged(pct);
        };
        row.Children.Add(sl);
        sliderOut = sl;
        return row;
    }

    // ---------- 音量 / 电源计划 / 系统开关 ----------
    private void OnVolumeSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DataContext is PanelData pd && Math.Abs(e.NewValue - pd.VolumeValue) > 0.001)
            pd.ApplyVolume(e.NewValue);
    }

    private void OnMuteToggle(object sender, RoutedEventArgs e)
    {
        if (DataContext is PanelData pd) pd.ApplyToggleMute();
    }

    private void OnMicToggle(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PanelData pd) return;
        pd.ApplyToggleMicMute();
        btnMic.Content = pd.MicMuted ? "已静音" : "麦克风";
    }

    private void OnCleanMemory(object sender, RoutedEventArgs e)
    {
        if (_cleaning) return;
        _cleaning = true;
        memCleanResult.Text = "正在清理…";
        ControlService.CleanMemoryAsync().ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _cleaning = false;
                if (t.Status != TaskStatus.RanToCompletion)
                {
                    memCleanResult.Text = "清理失败";
                    return;
                }
                long freed = t.Result;
                memCleanResult.Text = freed > 0
                    ? $"已释放约 {freed} MB 可用内存"
                    : "已清理（可用内存变化 0 MB）";
            });
        });
    }

    private bool _cleaning;
    private int _powerPlanGen;

    private void RefreshPowerPlans()
    {
        int gen = ++_powerPlanGen;
        powerPlanHost.Children.Clear();
        powerPlanHost.Children.Add(new TextBlock
        {
            Text = "读取中…", FontSize = 10,
            Foreground = TryFindResource("FgDim") as Brush,
        });
        Task.Run(ControlService.GetPowerPlans).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (gen != _powerPlanGen) return;
                if (t.Status != TaskStatus.RanToCompletion)
                {
                    powerPlanHost.Children.Clear();
                    powerPlanHost.Children.Add(new TextBlock
                    {
                        Text = "读取失败", FontSize = 10,
                        Foreground = TryFindResource("FgDim") as Brush,
                    });
                    return;
                }
                var (plans, active) = t.Result;
                FillPowerPlans(plans, active);
            });
        });
    }

    private void FillPowerPlans(List<(string Id, string Name)> plans, int active)
    {
        powerPlanHost.Children.Clear();
        var accent = TryFindResource("Accent") as SolidColorBrush;
        for (int idx = 0; idx < plans.Count; idx++)
        {
            var (id, name) = plans[idx];
            var b = new Button
            {
                Content = name,
                Height = 26,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(4, 2, 0, 2),
                FontSize = 11,
                Style = (Style)FindResource("FlatBtn"),
                Foreground = TryFindResource("Fg") as Brush,
                Tag = id,
            };
            if (idx == active && accent is not null)
                b.Background = new SolidColorBrush(accent.Color) { Opacity = 0.28 };
            b.Click += (_, _) =>
            {
                if (b.Tag is not string guid) return;
                b.IsEnabled = false;
                Task.Run(() => ControlService.SetPowerPlan(guid)).ContinueWith(t =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        b.IsEnabled = true;
                        if (t.Status == TaskStatus.RanToCompletion && t.Result)
                            RefreshPowerPlans();
                    });
                });
            };
            powerPlanHost.Children.Add(b);
        }
        if (plans.Count == 0)
            powerPlanHost.Children.Add(new TextBlock
            {
                Text = "读取失败", FontSize = 10,
                Foreground = TryFindResource("FgDim") as Brush,
            });
    }

    private void OnLightThemeToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        if (DataContext is PanelData pd && sender is CheckBox cb)
            pd.ApplyLightTheme(cb.IsChecked == true);
    }

    private void OnNightLightToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        if (DataContext is PanelData pd && sender is CheckBox cb)
            pd.ApplyNightLight(cb.IsChecked == true);
    }

    private void OnClickThroughToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        if (DataContext is PanelData pd && sender is CheckBox cb)
        {
            pd.ApplyClickThrough(cb.IsChecked == true);
            if (cb.IsChecked == true) CloseRequested?.Invoke();
        }
    }

    private void OnPage(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && int.TryParse(b.Tag?.ToString(), out int i))
        {
            SelectPage(i);
            PageSelected?.Invoke(i);
        }
    }

    public void SelectPage(int idx)
    {
        if (idx < 0 || idx >= _pages.Length) return;
        CurrentPageIndex = idx;
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;

        if (idx == ShelfPageIndex && DataContext is PanelData shelfPd)
            shelfPd.ForceRefreshShelf();

        if (idx == ControlPageIndex)
        {
            RefreshDisplayCards();
            RefreshPowerPlans();
            if (DataContext is PanelData pd)
                btnMic.Content = pd.MicMuted ? "已静音" : "麦克风";
        }

        var accent = TryFindResource("Accent") as SolidColorBrush;
        var fg = TryFindResource("Fg") as Brush ?? Brushes.White;
        var dim = TryFindResource("FgDim") as Brush ?? Brushes.Gray;
        for (int i = 0; i < _pageBtns.Length; i++)
        {
            bool on = i == idx;
            _pageBtns[i].Background = on && accent is not null
                ? new SolidColorBrush(accent.Color) { Opacity = 0.22 }
                : Brushes.Transparent;
            _pageIcons[i].Stroke = on && accent is not null ? accent : dim;
            _pageLabels[i].Foreground = on ? fg : dim;
            _pageLabels[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}
