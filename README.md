# MyIsland

MyIsland 是一款 Windows 与 macOS 菜单栏系统监控工具，以轻量胶囊展示实时指标，并提供可切换的小形象与主题。

**当前源码版本：1.1.2** · Windows WPF · macOS Avalonia · Apple Silicon 原生发布

Windows 与 macOS 各自一套原生实现（Windows WPF 自绘 + macOS Avalonia 自绘），共享
`src/Island.Core` 的数据模型与读数语义；两边共用的约定写在 [SOURCE-README.txt](SOURCE-README.txt)。

## 功能

- CPU、内存、GPU、磁盘和网络读数；可用功能随操作系统不同。
- **Windows**：灵动岛窗口、展开监控面板、快捷控制、剪贴板历史、小形象互动。
- **Windows 外设电量**：WMI 电池 + MSBatteryClass + BLE GATT 直读（0x180F/0x2A19）+ XInput 手柄档位；
  读不到就不显示，不做百分比换算伪装。
- **macOS**：菜单栏胶囊、显示器控制（DDC/内置背光）、剪贴板历史、图片预览。
- 8 种小形象款式，支持视线、表情、眨眼、呼吸、互动弹跳和待机动作。
- 主题与紧凑视图自定义；「暗色科技」设计系统（一套主题 = 近黑中性色 + 一个强调色 + 状态色）。

## 界面截图

![MyIsland macOS 监控胶囊](docs/screenshots/macos-capsule.png)

Windows 侧的实机截图与设计对照见 [docs/](docs/) 与 `branding/ref/`。

## 目录结构

```
├── src/                 Island.App(WPF/Windows) · Island.Avalonia(macOS) · Island.Core(模型) · Island.Native(PDH 采样)
├── tests/Island.Tests/  零依赖测试门禁（自研 harness，失败即非零退出码）
├── tools/               开发工具 + macOS 原生工具(Swift/ObjC)
├── packaging/           macOS / Windows / 便携版 打包脚本
├── branding/            品牌资产（Windows 图标 + mac/ 下的 macOS 图标）
├── driver/              PawnIO 温度驱动安装器（内嵌进程序，GPL-2.0，见 COPYING）
├── installer/           Inno Setup 安装包脚本
└── docs/                设计文档、改进记录、实机截图
```

## 构建

需要 .NET 8 SDK。详细的 macOS 环境和原生工具说明见 [README-Mac.md](README-Mac.md)。

macOS Apple Silicon 发布：

```bash
bash packaging/macos/publish.sh
bash packaging/macos/make-app.sh
```

Windows 本机开发构建：

```powershell
dotnet build src/Island.App/Island.App.csproj -c Release
```

Windows 免管理员安装包（需要 Inno Setup 7 + Windows SDK 的 signtool）：

```powershell
powershell -ExecutionPolicy Bypass -File release.ps1
```

详见 [DISTRIBUTION.md](DISTRIBUTION.md)。

Windows 便携版和单文件版也可在 macOS 上交叉构建：

```bash
bash packaging/win/build-win.sh
```

Windows 安装程序需在 Windows 上使用 Inno Setup 编译。Windows 包应在 Windows 设备上进行实际验证。

## 安全说明

- AMD SMN 直读属于实验功能，默认关闭；它会加载未签名内核驱动并映射 PCIe 物理内存。
  项目**不分发**会安装未签名内核驱动的 `inpout` 二进制（`tools/inpout/*.dll` 已从版本库排除，
  仅保留在本机源码目录供调试）。启用前请先阅读源码中的风险说明。
- 温度读取走微软签名的 PawnIO 驱动：程序内嵌官方安装器，仅在以管理员运行且驱动缺失时静默安装。
  许可证全文见 `driver/COPYING-PawnIO.txt`。
- 发布包内会排除 `inpout*.dll` 与 `*.pdb`（见 `installer/MyIsland.iss` 的 `Excludes`）。

## 许可证

本项目使用 MIT License。第三方代码保留各自许可证，见 `tools/display-tools/NOTICE.txt` 及相邻许可文件。
