MyIsland 源码包说明
====================

【版本】1.1.3 · 本目录是 git 仓库（分支 main），版本号集中定义在根目录的 Directory.Build.props。
        改版本号需同步三处：Directory.Build.props / packaging/macos/Info.plist / installer/MyIsland.iss
        （Windows 发布流水线 release.ps1 会自动读 props，再把 /DAppVersion= 注入安装包脚本。）

【结构】
仓库根 = 解决方案根（src/ tests/ tools/ 都在根下，不再套一层 MyIsland/ 子目录）：
├── Island.sln                     解决方案（VS 2022 / dotnet build）
├── Directory.Build.props          全解决方案共享属性：版本号（Version / AssemblyVersion）在此定义一次
├── run.ps1                        运行入口（内存加载，绕 WDAC；需 pwsh）
├── dev.ps1                        开发脚本（build+publish+重启；自动回落用户目录 SDK）
├── release.ps1                    Windows 发布流水线（测试→publish→签名→安装包→便携 zip→校验清单）
├── sign.ps1 / start-admin.ps1     签名 / 管理员启动辅助
├── src/
│   ├── Island.App/                WPF 主应用（.NET 8, Windows）
│   │   ├── Controls/              IslandCanvas(液态岛自绘: 玻璃外壳+顶部辉光+收起态各视图) IslandWindow(宿主弹簧)
│   │   │                          ExpandedPanel(展开面板XAML) PanelStyles(面板控件样式: 按钮/开关/滑杆/滚动条)
│   │   │                          MonitorPage(监控页自绘) ThermalDisplay(温度更新: 画面变没变的判断)
│   │   │                          RenderKit(共享渲染层: 缓存/等宽数据字体/发光)
│   │   │                          Glyphs(矢量图标: 页签/设备剪影/齿轮等, Glyphs.IconOf 设备类型→图标) ModuleSettingsWindow(显示项设置) PanelData(ViewModel)
│   │   │                          DevicesPage(设备电量自绘) SettingsWindow(设置中心: 与面板共用控件样式的两栏卡片)
│   │   ├── Services/              ThermalService(CPU/GPU/SSD温度: LHM+inpoutx64/SMN)
│   │   │                          PawnIoInstaller(温度驱动 PawnIO 自举: 内嵌安装器校验+静默安装)
│   │   │                          IntelComputePolicy(只有核显时拦下库的 Intel GCL 初始化)
│   │   │                          MemoryReclaim(用完即还: 面板/设置窗口关闭后压缩回收)
│   │   │                          WeatherService(Open-Meteo) ControlService(亮度/WLAN/蓝牙)
│   │   │                          DeviceService(设备电量: WMI 电池 + BLE GATT + XInput 档位)
│   │   │                          BleBatteryService(BLE GATT 电量直读) XInputService(手柄档位)
│   │   │                          ClipboardService FileTrayService
│   │   │                          VolumeService(音量浮条) GlassBackdrop(毛玻璃) TrayIcon Settings
│   │   │                          ThermalSnapshot(温度快照; 独立文件, 被三个工程链接)
│   │   ├── Animation/             Spring(阻尼弹簧) PetFace(小形象: 4款式+情境反应)
│   │   │                          LiquidGeometry(液桥/水滴)
│   │   ├── Themes/Theme.cs        Palette 语义色 + 9 预设主题（每套 = 近黑中性色 + 一个强调色 + 状态色）
│   │   └── app.ico                产品图标
│   ├── Island.Core/               监控模型（纯 .NET，跨平台）
│   │   └── Monitoring/            MonitorSnapshot / DiskStat / TopProcess
│   │                              Reading(读数三态: 读到了/读不到/没请求) SamplerDemand(采样需求位)
│   ├── Island.Native/             SystemMonitor(PDH: CPU/核心/内存/GPU/磁盘/网速/Top进程;
│   │                              明细按需: 进程榜等只在面板展开时采, 见 SetDetailWanted)
│   │                              NativeMethods(Win32) ScreenContext(全屏检测) GpuUsageReader
│   └── Island.Avalonia/           Mac 版（Avalonia 11 自绘；含显示器控制内核）—— Windows/macOS 各自一套原生实现，
│                                  共享 Island.Core 的模型与 Island.Native 的采样器。详见 README-Mac.md
│       ├── MacInterop/Haptics/DisplayControl/DeviceBattery   macOS 原生互操作（ObjC 运行时/DDC/电池/触感）
│       ├── AutoFollowEngine/HotKeyClient/HistoryBuffer        全局热键、事件历史、跟随窗口
│       └── ClipDetailWindow/ImagePreviewWindow               剪贴板详情与图片预览
├── tests/Island.Tests/            测试门禁（面向 Windows；零 NuGet 依赖，自研 harness，Check 失败即抛、退出码非 0）
│                                  mac 上需 -p:EnableWindowsTargeting=true 才能编译，且 net8.0-windows 依赖
│                                  WindowsDesktop 运行时 —— 在 mac 上编译得过但跑不起来
├── tools/
│   ├── inpout/inpoutx64.dll       LHM 0.9.4 内嵌提取（AMD CPU 温度用户态 dll）
│   │                              ⚠ 一被加载就会装未签名内核驱动，不得随产品分发（见交付说明「安全须知」）
│   │                              ⚠ 已被 .gitignore 排除 —— 不入库，避免给高危二进制建永久快照
│   ├── LogoGen/                   Logo 矢量渲染导出工具（PNG 多尺寸+ICO，Windows 侧）
│   ├── DumpTypes/                 反射探测工具（类型/成员枚举；纯反射，不碰驱动与物理内存）
│   ├── IconGen/                   Mac：应用图标生成（icongen.swift → island.iconset / island.icns）
│   ├── display-tools/             Mac：显示器控制工具（ddc-helper / screen-guardian，移植自 LunarCN 0.6.1 + m1ddc）
│   ├── thermal-probe/             Mac：CPU/SSD 温度探针 island-thermal（源码 thermal.m，需手动 clang 编译）
│   ├── screen-list/               Mac：屏幕枚举工具 island-screens（源码 screens.swift）
│   ├── device-battery/ hotkey/ app-icon/   Mac：电池/全局热键/应用图标工具（Swift）
│   └── analyze*.py                截图像素分析脚本（PNG 手工解码器）
├── driver/                        温度驱动（PawnIO）官方安装器 PawnIO_setup.exe 2.2.0（原样内嵌进 Island.App，
│                                  不要修改）+ COPYING-PawnIO.txt 许可证全文 + README.md（来源/SHA-256/升级步骤）
├── installer/MyIsland.iss         Inno Setup 安装包脚本（当前用户安装免管理员；版本由 /DAppVersion 注入）
├── packaging/                     打包脚本
│   ├── macos/                     Mac：publish.sh（干净发布 + 产物校验）、make-app.sh（组装 .app + 签名）、Info.plist
│   ├── win/                       Windows：在 macOS 上交叉构建便携版 zip + 单文件版 exe（含危险条目与中文文件名自检）
│   └── win-portable/              便携版的 4 个附加文件（README.txt / run.ps1 / 启动 MyIsland.bat / MyIsland图标.png）
├── branding/                      品牌资产（svg/ico/png 9尺寸 + README 品牌规范 + ref/ BrimDeck 官方参考图）
│   └── mac/                       Mac 专属品牌资产（island.icns / island.iconset / 菜单栏图标 / 新版 32px 图标）
└── docs/                          设计文档与实机截图（改进记录 = docs/改进记录.md）

【构建】
系统装有 .NET SDK 时：
  dotnet build src/Island.App/Island.App.csproj -c Release
  dotnet publish src/Island.App/Island.App.csproj -c Release -r win-x64 --self-contained
本机系统目录只装了运行时：dev.ps1 会自动回落到 %USERPROFILE%\dotnet-sdk
（dotnet-install.ps1 装的用户级 SDK）。
注意：某些精简 shell 缺 ProgramFiles/ProgramData 等标准环境变量，NuGet 还原会报
"Value cannot be null (path1)" —— dev.ps1 已内置补齐。

【运行】
普通：直接跑 publish 输出的 Island.App.exe（或分发目录的 Island.App.exe）
WDAC 机器：pwsh -NoProfile -File run.ps1
要温度（CPU/GPU）：以管理员启动（start-admin.ps1 或托盘菜单"以管理员重启"）；驱动由程序自己装，见下
开发迭代：pwsh -File dev.ps1（跑测试 → 构建 → publish → 重启）
出包：pwsh -File release.ps1（签名 → Inno 安装包 → 便携 zip → SHA256 清单）

【温度链路说明（ThermalService）】
- 驱动：LibreHardwareMonitorLib 0.9.6 读 CPU 温度（MSR）走 PawnIO 驱动（微软签名）。库只会"打开"驱动设备，
  不会安装。MyIsland 自己带了驱动：内嵌官方签名的 PawnIO 2.2.0 安装器（driver/PawnIO_setup.exe，详见
  driver/README.md），以管理员运行、且驱动缺失或低于 2.0.0.0 时在后台静默安装
  （Services/PawnIoInstaller.cs，2026-11-24）。界面不等待安装；装完之后温度服务在下一次采样时
  自动重建读数对象，不需要重启程序。用户不用手工安装任何东西。
- CPU 温度需要两个条件同时满足：① PawnIO 已安装 ② 进程以管理员运行。
  非管理员 → 温度位显示"需管理员"；管理员、但驱动还没到位（正在装 / 装失败）→ 显示"缺驱动"。
- CPU 覆盖以库为准：Intel 走 IntelCpu；AMD 只支持 Zen（Family 0x17 / 0x19 / 0x1A）与 K8（0x0F）——
  Family 0x10–0x16 上游不支持（库的 CpuGroup.cs 里创建 Amd10Cpu 的那行被注释掉了），
  这些 CPU 上装了驱动也读不到，温度显示 "--"。
- 常规路径：LibreHardwareMonitor + PawnIO 驱动（需管理员）→ 读不到再退 WMI 热区 → 都没有显示 "--"
- AMD SMN 直读（InpOutSmnService）：**默认关闭；AMD 机器上首次启动自动开启（只此一次，2026-11-25）**。
  入口在 设置 › 系统 › 高风险实验功能。它会加载未签名的 inpoutx64.sys 内核驱动并映射 PCIe ECAM/MMIO 物理内存，
  2026-11-23 在本机（Intel）曾三次触发 0x1EA THREAD_STUCK_IN_DEVICE_DRIVER 蓝屏；
  现已改为「先判 CPU 厂商再碰驱动」+「只认 0xE0000000」+「不随包发布 inpoutx64.dll」。
  自动开启的规则钉在 SmnChannel.AutoEnableOnFirstRun（纯函数，有测试）：只有 AMD 才开，Intel 保持关闭 ——
  本机设置里遗留的"已开启"就是被这一次自动纠正回 false 的（crash.log 里有记录）。首启落定后**不再自动改**：
  用户（包括 AMD 用户）在设置里关掉就是关掉；「全部重置」也不重置 SmnAutoDone，不会复活它。
- SSD：LHM 存储传感器（NVMe IOCTL）—— 2026-11-22 在本机同样触发过 0x1EA 蓝屏，已永久关闭
- Intel 核显温度：无公开接口，任何权限都读不到，显示 "--"
- 全部读数读不到时显示 "--"，绝不伪造

【性能与内存（2026-11-24 优化，改这些地方前先读）】
- 实测基线（本机，管理员运行）：空闲 CPU 7.66% → 5.21% 单核；启动峰值工作集 929–1132 MB → 252 MB（2026-11-25
  落地软件渲染后进一步降到 126.7 MB，见下面"软件渲染"）；工作集 276 MB → 121 MB。这组数字来自不含"明细按需采样"
  的构建；加上采样层的完整版过了构建、全部测试和采样器行为检查，实机数字见 docs/改进记录.md 第九节。
- 展开态的稳态 CPU（2026-11-24 实机诊断，按线程拆）：监控页每重绘一次约 30ms（录制指令约 5ms +
  软件光栅化约 25ms），是展开态最大的一块。温度更新原来每 1.4 秒都让监控页整页重绘，而页面上只用得到
  取整后的几个数 —— 现在"画面会变才重绘"（见下面"界面设计系统"一节）。排查这类问题用诊断构建：
  给渲染路径加计数器和计时器、每 5 秒按线程导出一次 CPU，比看任务管理器的总数有用得多。
- Intel GCL（IntelComputePolicy）：LibreHardwareMonitor 0.9.6 在 Windows 上会无条件初始化 Intel 的
  Level Zero 计算栈，实测不可预测地申请 700 MB – 4.5 GB 提交内存、耗 1.4–1.8 秒 CPU，而只有核显的
  机器上它一个读数都不产生。机器上的 Intel 显卡全是核显时把它拦下；有 Intel 独显或判断不出来则不动。
  必须在创建第一个 Computer 之前生效（ThermalService.OpenComputer），探测在 App 启动时后台完成。
- 毛玻璃（GlassBackdrop）：身后桌面没变就不换图、不重绘；连续 8 张没变化后截图间隔 0.5s → 1s。
  "收敛窗口"（ConvergeRounds）不能删：底图截的是岛体自己，每换一张岛体颜色只向主题色靠一步，
  少了它颜色会停在中途。改动判定逻辑时跑 Island.Tests 的 GlassBackdropTests。
- 小形象（IslandCanvas.InvalidatePet / PetFace）：小形象画在自己的子图层上，每帧只重画这一小块；
  画刷/画笔/渐变全部走 RenderKit 的冻结缓存。不想改外观的渲染改动（性能、重构）要先存一份当前渲染，
  改完逐像素对比再合入。⚠️ 2026-11-24 的界面重做是有意改了外观，之前的基线图作废，基线 = 重做后的渲染。
- 按需启停：鼠标跟踪器只在"没开穿透且不是长关"时运行（IslandWindow.UpdatePointerTracking）；
  只有监控页才用的明细采样在面板展开时才开（SystemMonitor.SetDetailWanted）；跨平台版与
  设置中心的实时预览要显式 SetDetailWanted(true)。
- 展开面板后的 CPU 进程榜（SystemMonitor.SampleTopProcesses）：榜是两次采样做差分出来的。
  上升沿那一帧不受 3 秒节流；上一份基线超过"3 秒 + 1.5 个采样间隔"（默认 4.5 秒）就只记基线，
  0.6 秒后再差分出第一份榜（这期间显示上一份榜）。**不要改回去拿整段时间做差**：那是收起期间的
  平均占用，一个展开前已经停掉的进程会排在榜首。进程榜的任何改动都要用真机行为检查验证
  （展开后 1.5 秒内出榜、已停的进程不在榜里、数值 0..100）。
- 用完即还（MemoryReclaim）：面板/设置窗口关闭后、界面静止 5 秒，做一次压缩回收并裁剪工作集。
  只在界面空闲时做（IslandWindow.IsUiIdle），Request 不阻塞调用方。
- 软件渲染（2026-11-25 落地，App.OnStartup 的第一行）：RenderOptions.ProcessRenderMode = SoftwareOnly。
  这个应用只有 AllowsTransparency 的分层窗口，永远用不上硬件加速，WPF 却为它创建 D3D 设备把显卡驱动的
  两百多兆拉进进程。实测（同一台机器、同一场景）：启动峰值工作集 245.7 → **126.7 MB**（提交 174 → 59 MB），
  收起态空闲 CPU 持平（4.4–4.8%），收起后 CPU 反而更低（2.9 → 1.4%）；代价是**展开态 CPU +2pp 单核**
  （2.8 → 4.75%，栅格化从渲染线程落到 UI 线程，大头是毛玻璃每 0.125 秒触发的整面重绘）与文字抗锯齿的
  亚像素差异（约 1% 像素，放大 5 倍肉眼才看出；不透明对话框从 ClearType 变灰度）。要回硬件渲染：
  删掉 App.OnStartup 里那一行即可。**不要**在别处再挂别的渲染模式开关 —— 渲染模式是进程级的，只该有一个地方决定。

【界面设计系统（2026-11-24 重做，改界面前先读）】
方向是"暗色科技"（近黑底 + 一束强调色辉光 + 等宽的数据字体）。方法论取自两个开源的界面设计技能包
（HeiGe-UI 的"暗色科技"配方、ui-aesthetics 的克制原则）。改动明细与对比图见
docs/改进记录.md 第十节。规则只有下面这几条，别破：
- 一套主题 = 一组近黑的中性色 + **一个**强调色 + 状态色（Themes/Theme.cs）。数据不按指标配色：
  UI 里不许再出现写死的"CPU 蓝 / 内存青 / GPU 粉"，颜色一律取 Palette 的语义色。
- 强调色只给主角和选中态：CPU 主读数（数字、环、走势线）、面板顶部那束辉光、选中的电源计划按钮。
  其余读数是中性色，只有 ≥70% / ≥90% 才换成状态色（RenderKit.LevelColor）—— 告警是画面里唯一跳出来的颜色。
- 发光是稀缺资源（暗色科技的全部"科技感"就靠它）：CPU 主读数的辉光是一张按内容缓存的位图
  （Controls/RenderKit.cs 末尾的 GlowText：半分辨率画字 + 三遍盒式模糊；数字和颜色没变就原样复用，重做一次
  不到 1ms），实心字照常画在它上面。开口环用 RenderKit.Dial 的 glow 参数，选中的电源计划用 PanelStyles 的
  AccentBtn（辉光由 ExpandedPanel.ApplyTheme 的 "Glow" 资源给）。
  **不要给页面挂 BlurEffect**：软件渲染里每个渲染通道都会重算一遍模糊（毛玻璃每更新一次就是一个通道），
  实测约 3.5ms。**也不要用"多层半透明错位描边"模拟发光**：每一次 DrawText 在软件渲染里约合 0.2ms，
  48 次描边就是 10ms/帧。
  **不要给页签挂 Effect**：软件渲染下按钮会被重新采样，强调色发灰、边缘发糊（试过，已撤）。
- 顶部辉光与细网格画在 IslandCanvas.OnRender 里（RenderKit.Spotlight，按颜色缓存），只属于深色主题；
  读数类收起视图的辉光画在压层之上（DrawCapsuleBase）。像素主题不铺网格。浅色主题不发光、不铺网格。
- 数据用等宽字体：读数走 RenderKit.Num —— **一段读数只调一次 DrawText（整串排版）**，数字天然等宽。
  不要为了"数字居中进格子"逐字画：每一次 DrawText 在软件渲染里约合 0.2ms，仪表盘视图一帧多出 5ms。
  拉丁标签 / 单位 / 进程名走 RenderKit.Mono，中文走 RenderKit.Text（界面字体）。等宽体没有汉字，
  混排时靠逐字回退到微软雅黑。限宽省略的文本（进程名、显卡名）走 Trimmed / MonoTrimmed：按"文本 + 可用宽度"
  缓存，拿到后只画不改。
- **重绘很贵，数据更新不等于画面变化**：监控页重绘一次（录制绘制指令 + 软件光栅化）约 30ms。温度服务
  每 1.4 秒推一次快照，但页面上只用得到取整后的几个数 —— 所以温度更新先过 Controls/ThermalDisplay.Signature
  （与 OnRender 的显示格式逐项对应），画面会变才 InvalidateVisual（MonitorPage.ThermalUpdate）。
  改监控页里温度的显示格式时要同步改 ThermalDisplay（DesignSystemTests 守着取整边界）。
  给别的页加"数据驱动"的重绘时照这个做：先问画面变没变。
- 收起态切视图的变形帧（小形象 → 信息胶囊、滚轮换视图）：本体还在长的那零点几秒，内容按**最终的本体矩形**排版、
  裁进当前的本体（IslandCanvas.FinalBodyRect 由宿主提供，每次渲染现取弹簧的目标值），胶囊长大时把最终版面
  从中间揭开。不要改回"按当前矩形排"：网速块贴着当前右缘，会和表盘叠在一起，装不下的部分画到胶囊外面。
  本体静止时两个矩形相等，走原来的路径 —— 不加裁剪、画面逐像素不变。各视图的压层（DrawCapsuleBase）永远按
  当前本体（_bodyNow）画，不跟随排版矩形。
- 面板字号只用四档：36（主读数）/ 17（行读数）/ 12（标签与说明）/ 11（页脚与计数）。
- 面板里的控件必须用 PanelStyles.xaml 的样式（QuietBtn / AccentBtn / TabBtn / ToggleSwitch / ThinSlider /
  ThinProgress），不要放默认模板的 WPF 控件；图标用 Glyphs.cs 的矢量图标，不要用 emoji。
- 设备图标统一走 `Glyphs.IconOf(kind)`（鼠标/键盘/耳机/手柄剪影，认不出给电池轮廓），设备页电量环与收起态
  设备行共用；不要再写单字"鼠/键/耳/柄"。
- 展开页顶栏 = 左页签 + 中时钟 + 右齿轮(设置中心, btnSettings) + ✕。整体 UI/UE 的参照是 BrimDeck
  （官方截图在 branding/ref/；进程榜排序的分段药丸、顶栏齿轮、设备剪影图标都是 2026-11-25 对齐它的）。
- 设置窗口（SettingsWindow）与面板共用同一套：构造时合并 PanelStyles.xaml，令牌由 ExpandedPanel.ApplyTheme 写进
  窗口资源（SettingsWindow.Res<T>("Fg") 取用）。给设置窗口加控件先找 PanelStyles 里有没有；面板按钮的尺寸是按
  小面板定的，设置窗口里用 SettingsWindow.WindowButton 放大一号。别的窗口要跟主题走也照这个做，不要各写一套颜色。
- 设置窗口里的交互逻辑是有来历的（保存失败的提示与确认、"全部重置"的两段确认和顺序、高风险开关的二次确认、
  开关的键盘可达和读屏接口）：改样式时逐段保留。验证交互用"进程内 + 设置目录重定向到临时目录"的办法
  （Settings.DirectoryOverride），别拿真实的 settings.json 试。
- 文字对比度、等宽数字、坏数值不崩，由 Island.Tests 的 DesignSystemTests 守着（改主题色先跑它）。
- 换主题：IslandWindow.UpdatePanelResources → ExpandedPanel.ApplyTheme 写全套令牌 → Retint 重取选中态。
  新增面板资源要在这里登记，浅色主题（IsDark = false）要有对应的取值。
- 验证界面改动的方法：用真实控件离屏渲染（每套主题 × 收起态各视图 × 面板各页），对比改前改后，
  再单独看三种边界状态（高负载告警 / 非管理员 / 没有 GPU 读数）会不会叠字或溢出。

【设备电量链路（2026-11-25 增强，改这里前先读）】
- 数据源四条，DeviceService.Sample 合并：① Win32_Battery（笔记本电池，多块取最低）
  ② MSBatteryClass（Windows 主动上报电量的蓝牙设备）③ BLE GATT 直读（BleBatteryService：
  Battery Service 0x180F / 0x2A19，补 ② 不上报的设备 —— 本机的 ThinkBook Bluetooth Mouse
  就只有这条能读到）④ XInput 手柄档位（XInputService：空/低/中/满）。
- **不伪装原则**（学自 ai-periph-monitor）：读不到的设备不进列表；回包超出 0..100 不显示；
  手柄只有档位，**不换算百分比**（DeviceInfo.LevelText，界面画文字）；缓存的读数过期
  （30 分钟）就从列表消失，宁可消失也不显示旧值。
- GATT 读数节流：一台设备读成功后 10 分钟内不重读；FindAllAsync 带额外属性（父设备名）
  在部分 Windows 版本会抛 COMException 0x80070490 —— 已退化为失败即回落不带属性的查询，
  改这里的兼容逻辑时两种路径都要能走通。
- 低电量提醒（IslandWindow.CheckBatteryAlert）：阈值设置里可调（默认 20%），同一台设备
  60 分钟最多一次，复用高温告警的活动浮条。**提醒开着时设备采样退到 5 分钟一轮的慢轮询**
  （DeviceService.BackgroundWatch）—— 设备采样本来是"谁要看才跑"的按需模型，别把它改回常开。
- 监控页显示项「外设电量」（MonitorPage 的 Want("devices")，ModuleSettingsWindow 可勾选）：
  每台外设一行（读数右对齐、名字等宽截断，最多 3 行），手柄档位照画文字；**本机电池不在这组**
  —— 页脚的"电池"负责它，两处别重复。排不下就不画（与页脚同一套规矩）。
- 厂商自定义 HID 协议（2.4G 接收器：罗技 HID++ / ROG / 雷蛇等）**没有做**：每条都要按
  VID/PID+接口+报文适配并实机回包验证，没有对应硬件前不写（ai-periph-monitor 的规矩）。
- 设备类型是按名字关键词猜的（猜不出显示"蓝牙设备"），不伪装成已知类型。

【Mac 版】
src/Island.Avalonia/ —— Mac 版（Avalonia 11 自绘；含显示器控制内核）。构建分三步：
  手动 clang 编译温度探针（tools/thermal-probe）→ dotnet publish（packaging/macos/publish.sh）
  → packaging/macos/make-app.sh 组装 .app 并签名。逐步命令与注意事项见 README-Mac.md。
Windows 与 macOS 各自一套原生实现，共享 Island.Core 的数据模型（MonitorSnapshot / Reading / SamplerDemand）
与 Island.Native 的 Windows 采样器；跨平台改动先看 Island.Core 的接口再动两边。
