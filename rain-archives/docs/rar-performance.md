# RAR creation performance repair — 2026-09-05

The user reported cancelling RAR creation after 11 minutes at approximately 868 MB of 1.3 GB.
This is a RAR preparation-overhead repair, not a change to the compression level or archive limits.
Rain Explorer stays at **2.0.0** and the private protocol stays at **1.24**.

## Cause and implementation

`WinRarArchiveWriterProvider` scanned the entire growing encoder workspace after preparing every
directory and before copying every file. The sum of those repeated filesystem walks grows quadratically
with entry count. The separately installed WinRAR encoder runs after that materialization stage.

Each materialization now pins only the destination directory ancestry, opens components without
following reparses, validates ordinary directories, and retains non-delete-shared handles until the
output file is closed. The running object budget counts each newly created directory/file once.
Complete workspace scans still precede encoder use, follow encoder validation, and precede cleanup;
operation-boundary scans now also observe cancellation. Cleanup remains independent of cancellation.

Unchanged: caller-owned input streams, source leases/snapshots, length and SHA-256 readback validation,
per-file durable flush, verified separately installed encoder identity, RAR5 non-solid preset mapping,
guarded staging, disk reserves, recovery journal, no-replace publication, original preservation,
encryption/split/update restrictions, and all negotiated limits. This remains a same-user process
boundary, not a filesystem sandbox. No Explorer UI, settings, registry, installed app, Git, or
production dependency was changed.

Production edits are limited to `WinRarArchiveWriterProvider.cs` and the new
`WinRarEncoderWorkspace.Materialization.cs` in `src/RainArchives.Providers.WinRar`.
The original writer is retained in the task's `before-source` evidence directory.

## Verification

- Locked solution restore and Release build passed, with zero build warnings/errors.
- Scoped whitespace verification and Explorer's `git diff --check` passed. All **19** queried
  direct/transitive NuGet project inventories (16 Archives, Explorer App/tests and the non-shipping
  probe) reported no known vulnerable package in the current source on this date.
- The full solution run passed **536 unit/WPF + 29 adversarial + 389 integration = 954**.
  A subsequent added many-file cancellation regression and all five helper regressions passed,
  together with the existing 14 RAR provider tests (**20/20**). This is **955 distinct passing
  Rain Archives tests** across the full run and focused follow-up, not a claim of a single 955-test run.
- **330/330 Explorer tests** passed against the new self-contained bundled Worker. The installed
  Explorer and its private Worker were preserved; automated checks do not imply installed GUI acceptance.
- New checks cover 1,000 repeated directory preparations, exact component counting, refusal before
  creating an excess component, parent/child rename prevention while held, reverse disposal, pre-cancel,
  ordinary-file prefix refusal, outside-workspace refusal, and cancellation after 256 completed files.
  The latter proves cancellation-token identity, an untouched destination stream, and empty owned workspace.
- A separate bounded read-only code review found no remaining scoped issue. A proposed post-timestamp
  scan concern was withdrawn: the predecessor performed the same path-based writes before its scan;
  moving that check before writes does not introduce the stated race. No new sandbox guarantee is claimed.

## Reproducible timing procedure

The non-shipping `eng/RainArchives.RarPerformanceProbe` project uses the public Client against an exact
packaged Worker. It creates a new GUID-owned fixture, fixes file and directory timestamps after creation,
records a path/kind/length/timestamp/content-hash manifest, and measures request-to-authoritative-terminal
RAR creation. Creation includes Rain's original verification and publication work. Additional reopen,
listing and integrity checks, output hashing, source rehashing, and exact cleanup are outside that timing.

Both builds use the same deterministic files and **Balanced (`-m3`), RAR5, non-solid** settings through
the exact signed installed WinRAR **7.23.0**. Warmups are retained separately. The measured series uses
five sequential operations in one fresh Worker per build, following separate-process warmups. Filesystem
cache is uncontrolled; this is not a cold-cache study. Provider DLL identities are recorded alongside
the source/package and Worker identities rather than identifying the backend by its executable alone.

Raw evidence lives under `artifacts/RainArchives.RarPerformance.20260905.01`; `environment.json` records
the source/provider/probe identities, Windows/CPU/RAM, drive placement, unchanged power/scanner state,
warmup exclusions, and a retained failed probe-setup report. The reported user's private folder was not
read or used as a benchmark fixture. The initial warmups are not included in the measured statistics.

### Five-run comparison

The identical fixture has **1,000 files, 6 directories, 8,192,000 source bytes**, manifest SHA-256
`1D51EC9950901335243A5B9EBBB315D353C29CD0C1CE929B7177F5442348B6C5`.

| Build | Median creation | Minimum–maximum | Median Worker CPU time |
| --- | ---: | ---: | ---: |
| Previous creation/theme package | 83.616 s | 67.451–95.191 s | 75.469 s |
| RAR performance repair | 11.603 s | 11.218–12.305 s | 7.297 s |

This is **7.21 times faster by median**, or **86.1% less elapsed time**, for this fixed many-small-file
RAR/Balanced fixture. Peak sampled Worker working sets were 105,775,104 and 104,734,720 bytes respectively.
The encoder child CPU is not part of those Worker-only CPU figures. Five samples support a median and
range, not a meaningful tail-latency percentile.

All ten creation, reopen/list, and integrity operations completed. Every output was **385,245 bytes**
with identical SHA-256 `6F244DE686CA480050B96913EF4AB016C8E7AF932C930D02F7704474477568F9`.
The baseline report is `50e8402d6e2a4f03893c55b42f4f02b4/measurement.json`, SHA-256
`CE5789D5243B3E01BA169AA330EF197B4CA4EAD0921F127F930D69A4E0C90EC8`; the repaired report is
`4e3f65d4b23c4561903af9eea76f9e4d/measurement.json`, SHA-256
`A53EB3601EA92A036C815CEA08EFB5C8E7332E660452E16DE01040ABA1C84F94`.

The baseline harness reported a post-measurement teardown failure on six empty generated directories
carrying Windows' read-only attribute. Payload validation and all operations had already succeeded.
Only those exact empty directories were subsequently removed after shape/reparse validation; the raw
report remains unchanged. The probe's cleanup-only correction clears that bit only on verified empty
owned directories. The candidate series completed with successful automatic cleanup. Both probe
identities and this follow-up disposition are recorded in `environment.json`; no failed operation was
excluded from the timing sample and no production refusal was bypassed.

### Larger-file-count capacity check

The new package also completed **20,000 files + 101 directories, 163,840,000 bytes (156.25 MiB)**
in **249.459 seconds**, then passed Rain reopen, exact names/kinds/count listing, integrity and source
hash revalidation. Automatic cleanup succeeded with no unexpected objects. This is one functional
capacity observation, not a five-run timing comparison and not the user's 1.3 GB folder.
Fixture manifest: `F48ECBBEA3341036FCB0E0EFA9849FB97AB7C8BA1EE53601FB0977EEF1805400`.
Output: 7,703,666 bytes, SHA-256
`8CFA0F8DAE2687D0433CF415F4468086D249D8D049ECD9973698308FBBE3D154`.
Raw report: `9b8ed89b914442e491f756b5a94b3e45/measurement.json`, SHA-256
`7B98D24E0044617497767A760FC03FFC0EAA2A92AD8B20E6F4243018EBE6381C`.

Example, using a **new absent GUID leaf** under an existing evidence parent:

```powershell
dotnet build eng/RainArchives.RarPerformanceProbe/RainArchives.RarPerformanceProbe.csproj -c Release -p:BuildProjectReferences=false -p:RestoreLockedMode=true
$runRoot = Join-Path 'E:\Downloads\Rain\Code stuff\Rain Archives\artifacts\RainArchives.RarPerformance.20260905.01' ([guid]::NewGuid().ToString('N'))
& ./eng/RainArchives.RarPerformanceProbe/bin/Release/net10.0-windows/RainArchives.RarPerformanceProbe.exe --worker 'E:\Downloads\Rain\Code stuff\Rain Archives\artifacts\RainExplorer.RarPerformanceFix.2.0.0.20260905.01\app\worker\RainArchives.Worker.exe' --output $runRoot --files 1000 --bytes 8192 --runs 5 --preset Balanced
```

The probe accepts at most 20,000 files, 64 KiB per file, 256 MiB of sources, five runs and a ten-minute
bound per operation. It cancels/drains failures and preserves unexpected evidence. Cleanup checks the
exact source inventory and payload hashes before deleting only generated files and known directories.
Measurements are retained; historical artifacts and real user sources are never cleanup targets.

## Deliverable and limits

The self-contained candidate is `artifacts/RainExplorer.RarPerformanceFix.2.0.0.20260905.01`, with its
private Worker and rebuilt, unsigned installer. It has not been installed. `Verify-Package.ps1` reproduces
the canonical source identity and seals package/tree/installer identities into `verification.json`.
The predecessor creation/theme package remains immutable evidence.

The current canonical production/benchmark identity is **165 inputs / 3,271,462 bytes /
`9F605C3D91E08B2952DC633765FDCF7F93A938B7E921D2E063433FCF744BE11A`**.
The packaged Explorer EXE/DLL, Client, Contracts and archive icon match the prior package byte-for-byte.
An unrelated non-shipping `Tests/RainExplorer.MitigationDiagnostic` project appeared and continued
changing concurrently in the Explorer workspace; it was not edited, built or packaged by this task.
The sealed broad Explorer source snapshot therefore records 71 inputs / 1,745,014 bytes /
`9C2EF318C7B25C4A5538DFE01AA4686291273F2D320B9F7EE5F9E06623E34BB8`, including that momentary
diagnostic state. Do not treat later broad Explorer source hashes as proof that this immutable package
changed. Rain Archives' production fingerprint and the package byte identities remain authoritative
for this repair. The performance probe and new regression test are separately recorded, outside the
established Archives production hash.

Sealed package identities:

- App payload: **458 files / 251,093,469 bytes /
  `1A9DD3EFF6EFC2FACB3A987C954A7689E8E409FEDA4BA8058FA2B1571237FE12`**.
- Worker payload: **207 files / 85,190,233 bytes /
  `2C28BBB9830D53BA83048CE4D1B9CC749553F28DB936846B7C84FBB7B22603DE`**.
- `verification.json`: `D7C90CA45721B836515FE168AFB7B7C03EC00F3C87F63DBEE943466E223FD911`.
- Installer: `installer/RainExplorer-RAR-Performance-Fix-Setup-2.0.0.exe`, **76,153,419 bytes**,
  SHA-256 `50C8B462DAA445D09184F9888F7CB60F40DD24A7D43BFA7DB01800C7052313CB`, unsigned.
- Contracts match across App/Worker. No `7z.dll`, `7za.exe`, `SharpSevenZip.dll`, `Rar.exe` or
  `WinRAR.exe` is bundled. All predecessor package evidence is preserved.

Final readback reverified the Archives source fingerprint, full sealed App/Worker tree hashes,
installer hash and predecessor manifest. All six benchmark GUID roots contain only their retained
`measurement.json`; generated payloads and outputs are gone. Default creation/extraction recovery and
RAR encoder-workspace roots have zero items. No task probe, RAR encoder or testhost remains; the
installed Rain Explorer (PID 57952) and its installed Worker (PID 55608) were intentionally left running.

The improvement addresses many-file preparation; it does not promise the same multiplier for large
individual files, incompressible data, other presets, disks or machines. Installed UI, physical network/
filesystem, accessibility, clean-machine, signing and release acceptance remain separate. The previously
recorded stale expanded-tree statement in `SECURITY.md` is unchanged and remains a documentation gate.
The next user action is to install the new build and retry the reported RAR creation; no retry, install,
new feature phase or system change is started automatically.
