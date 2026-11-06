# RedirectionTrust diagnostic

Build from the repository root:

```powershell
dotnet build Tests\RainExplorer.MitigationDiagnostic\RainExplorer.MitigationDiagnostic.csproj -c Release
```

Open the resulting `bin\Release\net10.0-windows\RainExplorer.MitigationDiagnostic.exe`
once from Windows Explorer and once by entering its full path in Rain Explorer's
address bar. Alternatively run it inside each terminal being compared. It defaults
to `C:\Program Files\nodejs\node.exe`; pass an absolute Node executable path as the
first argument for another installation. Use the same Node executable in both runs.

Each run leaves a small, uniquely named `%TEMP%\RainExplorer-Mitigation-*` folder
containing `report.json`, `node.json`, the test script, and an isolated junction and
target. It does not read project contents, run pnpm lifecycle scripts, install
packages, modify Windows policy, or change the running Rain Explorer instance.
Run unelevated: administrator-created junctions do not test the reported failure.

The report includes live Rain Explorer and Windows Explorer policy samples and a
diagnostic -> CMD -> Node chain. The Rain Explorer samples are observations of all
running instances, not an assertion that each is an ancestor. Node reports its
parent PID; the report also records executable paths and creation times so that
different images, exited processes, and reused PIDs are not mistaken for a policy
transition. Query failures are explicit errors, never reported as flags zero.

Exit codes: 0 = junction read succeeded and queries succeeded; 1 = junction read
failed; 2 = diagnostic/query error. Inspect the flags separately: a successful read
alone does not prove a policy is disabled. On an OS that does not support policy
16, the query error is an unsupported/inconclusive result.

For an already running CMD and Node, keep Node alive while inspecting their exact
PIDs from a separate terminal:

```powershell
.\Tests\RainExplorer.MitigationDiagnostic\bin\Release\net10.0-windows\RainExplorer.MitigationDiagnostic.exe --inspect <RainPID> <CmdPID> <NodePID>
Get-CimInstance Win32_Process | Where-Object ProcessId -in @(<RainPID>, <CmdPID>, <NodePID>) | Select-Object Name, ProcessId, ParentProcessId, CreationDate
```

Replace the angle-bracket placeholders with the observed decimal PIDs. Inspect
`cmd.exe` itself, not WindowsTerminal.exe, OpenConsole.exe, or a PowerShell process
started just to query itself.

## Installer reproduction

`InstallerLaunchProbe.iss` is a no-install fixture: no app directory, files,
shortcuts, registry entries, or uninstaller are installed. Compile it with Inno
Setup after building the diagnostic. It retains Inno's default protection and
uses original-user execution, matching the old postinstall entry's default.
Run its generated executable unelevated with `/VERYSILENT /LOG` and inspect the
new diagnostic report and Setup log. The Setup log records the diagnostic's exit
code; the fixture's own exit code is not the junction test result.

## Findings on 2026-09-05

Windows 11 build 26200.9168, .NET 10.0.9, Inno Setup 6.7.3:

| Launch origin | Diagnostic flags | CMD flags | Node flags | Junction read |
| --- | --- | --- | --- | --- |
| Tool shell | 0 | 0 | 0 | Passed |
| Windows Explorer, actual GUI double-click | 0 | 0 | 0 | Passed |
| Running Rain Explorer (flags 1), address-bar executable launch | 1 | 1 | 1 | Failed |
| No-install Inno fixture, default guard, original user | 1 | 1 | 1 | Failed |
| Same no-install fixture, guard off for this control run only | 0 | 0 | 0 | Passed |

The control used `/NOREDIRECTIONGUARD` only on the disposable, unelevated,
no-install fixture. The production installer retains protection. The A/B result
isolates Setup's opt-in as a reproducible source of the descendant policy on this
machine; it does not establish that all Windows versions behave identically.

The installed Rain Explorer PID 57952 had flags 1. A new interactive address-bar
CMD, PID 48420, also had flags 1; its conhost PID 33296 had flags 1. The separate
OpenConsole PID 66632 had flags 0. This demonstrates why a terminal-host sample
cannot stand in for CMD. The originally reported CMD=0/Node=1 pair was already
closed and could not be verified; no spontaneous re-enabling by Node was observed.

The original Rain Explorer parent PID 38528 had exited before investigation, so
its historical launch source could not be independently recovered. The installer
mechanism is proven by the controlled A/B; attributing that particular historical
process to Setup still requires its launch history. No Rain Explorer, CMD, or Node
IFEO key was present. The queried global kernel mitigation values were absent;
enumerating kernel subkeys was denied, so this is not an exhaustive policy audit.

Source inspection found no Rain Explorer mitigation-setting APIs, STARTUPINFOEX,
mitigation creation attributes, or ConPTY code. TerminalLauncher and the address
bar use Process.Start/UseShellExecute; manifests and runtime configuration contain
no RedirectionGuard opt-in. The installed .NET runtime is 10.0.9. Neither the
reviewed .NET launch code nor WPF PrintDialog source supplied an opt-in.

The production fix removes the post-install launch entry and directs the user to
Start or a desktop shortcut. This removes the reproduced propagation boundary
without weakening Setup, the application, or system/image policy. Merely opening
a second Rain Explorer does not repair an already affected instance: the
single-instance code forwards to it. Fully close the old instance and its affected
terminals, then start Rain Explorer from Windows and rerun the diagnostic.

Release build, self-contained win-x64 publish (including worker), diagnostic
build, fixture compilation, and production installer compilation passed. Outputs
and A/B evidence are under `dist\mitigation-investigation`. The production installer
was not installed, and the user's existing Rain Explorer was not restarted.
Fresh application startup and the complete pnpm project after restart remain
manual acceptance checks. No version bump, commit, push, or release was made for
this fix; existing working-tree changes were preserved.

References:

- [Windows policy layout](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-process-mitigation-redirection-trust-policy)
- [Microsoft's RedirectionGuard description](https://www.microsoft.com/en-us/msrc/blog/2025/06/redirectionguard-mitigating-unsafe-junction-traversal-in-windows/)
- [Inno Setup RedirectionGuard](https://jrsoftware.org/ishelp/topic_setup_redirectionguard.htm) says child inheritance does not occur; the local A/B above contradicts that statement on this build.
- [Inno Setup enabling code](https://github.com/jrsoftware/issrc/blob/main/Projects/Src/Setup.MainFunc.pas), `RedirectionGuardConfigure`, calls SetProcessMitigationPolicy with policy 16 and flags 1.
