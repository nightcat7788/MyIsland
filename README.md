# MyIsland

MyIsland 是一款 Windows 与 macOS 菜单栏系统监控工具，以轻量胶囊展示实时指标，并提供可切换的小形象与主题。

**当前源码版本：1.1.2** · Windows WPF · macOS Avalonia · Apple Silicon 原生发布

## 功能

- CPU、内存、GPU、磁盘和网络读数；可用功能随操作系统不同。
- macOS 菜单栏胶囊、显示器控制与剪贴板历史。
- Windows 灵动岛窗口、监控面板与快捷控制。
- 8 种小形象款式，支持视线、表情、眨眼、呼吸、互动弹跳和待机动作。
- 主题与紧凑视图自定义。

## 界面截图

![MyIsland macOS 监控胶囊](docs/screenshots/macos-capsule.png)

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

Windows 便携版和单文件版也可在 macOS 上交叉构建：

```bash
bash packaging/win/build-win.sh
```

Windows 安装程序需在 Windows 上使用 Inno Setup 编译。Windows 包应在 Windows 设备上进行实际验证。

## 安全说明

AMD SMN 直读属于实验功能，默认关闭；项目不分发会安装未签名内核驱动的 `inpout` 二进制。请先阅读源码中的风险说明，再考虑启用相关实验路径。

## 许可证

本项目使用 MIT License。第三方代码保留各自许可证，见 `tools/display-tools/NOTICE.txt` 及相邻许可文件。
