# Milestone 1 manual Windows acceptance

This checklist is the living Windows acceptance gate. It retains the completed disposable-volume cases alongside the physical and interactive behavior that automated seams, loopback SMB, accessibility-tree inspection, and manifest tests cannot honestly prove. Run remaining cases only with the exact package and source fingerprint named in `milestone-1.md`. Record failures as failures; do not repeat a case with relaxed limits and relabel it as a pass.

## Safety and evidence rules

- Use a disposable VM, test account, remote share, removable test volume, or purpose-created VHD/VHDX. Never fill, reformat, change ACLs on, or disconnect a volume containing user data.
- Resolve and record the exact source, destination parent, filesystem, volume identity, package path, package/source fingerprint, Windows build, scaling, and account elevation state before each case.
- Start with a destination name that does not exist. After every success, refusal, cancellation, or crash, record the final destination state, any `*.rainarchives-*.staging` sibling, and all remaining Rain Archives process IDs.
- Retain screenshots or a short screen recording for visual/assistive cases and scrubbed command/application logs for operation cases. Never record the password itself, private source URLs, or archive contents that are not test fixtures.
- A pass requires the expected terminal state, byte-identical/hash-verified output where output is expected, no unexpected overwrite, no unattributed staging residue, and no owned process after the App closes.

## Package identity

Before testing, copy the immutable CLI/worker bundle and the separately published WPF App package without modifying either. For the current gate:

- Bundle: `C:\Users\aliss\AppData\Local\Temp\RainArchives.M1FinalBundle.4651c78f811c4224b8f9e7270dbaa817`; manifest SHA-256 `40354B74DD36D728AC7C7A09961FEA7C04AD20817C084768A173087957F7403C`.
- Canonical 79-input source fingerprint: `95754497E21D10A57F99C37E8DC239DDEA20626A96A36694E558A1A763CFA859`.
- Current App package from that source: `C:\Users\aliss\AppData\Local\Temp\RainArchives.M1FinalApp.9fc813a536314c70b6d4e9156fb76a97` (607 files, 229,945,675 bytes, canonical fingerprint `F9C41D04016687F2E490FE09C43993C59DC537E1A14FCFF25959A10BA1288ED4`; 203 files in its isolated worker subtree and no `7z.dll`/`SharpSevenZip.dll`).

The App fingerprint is one SHA-256 over every ordinal-sorted, slash-normalized relative UTF-8 path, one NUL byte, and that file's raw SHA-256 bytes. Recompute it after transfer; a count or aggregate byte total alone is not package identity.

The clean-machine CLI/worker procedure is separate and remains defined in `milestone-1-clean-machine.md`. The exact bundle above passed strictly in a fresh offline Windows Sandbox; retained evidence is `C:\Users\aliss\AppData\Local\Temp\RainArchives.M1FinalCleanMachineEvidence.8cad551636ea4aa4adf3f0d5e970c7de\strict-final\evidence.json` with SHA-256 `3FA36A7C8FFB0CFA4BFADDF094B6AD0F0DFDDB99FD67CB351D5BF5B03F6764FC`. A development-machine override still never satisfies that gate.

Current status: the two disk-full transitions, destination ACL denial, non-NTFS provenance case, and actual second-machine SMB source/destination/interruption case below are complete against the exact final CLI/worker or WPF package named by each result. On 2026-08-22 local time, the product owner confirmed the exact final App package works with a contrast theme enabled and confirmed the complete manual wrong-password, correct-password, and extraction interaction. On 2026-08-23 local time, the product owner also confirmed mixed-DPI scaling works correctly and later completed ZIP selection and extraction using only the keyboard with Narrator running. Those WPF gates are accepted below in combination with the existing automated accessibility, output-integrity, secret-lifecycle, cleanup, Per-Monitor V2, and broader keyboard-control evidence. No Milestone 1 user-assisted case in this checklist remains open.

## Physical operation matrix

### True disk-full transition

**Completed 2026-08-23 UTC in a disposable Windows Sandbox VHD.** The accepted evidence root is `C:\Users\aliss\AppData\Local\Temp\RainArchives.M1FinalStorageEvidence.bac25a24d76f42aeb83ad789c06463e2`. Early exhaustion was injected after 68,882,432 decoded bytes and near-final exhaustion after 2,420,875,264 decoded bytes; each used a real 2 GiB filler on the validated 5 GiB NTFS VHD, returned `resource.insufficient_space_during_write`, committed no destination, left zero staging artifacts/processes, and records the final bundle/source identity. Result SHA-256 values are `5924BEDC57B190724F2C1CE27E1A11F5E6F79B18FA5B2A0B7C3102ADB8DED5AA` and `D2E5CE9F3FBE31FA1C5EF0D5C06E26118264FD93BAA779DE28A74CCE0C2D5AD0`.

Use only a disposable bounded volume with enough initial space to begin a multi-file extraction. Cause that disposable volume—not the development or user-data volume—to exhaust after decoded progress begins and before final commit.

Expected result: one structured low-space/write failure, no successful final destination, no writes outside the operation-owned staging tree, and checked cleanup or an explicitly attributable preserved staging tree. Repeat once with exhaustion during payload writing and once near finalization. The injected low-space unit seam is supporting evidence, not a substitute.

### Destination permission failure

**Completed 2026-08-23 UTC under the non-elevated Sandbox `WDAGUtilityAccount`.** A deny-write ACL was applied only to a fresh parent on the disposable NTFS VHD. A direct probe received `UnauthorizedAccessException`; the packaged worker returned `filesystem.permission_denied`, produced no destination/staging output, and exited cleanly. This run exposed and verified the fix for native Win32 access-denied classification. The accepted result is under the evidence root above and has SHA-256 `493D427AAC17763E411976ADD959E660A02CC581720820388614EF5E4CA41186`.

Under a standard non-elevated test account, use a disposable destination parent whose ACL deliberately denies creation to that account. Do not alter ACLs on an existing user folder.

Expected result: structured access denial before commit, no final destination, no fallback to another location, no elevation prompt, and no staging tree outside the selected parent. Restore or discard the disposable environment afterward.

### Remote SMB behavior

**Completed 2026-08-23 local time against the exact final WPF App package.** The Windows client was x64 Windows 25H2 build `26200.9168`; the remote host reported `PRETTY_NAME="CachyOS"` and Samba `4.24.6`. CachyOS is rolling-release and `/etc/os-release` reported no `VERSION_ID`, so no distro version is inferred. Windows SMB dialect inspection was unavailable to the standard account (`Get-SmbConnection` returned access denied), so the negotiated dialect remains unverified.

The success case opened `\\192.168.1.72\RainArchivesTest\source\plain.zip` and extracted through the native picker to `\\192.168.1.72\RainArchivesTest\destination`. The App reached authoritative `Extraction completed.` and committed exactly one `plain` directory. Host-side readback found no staging sibling and verified `alpha.txt` as 65 bytes / SHA-256 `42C82805FB9599A724D099683BB0AD73BA6931A5A859E5B5DD2F2BD43FB00868`, `beta.txt` as 17 bytes / SHA-256 `CF6E10CD1871B45A498F644ED8D37A6C935D89153745F5E4F3EEEC0354949A63`, and `unicode-雪.txt` as 9 bytes / SHA-256 `B550ABF52B3500720734589CE534AE1E6F5CA0575D7A8E78BA1659972C3CCE3E`.

The interruption case used a locally opened 1,073,742,020-byte stored ZIP, `C:\Users\aliss\AppData\Local\Temp\RainArchives.RemoteSmbInterruption.a4ef03df8be64a5e928ef9d9b1a1fdc4\remote-interruption.zip`, SHA-256 `EEE040125B294CD2324017EA587D54B442828877381594D92FC30CC72DC32648`, containing one 1,073,741,824-byte entry. The product owner disabled the remote CachyOS host's Wi-Fi after the App reported real extraction progress. The App remained `Running` while Windows exhausted the remote I/O timeout, then reached authoritative `Failed` with `A filesystem or archive I/O operation failed.` It never reported completion and published no final destination. After connectivity returned and the App exited, zero Rain processes remained. The remote parent retained exactly one attributable staging directory, `\\192.168.1.72\RainArchivesTest\interruption\.remote-interruption.rainarchives-a9c3a3e7e65e4a998c420678d2e93370.staging`, containing only a 77,594,624-byte partial `remote-interruption-payload.bin`. This is the expected recoverable residue when cleanup cannot reach a disconnected server; it was preserved for owner-reviewed cleanup rather than silently deleted after reconnect. The failure-state screenshot is `C:\Users\aliss\AppData\Local\Temp\codex-clipboard-4b880791-f559-47dc-a93e-390ba0c5f9f7.png`, SHA-256 `7191176C558BCFC31E4D4C620DD40BB167F234A1C79EFC0830A38C779AE4A632`.

Use an actual second machine or managed test server, not `\\localhost`. A Linux host running current Samba is valid for the Windows-client network, remote-path, commit, and interruption gate; record its distribution and Samba version, and keep Windows-hosted SMB interoperability as a separate future compatibility datapoint. Cover a remote archive source, a remote destination parent, and a run where connectivity is interrupted after real progress.

Expected result: successful cases hash-match the fixture and commit exactly once; interruption produces a structured failure or worker-loss result, never a false completion. Record which machine owns any recoverable staging output and confirm the client does not delete data whose ownership it cannot prove.

### Non-NTFS destination

**Completed 2026-08-23 UTC on a guest-local 2 GiB exFAT VHD.** The checked source carried `ZoneId=3`; extraction returned `Completed with warnings.`, produced exactly the three expected files with matching SHA-256 values, created no unsupported provenance stream, and left zero staging artifacts/processes. The accepted result is under the evidence root above and has SHA-256 `6D0DFF05292A23202388A170A3467E33B765182A8E18718127D5595C63362E1E`; the disposable-volume replacement record has SHA-256 `6CD85E8F11EDC089DDD323EEDB1CD4B20007E71C44782362FBFE5624455AA209`.

Use a disposable supported Windows-mounted filesystem without NTFS alternate data streams, such as a dedicated exFAT test volume. Include ordinary files, Unicode names representable by the target filesystem, and a source carrying valid Mark-of-the-Web provenance.

Expected result: data hashes match, unsupported provenance produces the documented structured warning rather than failing the extraction, and no reparse/link assumption is silently weakened. Clearly record any target-filesystem filename limitation as a capability/refusal result.

## WPF interaction and accessibility matrix

### High contrast

Enable a Windows High Contrast theme through Settings, then launch the packaged App fresh. Inspect archive listing, selection/focus, progress, warning/error states, password dialog, native pickers, and disabled multipart Test/Extract controls.

Expected result: text and essential icons remain perceivable; focus, selection, validation, disabled state, and progress do not depend only on subtle color; no control becomes clipped or unreachable. Restart under the ordinary theme and confirm state is not persisted incorrectly.

**Product-owner accepted, 2026-08-22 local:** with a contrast theme enabled, the product owner launched the exact final package and confirmed that the application, protected-archive interaction, and extraction behavior work. No contrast-specific usability failure was reported. This closes the manual contrast-theme gate; the statement is a product-owner observation rather than screenshot evidence and does not claim that later polished UI work is complete.

### Physical mixed DPI

Use two physical displays configured to different effective scales, covering at least 100% and 150% or 200%. Launch on each display, move the main window and password/native-picker dialogs across the boundary, and repeat while a listing or operation progress update is active.

Expected result: the process remains per-monitor aware, each top-level window adopts the destination monitor's effective DPI, text and controls rerender without blur/clipping, hit targets track visuals, and dialogs remain on a reachable monitor. The automated exact Per-Monitor V2 context assertion is a prerequisite, not this physical proof.

**Current-machine check, 2026-08-22 local:** Windows reports two attached displays (`DISPLAY1`, 2048×1152 at desktop origin `0,0`; `DISPLAY2`, 1920×1080 at origin `-1920,159`), but both currently report 96×96 effective DPI / 100% scaling. No display setting was changed. This proves that the final physical mixed-scale condition is not presently available and leaves this case open rather than relabeling same-scale movement as a pass.

**Product-owner accepted, 2026-08-23 local:** after configuring and exercising mixed-DPI scaling, the product owner reported that it works correctly. This closes the product-owner physical mixed-DPI gate when combined with the exact-package Per-Monitor V2 process/window assertion. No screenshot, exact temporary scale values, or separate dialog-by-dialog transcript was retained, so this statement records the product-owner observation and does not claim stronger evidence.

### Narrator and keyboard

With Narrator running, complete open, list navigation, search, integrity test, selected extraction, cancellation, multipart refusal, and close without a mouse.

Expected result: the named archive search field, entry grid, realized filenames, operation status, Cancel action, disabled capability state, errors/warnings, and password dialog are identifiable; focus order is stable; no archive-controlled control or bidi text is spoken as an unintended UI action. Record wording problems even when the operation succeeds.

**Automated keyboard-control sub-passes, 2026-08-22 local:** using the exact final package, the checked `plain.zip` was opened through the native picker, Unicode search reduced the listing to `unicode-雪.txt`, arrow-key navigation moved the active entry, integrity testing completed through the focused `Test` button, the accessibility tree exposed `Completed` and `Integrity test completed successfully.`, and `Alt+F4` closed the App with no remaining package window. A separate no-mouse extraction pass opened the same archive, invoked `Extract`, completed the native folder picker, and reached `Completed` / `Extraction completed.` The output contained exactly `alpha.txt` (65 bytes, SHA-256 `42C82805FB9599A724D099683BB0AD73BA6931A5A859E5B5DD2F2BD43FB00868`), `beta.txt` (17 bytes, SHA-256 `CF6E10CD1871B45A498F644ED8D37A6C935D89153745F5E4F3EEEC0354949A63`), and `unicode-雪.txt` (9 bytes, SHA-256 `B550ABF52B3500720734589CE534AE1E6F5CA0575D7A8E78BA1659972C3CCE3E`), with no reparse point or staging residue; the checked disposable output was then removed.

The exact App also opened the checked `classic-split.zip`, listed both entries, exposed `Extract` and `Test` as disabled, and skipped those disabled controls during Tab navigation. The UI did not expose an explicit multipart warning in this run, so this records capability refusal and focus behavior only, not warning wording. For cancellation, official 7-Zip Extra 26.02 generated a disposable single-volume ZIP64 containing one 4,296,015,872-byte logical entry (22,557,557 packed bytes; 22,557,773-byte archive; SHA-256 `F7B31A211E40D42B851001902847B95F8EA1988E5A6762EFDE171B6076E17F01`). After load, `Test` and then `Cancel` were focused and invoked with Tab/Return; the operation was observed in `Running` with real progress and the next terminal observation exposed `Cancelled` and `Operation cancelled.` No output or staging artifact existed, App close left zero Rain processes, and the validated disposable fixture root was removed. The helper's focused-element report lagged behind the visible focus rectangle in parts of the native picker and WPF surface, so these runs remain supporting automation evidence rather than a verbatim spoken transcript.

**Product-owner accepted, 2026-08-23 local:** with Narrator running, the product owner reported that all keyboard controls worked and completed ZIP selection and extraction using only the keyboard. Combined with the exact-package automation above for search, navigation, testing, cancellation, multipart refusal, terminal status, and close, this closes the human Narrator/keyboard usability gate. No recording or per-announcement transcript was retained, so the evidence is intentionally limited to the product-owner observation.

### Protected password entry

Using the checked AES-256 fixture, type the password manually into the packaged WPF `PasswordBox`; do not paste it into a shell, command argument, filename, or evidence note. Complete both integrity testing and extraction, then retry once with an incorrect password followed by the correct password.

Expected result: the field is focused, named `Archive password`, marked required, visually protected, and cleared after each submission; wrong-password handling is structured and secret-free; the successful output hash matches; process command lines, ordinary logs, exceptions, and retained evidence contain no password. Closing or cancelling the dialog settles its secret/request without starting an operation.

**Product-owner accepted, 2026-08-22 local:** with the contrast theme still active, the product owner manually entered an incorrect password, received the expected wrong-password result, then entered the correct password and confirmed extraction works. This closes the human interaction portion of the gate when combined with the checked AES-256/output-hash, accessibility-field, secret-lifecycle, close/cancel settlement, and cleanup automation already retained for the exact package and source. The manually chosen archive and password value were intentionally not added to this record.

## Exit record

For each case retain: case name, tester, UTC time, package/source identity, machine/OS/account state, source and disposable destination identities, expected result, observed terminal result, output/staging/process inventory, hashes where applicable, evidence filenames, and pass/fail with notes. Milestone 1 exits only when every case above and the strict clean-machine runner pass against the same accepted source/package identity, or a product owner explicitly records a deferred risk without describing it as tested.
