; A no-install reproduction of the old post-install launch mechanism.
; No application files, shortcuts, registry entries, or uninstaller are installed.
; Build the diagnostic first. Run this fixture unelevated with /VERYSILENT /LOG.
#define ProbeExe AddBackslash(SourcePath) + "bin\Release\net10.0-windows\RainExplorer.MitigationDiagnostic.exe"

[Setup]
AppName=Rain Explorer Mitigation Probe
AppVersion=1.0
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
OutputDir=..\..\dist\mitigation-investigation
OutputBaseFilename=InstallerLaunchProbe

[Run]
; postinstall uses original-user execution by default; make that explicit here
; so the same mechanism can be tested silently without the finish-page checkbox.
Filename: "{#ProbeExe}"; Flags: waituntilterminated runhidden runasoriginaluser
