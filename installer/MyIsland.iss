; MyIsland Inno Setup 安装包脚本
; ─────────────────────────────────────────────────────────────────
; 用法（Inno Setup 7）：
;   ISCC.exe installer\MyIsland.iss
; 产物：installer\dist\MyIsland-Setup-<版本>.exe（当前用户安装，免管理员）
;
; ⚠️ 安全红线（2026-11-23 事故教训，别删）：
;   1. [Files] 的 Excludes 必须排除 inpout*.dll —— 它一被加载就会自动安装
;      未签名的 inpoutx64.sys 内核驱动并映射 PCIe 物理内存，当天三次蓝屏 0x1EA。
;      源码树 tools\inpout\ 里的 dll 只供本机调试，绝不允许进任何分发物。
;   2. 同理排除 *.pdb（调试符号，用户机器不需要）与 LhmProbe*（探针工具）。
; ─────────────────────────────────────────────────────────────────

#define MyAppName "MyIsland"
#define MyAppVersion "1.1.2"
#define MyAppPublisher "MyIsland"
#define MyAppExeName "Island.App.exe"

[Setup]
AppId={{7B1E6C4A-52D8-4E7F-9A63-C5E09FB4D210}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
; 当前用户安装：不写 Program Files、不需要管理员（UAC 弹窗也不出）
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=MyIsland-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
; 运行中的实例自动关闭（与 App.xaml.cs MutexName 一致，升级时关掉旧实例）
AppMutex=Local\MyIsland.Instance
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
; 卸载时把设置/日志留在原处（用户数据不随卸载销毁；如需彻底清理可手动删 %LOCALAPPDATA%\MyIsland）
Uninstallable=yes

[Files]
; 发布目录整体拷入；排除危险与调试文件（见文件头安全红线）
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "inpout*.dll,*.pdb,LhmProbe*.exe,LhmProbe*.dll"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加任务："

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载时清掉应用在设置中心写入的开机自启项，避免留下指向已删路径的失效 Run 键
Filename: "reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v MyIsland /f"; Flags: runhidden; RunOnceId: "RemoveMyIslandRun"

[UninstallDelete]
; 只清程序自身生成的空目录结构，不碰用户设置
Type: filesandordirs; Name: "{app}"
