# MyIsland 品牌规范（Branding）

仓库里有**两套图标**，分别对应两个平台，不要互相替换：

| 目录 | 平台 | 设计 |
|---|---|---|
| `branding/`（根） | Windows | 玻璃岛屿 + 4 根频谱律动条（矢量源 `island-logo.svg`，由 `tools/LogoGen` 生成） |
| `branding/mac/` | macOS | 胶囊 + 270° 仪表环 + 三条读数条（2026-11-24 第二版，矢量源 `tools/IconGen/icongen.swift`） |

## Windows 图标构成

| 元素 | 含义 | 取值 |
|---|---|---|
| 深色圆角底板 | 桌面常驻工具的"底座"，保证在任意壁纸/任务栏上清晰 | 圆角 108 的方形，渐变 `#1E222C → #12141A` |
| 玻璃岛屿（squircle） | 产品本体"灵动岛" | 超椭圆 n=4.2，`#465570 → #161921`，42% 处 `#262A36` |
| 内部辉光 | 实时数据的"呼吸感"：橙(低音) / 蓝(中频) / 绿(高频) | `#E8A24A` / `#5A96FF` / `#3CBE78` |
| 4 根律动条 | 4 频段频谱（与程序内 FFT 完全对应） | 白 → `#D8E6FF` 渐变，60/90/120/150 高 |

## macOS 图标构成（2026-11-24 第二版）

| 元素 | 含义 | 取值 |
|---|---|---|
| 圆角方形底板 | macOS 规范的图标底座；深色渐变，浅/深壁纸都清晰 | 圆角 22.5%，`#2E3240 → #15171E` |
| 胶囊岛 | 产品本体"灵动岛"，屏幕顶端那个胶囊 | 宽 71.5%，`#0B0D12` |
| 270° 仪表环 | 监控的核心隐喻（与面板表盘同款），已用量 72% | `#5A96FF`，轨道 `#2A2F3C` |
| 三条读数条 | CPU / 内存 / 网速，长度各不同 | `#5A96FF` / `#4FE3D7` / `#3CBE78` |

> 两套设计的取向不同：Windows 侧是"频谱律动"，macOS 侧是"仪表读数"。
> 各自平台内保持统一即可；跨平台统一是产品决策，改之前先对齐两边。

## 色彩

```
主强调   #5A96FF   （界面 Accent）
青       #4FE3D7   （内存 / macOS 图标读数条）
暖强调   #E8A24A   （温度/警示）
绿色     #3CBE78   （下行速度/正常）
深底     #282B36   （面板卡底）
次要文字 #A0A6B6
```

## 资产清单（branding/）

| 文件 | 平台 | 用途 |
|---|---|---|
| `island-logo.svg` | Windows | 矢量主文件（可无损缩放、印刷） |
| `island-logo.ico` | Windows | Windows 图标（16/24/32/48/64/128/256 多尺寸） |
| `island-logo-{16..1024}.png` | Windows | 各尺寸位图（网页/文档/商店用） |
| `ref/` | — | 对照参考图（BrimDeck 官方截图，UI/UE 对齐基准） |
| `mac/island.icns` | macOS | macOS 应用图标（16→1024 全尺寸 @1x/@2x） |
| `mac/island.iconset/` | macOS | icns 的源目录 |
| `mac/island-logo-32.png` | macOS | 菜单栏/面板用的 32px 图标 |
| `mac/menubar/` | macOS | 菜单栏胶囊图标（22/44px） |

## 重新生成

Windows 侧（`tools/LogoGen`，C# 矢量渲染）：

```powershell
# 改 tools/LogoGen/Program.cs 里的几何参数后：
cd tools/LogoGen; dotnet build
# 用 pwsh 内存加载运行（WDAC 拦截 exe 直接启动）：
pwsh -NoProfile -File ..\logogen-run.ps1
```

macOS 侧（`tools/IconGen`，CoreGraphics）：

```bash
bash tools/IconGen/build.sh
```

> 注意：本机 WDAC 策略禁止直接执行未签名 exe，Windows 侧工具均以「内存加载」方式运行。

## 使用规范

Windows 侧：

- 最小使用尺寸 16px（托盘），此时仅保留玻璃岛 + 律动条，辉光自然弱化；
- 保留四周 ≥ 8% 的安全边距；
- 禁止改变律动条顺序（低→高从左到右，与频谱一致）；
- 单色场景：使用玻璃岛轮廓 + 单色律动条。

macOS 侧：

- 最小使用尺寸 16px（此时只保留胶囊 + 仪表环 + 三条色块，无高光）；
- 四周保留 ≥ 10% 透明边距（macOS 规范）；
- 禁止改变读数条顺序（CPU / 内存 / 网速，与面板一致）；
- 单色场景：胶囊轮廓 + 单色仪表环。
