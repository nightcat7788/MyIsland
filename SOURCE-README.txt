MyIsland 源码包说明
====================

【版本】1.1.9 · 版本号集中定义在 Directory.Build.props。
        改版本号需同步四处：Directory.Build.props / packaging/macos/Info.plist / packaging/macos/make-app.sh / installer/MyIsland.iss
        第三方开源许可声明集中维护于 THIRD-PARTY-NOTICES.txt。

【结构】
MyIsland/
├── Island.sln                     解决方案（VS 2022 / dotnet build）
├── Directory.Build.props          全解决方案共享属性：版本号（Version / AssemblyVersion）在此定义一次
├── run.ps1                        运行入口（内存加载，绕 WDAC；需 pwsh）
├── dev.ps1                        开发脚本（build+publish+重启；自动回落用户目录 SDK）
├── sign.ps1 / start-admin.ps1     签名 / 管理员启动辅助
├── src/
│   ├── Island.App/                WPF 主应用（.NET 8, Windows）
│   │   ├── Controls/              IslandCanvas(液态岛自绘) IslandWindow(宿主弹簧)
│   │   │                          ExpandedPanel(展开面板XAML+页签) MonitorPage(监控页自绘)
│   │   │                          RenderKit(共享渲染层) ModuleSettingsWindow(显示项设置)
│   │   │                          PanelData(ViewModel)
│   │   ├── Services/              ThermalService(CPU/GPU/SSD温度: LHM+PawnIO / NVMe IOCTL / WMI)
│   │   │                          PawnIoDetector(PawnIO 驱动状态检测) MediaService(SMTC媒体控制)
│   │   │                          WeatherService(Open-Meteo) ControlService(亮度/WLAN/蓝牙)
│   │   │                          DeviceService(设备电量WMI) ClipboardService FileTrayService
│   │   │                          VolumeService(音量浮条) GlassBackdrop(毛玻璃) TrayIcon Settings
│   │   ├── Animation/             Spring(阻尼弹簧) PetFace(小形象: 4款式+情境反应)
│   │   │                          LiquidGeometry(液桥/水滴)
│   │   ├── Themes/Theme.cs        Palette 语义色 + 预设主题
│   │   └── app.ico                产品图标
│   ├── Island.Core/               监控模型（纯 .NET，跨平台）
│   │   └── Monitoring/            MonitorSnapshot / DiskStat / TopProcess / ThermalSensorGuard
│   ├── Island.Native/             SystemMonitor(PDH/powrprof: CPU/频率/内存/GPU/磁盘/网速/Top进程)
│   │                              NativeMethods(Win32) ScreenContext(全屏检测) GpuUsageReader
│   └── Island.Avalonia/           Mac 版（Avalonia 11 自绘；含显示器控制内核）。详见 README-Mac.md
├── packaging/macos/               Mac 打包：publish.sh（干净发布 + 产物校验）、make-app.sh（组装 .app + 签名）、Info.plist
├── packaging/win/                 Windows 打包：build-win.sh（在 macOS 上交叉构建便携版 zip + 单文件版 exe；
│                                  含 P0 危险条目检查与中文文件名编码自检；安装包需在 Windows 上用 Inno Setup 构建）
├── packaging/win-portable/        便携版的 4 个附加文件（README.txt / run.ps1 / 启动 MyIsland.bat / MyIsland图标.png）
├── tests/Island.Tests/            测试门禁（面向 Windows；mac 上需 -p:EnableWindowsTargeting=true 才能编译。
│                                  注意：目标框架 net8.0-windows，依赖 WindowsDesktop 运行时 —— 在 mac 上
│                                  编译得过但跑不起来。其中 SettingsTests / MonitorModelTests 两组不依赖
│                                  Windows，可在 mac 上用独立宿主链接同一批源文件执行）
├── tools/
│   ├── inpout/inpoutx64.dll       LHM 0.9.4 内嵌提取（AMD CPU 温度用户态 dll）
│   │                              ⚠ 一被加载就会装未签名内核驱动，不得随产品分发（见交付说明「安全须知」）
│   │                              ⚠ 已被 .gitignore 排除 —— 不入库，避免给高危二进制建永久快照
│   ├── thermal-probe/             Mac：CPU/SSD 温度探针 island-thermal（源码 thermal.m，需手动 clang 编译）
│   ├── display-tools/             Mac：显示器控制工具（ddc-helper / screen-guardian，移植自 LunarCN 0.6.1 + m1ddc）
│   ├── screen-list/               Mac：屏幕枚举工具 island-screens（源码 screens.swift）
│   ├── LogoGen/                   Logo 矢量渲染导出工具（PNG 多尺寸+ICO）
│   ├── DumpTypes/                 反射探测工具（类型/成员枚举；纯反射，不碰驱动与物理内存）
│   └── analyze*.py                截图像素分析脚本（PNG 手工解码器）
└── branding/                      品牌资产（svg/ico/png 9尺寸 + README 品牌规范
                                   + ref/ BrimDeck 官方参考图；macos/ 下有 .icns 与菜单栏图标）

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

【温度链路说明（ThermalService）】
- 常规路径：LibreHardwareMonitor + PawnIO 驱动（微软签名，需管理员）→ 读不到再退 WMI 热区 → 都没有显示 "--"
- 已于 v1.1.7 彻底移除未签名 `inpoutx64` / AMD SMN 直读路径，避免蓝屏与内核驱动风险。
- 温度护栏（ThermalSensorGuard）：自动排除 `Distance to TjMax`、过滤异常跳变、支持系统休眠唤醒恢复（`NotifySystemResume`）。
- SSD 温度：优先 LHM 存储传感器，支持用户态 NVMe `IOCTL_STORAGE_QUERY_PROPERTY` 兜底。
- 全部读数读不到时显示 "--"，绝不伪造。

【Mac 版】
src/Island.Avalonia/ —— Mac 版（Avalonia 11）。构建分三步：手动 clang 编译温度探针 → dotnet publish
  → packaging/macos/make-app.sh 组装 .app。逐步命令与注意事项见 README-Mac.md。
