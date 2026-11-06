# Milestone 7 — Windows associations and shell integration

Status: **M7-D valid-RAR opening and conditional RAR creation remediation is automated/package complete;
Milestone 7 remains partial** as of 2026-09-05. Direct Open With/context-menu/extraction behavior, Windows-default selection and
icon-cache refresh, reversal, installed-path, upgrade/uninstall, accessibility, clean-machine, signing,
publication, and release acceptance remain `manual_pending`.

## M7-A scope

M7-A adds one explicit Rain Explorer Settings action for users who want supported archive files to open in
Rain Explorer/Rain Archives. It does not silently take defaults. The action:

1. validates ownership of one Rain Explorer per-user registration;
2. writes the owned ProgID and Registered Applications capability map under HKCU;
3. adds only owned OpenWithProgids values for supported archive extensions;
4. asks Windows to open the registered application's Default apps page; and
5. leaves the final per-extension choice to the user.

The owned identities are:

- ProgID: `SailHub.RainExplorer.Archive.2`
- Registered application: `Rain Explorer Archives`
- Capability path: `Software\SailHub\RainExplorer\Capabilities\ArchiveAssociationsV2`

No always-running service or shell extension is introduced. Rain Explorer continues to use the private
Rain Archives Worker and the additive protocol-1.23 Contracts/Client boundary. Protocol 1.22 peers retain
their exact prior feature set and never advertise RAR creation.

## M7-B remediation scope

M7-B addresses three product-owner findings without expanding registration or archive authority:

1. virtual archive rows resolve normal Windows file-type/folder icons by extension and attributes while
   retaining an empty physical path;
2. archive internal history composes with tab history so Back at archive root returns to the physical
   source folder and Forward reopens the archive; and
3. the owned archive ProgID uses a dedicated original archive icon instead of Rain Explorer's application
   icon.

The file-type lookup always uses `SHGFI_USEFILEATTRIBUTES` for virtual rows, including EXE, LNK, and ICO
names. It cannot inspect, extract, launch, or hand a fabricated archive path to the shell. Direct archive
launches seed the source folder as their Back destination, and opening an archive from a visible folder now
uses the normal outer navigation route.

`Assets\RainArchive.ico` contains nine 32-bit PNG-compressed sizes: 16, 20, 24, 32, 40, 48, 64, 128,
and 256 pixels. The ProgID `DefaultIcon` uses that asset; Registered Applications `ApplicationIcon` still
uses Rain Explorer's application executable. A missing archive icon fails before registration writes.

## M7-C remediation scope

M7-C addresses the product owner's next four observations while preserving every earlier boundary:

1. an archive refusal retains the canonical source name and maps only structured Worker error codes to a
   fixed actionable status instead of leaving an anonymous blank Archive tab;
2. the creation picker visibly lists ZIP, TAR, TAR.GZ, TAR.BZ2, GZIP, and BZIP2, while disabling raw
   GZIP/BZIP2 with an explanation unless the selection is exactly one ordinary file;
3. **Extract to...** opens an existing-folder picker and creates one distinct collision-free child per
   archive without merging or overwriting; and
4. **Open with...** uses `SHOpenWithDialog`, while Rain's projected context menu removes only its owned
   duplicate commands and exact repeated `WinRAR` labels.

The reported four-byte `__h3__...zip` samples are not ZIP archives: they have no end-of-central-directory,
and the Worker correctly returns `archive.invalid_zip_central_directory`. The reported 856,160-byte
`Wallpaper Engine.zip`, SHA-256
`11D9635FA0E0BBBF2E1C7F9FEA9506BA3E7D5C387D058FADABFB17915E45D4F2`, is a valid 12-entry ZIP and lists
through BCL, Windows tar, and the installed isolated Worker. No decoder safety check was weakened.

7Z, RAR, TAR.XZ, and XZ were browse/extract-only at the sealed M7-C checkpoint. WinRAR registry keys and commands remain foreign state: Rain neither
changes nor manufactures them. Context projection keeps unrelated same-label extensions and non-owned Edit,
Explore, Find, Copy to, and Move to verbs.

## M7-D remediation scope

M7-D addresses the product owner's valid-RAR and RAR-creation findings without weakening archive detection:

1. SharpCompress directory descriptors that omit optional RAR CRC metadata no longer crash listing. The
   optional CRC is treated as unavailable for directories, while malformed required metadata still fails
   through a fixed provider error. The user's reported 550,475,088-byte RAR5 with 18,418 entries now lists
   through the isolated Worker and remains byte-identical.
2. The generic `InvalidRequest` fallback no longer produces the misleading “rejected before reading” status
   for that reader defect. Rain Explorer retains the source name and maps the structured result to fixed,
   actionable text without showing provider exception details.
3. Protocol 1.23 adds conditional RAR5 explicit/tree creation and a typed capability query. The creation
   picker enables RAR only when the Worker's exact encoder gate is `Available`; otherwise it shows the fixed
   `NotInstalled`, `UpdateRequired`, `UntrustedBinary`, `UnsupportedPlatform`, or `RestartRequired` reason.
4. The encoder is the user's separately installed and separately licensed signed x64
   `C:\Program Files\WinRAR\Rar.exe`, version 7.23 through 7.x. Rain does not copy, bundle, download, update,
   or discover it through `PATH`, current directory, or registry. This machine's signed 7.20 installation is
   therefore reported as **update required**, not silently treated as create-capable.
5. Creation remains unencrypted, single-volume, new-destination-only RAR5. The Worker materializes validated
   source streams in one token-owned private workspace, holds the verified encoder binary against replacement,
   invokes it without a shell, requires a successful independent encoder test, then performs complete
   SharpCompress path/kind/length/timestamp/content-digest readback before Rain's existing durable no-replace
   commit. Encryption, split, SFX, update/rebuild, overwrite, arbitrary options, and password command-line
   use remain unavailable.
6. The RAR workspace has a bounded durable crash contract: an active Worker holds an exclusive owner claim;
   an exact-PID hard kill leaves only a token-verifiable local remnant; the next Worker automatically removes
   only that exact ordinary non-reparse remnant. Suspicious, changed, unreadable, or over-limit state is
   preserved and surfaced through one fixed warning. This remains same-user filesystem safety, not a hostile
   same-user sandbox.

## Supported suffixes

The association map contains `.zip`, `.7z`, `.rar`, `.tar`, `.tgz`, `.tbz2`, `.txz`, `.gzip`, `.bzip2`,
`.gz`, `.bz2`, and `.xz`. The shared launch model additionally recognizes compound `.tar.gz`, `.tar.bz2`,
and `.tar.xz` names before their terminal suffixes.

Generic numeric `.001` is intentionally not registered because it is used by unrelated split files. A
recognized terminal archive path can still enter the existing Rain Archives volume-set detection; suffixes
remain UI/activation hints, not decoder authority.

## Enforceable registration invariants

- Windows controls the actual default application. Production does not write `UserChoice`.
- Production does not assign an extension's unnamed default value.
- Production does not read, modify, delete, rename, or replace WinRAR ProgIDs, verbs, commands, icons, or
  Registered Applications values.
- A foreign owner marker, ProgID, or Registered Applications name collision fails before any write.
- The command is an exactly quoted absolute executable path followed by exactly quoted `%1`.
- Registration occurs only after the explicit Settings action and only under HKCU.
- Reversal uses the same Windows Default apps page: select WinRAR or another installed handler again.
- Direct and single-instance-forwarded archive paths use the Rain Archives virtual-location route.
  Ordinary-file process arguments retain parent-folder reveal and selection behavior.
- Registration grants no extraction, test, creation, overwrite, delete, retry, recovery, provider, or
  filesystem authority.

This approach deliberately preserves WinRAR's ordinary right-click features. Windows may change how it
presents legacy context-menu entries, so their visible presence remains a direct manual acceptance item.

## Automated evidence

### Current M7-D evidence

- Complete Rain Archives gate: **530 unit/WPF + 29 adversarial + 375 integration = 934/934**, zero failures/skips.
- Complete Rain Explorer suite: **315/315** both against the normal Release output and with every real-worker
  route redirected to the exact immutable packaged Worker.
- Focused WinRAR provider/workspace security and reliability suite: **14/14**.
- Locked restores, scoped formatter verification, `git diff --check`, and both Release builds pass with zero
  warnings/errors. All 16 Rain Archives projects and both Rain Explorer projects report no known direct or
  transitive NuGet vulnerability from the current configured source.
- The exact reported 550,475,088-byte RAR5, SHA-256
  `28F6AAC54C43CE10A39C0E930861D658229D7359F3466503C30D956D3222F92B`, lists 18,418 entries through the
  exact packaged Worker and remains byte-identical. The 73-byte checked directory-without-CRC fixture opens,
  lists, and extracts through the real protocol route.
- The signed production WinRAR 7.20 installation reports the truthful typed `UpdateRequired` state. Positive
  writer tests use an isolated signature-checked official 7.23 encoder; that binary is not copied into source,
  either package, or the installer.
- A fresh Daybreak Blue max-reasoning review found bounded-maintenance, process-termination, cleanup-reporting,
  no-follow-owner, and fixture-isolation edges. The one permitted remediation pass moves maintenance after
  authentication and off dispatch, adds one global work/time budget, no-follow ancestry/owner validation,
  exact child-exit proof with Worker teardown on uncertainty, cleanup-failure propagation, and an isolated
  exact-PID crash fixture. The 14 focused cases and complete gates pass after remediation. This is strong
  same-task review evidence, not independent product acceptance or a hostile same-user sandbox claim.

Rain Archives production identity is 164 inputs / 3,260,519 bytes / SHA-256
`0D84243CA1A09AE3BD77942EC66578E44E3EB5A7547411CCD40E224CD2460AB2`. Rain Explorer's cumulative
uncommitted identity over base `0bc3fb090bef4d93e0db5bb6d66813b80e8385f7` is 67 inputs /
1,661,839 bytes / SHA-256
`266A39F2A2286D9116F41A5A11C2F587B758C2C953AEDBC9A3E902D1E2560C4C`, using ordinal repo-relative
path, NUL, decimal raw length, NUL, then raw bytes; the deleted baseline file is excluded. No commit, push,
pull request, install, live association change, signing, publication, or interruption of the installed Rain
Explorer occurred.

### M7-C predecessor evidence

- Focused archive-usability suite: **108 passed, 0 failed, 0 skipped**.
- Complete Rain Explorer suite on final source: **311 passed, 0 failed, 0 skipped**.
- Exact packaged-Worker archive suite: **19 passed, 0 failed, 0 skipped**.
- Rain Explorer Release build: **0 warnings, 0 errors**.
- Main and test direct/transitive NuGet advisory queries: **no known vulnerable package**.
- Scoped formatter verification and `git diff --check`: pass.
- Regressions cover fixed refusal text without provider-text disclosure, all six writer-qualified creation
  choices and raw one-file disablement, explicit destination planning, `SHOpenWithDialog` export/path
  validation, precise owned-command suppression, exact-WinRAR deduplication, and preservation of unrelated
  same-label and shell-extension commands.

The cumulative uncommitted Rain Explorer identity over base
`0bc3fb090bef4d93e0db5bb6d66813b80e8385f7` is 67 inputs / 1,649,400 raw bytes / SHA-256
`834E5F4A60BD5734A955C38B1831288E9169E7F00A7D9C538B19AC8AB085D7F4`.

### M7-B predecessor evidence

- Focused navigation/icon/association suite: **33 passed, 0 failed, 0 skipped**.
- Complete Rain Explorer suite on final source: **293 passed, 0 failed, 0 skipped**.
- Rain Explorer Release build: **0 warnings, 0 errors**.
- Main and test direct/transitive NuGet advisory queries: **no known vulnerable package**.
- Scoped formatter verification and `git diff --check`: pass.
- Regressions prove pathless virtual EXE/extensionless/folder icon lookup, direct and folder-origin archive
  Back/Forward behavior, missing-icon refusal before registry mutation, source/package ICO integrity, every
  embedded image bound/signature, and publish-copy wiring.

The cumulative uncommitted Rain Explorer identity over base
`0bc3fb090bef4d93e0db5bb6d66813b80e8385f7` is 61 inputs / 1,596,531 raw bytes / SHA-256
`35A10DC08EC6A6FDC49DE1FC88FCE7F9FFC93DCF1DB97F2141FF146AA2909E5D`.

### M7-A predecessor evidence

- Focused `ArchiveFileAssociationServiceTests`: **24 passed, 0 failed, 0 skipped**.
- Complete Rain Explorer suite on the final unchanged source: **286 passed, 0 failed, 0 skipped**.
- Rain Explorer Release build: **0 warnings, 0 errors**.
- Main and test direct/transitive NuGet advisory queries: **no known vulnerable package**.
- Scoped formatter verification: eight M7-A production C# files and one test file clean.
- `git diff --check`: pass. Repository-wide formatter status is not claimed because unrelated historical
  formatting debt remains.
- Fake-registry coverage seeds WinRAR defaults/commands and requires exact preservation; it also proves no
  `UserChoice` or extension-default write, foreign-collision refusal before mutation, every claimed
  association, exact command quoting, reversal URI, and stale-owned-registration repair state.
- Archive activation tests distinguish a supported archive argument from an ordinary file argument.
- Production XAML resource validation proves every `Ic.*` reference exists.

The cumulative uncommitted Rain Explorer changed-input identity over base
`0bc3fb090bef4d93e0db5bb6d66813b80e8385f7` is:

- 56 modified/untracked input files; the deleted baseline file is excluded;
- 1,535,473 raw bytes; and
- SHA-256 `E6C6A372F0964CC6A5CE19C1AB6A2BFFAF197B763D3C0C6A74744EB35AE26A81`.

The algorithm is ordinal repo-relative path, NUL, decimal raw length, NUL, then raw bytes. No commit, push,
or pull request occurred.

## Current M7-D package and installer evidence

Self-contained local package:

`artifacts/RainExplorer.M7RarRemediationPackage.2.0.0.266A39F2.01`

- package manifest: 6,749 bytes, SHA-256
  `6E9AE928C36F9E2F7C2FA718D24D6C6337B7054A825D168603553A409ECCF23E`;
- app tree: 458 files / 251,080,369 bytes /
  `37B03FE1E36B2BF2D647831F3AEB43C6598FA0599D7692E46FFDE45F1F948628`;
- private Worker tree: 207 files / 85,185,437 bytes /
  `B523EBE613581D176389C0C9EF6E6C6D77DDFC866E3D5485845E8555DE5AF1C8`;
- Rain Explorer DLL: `0AC880A1B615EB864F1DAD609CE527B2DD0289322E2AD7A88B4A9080FE1A2951`;
- App/Worker Contracts are byte-identical at
  `E71B945555031A9933EB0631BE260CFCA5CF9AA0257BEEF658BAD9319CB398EF`;
- the WinRAR adapter is present only in the private Worker; SharpCompress and Worker-only assemblies are
  absent from the App root; and
- `Rar.exe`, `WinRAR.exe`, `7z.dll`, `7za.exe`, `7zz.exe`, and `SharpSevenZip.dll` are absent.

Matching local installer:

`artifacts/RainExplorer.M7RarRemediationInstaller.2.0.0.266A39F2.01/RainExplorer-M7-RAR-Remediation-Setup-2.0.0.exe`

- 76,160,790 bytes;
- SHA-256 `AB7E8BE476990B2CC0EC0CFB86ED6353196AF2DD14B019D5231370B99CCE19CB`;
- file version 2.0.0.0;
- Authenticode status `NotSigned`; and
- compiled by Inno Setup 6.7.3 directly from the immutable package.

The package and installer were not executed, installed, signed, or published. The currently installed
single-instance Rain Explorer and its Worker were left running and untouched, so direct exact-package UI,
Windows shell, association, and qualifying-installed-WinRAR creation evidence remains manual. The `.01`
package above is the only sealed M7-D Rain Explorer artifact. The M7-C `834E5F4A` package/installer and every earlier artifact
remain immutable predecessor evidence.

## M7-C predecessor package and installer evidence

Self-contained local package:

`artifacts/RainExplorer.M7ArchiveUsabilityPackage.2.0.0.834E5F4A`

- package manifest: 6,728 bytes, SHA-256
  `60CD60D923D640E95B249BDA989168C659846057E8F65706CE5CA5842F4437D1`;
- app tree: 456 files, 250,885,777 bytes, SHA-256
  `D463B46E69EF4CBF6B50C0D781A5EB1CE2B7DA6FB45D0D72C4BD23BE9F14B2C5`;
- private Worker tree: 205 files, 85,041,541 bytes, SHA-256
  `D1A666333CD9588F8C71C439F729BB66886CDED5805DD90E852A8A9055CE7F49`;
- App/Worker Contracts are byte-identical at
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`;
- no SharpCompress or Worker-only assembly is at the app root, while `7z.dll`, `7za.exe`, `7zz.exe`, and
  `SharpSevenZip.dll` are absent.

Matching local installer:

`artifacts/RainExplorer.M7ArchiveUsabilityInstaller.2.0.0.834E5F4A/RainExplorer-M7-Archive-Usability-Setup-2.0.0.exe`

- 76,116,013 bytes;
- SHA-256 `246F7053C582FDDF08C792A33293EE1D439A3BD1EBC083B5CE2AE434BDA9FF7D`;
- file version 2.0.0.0;
- Authenticode status `NotSigned`; and
- compiled by Inno Setup 6.7.3 directly from the immutable package.

The package and installer were not installed, signed, or published. The currently running installed Rain
Explorer was not closed or redirected during automated verification, so direct exact-package UI evidence
remains manual. The `8F24FD12` package/installer pair is an unsealed pre-final intermediate and must not be
treated as the accepted checkpoint. M7-B `35A10DC0` remains immutable predecessor evidence.
After build-server shutdown, the only scoped processes are the intentionally preserved installed Rain
Explorer and its private Worker child. No test host/build server remains, and default creation recovery,
extraction recovery, Preview Cache, and temporary nested-workspace inventories are empty.

## M7-B predecessor package and installer evidence

- Package: `artifacts/RainExplorer.M7ArchiveNavigationIconsPackage.2.0.0.35A10DC0`.
- Manifest: 5,756 bytes / `FEE42BEF45596275FFA04E7BA62A89415C81234EC4F92F40AAF184A8A6C30A4F`.
- App tree: 456 files / 250,875,089 bytes /
  `B77CBD98E71836D910AC53969F9D9C9518F1E4EC70842784DCF78FEF8873C6CF`.
- Installer: 76,124,799 bytes /
  `FD8A64082F3C06EC717F6BE51574AA4913F8AA3322357AA584B002FD460D3699`.

## M7-A predecessor package and installer evidence

Self-contained local package:

`artifacts/RainExplorer.M7ArchiveAssociationsPackage.2.0.0.E6C6A372`

- package manifest: 4,547 bytes, SHA-256
  `37C4167A2D8DBFF298CD2D583166D491C2ED7DED3654F136916A4E31F9B1E51C`;
- app tree: 455 files, 250,832,467 bytes, SHA-256
  `9E9B37B3D5EA6F3106840F3103CD013F6D2B9437EC729DFE9A7DD7D519169C52`;
- private Worker tree: 205 files, 85,041,541 bytes, SHA-256
  `D1A666333CD9588F8C71C439F729BB66886CDED5805DD90E852A8A9055CE7F49`;
- App/Worker Contracts are byte-identical at
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`;
- no SharpCompress or Worker-only assembly is at the app root; and
- `7z.dll`, `7za.exe`, `7zz.exe`, and `SharpSevenZip.dll` are absent.

Launching this exact package with checked `plain.zip` created its visible Rain Explorer main window and its
exact private authenticated Worker child. Verification deliberately did not click **Set as default...** or
mutate the live association registry.

Matching local installer:

`artifacts/RainExplorer.M7ArchiveAssociationsInstaller.2.0.0.E6C6A372/RainExplorer-M7-Archive-Associations-Setup-2.0.0.exe`

- 76,079,801 bytes;
- SHA-256 `252CB6C3D325E648276DDEB622852D49EA3881BF63E0989BC45CE8611A2B1A86`;
- file version 2.0.0.0; and
- Authenticode status `NotSigned`.

The installer was built but not run, installed, signed, or published. The portable registration points to
the exact executable that performed registration; do not move or delete that package while it is selected
as a default.

## Manual M7-D acceptance

Use disposable sample archives and the exact candidate path. Do not change defaults for unrelated file
types.

1. Close the installed single-instance Rain Explorer, then install or launch the exact M7-D `.01` candidate.
2. Right-click one ordinary file, activate Rain's **Open with...**, confirm the Windows chooser opens, and
   launch one selected application. Cancel once and confirm cancellation is silent.
3. On an archive, inspect both projected and **Show more options** menus. Confirm Rain presents one owned
   Open With route, does not repeat its own commands, and preserves functional WinRAR commands without
   manufacturing or removing unrelated extensions.
4. Use **Extract here**, then **Extract to...** with another existing folder. Confirm each archive receives a
   distinct child and no existing file/folder is merged or overwritten.
5. Open **Create archive...** for one file and confirm ZIP, TAR, TAR.GZ, TAR.BZ2, GZIP, BZIP2, and RAR are
   visible. On the current signed WinRAR 7.20 installation, confirm RAR is disabled with the fixed
   **Requires WinRAR 7.23 or later** reason. Repeat with a folder or multiple files and confirm GZIP/BZIP2
   stay visible but disabled with their separate one-file reason.
6. Manually update WinRAR from the official RARLAB download to a signed 7.23 or later 7.x build, restart the
   candidate, confirm RAR becomes enabled, and create one disposable file RAR plus one folder-tree RAR at new
   absent destinations. Reopen/test/extract both through Rain and independently through WinRAR; compare exact
   paths and payload hashes and confirm every source remains unchanged. Rain must never download or update it.
7. Open the reported 550,475,088-byte RAR and representative valid ZIP, 7Z, RAR, TAR, GZIP, BZIP2, and
   XZ-family samples. Open one checked malformed four-byte `.zip` and confirm a fixed damaged/truncated
   message appears with its filename rather than the misleading generic pre-read rejection.
8. Open **Settings > Advanced > System**.
9. Under **Use Rain Explorer for every archive**, activate **Set as default...**.
10. Confirm Windows opens Default apps for **Rain Explorer Archives**. If Windows falls back to the generic
   Default apps page, search for Rain Explorer there.
11. Press Windows' **Set default** control once, then verify Rain Explorer is assigned to the listed archive
   types. If that control is unavailable on the Windows build, assign the types individually.
12. Confirm the assigned archive types display the dedicated violet/magenta/cyan stack-and-zipper icon rather
   than Rain Explorer's application icon. Allow normal Explorer shell-cache refresh time.
13. Confirm virtual archive folders and entries display normal Windows folder/file-type icons, including an
   EXE entry, without extracting or launching content.
14. Open an archive from a physical folder, return to its archive root, press Back, and confirm the tab
   returns to that physical folder. Press Forward and confirm the archive reopens.
15. Double-click each assigned sample and confirm it opens as an archive tab through Rain Archives, not as a
   physical folder or extracted copy.
16. Return to the same Windows Default apps page, assign one tested extension back to WinRAR, and confirm a
    double-click opens WinRAR again.
17. Reassign only the extensions the product owner actually wants Rain Explorer to own.
18. Close Rain Explorer and verify no unexpected Worker or RAR encoder workspace remains after bounded shutdown.

Record Windows build, account privilege, exact package/installer hash, assigned extensions, format results,
WinRAR version/context-menu result, reversal result, and any policy-disabled controls. One machine/account
does not close supported-Windows, managed-policy, upgrade, or uninstall matrices.

## Official Windows basis

- [Windows app defaults platform](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/default-apps-platform)
  requires actual default changes to occur through Windows system UI.
- [Launch the Default Apps settings page](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-default-apps-settings)
  documents `registeredAppUser` for names under `HKCU\Software\RegisteredApplications`.
- [File Types](https://learn.microsoft.com/en-us/windows/win32/shell/fa-file-types) documents per-user
  `HKCU\Software\Classes`, `OpenWithProgIds`, and `SHChangeNotify(SHCNE_ASSOCCHANGED)`.
- [File type and URI associations model](https://learn.microsoft.com/en-us/windows/compatibility/file-type-and-protocol-associations-model)
  states that applications must not programmatically replace the user's chosen default.

## Deferred Phase 7 work

- installer-owned register/repair/unregister lifecycle and exact uninstall cleanup;
- upgrade/rollback behavior when executable paths or ProgID versions change;
- explicit Open/Extract/Test/Create ordinary shell verbs and long/quoted-path matrices;
- direct icon behavior across every registered type and Windows shell-cache refresh;
- standard/elevated/managed-policy and supported-Windows-version matrices;
- absent/moved executable fallback and orphan-registration cleanup;
- qualifying-installed-WinRAR GUI/interoperability and supported-version upgrade/rollback matrices;
- any modern shell command integration, only if ordinary registration cannot meet the need;
- signing, publication, updater integration, and release acceptance.

No invasive shell extension or persistent background service is justified by M7-D.
