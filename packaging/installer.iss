#ifndef MyAppVersion
  #define MyAppVersion "0.3.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\package\InterviewScribe"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif
#ifndef AppExeName
  #define AppExeName "InterviewScribe.App.exe"
#endif

#define MyAppName "InterviewScribe 面试转写助手"
#define MyAppPublisher "InterviewScribe Contributors"

[Setup]
AppId={{8D659C29-65C6-4F17-956D-E082B92D911A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\InterviewScribe
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=InterviewScribe-Setup-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
DesktopShortcutTask=在桌面创建快捷方式（推荐）
DesktopShortcutGroup=快捷方式：

[Tasks]
; 首次安装默认勾选，用户仍可在安装向导中取消。静默安装可用 /TASKS=desktopicon 显式启用。
Name: "desktopicon"; Description: "{cm:DesktopShortcutTask}"; GroupDescription: "{cm:DesktopShortcutGroup}"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
