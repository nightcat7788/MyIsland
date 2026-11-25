# 阶段 2：Windows 展开面板对齐 mac 版 —— Implementation Plan

**Goal:** 把 Windows 展开面板的**顶栏与排版规格**对齐 mac 版现有实现，并把 mac 独有的 3 款小形象补到 Windows。

**规格：** `docs/superpowers/specs/2026-11-26-windows-expanded-align-mac-design.md`（下称「规格」）

## Global Constraints

- **基准 = mac 现有实现**（总纲 §1）；数值抄进 `src/Island.App/Controls/ExpandedPanelSpec.cs`，实现引用它。
- **配色身份不变**：保留 Windows 的「暗色科技」（一屏一主角 + Accent），不引入 mac 的按资源配色。
- **平台专属能力保留**：Windows 的齿轮 / 收起 / 文件列 / 控制页；mac 的屏幕页 / 菜单栏。
- **不合并两套 RenderKit**：只对齐调用点参数。
- **构建门禁**：`dotnet build Island.sln -c Release` **0 警告 0 错误**。
- **测试门禁**：`Island.Tests` 全过、退出码 0。
- **命令**：`D:\YQDESK\.dotnet\dotnet.exe`。
- **编码**：UTF-8 无 BOM、LF 行尾。
- **并发约束**：本计划的任务分给不同 agent 时**必须各自独占文件**，不许互相改对方的文件。

---

### Task 1: 共享规格类

**Files:** Create `src/Island.App/Controls/ExpandedPanelSpec.cs`

- [x] 把 mac 的顶栏/监控页/设备页数值抄成常量（顶栏、字号阶梯、进程榜、设备卡片、小形象款式表）
- [x] 纯计算、无 WPF 依赖 ⇒ 可链进测试工程

### Task 2: 顶栏（批次 1）

**Files:** Modify `src/Island.App/Controls/ExpandedPanel.xaml`、`.xaml.cs`

- [x] 页签改成「图标 14 + 文字 12」；激活态 Accent 20% 底 + 圆角 8；热区 7/5；页签间距 6
- [x] 时钟挪到**右侧**，12px Regular 比例字体（右留白 24）；不再居中、不再等宽加粗
- [x] 内容区内边距 18/14/18
- [x] 保留齿轮 / 收起 / 四个页签的功能与顺序

### Task 3: 监控页排版（批次 1）

**Files:** Modify `src/Island.App/Controls/MonitorPage.cs`

- [x] 字号阶梯收敛到 14/12/11/10/9（放弃 36px 主读数与 17px 行读数）
- [x] 卡片标题 12px SemiBold + 12×12 图标 + 标题下 1px 发丝线（内缩 15）
- [x] 分隔线：去掉区块之间的，改为只在卡片内标题下
- [x] 进度条 4px/圆角 2；走势线 1.4px、框高 14
- [x] 进程榜：标题「占用最高 · CPU/内存」12px SemiBold；行距 21；最多 5 行；图标 15；名称 11px 比例字体；数值 11px
- [x] 排序分段控件 96×20 圆角 6，选中段 Accent 实心
- [x] 底部温度状态提示字号对齐 9px（不新造「问题列表」机制）

### Task 4: 小形象补 3 款（批次 2）

**Files:** Modify `src/Island.App/Animation/PetFace.cs`；Modify `src/Island.App/Controls/SettingsWindow.cs`（下拉补 3 项）

- [x] alien：正圆眼（radFactor 0.5）+ 高光微眸 + 左右两根触角（摆动 + 顶端绿松石呼吸光）
- [x] frog：正圆眼 + 随视线移动的深色瞳孔 + 左上白色高光 + 平直横嘴（宽 13*s）
- [x] ghost：独立绘制分支 —— 实心半圆脑袋 + 3 段波浪底边 + 浮动 + 深墨色圆眼（6 种表情）+ 腮红 + 嘴
- [x] 设置下拉补 alien / ghost / frog
- [x] **不动既有 5 款**（公共前置项：呼吸/挤压/闲置扫视/laugh 表情/眼睑两块/猫 ω 嘴/机器人扫描线/像素刘海 —— 归后续批次）

### Task 5: 设备页卡片网格（批次 3）

**Files:** Modify `src/Island.App/Controls/DevicesPage.cs`

- [x] 布局改 132×150 卡片网格（去掉翻页器），百分比 26 单串、状态行 10px、空状态 11.5px 左上
- [x] 改用共享的 `RenderKit.Ring`（现在自写 `Arc()`，绕过了死区与轨道透明度口径）
- [x] 保留 Windows 专属：设备类型图标集、充电角标、档位文字

### Task 6: 验收与回填

- [x] 构建 0 警告 0 错误
- [x] `Island.Tests` 全过
- [x] `src/Island.Avalonia/**` 本阶段零改动
- [x] 总纲 §3/§4 按实测修正（收起视图集、主题 token 不同源、小形象两份独立实现）

---

## 实现回填（2026-11-26，批次 1–5 + 小形象公共项 全部完成）

| 批次 | 实际改动 | 文件 |
|---|---|---|
| 1 · 顶栏 | 页签改成「图标 14 + 文字 12」；激活态 Accent 20% 底 + 圆角 8；热区 7/5；页签间距 6；时钟挪到**右侧**、12px Regular 比例字体（右留白 24）；内容区内边距 18/14/18。保留齿轮 / 收起 / 四个页签的功能与顺序 | `ExpandedPanel.xaml`、`.xaml.cs` |
| 1 · 监控页 | 字号阶梯收敛到 14/12/11/10/9（**删除 36px CPU 主读数**）；卡片标题 12px SemiBold + 12×12 矢量图标 + 标题下 1px 发丝线（内缩 15）；去掉区块间分隔线；进度条 4px/圆角 2；走势 1.4px、框高 14；进程榜标题「占用最高 · CPU/内存」、行距 21、最多 5 行、图标 15、名称 11px 比例字体、数值 11px；分段控件 96×20 圆角 6、选中段 Accent 实心；温度三态提示 9px | `MonitorPage.cs` |
| 2 · 小形象 | 新增 **alien**（正圆眼 + 高光微眸 + 两根触角带绿松石呼吸光）、**ghost**（独立绘制分支：实心半圆脑袋 + 3 段波浪底边 + 浮动 + 深墨圆眼 6 种表情 + 腮红 + 嘴）、**frog**（正圆眼 + 随视线瞳孔 + 左上高光 + 平直宽嘴）；设置下拉补 3 项 | `Animation/PetFace.cs`、`SettingsWindow.cs` |

**共享规格类**：新增 `Controls/ExpandedPanelSpec.cs`（纯常量）+ `ExpandedPanelSpecTests`（13 条断言守住 mac 的数值）。

**顺带修掉的两处偏离**（agent 按我的任务表实现、但与 mac 源码不一致，以 mac 源码为准）：
ghost 眼睛 `erY` 的 wide 系数 0.15 → **0.2**（mac `PetFaceAv.cs:637`）；alien 触角外晕由径向渐变改回 **实色 BrushOf**（mac 口径）。

**顺带消掉的一处代码重复**：监控页原本自己抄了一份走势线绘制（因为共享 `RenderKit.DrawSpark` 笔宽写死 1.2），
已把共享实现**参数化**（`strokeWidth` 默认 1.2，既有调用行为不变）并让监控页改调共享实现。

**验证**：构建 **0 警告 0 错误**；`Island.Tests` **504 条全过**（491 + 13 条规格断言）。

**批次 3–5 + 小形象公共前置项（2026-11-26 补做，提交 `6e8b31b`）**：设备页 **132×150 卡片网格**（去掉翻页器）、
监控页 **2×2 卡片栅格**、剪贴板页重构（分类标签栏 + 头部 + 列表 + 自绘 3px 滚动条 + 自绘右键浮层）、
以及小形象公共前置项（眼睑两块 / 呼吸起伏 / 挤压回弹 / 上下弹跳 / 左右摆 / 闲置扫视 / 闲置眨眼 / laugh 表情 /
逐款补齐：mono 高光微眸、cat ω 嘴、robot 扫描线、pixel 刘海）。

**批次 3–5 的验证**（`6e8b31b`）：构建 **0 警告 0 错误**（clean rebuild）；`Island.Tests` **553 条全过**；
小形象 8 款 × 60 帧离屏渲染探针无异常；剪贴板页离屏探针逐项验证。

---

## 未完成项清单（2026-11-26 复核）

**本计划 Task 1–6 的条目全部完成，没有未勾项。** 以下是**仍待人定 / 跨批次**的事项：

- **展开页监控不再显示 功耗 / 主板温度 / SSD 温度 / 运行时长 / 今日流量**：2×2 栅格放不下
  （**收起态的 dash / combo 里仍然有**）。属**待产品拍板**，见 `docs/capabilities.md` §7。
- **小形象剩余的 mac 差异**：连眨（double blink）、pout / hot 嘴型、curious 景深系数。
- **mac 侧 2.4G 接收器原生探针**待实机验证（Windows 侧已在 `d1820e2` / `11ad76b` 完成）。
