# RAR long-path regression repair — 2026-09-05

Rain Explorer **2.0.0**, protocol **1.24**. This repairs the RAR preparation failure reported after
67.6 MiB. It does not change archive limits, compression presets, source selection, or write authority.

## Cause and fix

The performance repair introduced per-component native directory creation/pinning. Unlike managed
filesystem calls, the unprefixed `CreateDirectoryW`/`CreateFileW` calls depend on the executable's
long-path manifest opt-in as well as the Windows setting. The installed Worker lacked that manifest
element. A generated deep-tree regression failed in the real Worker with `filesystem.native_failure`;
the same operation inside the long-path-aware testhost did not reveal the defect.

Every native workspace directory/owner open and directory creation now uses an extended-length
representation of the already canonical internal path. This does not accept additional source paths,
follow reparses, remove directory pins, or alter the workspace-owner proof. Drive and UNC spelling
are converted separately. No manifest or Windows registry setting is changed.

Native preparation failures have fixed `rar.workspace_path_too_long` and `rar.workspace_io_failure`
codes. Permission errors retain their existing classification. Explorer recognizes only these exact
creation error codes and renders fixed helpful text; raw provider messages, native text, paths and
secrets remain excluded. Existing enum values are retained; the two UI-only values are appended.

Unchanged: length/digest/source revalidation, verified separately installed encoder lease, RAR5
non-solid settings, per-file durable flush, bounded workspace accounting, full boundary scans,
cancellation and cleanup, staging journal, no-replace publication and original preservation.

Production changes are limited to the two WinRAR workspace partial files plus Explorer's
`ActivityOperationUpdate.cs` and `ArchiveCreationService.cs`. Regression changes are in
`WinRarMaterializationPathTests.cs`, `ManagedCreationEndToEndTests.cs`, and Explorer's
`ArchiveCreationServiceTests.cs`. No encoder, dependency, installed app, profile, registry, Git or
unrelated mitigation-diagnostic source was changed.

## Verification

- Pre-fix actual-Worker cases at two depths failed with `filesystem.native_failure`.
- Final focused RAR set: **32/32**, including both real-Worker long-path cases, Unicode paths beyond
  260/500 characters, directory pinning/release, exact object accounting, native error classification,
  cancellation, existing no-reparse refusal, cleanup/maintenance and existing RAR provider checks.
- Actual Worker creates, reopens, lists, integrity-tests and extracts the generated deep trees;
  payload bytes, original-source hash and archive hash are preserved. RAR path separators are
  normalized only in the format-aware test comparison. Empty directories remain represented.
- Full unit/WPF **536/536**, adversarial **29/29**, Explorer **336/336**. The final scoped gate is
  **933 distinct cases**, not a full integration-suite rerun.
- The **336 Explorer cases** and **two actual-Worker long-path cases** were repeated against the
  exact self-contained package below. Optional RAR tests were enabled with the verified installed
  `C:\Program Files\WinRAR\Rar.exe`, version **7.23.0**, with a valid win.rar GmbH signature.
- Locked restore, Release build with **0 warnings/errors**, scoped whitespace checks and Explorer
  `git diff --check` passed. All **18** queried solution/Explorer direct/transitive NuGet inventories
  reported no known vulnerable package at the time of the check.
- Separate finite read-only review found no actionable defect. Physical UNC materialization,
  machine-wide long-path-setting variations and manual installed GUI acceptance are not claimed.

Final TRX files are under the package's `tests` directory. `rar-focused.trx` is a retained intermediate
failure from assuming every filesystem returns native code 206 for an overlong component. The final
test separately verifies fixed native-code mapping and permits the platform's alternative refusal;
`rar-focused-final.trx` is authoritative. No production refusal was bypassed to make a test pass.

## Package

Directory: `artifacts/RainExplorer.RarLongPathFix.2.0.0.20260905.01`.

- Canonical Archives source: **165 inputs / 3,272,992 bytes**,
  `DB6BBEDA3A758E14F865C6A0BAD6CDF74091685F13B127E4E5AD1F894BD7E5F0`.
- App tree: **458 files / 251,096,673 bytes**,
  `563BA73A6B0242B91274CE03229D5D2AA0696358B6636FB012E3CF8CED5A0BCF`.
- Worker tree: **207 files / 85,190,897 bytes**,
  `E1DFF549BECB85D056384AA09C6099A17FC40EC988751EAB0F2D53F11F639C33`.
- Installer: `installer/RainExplorer-RAR-Long-Path-Fix-Setup-2.0.0.exe`, **76,158,834 bytes**,
  `8DA38DFAB5C3D8DB6D144875B948F419B000D063F7EFD2911C5463945F6B7C6B`, **unsigned**.
- App/Worker Contracts hashes match. Bundled `7z.dll`, `7za.exe`, `SharpSevenZip.dll`, `Rar.exe`
  and `WinRAR.exe`: **zero**. The installer continues to exclude PDBs.

Source and tree hashes use ordinal slash-normalized relative path, NUL, and binary SHA-256(file),
concatenated into SHA-256. The source input set is the canonical set in `CONTINUATION.md`.
The two pre-edit provider source files are retained in `before-source`.

The default RAR workspace and creation/extraction recovery roots were empty after verification.
The installed Explorer PID 50664 and its Worker PID 40884 were left running. Generated test payloads
were cleaned by their owned fixture lifecycles; real source files were not retried or changed.

Install/retry is left to the user. Earlier benchmarks remain predecessor-bound; no new timing or
whole-private-folder success claim is made. Physical network/filesystem, manual GUI/accessibility,
clean-machine, installation, signing and release acceptance remain separate. Historical artifact
paths were already absent when this repair began and were not recreated or relabelled.
