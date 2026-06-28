#define AppName "FastTaskMgr"
#ifndef AppVersion
#define AppVersion "0.0.0.0"
#endif
#ifndef SourceDir
#define SourceDir "..\src\FastTaskMgr\obj\script-publish"
#endif
#ifndef OutputDir
#define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{78D4C498-F7C1-4BB0-80B2-AF983FDDA962}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir={#OutputDir}
OutputBaseFilename=FastTaskMgr-Setup
SetupIconFile=..\src\FastTaskMgr\Assets\FastTaskMgr.ico
UninstallDisplayIcon={app}\FastTaskMgr.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
CloseApplications=yes
CloseApplicationsFilter=FastTaskMgr.exe
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppName}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\FastTaskMgr.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\FastTaskMgr.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\FastTaskMgr.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\FastTaskMgr.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\FastTaskMgr.exe"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\FastTaskMgr.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\FastTaskMgr.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
