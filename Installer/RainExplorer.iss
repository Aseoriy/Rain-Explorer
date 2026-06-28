; Inno Setup script for Rain Explorer (framework-dependent build).
; Builds a per-user (no-admin) installer with Start-menu shortcut, uninstaller,
; and a check for the required .NET 10 Desktop Runtime.

#define MyAppName "Rain Explorer"
#define MyAppVersion "1.0.0-PreRelease"
#define MyAppPublisher "Aseoriy"
#define MyAppExeName "RainExplorer.exe"
#define SourceDir "E:\Downloads\Rain\Code stuff\File Explorer\dist\app"
#define IconFile "E:\Downloads\Rain\Code stuff\File Explorer\Main\Assets\rain.ico"

[Setup]
AppId={{8A9C2F31-4D6B-4A1E-9E2C-7F3B5D8A1C42}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion=1.0.0.0
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

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// True if any "10.*" Microsoft.WindowsDesktop.App runtime folder exists.
function IsDotNet10DesktopInstalled(): Boolean;
var
  FindRec: TFindRec;
  BasePath: String;
begin
  Result := False;
  BasePath := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(BasePath + '\10.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDotNet10DesktopInstalled() then
  begin
    if MsgBox('Rain Explorer needs the .NET 10 Desktop Runtime (x64), which doesn''t appear to be installed.'
        + #13#10#13#10
        + 'Click Yes to open the download page now (install the "Desktop Runtime", then run this setup again), '
        + 'or No to continue anyway.',
        mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/en-us/download/dotnet/10.0',
        '', '', SW_SHOW, ewNoWait, ErrorCode);
      Result := False;
    end;
  end;
end;
