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
; Chart Builder, the chart editor, is a second app in the same install (docs/charter-spec-v1.md §4.8). It's an
; optional component, selected by default.
#define BuilderName "Chart Builder"
#define BuilderExe "ChartBuilder.exe"
#define AppUrl "https://github.com/jcub1011/Chart-Packer"

; The .sng file type. Its icon is a disc (installer/SngFile.ico, made by tools/IconGen); Explorer shows a song's
; album art instead through the thumbnail handler, ChartPacker.Thumbnails.dll, an in-process COM server.
#define SngProgId "ChartPacker.Sng"
#define SngIcon "SngFile.ico"
#define ThumbnailDll "ChartPacker.Thumbnails.dll"
; SngThumbnailProvider.Clsid in ChartPacker.Thumbnails. Never change it.
#define ThumbnailClsid "{{227CBBE3-F529-4DBC-969C-AD8C87203253}"
; IThumbnailProvider's interface ID: the ShellEx key Explorer looks a type's thumbnail handler up under.
#define ThumbnailProviderIid "{{E357FCCD-A995-4576-B01F-234630154E96}"
; The folder context action that opens a song folder in Chart Builder.
#define FolderVerb "ChartBuilder.Open"

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
SetupIconFile=..\ChartPacker\Assets\ChartPacker.ico
UninstallDisplayIcon={app}\{#AppExe}
; Tells Explorer the .sng type changed, so its icon, thumbnails and Open command take effect without a sign-out.
ChangesAssociations=yes
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Chart Packer and Chart Builder"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "main"; Description: "{#AppName}"; Types: full custom; Flags: fixed
Name: "builder"; Description: "{#BuilderName} (chart editor)"; Types: full custom

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon} ({#AppName})"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "builderdesktopicon"; Description: "{cm:CreateDesktopIcon} ({#BuilderName})"; GroupDescription: "{cm:AdditionalIcons}"; Components: builder; Flags: unchecked

[InstallDelete]
; Leaving Chart Builder out of an existing install removes it.
Type: files; Name: "{app}\{#BuilderExe}"; Components: not builder
; Setup doesn't remove a deselected component's shortcuts either.
Type: files; Name: "{group}\{#BuilderName}.lnk"; Components: not builder
Type: files; Name: "{autodesktop}\{#BuilderName}.lnk"; Components: not builder
; Versions before 0.4.0 installed an icon file for the shortcuts; the executables carry their own now.
Type: files; Name: "{app}\ChartPacker.ico"

[Files]
; Everything but Chart Builder and the thumbnail handler, which the apps' shared native libraries come with.
Source: "{#SourceDir}\*"; Excludes: "\{#BuilderExe},\{#ThumbnailDll}"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "{#SourceDir}\{#BuilderExe}"; DestDir: "{app}"; Components: builder; Flags: ignoreversion
; Explorer's thumbnail host keeps the handler loaded for good; setup ends it first (see [Code]). Should the file
; still be in use, an all-users install replaces or removes it at the next restart instead.
Source: "{#SourceDir}\{#ThumbnailDll}"; DestDir: "{app}"; Flags: ignoreversion restartreplace uninsrestartdelete
Source: "{#SngIcon}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{#BuilderName}"; Filename: "{app}\{#BuilderExe}"; Components: builder
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{autodesktop}\{#BuilderName}"; Filename: "{app}\{#BuilderExe}"; Tasks: builderdesktopicon

[Registry]
; HKA is the current user's classes for a per-user install, and the machine's for an all-users one.

; The .sng type, with its disc icon. Opening one opens it in Chart Builder. Uninstall removes the type's default
; only while it's still ours (see [Code]), so it doesn't take another app's association with it.
Root: HKA; Subkey: "Software\Classes\.sng"; ValueType: string; ValueName: ""; ValueData: "{#SngProgId}"
Root: HKA; Subkey: "Software\Classes\{#SngProgId}"; ValueType: string; ValueName: ""; ValueData: "SNG song"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#SngProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#SngIcon}"
Root: HKA; Subkey: "Software\Classes\{#SngProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#BuilderExe}"" ""%1"""; Components: builder
Root: HKA; Subkey: "Software\Classes\.sng\OpenWithProgids"; ValueType: string; ValueName: "{#SngProgId}"; ValueData: ""; Components: builder; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\{#BuilderExe}\SupportedTypes"; ValueType: string; ValueName: ".sng"; ValueData: ""; Components: builder; Flags: uninsdeletekey

; The thumbnail handler. Explorer runs it in a separate host process, and shows the disc when it finds no art.
Root: HKA; Subkey: "Software\Classes\CLSID\{#ThumbnailClsid}"; ValueType: string; ValueName: ""; ValueData: "{#AppName} .sng thumbnails"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\CLSID\{#ThumbnailClsid}\InprocServer32"; ValueType: string; ValueName: ""; ValueData: "{app}\{#ThumbnailDll}"
Root: HKA; Subkey: "Software\Classes\CLSID\{#ThumbnailClsid}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"
Root: HKA; Subkey: "Software\Classes\.sng\ShellEx\{#ThumbnailProviderIid}"; ValueType: string; ValueName: ""; ValueData: "{#ThumbnailClsid}"; Flags: uninsdeletekey

; "Open with Chart Builder" on a folder, and on the empty space inside one (%V is then the folder shown).
Root: HKA; Subkey: "Software\Classes\Directory\shell\{#FolderVerb}"; ValueType: string; ValueName: ""; ValueData: "Open with {#BuilderName}"; Components: builder; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Directory\shell\{#FolderVerb}"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#BuilderExe}"; Components: builder
Root: HKA; Subkey: "Software\Classes\Directory\shell\{#FolderVerb}\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#BuilderExe}"" ""%1"""; Components: builder
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\{#FolderVerb}"; ValueType: string; ValueName: ""; ValueData: "Open with {#BuilderName}"; Components: builder; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\{#FolderVerb}"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#BuilderExe}"; Components: builder
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\{#FolderVerb}\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#BuilderExe}"" ""%V"""; Components: builder

; Setup only removes a component's entries on uninstall, so leaving Chart Builder out of an existing install
; removes what it added here.
Root: HKA; Subkey: "Software\Classes\{#SngProgId}\shell"; ValueType: none; Components: not builder; Flags: deletekey
Root: HKA; Subkey: "Software\Classes\.sng\OpenWithProgids"; ValueType: none; ValueName: "{#SngProgId}"; Components: not builder; Flags: deletevalue
Root: HKA; Subkey: "Software\Classes\Applications\{#BuilderExe}"; ValueType: none; Components: not builder; Flags: deletekey
Root: HKA; Subkey: "Software\Classes\Directory\shell\{#FolderVerb}"; ValueType: none; Components: not builder; Flags: deletekey
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\{#FolderVerb}"; ValueType: none; Components: not builder; Flags: deletekey

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// Explorer runs the thumbnail handler in a COM surrogate (dllhost) that never lets go of it: a Native AOT DLL
// can't be unloaded. Ending the surrogates that have it loaded frees the file; Explorer starts another when it
// next needs a thumbnail. Only dllhost processes are matched, so Explorer and the apps are never touched.
procedure StopThumbnailHosts;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM dllhost.exe /FI "MODULES eq {#ThumbnailDll}"', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Before the Restart Manager check, so the surrogate isn't offered as an app to close.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopThumbnailHosts;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ProgId: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopThumbnailHosts;
    if RegQueryStringValue(HKA, 'Software\Classes\.sng', '', ProgId) and (ProgId = '{#SngProgId}') then
      RegDeleteValue(HKA, 'Software\Classes\.sng', '');
  end;
end;
