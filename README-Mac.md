# MyIsland for Mac（Avalonia 版）

Windows 灵动岛 MyIsland 的 Mac 版本，基于 Avalonia 11 自绘界面。与 Windows 版共用
数据模型（`Island.Core`）、弹簧动画、设置与天气服务；监控采样与显示器控制为 Mac 专属实现。
菜单栏常驻，不在 Dock 显示。

> 用户向的使用说明见交付包的 `Mac版/Mac版使用说明.md`；本文面向**改代码的人**。

## 构建

需要：**Apple Silicon Mac**、macOS 13+、Xcode 命令行工具、.NET 8 SDK。

```bash
cd 源码/MyIsland

# 0) 只编译（0 警告 0 错误是硬要求）
dotnet build src/Island.Avalonia/Island.Avalonia.csproj -c Release

# 1) 编译温度探针 —— 必须手动做。csproj 只负责"拷贝这个二进制"，不负责编译它
( cd tools/thermal-probe && clang -O2 -framework IOKit -framework CoreFoundation -o island-thermal thermal.m )

# 2) 发布到 publish/osx-arm64/
#    csproj 的 BuildDisplayTools target 会自动编译 ddc-helper / screen-guardian / island-screens
dotnet publish src/Island.Avalonia/Island.Avalonia.csproj \
  -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o publish/osx-arm64

# 3) 组装 .app + 图标 + Info.plist + 临时签名
bash packaging/macos/make-app.sh
# → publish/osx-arm64/MyIsland.app
```

第 3 步**不能省**：第 2 步只产出散文件，不是可双击的 `.app`。`make-app.sh` 还会再调一次
`tools/display-tools/build.sh`，保证随包工具与主程序协议一致（工具的退出码约定是 .NET 侧
判断"显示器是否支持某项"的依据，版本不匹配会静默失效）。

签名是 ad-hoc（`codesign --sign -`），不是 Developer ID，也未公证。分发给别人时对方需要
`xattr -dr com.apple.quarantine MyIsland.app`。

**Intel Mac 未适配**：显示器工具的 `build.sh` 目前只支持 arm64。

## 源码地图（src/Island.Avalonia/）

| 文件 | 职责 |
|---|---|
| `Program.cs` / `App.axaml.cs` | 入口 |
| `IslandWindow.cs` | 主窗口：置顶透明、悬停展开、弹簧、滚轮切视图、菜单栏托盘、**实时亮度写入管线**、事件接线 |
| `IslandCanvas.cs` | 全部自绘：4 个收起视图 + 监控 / 剪贴板 / 屏幕三个页签 + 图标库（`Glyph`） |
| `RenderKit.cs` | 渲染助手：文本 / 画笔 / 几何缓存、表盘、走势图、`Reading<T>` 三态着色 |
| `Samplers.cs` | Mac 采样：CPU（`host_statistics64`）、内存、网速、GPU（ioreg）、电池（IOKit）、温度（探针） |
| `DisplayControl.cs` | **显示器控制内核**：DDC/CI 读写（经 `ddc-helper`）、内建屏亮度（DisplayServices）、关闭 / 恢复内建屏（经 `screen-guardian` + SkyLight） |
| `AutoFollowEngine.cs` | 自动亮度：外接屏跟随内建屏（±5% 分步、1.5% 死区、2 秒轮询） |
| `LunarSettings.cs` | 每屏设置（范围 / 预设 / 跟随）读写，存于 plist `local.lunarcn.displaycontrol` |
| `ClipboardHistory.cs` | 剪贴板历史，纯内存 20 条 |
| `PetFaceAv.cs` / `Haptics.cs` | 小形象 / 触觉反馈 |
| `Themes.cs` | Palette 语义色，11 套主题 |

## 随包原生工具（tools/）

| 工具 | 源码 | 作用 |
|---|---|---|
| `island-thermal` | `thermal-probe/thermal.m` | CPU / SSD 温度（IOHID 传感器，无 root） |
| `ddc-helper` | `display-tools/DDCHelper.m` + `m1ddc/` | 外接屏 DDC/CI 读写。**退出码约定**：0 成功 / 2 写入未确认 / 3 显示器不支持 / 1 其它失败 |
| `screen-guardian` | `display-tools/ScreenGuardian.swift` + `BuiltinDisplayEngine.swift` | 关闭内建屏的独立守护进程：主程序崩溃、stdin 断开、外接屏被拔时自动恢复 |
| `island-screens` | `screen-list/screens.swift` | 屏幕枚举，输出 `ID\|UUID\|名称\|builtin/external\|physical/virtual` |

`ddc-helper` 与 `screen-guardian` 移植自开源项目 [LunarCN 0.6.1](https://github.com/fengly78/LunarCN)
（MIT，显示器控制层及其稳定性保护的参考实现），DDC 后端为 [m1ddc](https://github.com/waydabber/m1ddc)
（MIT，固定于提交 `7339ceeb`）。许可文本见 `tools/display-tools/NOTICE.txt`。

## 功能状态

| 功能 | 状态 | 说明 |
|---|---|---|
| CPU / 内存 / 网速 / 进程榜 | ✅ | 差分采样；`Reading<T>` 三态，读不到显示 `--` |
| CPU / GPU / SSD 温度 | ✅ | Apple Silicon 实测可读；其它机型未验证。开发机排除过的死路：SMC user client、IOReport、powermetrics |
| GPU 使用率 | ✅ | ioreg 通道，约 80–150ms/次，2 秒节流 |
| 电池 | ✅ | IOKit |
| 天气 | ✅ | Open-Meteo，复用 Windows 版 `WeatherService` |
| 剪贴板历史 | ✅ | 20 条，两列全可见，点击粘回 |
| **显示器控制** | ✅ | 亮度 / 对比度 / 音量 / 静音 / 预设 / 亮度范围 / 跟随内建屏 / 关闭内建屏 |
| 4 个收起视图 + 3 页签面板 | ✅ | 滚轮切视图，悬停展开 |
| 11 套主题 | ✅ | |
| 文件中转 / 音量浮条 / WLAN·蓝牙开关 | 🔲 | Windows 版专属功能，Mac 版未做 |
| 分辨率 / 刷新率 / 输入源 / HDR / 软件调光 | 🔲 | 未做 |
| Intel Mac | 🔲 | 未适配 |

## 设计原则

与 Windows 版一致：

- 采样失败保留上一帧，绝不让后台线程崩掉进程；
- 读不到的数据显示 `--` 或"不支持"，**绝不伪造**（包括不画假的 0%）；
- 全部本地数据，零联网（天气除外）。

显示器控制额外几条（移植 LunarCN 时学到的，改动前务必读）：

- **DDC/CI 必须串行**。同一块屏并发读写会让所有项返回空——这是 I2C 总线的物理限制。
  所有 DDC 操作都过 `DisplayControl._ddcGate` 这把信号量。
- **"读取失败" ≠ "显示器不支持"**。只有 `ddc-helper` 退出码 3 才可以永久跳过某项；
  临时失败必须允许重试，否则亮度会永远调不动。
- **内建屏亮度不能用 CoreDisplay**。它在本机恒返回 `1.0`；要用 DisplayServices。
- **关闭内建屏前只认"可见的实体外接屏"**。虚拟屏 / AirPlay / 睡眠中的屏不算，否则会黑屏。
- 写入后用**设备回读值**更新界面，不显示目标值。
