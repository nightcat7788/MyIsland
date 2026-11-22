MyIsland 源码包说明
====================

【结构】
MyIsland/
├── Island.sln                     解决方案（VS 2022 / dotnet build）
├── run.ps1                        运行入口（内存加载，绕 WDAC；需 pwsh）
├── dev.ps1                        开发脚本（build+publish+重启；自动回落用户目录 SDK）
├── sign.ps1 / start-admin.ps1     签名 / 管理员启动辅助
├── src/
│   ├── Island.App/                WPF 主应用（.NET 8, Windows）
│   │   ├── Controls/              IslandCanvas(液态岛自绘) IslandWindow(宿主弹簧)
│   │   │                          ExpandedPanel(展开面板XAML+页签) MonitorPage(监控页自绘)
│   │   │                          RenderKit(共享渲染层: 文本/画笔缓存+表盘+走势图+分级色)
│   │   │                          PanelData(ViewModel)
│   │   ├── Services/              ThermalService(CPU/GPU/SSD温度: LHM+inpoutx64/SMN)
│   │   │                          WeatherService(Open-Meteo) ControlService(亮度/WLAN/蓝牙)
│   │   │                          DeviceService(设备电量WMI) ClipboardService FileTrayService
│   │   │                          VolumeService(音量浮条) GlassBackdrop(毛玻璃) TrayIcon Settings
│   │   ├── Animation/             Spring(阻尼弹簧) PetFace(小形象: 4款式+情境反应)
│   │   │                          LiquidGeometry(液桥/水滴)
│   │   ├── Themes/Theme.cs        Palette 语义色 + 9 预设主题
│   │   └── app.ico                产品图标
│   ├── Island.Core/               监控与配额模型（纯 .NET，跨平台）
│   │   ├── Monitoring/            MonitorSnapshot / DiskStat / TopProcess
│   │   └── Quota/                 QuotaService + JSONL 本地用量扫描（Claude/Codex）
│   ├── Island.Native/             SystemMonitor(PDH: CPU/核心/内存/GPU/磁盘/网速/Top进程)
│   │                              NativeMethods(Win32) ScreenContext(全屏检测) GpuUsageReader
│   └── Island.Avalonia/           Mac/跨平台版（Avalonia 11，详见 README-Mac.md）
├── tools/
│   ├── inpout/inpoutx64.dll       LHM 0.9.4 内嵌提取（AMD CPU 温度用户态 dll）
│   ├── LogoGen/                   Logo 矢量渲染导出工具（PNG 多尺寸+ICO）
│   ├── DumpTypes/                 反射探测工具（资源提取/驱动行为探测）
│   └── analyze*.py                截图像素分析脚本（PNG 手工解码器）
└── branding/                      品牌资产（svg/ico/png 9尺寸 + README 品牌规范
                                   + ref/ BrimDeck 官方参考图）

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
要温度（CPU/SSD）：以管理员启动（start-admin.ps1 或托盘菜单"以管理员重启"）

【温度链路说明（InpOutSmnService / ThermalService）】
- AMD：系统已装的 inpoutx64 内核驱动 + ECAM 内存映射直读 SMN（零提权）
- Intel/其他：LibreHardwareMonitor + PawnIO 驱动（需管理员）
- SSD：LHM 存储传感器（NVMe IOCTL，多数机器需管理员）
- Intel 核显温度：无公开接口，任何权限都读不到，显示 "--"
- 全部读数读不到时显示 "--"，绝不伪造

【Mac 版】
src/Island.Avalonia/ —— Avalonia 11 跨平台实现，详见 README-Mac.md
