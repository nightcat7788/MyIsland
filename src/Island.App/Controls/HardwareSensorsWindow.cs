using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Island.App.Services;
using Island.App.Themes;

namespace Island.App.Controls;

/// <summary>
/// 只读的硬件传感器窗口（2026-11-26 task4）。
///
/// 解决的问题：LibreHardwareMonitor 枚举到的传感器里，绝大部分（各电压轨、各风扇转速、
/// 每颗内存 / 每个 PCIe 设备的温度、主板 SuperIO 温度……）**没有任何入口能看到**。
/// 这个窗口把它们全列出来：传感器名 + 当前值 + 单位。
///
/// 硬红线：
///  · **没有值就是"未知"（需要提权时是"需管理员"），绝不用 0 代替**（0 是合法读数：风扇停转、显卡空闲）；
///  · 只读：没有输入控件，不改任何硬件设置；
///  · 复用温度服务已经枚举出来的结果（<see cref="IHardwareSensorSource"/>），不另起一套 LHM 实例。
/// </summary>
public sealed class HardwareSensorsWindow : Window
{
    /// <param name="palette">当前主题（暗色科技配色的唯一来源）。</param>
    /// <param name="source">传感器数据源（温度服务）。</param>
    public HardwareSensorsWindow(Palette palette, IHardwareSensorSource source)
    {
        Title = "硬件传感器";
        Width = 560;
        Height = 620;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(palette.Surface);
        Foreground = new SolidColorBrush(palette.Text);
        // 与设置中心 / 模块设置窗口一致（同一个桌面小工具家族，窗口都是置顶的）
        Topmost = true;
        Content = new HardwareSensorsView(palette, source);
    }
}

/// <summary>
/// 硬件传感器列表视图（窗口的内容部分）。
///
/// 单独成类是为了能**离屏渲染**：它只依赖 <see cref="IHardwareSensorSource"/> 这个接口，
/// 不依赖任何硬件库 / Win32 ⇒ 测试工程可以链接它，用合成数据渲染出 PNG 做像素断言。
///
/// 刷新策略：每秒向数据源要一次新枚举（后台线程采样），
/// 拿到新读数后**只改数值 TextBlock 的文本与颜色**——传感器清单没变就不重建可视树，
/// 于是刷新不会触发整窗重排。
/// </summary>
public sealed class HardwareSensorsView : UserControl
{
    private readonly IHardwareSensorSource _source;
    private readonly Palette _palette;
    private readonly StackPanel _rowsHost = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _hint = new();
    private readonly TextBlock _empty = new();
    private readonly DispatcherTimer _timer;

    // 当前列表的行控件（与 _source.Sensors 顺序一一对应）：刷新时原地改，不重建。
    private readonly List<RowUi> _rows = new();

    // 当前清单的键序列：判断"还是同一批传感器吗"（变了才重建列表）。
    private string? _keys;

    /// <summary>一行传感器在界面上的两个可变量（读数 + 单位）。</summary>
    private sealed record RowUi(string Key, TextBlock Value, TextBlock Unit);

    public HardwareSensorsView(Palette palette, IHardwareSensorSource source)
    {
        _palette = palette;
        _source = source;
        BuildUi();

        _source.SensorsChanged += OnSensorsChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _source.RequestRefresh();
        _timer.Start();
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _source.SensorsChanged -= OnSensorsChanged;
        };

        Apply(_source.Sensors);
    }

    /// <summary>
    /// 当前显示的读数文本（每行一条）：给测试断言用（"读不到的那行显示的是'未知'，不是'0'"）。
    /// </summary>
    public IReadOnlyList<string> RowTexts => _rows.Select(r => r.Value.Text).ToList();

    /// <summary>当前显示的传感器名（每行一条）。</summary>
    public IReadOnlyList<string> RowNames => _rows.Select(r => r.Key).ToList();

    /// <summary>
    /// 清单被重建过几次。刷新时只有"传感器清单变了"才会重建（其余只改数值），
    /// 这个计数器就是"刷新不触发整窗重排"的可断言证据。
    /// </summary>
    public int RebuildCount { get; private set; }

    // ================= 界面 =================

    private void BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 顶栏：标题 + 汇总（条数 / 有值 / 未知）
        var header = new Grid { Margin = new Thickness(16, 14, 16, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new TextBlock
        {
            Text = "硬件传感器",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(_palette.Text),
            VerticalAlignment = VerticalAlignment.Center,
        });
        _summary.FontSize = 11;
        _summary.Foreground = Brush(_palette.TextTertiary);
        _summary.HorizontalAlignment = HorizontalAlignment.Right;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_summary, 1);
        header.Children.Add(_summary);
        root.Children.Add(header);

        // 提示行：说明"读不到"是怎么回事（不假装有值，也不让人以为程序坏了）
        _hint.FontSize = 10.5;
        _hint.Foreground = Brush(_palette.TextTertiary);
        _hint.Margin = new Thickness(16, 0, 16, 8);
        _hint.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_hint, 1);
        root.Children.Add(_hint);

        // 列表：可滚动（传感器条数随硬件走，几十到几百条都有）
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 4, 0),
        };
        _rowsHost.Margin = new Thickness(12, 0, 8, 12);
        scroll.Content = _rowsHost;

        _empty.Text = "暂无传感器数据";
        _empty.FontSize = 12;
        _empty.Foreground = Brush(_palette.TextTertiary);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Margin = new Thickness(0, 40, 0, 0);
        _empty.Visibility = Visibility.Collapsed;

        var listHost = new Grid();
        listHost.Children.Add(scroll);
        listHost.Children.Add(_empty);
        Grid.SetRow(listHost, 2);
        root.Children.Add(listHost);

        Content = root;

        // 滚动条样式：默认 WPF 滚动条是浅色系统样式，压在深色窗上是两块白条 —— 换成暗色科技那一根。
        // 样式挂在**滚动条自己所在的 ScrollViewer** 上：模板生成的元素按逻辑树往上找资源，
        // 挂在视图（UserControl）这一级实测没被找到 —— 主题样式照样生效，滚动条还是 17px 的浅色系统条。
        scroll.Resources[typeof(ScrollBar)] = SlimScrollBarStyle(_palette);
        // 隐式样式对**模板生成**的滚动条实测不生效（查找停在模板作用域，主题样式照样优先，
        // 滚动条还是 17px 的浅色系统条）⇒ 把样式直接挂到滚动条控件上。
        scroll.ApplyTemplate();
        if (scroll.Template?.FindName("PART_VerticalScrollBar", scroll) is ScrollBar bar)
            bar.Style = SlimScrollBarStyle(_palette);
    }

    // ================= 刷新 =================

    /// <summary>数据源更新了（**可能在后台线程上**）⇒ 切回 UI 线程再改界面。</summary>
    private void OnSensorsChanged()
    {
        var dispatcher = Dispatcher;
        if (dispatcher.CheckAccess()) Apply(_source.Sensors);
        else dispatcher.BeginInvoke(new Action(() => Apply(_source.Sensors)));
    }

    /// <summary>
    /// 应用一份枚举结果：清单没变就**只更新数值**（不重建可视树 ⇒ 不触发整窗重排），
    /// 清单变了才重建（换硬件 / 驱动装好后传感器会多出来）。
    /// </summary>
    private void Apply(IReadOnlyList<HardwareSensorEntry>? entries)
    {
        entries ??= Array.Empty<HardwareSensorEntry>();
        var keys = string.Join("\u001f", entries.Select(e => e.Key));
        if (keys != _keys)
        {
            _keys = keys;
            Rebuild(entries);
        }
        else
        {
            UpdateValues(entries);
        }
        UpdateSummary(entries);
    }

    /// <summary>重建清单（按硬件分组）。</summary>
    private void Rebuild(IReadOnlyList<HardwareSensorEntry> entries)
    {
        RebuildCount++;
        _rowsHost.Children.Clear();
        _rows.Clear();

        foreach (var group in HardwareSensorList.GroupByHardware(entries))
        {
            // 组标题：硬件名
            _rowsHost.Children.Add(new TextBlock
            {
                Text = group.Hardware,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(_palette.TextDim),
                Margin = new Thickness(2, 10, 0, 4),
            });

            foreach (var entry in group.Sensors)
            {
                var row = new Grid { Height = 20, Margin = new Thickness(0, 0, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });

                var name = new TextBlock
                {
                    Text = entry.Sensor,
                    FontSize = 11.5,
                    Foreground = Brush(_palette.Text),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = entry.Hardware + " · " + entry.Sensor + "（" + HardwareSensorList.SemanticLabel(entry) + "）",
                };
                row.Children.Add(name);

                // 数值列定宽 ⇒ 改文本不会引起重排（这是"刷新不触发整窗重排"的关键）
                var value = new TextBlock
                {
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush(_palette.Text),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right,
                    // 等宽数字（读数列每行右对齐，位数变化时不抖动）
                    FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                };
                Grid.SetColumn(value, 1);
                row.Children.Add(value);

                var unit = new TextBlock
                {
                    Text = HardwareSensorList.UnitOf(entry.Category),
                    FontSize = 10,
                    Foreground = Brush(_palette.TextTertiary),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0),
                };
                Grid.SetColumn(unit, 2);
                row.Children.Add(unit);

                _rowsHost.Children.Add(row);
                _rows.Add(new RowUi(entry.Key, value, unit));
            }
        }

        UpdateValues(entries);
    }

    /// <summary>只更新读数与颜色（清单没变时走这条路径）。</summary>
    private void UpdateValues(IReadOnlyList<HardwareSensorEntry> entries)
    {
        int n = Math.Min(_rows.Count, entries.Count);
        for (int i = 0; i < n; i++)
        {
            var entry = entries[i];
            var row = _rows[i];
            row.Value.Text = HardwareSensorList.StatusText(entry, _source.Elevated);
            row.Value.Foreground = ValueBrush(entry);
            row.Unit.Text = HardwareSensorList.UnitOf(entry.Category);
        }
    }

    /// <summary>
    /// 读数的颜色：**正常是中性色，异常才上告警色**（与监控页既有规则一致）。
    /// 读不到分两种："需管理员"（要用户处理 ⇒ 告警色，与监控页一致）与"未知"（弱化色）。
    /// </summary>
    private Brush ValueBrush(HardwareSensorEntry entry)
    {
        if (!entry.HasValue)
        {
            // "需管理员"用告警色（与监控页一致：那是一个**要用户处理**的状态），
            // "未知"用弱化色（没有信息，不该抢戏）。两者都不是"读到了异常值"。
            bool needAdmin = entry.RequiresElevation && !_source.Elevated;
            return needAdmin ? Brush(_palette.Warning) : Brush(_palette.TextTertiary);
        }
        return HardwareSensorList.SeverityOf(entry.Category, entry.Value) switch
        {
            HardwareSensorSeverity.Danger => Brush(_palette.Up),
            HardwareSensorSeverity.Warning => Brush(_palette.Warning),
            _ => Brush(_palette.Text),
        };
    }

    /// <summary>顶部的汇总 + 提示行。</summary>
    private void UpdateSummary(IReadOnlyList<HardwareSensorEntry> entries)
    {
        var (total, withValue, missing) = HardwareSensorList.Count(entries);
        _summary.Text = total == 0 ? "" : $"共 {total} 条 · 有值 {withValue} · 未知 {missing}";
        _empty.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        _hint.Text = !_source.Elevated
            ? "部分传感器（主板 / SuperIO / CPU 温度）需要管理员权限才能读出读数，其余照常显示。"
            : "每秒刷新 · 只读视图（这里改不了任何硬件设置）。";
    }

    // ================= 小工具 =================

    private static SolidColorBrush Brush(Color c) => new(c);

    /// <summary>
    /// 细滚动条（暗色科技配色的那一根）：默认 WPF 滚动条是浅色系统样式，压在深色窗上是两块白条。
    /// 用 XamlReader 在运行时拼一个 Style —— 这样本文件不依赖 PanelStyles.xaml
    /// （那份资源字典按程序集名寻址，只有真的加载 Island.App 程序集才解析得了，
    /// 离屏渲染的测试工程正是"不加载那个程序集"）。
    /// </summary>
    private static Style SlimScrollBarStyle(Palette palette)
    {
        byte alpha = (byte)(palette.IsDark ? 0.19 : 0.22);
        string thumb = string.Format(
            "#{0:X2}{1:X2}{2:X2}{3:X2}",
            alpha, palette.Text.R, palette.Text.G, palette.Text.B);
        string xaml = @"<Style xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                          xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                          TargetType=""{x:Type ScrollBar}"">
  <Setter Property=""Width"" Value=""9""/>
  <Setter Property=""MinWidth"" Value=""9""/>
  <Setter Property=""MaxWidth"" Value=""9""/>
  <Setter Property=""Background"" Value=""Transparent""/>
  <Setter Property=""Template"">
   <Setter.Value>
    <ControlTemplate TargetType=""{x:Type ScrollBar}"">
      <Grid Background=""{TemplateBinding Background}"">
        <Track x:Name=""PART_Track"" IsDirectionReversed=""True"">
          <Track.DecreaseRepeatButton>
            <RepeatButton Command=""ScrollBar.PageUpCommand"" Focusable=""False"" Opacity=""0""/>
          </Track.DecreaseRepeatButton>
          <Track.Thumb>
            <Thumb>
              <Thumb.Template>
                <ControlTemplate TargetType=""{x:Type Thumb}"">
                  <Border Background=""" + thumb + @""" CornerRadius=""2"" Margin=""1,0""/>
                </ControlTemplate>
              </Thumb.Template>
            </Thumb>
          </Track.Thumb>
          <Track.IncreaseRepeatButton>
            <RepeatButton Command=""ScrollBar.PageDownCommand"" Focusable=""False"" Opacity=""0""/>
          </Track.IncreaseRepeatButton>
        </Track>
      </Grid>
    </ControlTemplate>
   </Setter.Value>
  </Setter>
</Style>";
        return (Style)XamlReader.Parse(xaml);
    }
}
