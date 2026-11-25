# MyIsland 发布与签名说明

## 一键发布

```powershell
powershell -ExecutionPolicy Bypass -File release.ps1
```

流程：测试 → publish 到 `publish\release` → 签主 exe → ISCC 编译并签名安装包
→ 打便携 zip → 生成 `SHA256SUMS.txt` → 签名门禁自检（不过则退出码非 0）。

版本号统一读仓库根的 `Directory.Build.props`，再以 `/DAppVersion=<版本>` 注入安装包脚本；
要临时出别的版本用 `release.ps1 -Version 1.2.3`。

产物落在仓库根的 `dist\`：

| 产物 | 签名 | 说明 |
|---|---|---|
| `MyIsland-Setup-<版本>.exe` | ✅ 可签名 | Inno 安装包（当前用户安装，免管理员）；**没装 Inno Setup 时不产出**，见下节 |
| `MyIsland-<版本>-portable.zip` | ❌ 格式不支持 | 便携版；里面是**已签名**的 Island.App.exe |
| `SHA256SUMS.txt` | — | 每个产物的 SHA256 + 签名状态 + 签名者 + 时间戳 |

## 没装 Inno Setup 时：只出便携包

流水线里只有**安装包**这一步依赖 Inno Setup 7（`ISCC.exe`）；测试 → publish → 签名 →
便携 zip → 校验清单 → 门禁**完全不依赖**它。所以没装 Inno Setup 的机器照样能产出
**已签名的便携 zip + `SHA256SUMS.txt`**：

```powershell
# 显式跳过安装包（意图清楚，推荐）
powershell -ExecutionPolicy Bypass -File release.ps1 -SkipInstaller -OutDir D:\out

# 不加参数也行：找不到 ISCC.exe 时脚本自动降级为"只出便携包"，
# 并在日志与脚本结尾明确写出「本次发布没有安装包」，不会假装安装包出了
powershell -ExecutionPolicy Bypass -File release.ps1 -OutDir D:\out
```

反过来，**要求必须有安装包**时（CI 里防止静默少产物）：

```powershell
powershell -ExecutionPolicy Bypass -File release.ps1 -RequireInstaller
# 找不到 ISCC.exe → 直接失败，退出码非 0（不会把失败吞成降级）
```

工具链相关的开关：

| 参数 | 作用 |
|---|---|
| `-Dotnet <路径>` | 指定带 SDK 的 dotnet。不给就依次找：`$env:DOTNET_ROOT` → PATH 上的 `dotnet` → 仓库及上级的 `.dotnet\` 目录 → 旧的两个默认路径。每个候选都要用 `--list-sdks` 确认**真的有 SDK**（很多机器的系统 dotnet 只有运行时、没有 SDK） |
| `-Iscc <路径>` | 指定 `ISCC.exe`（装在非默认位置时用） |
| `-SkipInstaller` | 跳过安装包编译，只出便携包 |
| `-RequireInstaller` | 必须出安装包；缺 ISCC 时直接失败（退出码非 0） |

降级时 `SHA256SUMS.txt` 里**只有便携 zip 与主 exe**，门禁也只对**实际存在**的产物判定
（exe/dll 必须 `Status=Valid` 且带 RFC3161 时间戳，zip 不参与签名判定）；
清单文件里也会写明「本次发布没有安装包」。

## 签名工具链

- `signtool.exe`：Windows SDK 10.0.26100（`winget install Microsoft.WindowsSDK.10.0.26100 -e`）
- `ISCC.exe`：Inno Setup 7（`%LOCALAPPDATA%\Programs\Inno Setup 7\`）
- 时间戳：RFC3161 公共 TSA `http://timestamp.digicert.com`（签名不会因证书到期而失效）

## 证书来源（三选一，优先级从高到低）

```powershell
# 1) PFX 文件（密码只从环境变量读，绝不进命令行/日志）
$env:MYISLAND_SIGN_PWD = '...'
release.ps1 -CertPfx D:\keys\myisland.pfx

# 2) 系统证书库里的证书（公有 CA 证书 / 云签名都走这条）
release.ps1 -CertThumbprint ABCDEF0123456789...

# 3) 默认：自签 CN=MyIsland（当前阶段用）
release.ps1
```

**换成公有 CA 证书时，流水线一行都不用改**，只需改上面的参数。

## 当前阶段用的是自签证书 —— 这意味着什么

自签证书只让**信任它的那台机器**显示"已签名"。普通用户下载安装包时依然会看到
→「Windows 已保护你的电脑 / 未知发布者」—— 那是信任链问题，只有公有 CA（OV/EV）签发的
代码签名证书才有公信力。

要真正面向普通用户分发，需要：

1. 采购公有 CA 的 **OV 或 EV 代码签名证书**（DigiCert / Sectigo / GlobalSign / SSL.com；
   国内腾讯云/阿里云也有代售）。需企业或个体工商户营业执照 + 法人电话验证，1~3 个工作日。
2. 导出 PFX 或把证书装进本机证书库 → 用上面的 `-CertPfx` / `-CertThumbprint` 重新跑流水线。
3. 即使签了，新发布者仍可能被 SmartScreen 拦（需要下载量积累信誉，或上 EV）。
   这是运营问题，不是签名问题。

## 密钥管理（硬约束）

- PFX **只放仓库外**：`%LOCALAPPDATA%\MyIsland\signing\`
- 密码**只走环境变量** `MYISLAND_SIGN_PWD`，不写进脚本/命令行/日志
- `.gitignore` 已排除 `*.pfx` / `*.p12` / `*.cer` / `signing/`
- 自签证书会自动加入本机 `CurrentUser\Root`（否则本机校验只会是 UnknownError，
  链校验要求根受信任）

## 第三方软件声明：PawnIO 安装器（2026-11-24）

- **带了什么**：PawnIO 的官方安装器 `PawnIO_setup.exe` 2.2.0（3410960 字节，原样、未修改），
  内嵌在 Island.App 程序集里（当前发布布局下是 `Island.App.dll`，`Island.App.exe` 是它的启动器），
  程序集因此大了约 3.4 MB（未压缩）。来源 <https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0>，
  SHA-256 `1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032`。
  来源、签名、许可与升级步骤见 `driver/README.md`。
- **许可证文件 `licenses\PawnIO-COPYING.txt`**（GPL-2.0 全文）随构建输出走：
  `installer\MyIsland.iss` 的 `[Files]` 是 `recursesubdirs` 整目录拷贝，`Excludes` 里没有它；
  便携 zip 是 `ZipFile.CreateFromDirectory` 整目录打包。所以安装包与便携版里都会带上它，不需要另外登记。
- **运行时做什么**：以管理员运行、且机器上的 PawnIO 缺失或低于 2.0.0.0 时，程序把安装器释放到
  `%TEMP%\MyIsland\PawnIO_setup.exe`，校验 SHA-256 后以 `-install -silent` 静默安装，装完删除临时文件
  （装着旧版时先 `-uninstall -silent`）。非管理员运行时什么都不做。结果写一行 `pawnio: …`
  到 `%LOCALAPPDATA%\MyIsland\run.log`。
- **卸载 MyIsland 不会卸载 PawnIO 驱动**：它是系统级的共享组件，其它软件（LibreHardwareMonitor、
  FanControl 等）可能也在用。卸载 MyIsland 之后它留在 `C:\Program Files\PawnIO`，
  需要时从"设置 → 应用"里单独卸载。
- 以上是事实说明，不构成法律意见。

## 单独签名某个文件

```powershell
powershell -ExecutionPolicy Bypass -File sign.ps1 -Target <文件|目录>
powershell -ExecutionPolicy Bypass -File sign.ps1 -Target <文件> -VerifyOnly
```

## 踩过的坑（改脚本前先看）

1. **Inno 7 的 `SignTool=` 只认"命名工具"**：`[Setup]` 里写 `SignTool =myisland`，
   完整命令要用 ISCC 的 `--signtool=myisland=<命令>` 定义（名字里不能有空格/等号）。
   直接在 `SignTool=` 后面写命令行会报 *"Value of [Setup] section directive "SignTool" is invalid"*。
2. **`signtool sign` 没有 `/nologo`**（那是 `verify` 的选项）。
3. **发布到独立暂存目录**：正在运行的实例锁着 `publish\win-x64\Island.App.exe`，
   signtool 会报 *"The file is being used by another process"*；而提权实例非提权脚本杀不掉。
   流水线因此 publish 到 `publish\release`，与运行实例彻底解耦，**不需要 UAC**。
4. **`powershell -File` 传空字符串参数会被丢弃** → 子进程报 *"Missing an argument"*。
   可选参数必须"非空才传"。
5. **所有 .ps1 必须是 UTF-8 with BOM**：Windows PowerShell 5.1 按 ANSI 读无 BOM 文件，
   中文串乱码会吃掉引号变成语法错误，而且**没有任何报错输出**（进程秒退）。
6. **"有 dotnet ≠ 有 SDK"**：`C:\Program Files\dotnet\dotnet.exe` 常常只装了运行时，
   `dotnet --list-sdks` 返回空输出。发布脚本因此对每个候选都跑一次 `--list-sdks`，
   空输出即视为不可用（只判文件存在会选到一个装不了 SDK 的 dotnet）。
7. **`start-admin.ps1` 里凡是 `$变量` 后面接反斜杠的路径**（如 `"$root\publish\win-x64"`）
   都必须是**真反斜杠**。这个文件历史上被模板处理吃掉过反斜杠（`\r` 被当成换行、其余 `\x` 被吞掉），
   结果 `"$env:LOCALAPPDATAMyIsland\nun.log"` 这类路径全部失效、脚本静默失败。
   改这个文件后请逐条核对插值路径里有没有反斜杠。
