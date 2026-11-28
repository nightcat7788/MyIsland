# MyIsland

MyIsland 是一款 Windows 与 macOS 双端轻量级灵动岛系统监控与效率工具，以悬浮圆角胶囊实时展示硬件指标、媒体播放与时间，并支持悬停展开面板、多主题切换与灵动小形象互动。

**当前版本：1.1.9** · Windows WPF（.NET 8）· macOS Avalonia 11（Apple Silicon 原生）

Windows 与 macOS 各自一套原生自绘实现，共享 `src/Island.Core` 数据模型、读数三态语义、硬件传感器护栏（`ThermalSensorGuard`）、2.4G HID 无线外设协议引擎（`HidPeripheralProtocol`）与设置配置，两端功能与视觉高度对齐。开发与目录约定详见 [SOURCE-README.txt](SOURCE-README.txt) 与 [README-Mac.md](README-Mac.md)。

## 核心特性（v1.1.9）

- **双端统一悬浮胶囊**：
  - `combo` 复合胶囊与 Mac 视觉 1:1 对齐：`时间 | (C)CPU% (M)MEM% (G)GPU% (D)DISK% | (C)CPU° (G)GPU° (D)SSD° (B)BATT%⚡ | ↓ ↑`
  - 统一三档温度配色：`≤60°C` 绿色（`#2FD06B`）、`60°C–90°C` 黄色（`#F5C242`）、`≥90°C` 红色（`#FF453A`）
  - 支持 5 套收起态视图（复合 / 系统 / 温控 / 媒体 / 时钟）滚轮切换与自动轮播
- **工业级硬件监控与稳定性（参考 LiteMonitor 实践加固）**：
  - `ThermalSensorGuard`：异常值过滤、`Distance to TjMax` 自动排除、多核/单核优先级仲裁、历史回退保护与系统休眠唤醒（`PowerModes.Resume`）自动恢复
  - **温度采集链路**：基于微软签名的 **PawnIO** 驱动 + LibreHardwareMonitor（已彻底移除未签名 `inpoutx64` / SMN 直读路径），内置 `PawnIoDetector` 检测状态卡，并提供用户态 NVMe `IOCTL_STORAGE_QUERY_PROPERTY` 温度兜底与 WMI 热区回退
  - **处理器与显卡**：支持真实 LHM CPU 频率/电压采集 + 原生 `powrprof` (`CallNtPowerInformation`) 频率兜底；多 GPU 优先策略（独显优先于虚拟/核显）
  - **展开监控面板**：实时资源负载与温度、磁盘读写与占用、今日/会话双行累计流量、已开机时长、Top 进程榜、一键跳转任务管理器
- **双端完整效率组件**：
  - **媒体控制**：系统媒体会话播放控制与专辑封面显示（Windows SMTC / macOS 原生媒体服务）
  - **剪贴板历史**：文本与图片记录、快速预览弹窗、一键粘回
  - **文件中转站**：拖放暂存、快捷打开与复制
  - **设备与快捷控制**：内置电池 + Win10/11 原生蓝牙/BLE GATT + **2.4GHz USB HID 无线外设电量引擎**（参考 `Sapd/HeadsetControl`，支持 Logitech G / Lightspeed / Unifying / Bolt、Corsair Void/HS、SteelSeries Arctis/Nova、HyperX Cloud 等）+ XInput 手柄；内置零 PDH 开销内存清理与后台自动工作集瘦身；macOS 集成 Lunar DDC 外接显示器控制与内建屏守护
  - **个性化与热键**：多套暗色科技预设主题、小形象互动、全屏避让/鼠标穿透、全局快捷键速查与自定义设置窗口

## 目录结构

```text
├── src/
│   ├── Island.Core/       跨平台监控模型、三态 Reading<T>、ThermalSensorGuard
│   ├── Island.Native/     Windows PDH / NVMe / powrprof / 全屏检测底层采样
│   ├── Island.App/        Windows WPF 主应用（.NET 8）
│   └── Island.Avalonia/   macOS Avalonia 11 主应用（含显示器控制与原生工具桥接）
├── tests/Island.Tests/    零依赖单元测试套件（153 项断言全通过）
├── packaging/             Windows 便携包 / 交叉构建脚本与 macOS .app 打包脚本
├── installer/             Windows Inno Setup 安装包脚本（MyIsland.iss）
├── tools/                 macOS 原生辅助工具源码（Swift / Objective-C）与图标生成工具
└── branding/              产品图标与品牌资源
```

## 快速构建与测试

需要 **.NET 8 SDK**。

### Windows

```powershell
# 编译完整解决方案（0 警告 0 错误）
dotnet build Island.sln -c Release

# 运行单元测试（130 项用例）
dotnet run --project tests/Island.Tests -c Release

# 发布 Windows 自包含产物
dotnet publish src/Island.App/Island.App.csproj -c Release -r win-x64 --self-contained true
```

### macOS（Apple Silicon）

详细步骤与原生工具编译说明见 [README-Mac.md](README-Mac.md)：

```bash
bash packaging/macos/publish.sh
bash packaging/macos/make-app.sh
```

## 安全与许可

- 本项目**不包含也不分发**任何未签名内核驱动（如 `inpoutx64.dll`），Windows 传感器底层依赖微软签名的 [PawnIO](https://pawnio.eu/) 驱动。
- 全部监控数据本地采集，除天气功能（Open-Meteo）外零联网请求。
- 第三方开源组件许可声明见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
- 本项目基于 [MIT License](LICENSE) 开源。
