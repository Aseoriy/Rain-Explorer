# Creation limits and theme repair — 2026-09-05

This checkpoint addresses Rain Explorer's resource-limit failure, unclear creation review, visible access-key underscores, and dialogs/menus that ignored the selected theme. Product version stays **2.0.0**; the private Worker protocol is **1.24**.

## Cause and implemented changes

A read-only, bounded metadata inventory of the reported Sail Launcher `Main launcher files` folder found **15,712 files and 2,718 directories: 18,430 total entries**, approximately 1.35 GB. It had no encountered reparses or read errors and ample destination space. The failure was the former **4,096-entry expanded-tree ceiling**, not evidence of exhausted RAM or disk.

- `creation-expanded-tree-budget-v1` negotiates a **65,536 material-entry** budget for ZIP, TAR, TAR.GZ, TAR.BZ2, and conditionally qualified RAR directory creation. Both initial planning and final source-manifest validation use it. Older peers or feature omission still enforce 4,096.
- `IArchiveClient.MaximumCreationTreeEntries` exposes the negotiated budget. Existing capability DTO `MaximumEntries` values, explicit file/source-root limits, wire-frame bounds, ZIP rebuild limits, and older wire shapes are unchanged. Raw GZIP/BZIP2 still require exactly one file. Source-byte ceilings remain 64 GiB (8 GiB for RAR), and creation metadata remains bounded at 64 MiB.
- Explorer keeps expanded-tree limits separate from selected-source limits. It maps fixed error codes for entry count, source bytes, metadata, path length, and output budget to distinct messages. Known limits come from validated capabilities, not provider text. Activity and the status bar retain the reason; the Activity tooltip exposes the full message. There is no automatic retry.
- The creation dialog now says **Item kind**, **Name in archive**, and **Current location**. The first identifies a file or folder; the second is its top-level name inside the new archive. The selected-source list has its own visible height; the body scrolls while review/actions remain outside it. Only the selected format's explanation and real limits occupy the form.
- Shared button templates recognize WPF access keys, so `_Cancel` no longer renders its underscore. Cancel, Create, and Browse retain keyboard access; Create uses a distinct access key from Cancel.
- Opaque popup/dialog surfaces derive from the selected theme, including neutral Mono and the custom-gradient built-ins. Shared controls and the main-window palette use live resource references. Frozen palette resources can be replaced. Theme tests share an exclusive WPF application collection because dispatcher pumping otherwise admits unrelated modal test work.

Provider authority, filesystem/source identity checks, cancellation, bounded staging/readback, original preservation, and no-replace publication are unchanged. No registry, file associations, installed application, dependency, Git, or signing changes were made.

## Verification

- Rain Archives Release solution build: **0 warnings, 0 errors**. Full gate: **536 unit/WPF + 29 adversarial + 384 integration = 949**, zero failures/skips.
- Rain Explorer full Release suite: **330/330**, zero failures/skips. This includes the real qualified WinRAR creation route on this machine. Its round-trip test now compares validated normalized archive paths; original provider path evidence is not rewritten to hide RAR's Windows separators.
- Larger-folder proof: 20,000-entry ZIP and 4,200-entry TAR, TAR.GZ, and TAR.BZ2, with reopen, paged listing, integrity/payload equality, original preservation, cancellation and cleanup. The legacy 4,096 refusal, new ceiling, unsupported raw tree shapes, and feature dependencies are covered. Exact retained 1.23 Client/Contracts-to-current-Worker and current-Client-to-retained-Worker checks pass.
- UI checks cover both 560×480 and 660×560 creation layouts, visible source rows/fixed actions, scroll reachability, source labels, real popup theme switching, palette distinction/opacity, and shared access-key templates. These automated checks are not direct installed UI, Narrator, physical DPI, or clean-machine acceptance.

The deliverable is `artifacts/RainExplorer.CreationThemeFix.2.0.0.20260905.01`. The installer is built from its `app` directory, which includes the private Worker. `Verify-Package.ps1` reproduces source and tree identities; its sealed `verification.json` records all identities and installer SHA-256. Historical packages remain unchanged.

Final package repeats pass: **4/4 large-folder cases** and **330/330 Explorer tests** against the bundled Worker, with zero failures/skips. All **18 current direct/transitive NuGet advisory inventories** reported no vulnerable package. Scoped whitespace verification and `git diff --check` passed. The final process inventory found only the intentionally preserved installed Rain Explorer and its installed private Worker; no testhost, recovery probe, or package/test Worker remained.

- App payload: **458 files / 251,091,201 bytes / `77FEEA3B86716858B648E754339EE176217B276145D0BCB795838CEE272FAD5A`**.
- Private Worker: **207 files / 85,187,965 bytes / `3178A3780F56FC3A23E7AB0C7C12BD6D1883B52B71CE362B88B3AD105E88BF13`**.
- App/Worker Contracts are byte-identical at `9F3B49FAE501653170A725E538C46DA1A1166FA27481F1F28E5C5A5EEDFC2D72`. Provider assemblies remain Worker-only; `7z.dll`, `7za.exe`, `SharpSevenZip.dll`, `Rar.exe`, and `WinRAR.exe` are not bundled.
- `verification.json` SHA-256: `331228EDE65A7481AB9EC9E42CE99C3352F898658E7C1E51064674AA877E3F9D`.
- Installer: `installer/RainExplorer-Creation-Theme-Fix-Setup-2.0.0.exe`, **76,157,509 bytes**, SHA-256 `11562E789EBDF9D5A4FAC409C2495FB15C20CB8B8F3E157AF2A6DDF3A5B843C9`. Product/file versions are 2.0.0/2.0.0.0. It is unsigned and has not been installed.

Current Rain Archives production identity: **164 inputs / 3,266,783 bytes / `33912C07420D5C87DA97DB200D168278E32054B6C718AB12789BF5E0434A29B9`**. Current Explorer present changed inputs under Main/Tests/Installer: **69 / 1,740,042 bytes / `4EB52920DA816356A2ECFCFB5E61E5C97E1A4EE89BFA45AC358F2E9E3C91EB8F`**, over `0bc3fb090bef4d93e0db5bb6d66813b80e8385f7`; the pre-existing `Main/Services/CompressionService.cs` deletion is separately recorded. Algorithms and scope are explicit in the verifier.

## Reproduce

From the Rain Archives root:

```powershell
dotnet build RainArchives.slnx -c Release --no-restore
dotnet test RainArchives.slnx -c Release --no-build --no-restore
$env:RAIN_ARCHIVES_WORKER_PATH = Join-Path (Get-Location) 'artifacts/RainExplorer.CreationThemeFix.2.0.0.20260905.01/app/worker/RainArchives.Worker.exe'
dotnet test tests/RainArchives.IntegrationTests/RainArchives.IntegrationTests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~RealWorkerCreatesAndReopensExpandedTree
Remove-Item Env:RAIN_ARCHIVES_WORKER_PATH
& ./artifacts/RainExplorer.CreationThemeFix.2.0.0.20260905.01/Verify-Package.ps1
```

From the File Explorer root:

```powershell
dotnet test Tests/RainExplorer.Git.Tests/RainExplorer.Git.Tests.csproj -c Release --no-restore
$env:RAINEXPLORER_TEST_WORKER_PATH = 'E:\Downloads\Rain\Code stuff\Rain Archives\artifacts\RainExplorer.CreationThemeFix.2.0.0.20260905.01\app\worker\RainArchives.Worker.exe'
dotnet test Tests/RainExplorer.Git.Tests/RainExplorer.Git.Tests.csproj -c Release --no-build --no-restore
Remove-Item Env:RAINEXPLORER_TEST_WORKER_PATH
```

## Remaining boundaries and next action

The next action is to try the new installer and the reported folder; it has not been installed automatically. This is not unlimited creation: 65,536 entries, byte/metadata/path budgets and other validation still apply. No UI claim grants encryption, split, overwrite, in-place update, 7z or XZ writing. RAR still requires the qualified separately installed encoder.

`SECURITY.md` retains the older 4,096 expanded-tree statement. Its edit was paused because the security-policy workflow requires an explicit diff approval before writing. The narrow proposed correction is to state that negotiated protocol-1.24 tree creation permits 65,536 material entries while legacy peers, explicit roots, and rebuild remain at 4,096; all byte, metadata, path, ownership, and publication protections remain unchanged. This is a documentation discrepancy, not approval to remove those protections. No new risk exclusion or security suppression was added.

Manual installed-app, shell, accessibility, clean-machine, physical network/filesystem, signing, publication and release gates remain separate. Do not relabel earlier package evidence or start another phase automatically from this checkpoint.
