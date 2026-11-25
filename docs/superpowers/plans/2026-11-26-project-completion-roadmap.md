# MyIsland 完善总开发计划（2026-11-26 起）

依据：这轮实测盘点出的 17 项缺口（外设电量 → CPU 温度 → 展开面板 UI → 采集口径，四轮实证）。
基准：**以 mac 版现有实现为准**（总纲 §1）；**不伪装**：读不到就是「未知」，不用 0 或默认值充数。

---

## 0. 现状快照

| 轮次 | 交付 | 提交 |
|---|---|---|
| 外设电量 | HID 访问层 + L1/L3/L4 分层探测 + 数据驱动协议表 | `d1820e2` + `11ad76b` |
| CPU 温度口径 | 分层匹配 + 排除 Distance to TjMax 等干扰项 | `a2bee97` |
| mac 对齐 | 共享层 HID 描述符解析器 + 两个原生探针 + 温度口径 | `dfaf20f` + `effebda` |
| 展开面板 | 批次 1–5（顶栏/监控页/设备页/剪贴板页/小形象） | `92041e2` + `6e8b31b` |
| 采集口径 | CPU 占用/频率/运行时长/网速对齐 LiteMonitor + 传感器规则化 | `d34247f` |
| 能力清单 | 共享层清单 + 公开能力文档 + 两端改为从清单取列表 + 28 条漂移断言 | `2bcb82a` + `3f75f04` |

---

## 阶段 A：能力清单与一致性守护（总纲阶段 4 + 5）

**动机**：这轮实测推翻了总纲里三条记录，且几份计划文档的复选框大面积漂移（工作做完框没勾）。
根因是「两端各写一份、靠人记」。解法：**清单是唯一真相源 + 漂移检查让门禁红**。

### A1 共享能力清单

- [x] `src/Island.Core/Capabilities/CapabilityManifest.cs`：收起视图 / 展开页签 / 数据源 / 小形象款式
      四组清单 + `Available` / `CollapsedViewsFor(macOS)` / `ExpandedTabsFor(macOS)` 查询。
- [x] 两端**从清单取列表**（不再各写一份）：Windows 收起视图 / 展开页签顺序 / 小形象下拉；mac 收起视图 / 展开页签。（`3f75f04`）
- [x] 漂移检查测试 `CapabilityManifestTests`：清单自身完整性 + 两端代码引用的就是清单里的值，共 28 条断言。（`3f75f04`）
      ↳ 门禁有效性已实测：把 Windows 侧改回硬编码列表，测试立刻变红。
- [x] 公开能力文档 `docs/capabilities.md`：对外公开的能力清单与两端差异。

### A2 文档可信度

- [x] 修正几份计划文档里**已完成却没勾**的复选框（外设电量 37 项、收起态 34 项、展开面板 23 项）。
- [x] 总纲 §3 矩阵改为「由能力清单生成」的说明，不再手写两份表格。

---

## 阶段 B：发布链闭环

**动机**：`installer/dist/` 只有 1.0.1 的旧安装包（当前 1.1.2），本机没装 Inno Setup ⇒ 正式安装包没出。

- [ ] B1 在装了 Inno Setup 的机器上跑 `iscc installer\MyIsland.iss` 出 1.1.2 安装包。
      ↳ **本机无 Inno Setup（ISCC.exe 不存在）**；但 `signtool.exe` **存在**（Windows SDK 10.0.26100.0）。
      ↳ `release.ps1` 原有实现第 0 步就无条件 `throw` 找不到 ISCC ⇒ 没有 Inno Setup 时连 publish/签名/便携 zip/校验清单都跑不了。
      ↳ 收口动作：给 `release.ps1` 加 `-SkipInstaller` 降级通道（**进行中**，见下方收口批次）。
- [x] B2 签名：`sign.ps1` 已有可插拔证书来源（PFX / 证书库 thumbprint / 默认自签 CN=MyIsland），
      签名 + RFC3161 时间戳 + 门禁**已端到端跑通并有实物证据**：
      `dist/MyIsland-Setup-1.0.6.exe` 与 `dist/Island.App.exe` 的 `Get-AuthenticodeSignature` = **Valid**（签名者 `CN=MyIsland`），
      `signtool verify /pa /v` 输出含 `The signature is timestamped`，`dist/SHA256SUMS.txt` 哈希与重算**逐位一致**。
      ↳ 遗留事实：默认证书是**自签**，对用户端 SmartScreen 无信誉价值；换公有 CA 证书只需改 `-CertPfx` / `-CertThumbprint`。
      ↳ 遗留事实：`publish/win-dist/MyIsland-1.1.2.exe`（当前版本）**未签名**（`Status=NotSigned`）。
- [ ] B3 `release.ps1` 端到端跑一次，确认安装包 / 便携 zip / SHA256SUMS 三样产物齐。
      ↳ 1.0.6 已在有 Inno Setup 的环境下跑通过（产物在 `dist/`）；**1.1.2 尚未重跑**。
      ↳ 收口动作：先跑「无安装包」通道产出 1.1.2 的已签名便携包 + 校验清单，安装包待有 Inno Setup 的环境补齐。**进行中**。
- [ ] B4 **SmartScreen 误报**：单文件自包含 exe 未签名时会被拦，需实测并写进 README。

---

## 阶段 C：mac 侧收尾

- [ ] C1 用户在 Mac 上编译两个原生探针（`bash tools/build-mac-probes.sh`）并核对三处：
      ① Feature 报告**第 0 字节是不是报告 ID**；② `thermal: cpu source` 是否指向 `PMU tdie`；
      ③ 设备页读不到电量时是否显示「未知」而不是 0%。
- [ ] C2 mac 设备页补 per-type 矢量图标（现在只有电池 / 圆盘两种）。
- [ ] C3 mac 收起视图补 `devices`（mac 已有设备电量数据，但收起态没有这一视图）。

---

## 阶段 D：能力扩展

- [ ] D1 **协议表扩充**：目前只有联想 Idea5003 一条内置条目；「填表加品牌」这条路没被真实用户验证过 ——
      至少再支持 2–3 个常见 2.4G 接收器（罗技 HID++、雷蛇、VGN 等），每条都要实机回包验证。
- [ ] D2 **硬件详情面板**：像 LiteMonitor 那样列出全部硬件传感器（现在只能看固定的几个）。
- [ ] D3 **SSD 温度**：Windows 侧因 0x1EA 蓝屏史一直关闭；需在别的机型上验证，或找不经 SMART 的读法。
- [ ] D4 收起态 dash 的元素形态对齐（mac 用图标盒，Windows 用文字标签）。

---

## 阶段 E：行为一致性收口（总纲阶段 3）

- [ ] E1 收起视图集定案：Windows 多出的 monitor/devices/clipboard/window ——
      要么补 mac 视图，要么明确归为平台专属（清单已能表达，等产品定）。
- [ ] E2 展开页监控的底部状态行统一（两端同结构：9px 一行）。
- [ ] E3 主题 token 口径：12 个同名 token 在每套主题下取值都不同 ——
      统一「语义名」还是统一「具体色值」？前者已由清单体现，后者要重做两套主题。

---

## 阶段 F：验证矩阵

**动机**：这四轮全部只在一台机器（Intel Core Ultra 5 225H）上验证过。

- [ ] F1 AMD 平台验证（SMN 直读通道、CCD 温度、Tctl/Tdie 取舍）。
- [ ] F2 Intel 桌面平台验证（无核显、无电池、主板 SuperIO 温度）。
- [ ] F3 核显 + 独显组合验证（GPU 选择口径、显存类型）。
- [ ] F4 其它 2.4G 接收器验证（协议表每条都要实机回包）。

---

## 收口批次（2026-11-26 第 5 轮）

**动机**：前四轮把「读数口径」和「UI 对齐」做完了，但留下三类收口缺口：
① **验证盲区** —— 本会话屏幕捕获全黑，展开面板/监控页/设备页只能确认「不崩」，看不到画面；
② **协议表是盲写** —— 填表加品牌这条路没有任何工具能回答「表本身合法吗、命中的哪一条」；
③ **发布链在本机断掉** —— `release.ps1` 第 0 步就因缺 ISCC 而退出，连便携包都出不来。

| 任务 | 交付 | 状态 |
|---|---|---|
| Task 1 | 离屏渲染视觉回归探针（`tools/visual-probe`）+ 渲染不变量断言进 `Island.Tests` | 进行中 |
| Task 2 | 协议表自检：共享层校验逻辑 + `hid-probe --selftest` 真机命中报告 | 进行中 |
| Task 8 | `release.ps1` 加 `-SkipInstaller` 降级 + dotnet 可配置发现 → 无 Inno Setup 也能出签名便携包 | 进行中 |

**本批次的门禁**（与通用门禁相同）：build 0 警告 0 错误、Island.Tests 全过、UTF-8 无 BOM + LF、
读不到的量一律「未知」（禁止 0 / 默认值 / 换算百分比充数）。

**本批次不碰的**：mac 侧除计划明确要求的 C2/C3 外零改动；合并两套 RenderKit 属重构不在本轮范围。

## 每阶段的通用门禁

1. `dotnet build Island.sln -c Release`：**0 警告 0 错误**
2. `Island.Tests`：**全过**，退出码 0
3. `src/Island.Avalonia/**` 的改动必须是**本阶段明确要求**的（跨端阶段默认零改动）
4. 新增/改动的文件：UTF-8 无 BOM、LF 行尾
5. 涉及读数的改动：**读不到就是「未知」**，不得出现 0% 之类的假值

## 待产品拍板的三点（2026-11-26 已征询）

1. 展开页监控是否恢复「功耗 / 主板温度 / SSD 温度 / 运行时长 / 今日流量」这一行（2×2 栅格放不下，收起态仍有）。
2. 收起视图集怎么收口（补 mac 视图 vs 明确归为平台专属）。
3. 是否允许在本机安装 Inno Setup（只本地编译安装包，不签名/不上传/不发布）。
