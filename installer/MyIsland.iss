; MyIsland 安装包脚本（Inno Setup 7，当前用户安装免管理员）
#define AppName "MyIsland"
#define AppVersion "1.0.1"
#define AppPublisher "YQ"
#define AppExe "Island.App.exe"
#define SrcDir "D:\YQDESK\MyIsland-src\MyIsland\publish\win-x64"
#define OutDir "D:\YQDESK\MyIsland-src\installer\dist"

[Setup]
AppId={{7E4A2C1D-9F3B-4E8A-B5D2-1A6C8E0F2B44}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\MyIsland
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
OutputDir={#OutDir}
OutputBaseFilename=MyIsland-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; 运行中的实例自动关闭（互斥体识别）
AppMutex=Local\MyIsland.Instance
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=D:\YQDESK\MyIsland-src\MyIsland\src\Island.App\app.ico

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

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
