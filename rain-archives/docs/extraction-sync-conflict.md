# Extraction sync conflict — 2026-09-05

Rain Explorer **2.0.0**, protocol **1.24**. Status: runtime cause identified and misleading UI repaired;
**reliable extraction into actively synced folders remains unresolved**.

## Runtime evidence

The reported `Vencord-main.zip` is 1,182,144 bytes, SHA-256
`1A23A38DDBD7AC171604D70F4FC8437A705FF055A4D18820B4D5BB929C5CA9EB`.
It contains 773 files, 240 explicit directories and 2,931,086 decoded file bytes. Its original hash
remained unchanged. The existing failed user staging tree already contained matching payloads; the
failure occurs during final validation, not decompression.

Copied-ZIP probes using actual installed/development Workers returned
`destination.hard_link_appeared` after processing all payload bytes. Synced-folder runs were
intermittent. A generated ordinary-text fixture and a read-only before-commit probe captured real
link count 2 and both native names: one inside Rain's exact task-owned staging tree and another
under `E:\Downloads\.tmp.driveupload\<numeric-id>`, Google Drive's upload staging location.
Several files exhibited this at the same checkpoint. This is not an 8.3 alias or a guessed UI cause.
Later runs can pass after those transient upload links disappear; that is not a compatibility fix.

The runtime-first investigation kept the Worker checks intact. A copied ZIP extracted successfully
through the final exact packaged Worker to the task-owned GUID directory under
`C:\Users\aliss\AppData\Local\Temp\RainArchives.ExtractionCheck`, outside that synced E: tree.
Every file's length/SHA-256, exact directory set, absence of extra entries/reparses, and all 773
26-byte provenance streams were checked. The verified output was copied, rehashed and compared
with provenance intact at the artifact's `verified-extraction/Vencord-main` directory.

## Production change and unchanged boundaries

Explorer now carries a UI-only fixed enum from extraction error mapping into both Activity and
the bottom status message. Exact codes distinguish an existing final destination, a final
destination appearing before commit, a temporary-file hard-link conflict, other staging
validation conflicts, and unknown conflicts. The hard-link message advises choosing a folder
that is not being synced. Unknown and near-match codes never render provider text or paths.

Changed Explorer production files: `ActivityOperationUpdate.cs`, `ArchiveOperationResult.cs`,
`ArchiveLocationAdapter.cs`, `PaneView.xaml.cs`, and `ActivityService.cs`. Regressions are in
`ArchiveLocationAdapterTests.cs`. No WinRAR/context-menu changes were made for the withdrawn report.

No Archives production change remains. Temporary instrumentation was removed and both
`HandleRelativeExtractionRoot.cs` and `StagedExtractionWriter.cs` were checked byte-for-byte against
their pre-investigation copies. The entire packaged Worker tree matches the RAR long-path predecessor.
No dependency, protocol, source-file content, overwrite, retry, extraction-recovery disposition,
Google Drive setting, registry, installed app, or installed encoder changed.

The existing contract rejects extra links before commit and preserves suspicious staging. Neither
trusting a `.tmp.driveupload` path nor checking payload hashes alone replaces that contract. Retaining
one handle per possible entry also needs explicit resource accounting at the 500,000-entry default
extraction limit. A reliable interoperable staging/lifetime design remains the next engineering task;
no blind delay or automatic retry was shipped.

## Verification

- Full Explorer suite: **350/350**, zero failures/skips, including 14 added typed-message cases.
- Full Explorer repeat configured with the exact self-contained Worker: **350/350**, zero skips.
  Worker-backed cases use that payload; this is not an App-executable GUI run.
- Focused Archives extraction/recovery/runtime-stream/provenance suite: **41/41**, zero skips.
- Copied user ZIP: exact-package extraction plus full payload/directory/provenance proof described above.
- Release publish and Inno Setup compilation passed. `git diff --check` passed on the existing dirty
  Explorer worktree (line-ending notices only). A scoped formatter check reported existing indentation
  in the unrelated `ArchiveWriteOperationStatus` creation switch at `PaneView.xaml.cs:4076-4102`;
  that block was left unchanged. No all-files formatter pass is claimed.
- An optional ReadyToRun publish attempt failed with NETSDK1094 because the restored runtime pack
  was unavailable; the established non-ReadyToRun publish then passed. No publish setting changed.
- No new full Archives integration/adversarial matrix, dependency advisory inventory, installed GUI,
  keyboard/Narrator/High Contrast/DPI/clean-machine, signing, installation or release acceptance is claimed.

TRX evidence: `results/explorer-full.trx`, `results/explorer-exact-package.trx`, and
`results/extraction-baseline.trx` under the artifact below.

## Exact package

Artifact: `artifacts/RainExplorer.ExtractionFix.2.0.0.20260905.01`.

- Archives canonical source remains **165 inputs / 3,272,992 bytes**,
  `DB6BBEDA3A758E14F865C6A0BAD6CDF74091685F13B127E4E5AD1F894BD7E5F0`.
- App: **458 files / 251,100,085 bytes**,
  `3D87C1B5F7D0AB2A5C8D2671C6D0E0333CA21C9D940AE555E1385778C9F13CB1`.
- Worker: **207 files / 85,190,897 bytes**,
  `E1DFF549BECB85D056384AA09C6099A17FC40EC988751EAB0F2D53F11F639C33`.
- Installer: `installer/RainExplorer-Extraction-Diagnostics-Setup-2.0.0.exe`, **76,163,432 bytes**,
  `93BDBCA57BD5FD3B70E5E367A814FD6522953BE01162C8350201D35297CAE6FB`, **unsigned and uninstalled**.
- Explorer DLL: `28641D31E91D9B6D2FBA1215D055581932B87F48B0229BA751078886C4301BBC`.
- Coherent App/Worker Contracts: `9F3B49FAE501653170A725E538C46DA1A1166FA27481F1F28E5C5A5EEDFC2D72`.
- Worker DLL: `45BCBA56451F1AA5170B4F520986A312114C9477548544D5CE3A622C264982B7`.
- Security DLL: `065EE1A9C6D39DD4933D989F7D2910FDEEFFF72227365F06C49CEFC0B9B42740`.
- Bundled `7z.dll`, `7za.exe`, `SharpSevenZip.dll`, `Rar.exe`, and `WinRAR.exe`: **zero**.

Canonical source/tree hashes use ordinal slash-normalized relative path, NUL and binary file SHA-256,
aggregated into SHA-256. Source includes `.ico` as defined in `CONTINUATION.md`; the older milestone-1
helper's input filter omits it and is not the current identity definition.

## Preservation and remaining test residue

The installed Explorer PID **47536** and its Worker PID **8024** remained running and untouched.
Final role-specific inventory found only that installed pair, not a remaining test Worker/CLI/testhost.
The original ZIP, 773-file user staging tree, and both pre-existing user recovery journals remain.
Their journal SHA-256 values are respectively:

- `47568c0d...`: `D9713FF6C15B4FF6DE084151AA76144A703085A90072CD7A2ACA86B8E2BB997F`.
- `911c38e0...`: `37730BFF7031B5AEBAD6ADF4F4815217C576DA6AB9560255652BB85BDA733623`.

An exact-owned-fixture cleanup command was rejected by the execution policy before it ran. It was
not retried through another mechanism. **No fixture deletion or zero-residue result is claimed.**
Retained test roots are the artifact's `fixtures-b4099ff4c96441fcae9f4c2a5a6e7813` and
`alternate-7200354976af4eb7bb619fdf2c85401d`, plus
`C:\Users\aliss\AppData\Local\Temp\RainArchives.ExtractionCheck\5f1e4c8aeea342dd8e522f67f3c9175e`.
The default extraction journal inventory contains the two user records and four inactive complete
task-owned records, with no suspicious/truncated entries. Those four task records have these tokens:

- `2670250697064297ed08564766aa39121c240ae1ecb6359dec6221e5d4fbd301`
- `801ef47a8f76445b36c0b523bddbc447634886d95ef88ecd92a9fbaa647afef1`
- `9df6396fb17434ec38880e09d640e9ccc3caac75f7ad2b0d7435792752dcff43`
- `b0353c75e35a06cce158f3a56e35dcf1c1a60250d773a7f8d151e58c8db016da`

Their destinations are all under the exact task-owned fixture root and their journals were copied
and hash-checked at `owned-recovery-evidence`. No user journal or historical evidence was removed.
The obsolete temporary-hook monitoring script was removed; the standalone diagnostic probe and its
read-only inventory route remain as local evidence. It is not shipped in the App/installer.
