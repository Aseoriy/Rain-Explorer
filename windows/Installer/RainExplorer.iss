; Inno Setup script for Rain Explorer (self-contained build).
; Builds a per-user (no-admin) installer with Start-menu shortcut and uninstaller.
; The .NET 10 runtime is bundled with the app, so no separate runtime is required.

#define MyAppName "Rain Explorer"
#define MyAppVersion "2.0.0-beta-1"
#define MyAppNumericVersion "2.0.0.0"
#define MyAppPublisher "Aseoriy"
#define MyAppExeName "RainExplorer.exe"
#ifndef SourceDir
  #define SourceDir "E:\Downloads\Rain\Code stuff\File Explorer\dist\app-sc"
#endif
#ifndef IconFile
  #define IconFile "E:\Downloads\Rain\Code stuff\File Explorer\Main\Assets\rain.ico"
#endif

[Setup]
AppId={{8A9C2F31-4D6B-4A1E-9E2C-7F3B5D8A1C42}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppNumericVersion}
VersionInfoProductTextVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=E:\Downloads\Rain\Code stuff\File Explorer\dist
OutputBaseFilename=RainExplorer-Setup-{#MyAppVersion}
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Messages]
FinishedLabel=Setup has finished installing [name] on your computer.%n%nOpen Rain Explorer from the Start menu or desktop shortcut.

; Do not launch the file manager from Setup's process tree. On Windows build
; 26200.9168, Inno Setup 6.7.3's RedirectionGuard propagates to the launched app
; and its terminals, including with runasoriginaluser. Keep Setup's protection
; enabled and let the user's normal Windows shell start the application.

; Self-contained build — the .NET runtime ships inside {app}, so there is no
; runtime prerequisite check here.
