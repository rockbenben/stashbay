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
; 默认输出文件名（release.ps1 会通过 ISCC /F 显式指定 StashBay-v<version>-setup 保持 'v' 前缀统一）
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
; SQLite 已内嵌，只安装 EXE。
Source: "..\..\artifacts\publish-x64-needs-dotnet10\{#AppExeName}"; DestDir: "{app}-needs-dotnet10"; Flags: ignoreversion

[InstallDelete]
; 清理旧版安装清单拥有的 SQLite DLL，保留目录中的其他用户文件。
Type: files; Name: "{app}-needs-dotnet10\e_sqlite3.dll"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} (requires .NET 10)"; Filename: "{app}-needs-dotnet10\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

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

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  StartupExitCode: Integer;
  StartupApplied: Boolean;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('startwithwindows') then begin
    StartupApplied := Exec(ExpandConstant('{app}\{#AppExeName}'), '--install-startup', '',
      SW_HIDE, ewWaitUntilTerminated, StartupExitCode);
    if StartupApplied then
      StartupApplied := StartupExitCode = 0;
    if not StartupApplied then begin
      Log('Windows startup registration failed; existing administrator startup was not intentionally replaced.');
      if not WizardSilent then
        MsgBox('Windows startup could not be configured. Open StashBay settings, refresh startup status and repair it. See the application logs for details.', mbError, MB_OK);
    end;
  end;
end;

function InitializeUninstall(): Boolean;
var
  ExitCode: Integer;
begin
  Result := Exec(ExpandConstant('{app}\{#AppExeName}'), '--unregister-shell', '',
    SW_HIDE, ewWaitUntilTerminated, ExitCode);
  if Result then
    Result := ExitCode = 0;
  if not Result then begin
    Log('Startup cleanup failed or was cancelled. Uninstall stopped before deleting files.');
    if not UninstallSilent then
      MsgBox('Startup cleanup could not be completed. Approve UAC using the same administrator account, or repair startup in StashBay settings and try uninstalling again. No application files have been removed.', mbError, MB_OK);
  end;
end;
