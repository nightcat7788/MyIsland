# MyIsland for Mac（Avalonia 版）

Windows 灵动岛 MyIsland 的跨平台版本，基于 Avalonia 11 开发，与 Windows 版共享数据模型、
弹簧动画与渲染层设计，菜单栏托盘常驻。

## 构建与运行

需要 .NET 8 SDK（Mac 上 `brew install dotnet-sdk` 或官网安装包）：

```bash
# 开发运行（Windows/Linux/macOS 均可直接跑）
dotnet run --project src/Island.Avalonia -c Release

# Mac 发布（Apple Silicon）
dotnet publish src/Island.Avalonia -c Release -r osx-arm64 --self-contained \
  -p:PublishSingleFile=true

# Mac 发布（Intel）
dotnet publish src/Island.Avalonia -c Release -r osx-x64 --self-contained \
  -p:PublishSingleFile=true
```

发布产物在 `src/Island.Avalonia/bin/Release/net8.0/<rid>/publish/`。

## 功能对照（与 Windows 版）

| 功能 | Mac 版状态 | 说明 |
|---|---|---|
| 胶囊收起态 | ✅ | dash 仪表盘 / 时钟 / 小形象 / 信息胶囊（pet⇄胶囊 10s/30s 轮换） |
| 悬停展开 | ✅ | 150ms 开 / 350ms 关防抖（PointerMoved 热区判定） |
| 监控页 | ✅ | CPU 表盘 + 核心柱 + 内存条 + GPU + 硬盘 + 网速曲线 + Top 进程 + 电池 |
| 9 套主题 | ✅ | 托盘「面板主题」子菜单 |
| 小形象 | ✅ | 4 款式 + 心情 + 情境（流汗/晕眩/困倦）|
| CPU/内存/网速/进程 | ✅ | mach host_statistics64 + NetworkInterface + Process 差分 |
| 电池 | ✅ | IOKit IOPSCopyPowerSourcesInfo |
| CPU/GPU/SSD 温度 | ⚠️ | Apple Silicon 的 SMC/温度读取受系统限制，显示 "--"（不伪造） |
| GPU 使用率 | ⚠️ | macOS 需 IOReport（私有接口），显示 0 |
| 天气 / 剪贴板 / 文件中转 | 🔲 | 待做（Open-Meteo 跨平台可直接接） |
| 毛玻璃 | 🔲 | 待做（Avalonia TransparencyLevelHint 支持平台模糊） |

## 设计原则

与 Windows 版一致：
- 采样失败保留上一帧，绝不让后台线程崩掉进程；
- 读不到的数据显示 "--" 或隐藏，绝不伪造；
- 全部本地数据，零联网（天气除外，Open-Meteo 仅取天气）。

## 目录

```
src/Island.Avalonia/
├── IslandWindow.cs    主窗口：置顶透明 + 悬停展开 + 弹簧 + pet 轮换 + 托盘
├── IslandCanvas.cs    自绘：dash/clock/pet/combo + 监控页 + 剪贴板页
├── RenderKit.cs       渲染助手（文本/画笔缓存、表盘、平滑走势图）
├── PetFaceAv.cs       小形象（PetFace 的 Avalonia 移植）
├── Samplers.cs        ISampler：WinSampler（复用 Windows 全套）/ MacSampler（mach+IOKit）
├── Themes.cs          Palette 语义色（9 套）
└── App.axaml(.cs)     应用入口
```
