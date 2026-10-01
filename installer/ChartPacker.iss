; Inno Setup (7 or later) script for the Windows installer. The Release workflow (.github/workflows/release.yml)
; passes the version and the publish folder; built by hand, it installs what build/publish.ps1 left in
; publish/win-x64:
;
;   pwsh build/publish.ps1
;   iscc installer/ChartPacker.iss

#ifndef AppVersion
  #define AppVersion "0.0.0-local"
#endif
; The Windows file version takes numbers only.
#ifndef AppNumericVersion
  #define AppNumericVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif

#define AppName "Chart Packer"
#define AppExe "ChartPacker.exe"
#define AppIcon "ChartPacker.ico"
#define AppUrl "https://github.com/jcub1011/Chart-Packer"

[Setup]
; Identifies the app to Windows across versions, so a new installer upgrades the old install. Never change it.
AppId={{7520BBDA-1A8D-4AC1-BB44-BCE64D776C83}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppNumericVersion}
AppPublisher=Jacob McCormack
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
; Installs for the current user without asking for admin rights; the wizard offers all users too.
; Settings live in LocalAppData, so nothing needs to write to the install folder.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=ChartPacker-{#AppVersion}-win-x64-setup
SetupIconFile=..\ChartPacker\Assets\avalonia-logo.ico
UninstallDisplayIcon={app}\{#AppIcon}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
; The executable has no icon of its own, so the shortcuts use this one.
Source: "..\ChartPacker\Assets\avalonia-logo.ico"; DestDir: "{app}"; DestName: "{#AppIcon}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppIcon}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppIcon}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
