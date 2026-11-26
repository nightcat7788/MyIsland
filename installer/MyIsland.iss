; MyIsland 安装包脚本（Inno Setup 7，当前用户安装免管理员）
#define AppName "MyIsland"
; 版本可由命令行注入：ISCC /DAppVersion=1.0.2 MyIsland.iss
#ifndef AppVersion
#define AppVersion "1.1.4"
#endif
; 发布者 = 证书 Subject 之外的展示名；用户端"发布者"那一行取自签名证书的 Subject
#define AppPublisher "MyIsland"
#define AppExe "Island.App.exe"
; 路径一律相对脚本自身，目录树整体搬迁/换机后仍可重新编译。
; ⚠️ 注意：不能写成 "{src}\..." —— Inno 的 #define 是预处理器常量，
;    {src} 在这里不会展开（实测编译报 "系统找不到指定的路径"，还会建出名为 "{src}" 的垃圾目录）。
;    相对路径在 [Setup]/[Files] 里按**脚本所在目录**解析，效果正是想要的。
;    需要指定别的输出位置时用命令行覆盖：ISCC /DSrcDir=... /DOutDir=... MyIsland.iss
#ifndef SrcDir
#define SrcDir "..\publish\win-x64"
#endif
#ifndef OutDir
#define OutDir dist
#endif
; ---- 签名相关（由命令行注入；不注入就不签名，普通开发构建不依赖 signtool）----
; MyIslandSignTool = 命名签名工具的名字（由 ISCC --signtool=myisland=... 定义）
#ifndef MyIslandSignTool
#define MyIslandSignTool ""
#endif

[Setup]
AppId={{7E4A2C1D-9F3B-4E8A-B5D2-1A6C8E0F2B44}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\MyIsland
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
OutputDir="{#OutDir}"
OutputBaseFilename=MyIsland-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; 运行中的实例自动关闭（互斥体识别）
; 必须与 Island.App 的 MutexName 完全一致（App.xaml.cs: Local\MyIsland.Instance），
; 否则升级安装时关不掉正在运行的旧实例
AppMutex=Local\MyIsland.Instance
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\Island.App\app.ico
; 安装包在编译期签名。
; ⚠️ Inno 7 的 SignTool= 必须引用一个"已命名"的签名工具：名字要在 ISCC 命令行
;    用 --signtool=<名字>=<完整命令> 定义（名字里不能有空格或等号）。
;    这里只写名字；实际命令由 release.ps1 通过 /DMyIslandSignTool=myisland 注入。
#ifdef MyIslandSignTool
SignTool ={#MyIslandSignTool}
#endif

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; 剔掉开发期探针与调试符号：它们不是运行所需，随包发给用户只是噪音
; （lhmprobe*.dll 是当年的硬件探测工具，inpoutx64.dll 一律不得进包 —— 它一被加载就会装未签名内核驱动）
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Excludes: "lhmprobe*.dll,lhmprobe*.runtimeconfig.json,Island.App.pdb,inpout*.dll,*.pfx,*.p12,cs\*,de\*,es\*,fr\*,it\*,ja\*,ko\*,pl\*,pt-BR\*,ru\*,tr\*,zh-Hant\*"

[Icons]
Name: "{group}\MyIsland"; Filename: "{app}\{#AppExe}"
Name: "{group}\卸载 MyIsland"; Filename: "{uninstallexe}"
Name: "{userdesktop}\MyIsland"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加："
Name: "autostart"; Description: "开机自动启动"; GroupDescription: "附加："; Flags: unchecked

[Registry]
; 选了"开机自动启动"才写 Run 键（应用内设置中心也可随时开关，二者同源）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "MyIsland"; \
  ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "启动 MyIsland"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载时清掉"应用自己在设置中心写的"开机自启项。
; 安装器写的那个值由 uninsdeletevalue 负责，但设置中心写的那次不受它管 ——
; 不清会留下指向已删路径的自启项，登录时静默失败。
Filename: "reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v MyIsland /f"; Flags: runhidden; RunOnceId: "RemoveMyIslandRun"
