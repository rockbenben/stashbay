; StashBay 安装脚本（Inno Setup）
;
; 装到 %LOCALAPPDATA%\Programs\StashBay，不需要管理员权限。
; 卸载时要清掉右键菜单、"发送到"快捷方式与开机自启 —— 应用自己也会注销，
; 但用户可能直接删了安装目录，那一条就走不到了。

#define AppName "StashBay"
#define AppPublisher "StashBay"
#define AppExeName "StashBay.exe"

[Setup]
AppId={{6F3B9A21-7C64-4E2C-9E9B-2E4B1C7A5D31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename=StashBay-{#AppVersion}-setup
OutputDir=..\..\artifacts
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\..\src\FileStash.App\Resources\app.ico
; 不需要管理员
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
; Older uninstall logs contain recursive deletion of {app} and its sibling.
; Reinstall every owned file from [Files] and replace that unsafe legacy log.
UninstallLogMode=overwrite
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
; 文件名里带版本号，装一次升一次
; AppId 使用固定 GUID，升级时能被识别成同一个应用
SetupLogging=yes
ShowLanguageDialog=no

[Languages]
; 安装与卸载向导统一英文，与系统和应用语言无关。
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional tasks:"
Name: "startwithwindows"; Description: "Start with Windows"; GroupDescription: "Additional tasks:"; Flags: unchecked

[Files]
; 自包含版本（体积大、下载即用）
Source: "..\..\artifacts\publish-x64\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; 框架依赖版本（体积小、要求装 .NET 10）
; 还需要 e_sqlite3.dll：只安装 EXE 会导致数据库初始化失败。
Source: "..\..\artifacts\publish-x64-needs-dotnet10\*"; DestDir: "{app}-needs-dotnet10"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} (requires .NET 10)"; Filename: "{app}-needs-dotnet10\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; 开机自启（应用自己也会写一份，卸载时清掉）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: startwithwindows

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时清掉资源管理器右键菜单与"发送到"快捷方式
Type: dirifempty; Name: "{app}"
Type: dirifempty; Name: "{app}-needs-dotnet10"
Type: files; Name: "{userappdata}\Microsoft\Windows\SendTo\StashBay.lnk"
Type: files; Name: "{userappdata}\Microsoft\Windows\SendTo\FileStash.lnk"
Type: files; Name: "{userappdata}\Microsoft\Windows\SendTo\FileStash 暂存.lnk"
Type: files; Name: "{userappdata}\StashBay\settings.json"
Type: files; Name: "{userappdata}\FileStash\settings.json"

[UninstallRun]
; 先让应用自己注销一遍注册表项
Filename: "{app}\{#AppExeName}"; Parameters: "--unregister-shell"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterShell"
