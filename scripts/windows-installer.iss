; T-256 - Windows installer (Inno Setup) over the release pipeline's win-x64 publish.
;
; Input: the win-x64 self-contained publish output - the same tree the
; release.yml win-x64 leg zips - compiled by ISCC with three defines:
;
;   /DAppVersion=<x.y.z>     parsed from Directory.Build.props (the release
;                            pipeline's single version source; release.yml
;                            passes it at workflow level already)
;   /DPublishDir=<path>      the publish output the setup installs
;   /DInstallerOutDir=<path> where the setup exe is written (default: the
;                            script's own directory)
;
; Install modes (PrivilegesRequiredOverridesAllowed): per-user is the
; default - a plain double-click never triggers UAC and installs into
; %LOCALAPPDATA%\Programs\ZettaTrade; an elevated run (or the /ALLUSERS
; command-line switch) offers the per-machine mode into Program Files.
; The {autopf} constant resolves per install mode (verified against the
; Inno Setup 6.7 compiler source: the 'auto' prefix expands to user/common
; via IsAdminInstallMode; both Inno Setup 6.7+ and 7.x support it).
;
; User data NEVER dies with the app: the profile lives OUTSIDE the install
; directory - %APPDATA%\ZettaTrade (settings, themes, MCP config) and
; %LOCALAPPDATA%\ZettaTrade (databases, logs, the DPAPI-protected secret
; store) - and this script deliberately carries no [UninstallDelete], no
; [InstallDelete] and no [UninstallRun] sections and never references the
; profile paths. The uninstaller removes only what [Files] put into {app}
; plus the shortcuts [Icons] created; uninstalling keeps the profile and a
; reinstall finds it again. scripts/windows-installer-smoke.ps1 pins these
; invariants statically; the release.yml Windows leg compiles this script
; in CI (a clean-environment install/launch/uninstall ride of the compiled
; setup is the release acceptance, not this machine).
;
; Signing: the build is unsigned (the release pipeline's hash-only
; posture; the Authenticode certificate is a separate acquisition decision
; - docs/SECURITY.md carries the SmartScreen caveat for the setup exe and
; the signing-flip checklist that will cover it).
;
; Keep this file ASCII-only and BOM-less: it must read identically in the
; ISCC preprocessor and in the PS 5.1 lint smoke (which reads BOM-less
; files as ANSI).

#define AppName "ZettaTrade"
#define AppExe "ZettaTrade.App.exe"
#define AppPublisher "ZettaTrade Contributors"
#define AppURL "https://github.com/zettatrade/zettatrade"

; The installation identity: stable across versions, never regenerated
; (a changed AppId would orphan the uninstall registration of every
; previously installed build). The leading double brace is the Inno
; literal-brace escape; the registered value is the single-brace GUID.

#ifndef AppVersion
  #error "Pass /DAppVersion=<x.y.z> - release.yml parses it from Directory.Build.props."
#endif
#ifndef PublishDir
  #error "Pass /DPublishDir=<path to the win-x64 publish output>."
#endif
#ifndef InstallerOutDir
  #define InstallerOutDir "."
#endif

[Setup]
AppId={{DA01BD82-1523-43A8-8F39-2491CAE4A901}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
LicenseFile=..\LICENSE
SetupIconFile=..\src\ZettaTrade.App\Assets\ZettaTrade.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
OutputDir={#InstallerOutDir}
OutputBaseFilename=zettatrade-{#AppVersion}-win-x64-setup
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Windows installer

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; runasoriginaluser: the app must never launch with the setup's elevation
; (a per-machine install runs elevated; the terminal itself is per-user).
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
