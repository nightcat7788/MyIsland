# driver/ — 温度驱动（PawnIO）的安装器

`PawnIO_setup.exe` 是 PawnIO 的官方安装器，**原样**放在这里，一个字节都没有改。
MyIsland 把它内嵌进程序（`src/Island.App/Island.App.csproj` 里的 `EmbeddedResource`），
以管理员运行、且机器上的 PawnIO 缺失或低于 2.0.0.0 时，由 `src/Island.App/Services/PawnIoInstaller.cs`
把它释放到临时目录、校验 SHA-256、静默安装。用户不需要手工安装任何东西。

## 这个文件是什么

| 项 | 值 |
|---|---|
| 文件 | `PawnIO_setup.exe`，3410960 字节 |
| 版本 | 2.2.0（安装后注册表里的 `DisplayVersion` 为 `2.2.0.0`） |
| 来源 | <https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0> |
| SHA-256 | `1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032` |
| 签名 | Authenticode 状态 Valid；签名者 `E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU`；时间戳来自 Microsoft Public RSA Time Stamping Authority |

以上各项 2026-11-24 核对过：文件哈希与 GitHub release 元数据里的摘要一致；
签名证书与 LibreHardwareMonitor 自带安装器所用的是同一张。

## 安装器自带的用法说明

```
Usage: PawnIOSetup.exe [-install] [-uninstall] [-unrestricted] [-debuginfo] [-silent]
  -install       Install PawnIO
  -uninstall     Uninstall PawnIO
  -unrestricted  Install unrestricted edition
  -debuginfo     Install debug info
  -silent        Run in silent mode (no UI, even on error)
```

MyIsland 只用两组参数，写死在 `PawnIoInstaller.Arguments` / `PawnIoInstaller.UninstallArguments`：

- `-install -silent` —— 安装。`-silent` 让它连出错也不弹界面。
- `-uninstall -silent` —— 装着旧版时先卸载：安装器拒绝覆盖安装
  （"A previous installation of PawnIO was found. Please uninstall it first before installing again."）。

**不带 `-unrestricted`，也不带 `-debuginfo`**。前者装的是 unrestricted edition —— 官网 pawnio.eu 对它的说明是
"An unsigned version without signature checking"，也就是不带签名的驱动，开着内存完整性（HVCI）的机器上加载不了；
后者额外安装调试信息。两者都不是默认安装的一部分，不是 MyIsland 该替用户做的选择。
测试 `PawnIoInstallerTests` 把这两个参数常量钉住了。

## 许可与再分发

- PawnIO 以 GPL-2.0-or-later 发布，许可证全文在同目录的 `COPYING-PawnIO.txt`；
  它附带"链接例外"：只通过设备 IO-control 接口使用它的独立程序不受 GPL 约束。
  MyIsland 正是这种情况 —— 只通过 LibreHardwareMonitorLib 间接使用 PawnIO。
- 安装器原样分发，随附许可证文本（构建后位于 `licenses\PawnIO-COPYING.txt`，见 `DISTRIBUTION.md`）。
  安装器自己的说明里也写着："This installer can be redistributed unmodified."

以上是事实说明，不构成法律意见。

## 驱动装到哪里、怎么卸载

- 安装位置 `C:\Program Files\PawnIO`，卸载项登记在
  `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO`。
- 驱动是系统级的共享组件，LibreHardwareMonitor、FanControl 等基于它的软件也在用，所以**卸载 MyIsland 不会卸载它**。
  需要移除时：设置 → 应用 → PawnIO → 卸载，或运行 `"C:\Program Files\PawnIO\uninstall.exe" -uninstall`。

## 怎么升级

1. 从官方 release 页面下载新版 `PawnIO_setup.exe`，**不要修改、不要重新打包**，覆盖本目录的同名文件。
2. 核对：`Get-AuthenticodeSignature` 状态为 Valid、签名者仍是 namazso.eu；`Get-FileHash -Algorithm SHA256`
   与 release 元数据里的摘要一致。
3. 把新哈希写进 `PawnIoInstaller.ExpectedSha256`（`src/Island.App/Services/PawnIoInstaller.cs`）。
   如需同时抬高"旧到要重装"的门槛，再改 `PawnIoInstaller.MinimumVersion`。
4. 更新上面表格里的版本与哈希。
5. 跑一遍测试（`dev.ps1` 的第 2 步，或 `dotnet run --project tests\Island.Tests\Island.Tests.csproj -c Release`）：
   `PawnIO：仓库里的安装器与 ExpectedSha256 完全一致` 这条必须保持通过。

哈希对不上的安装器永远不会被运行：释放后、启动前各校验一次。所以忘了第 3 步的后果是"装不上"（界面显示"缺驱动"，
`run.log` 里有一行 `pawnio: 失败：InvalidOperationException：SHA-256 不符…`），而不是跑了一个来历不明的文件。
