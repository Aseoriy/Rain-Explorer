# Dependency evidence and decision gates

Status date: 2026-09-04. “Upstream verified” below means checked against an official upstream page or exact tagged source. Local build/test evidence is stated separately and does not imply exhaustive fuzzing or release acceptance.

## Protocol 1.23 conditional WinRAR encoder decision

RAR reading remains on SharpCompress 0.50.4. RAR creation is a separate, optional Worker-only
adapter over the user's own installation of `C:\Program Files\WinRAR\Rar.exe`; no WinRAR binary,
installer, component, license key, or trial copy is distributed by Rain. RARLAB's
[official licence](https://www.rarlab.com/license.htm) permits a licensed user to create and
distribute RAR archives without an additional royalty while separately restricting copying,
redistributing, modifying, or reverse-engineering WinRAR package parts. Rain therefore invokes only
the separately installed encoder and does not treat unRAR/decoder availability as compression
authority. RARLAB's [download page](https://www.rarlab.com/download.htm) and
[release notes](https://www.rarlab.com/rarnew.htm) identify 7.23 as the current Windows release and
record security fixes after 7.20, so the production gate accepts signed x64 7.23 through 7.x and
returns `UpdateRequired` for this machine's signed 7.20 installation.

The gate accepts only an ordinary non-reparse AMD64 PE at that exact production path, successful
offline-cache Authenticode trust, signer simple name `win.rar GmbH`, and the bounded version range.
There is no `PATH`, current-directory, registry, auto-download, or copied-binary fallback. Protocol
1.23 adds distinct explicit/tree/capability messages and typed `Available`, `NotInstalled`,
`UpdateRequired`, `UntrustedBinary`, `UnsupportedPlatform`, and `RestartRequired` states; protocol 1.22 and earlier negotiate none
of them. The initial writer is unencrypted, single-volume, new-destination-only RAR5. It uses
argument-list invocation with `-cfg-`, no shell/window, closed stdin, a random private workspace,
separate file/directory manifests, bounded source materialization and free-space checks, a two-hour
timeout, process-tree cancellation, zero-exit enforcement, an encoder integrity test, and complete
SharpCompress path/kind/length/timestamp/content-digest readback before Rain's durable no-replace
commit. Native stdout/stderr is drained but never forwarded. The Client Job permits exactly the
authenticated Worker plus one encoder child and retains kill-on-close and the 2 GiB per-process
limit; grandchildren remain blocked.

The Worker re-resolves only the concrete qualified provider at capability query, admission, and after
the operation queue gate; a format-shaped injected provider cannot claim encoder authority. Creation opens
the verified executable with no write/delete sharing and retains that lease across every encoder invocation,
so removal or replacement cannot silently change the admitted binary. Installation after Worker startup is
reported as `RestartRequired` because the immutable provider registry has not adopted it yet. Removal or
replacement after startup closes the capability and is rechecked again before source/destination work.

The filesystem bridge lives at `%LocalAppData%\SailHub\Rain Archives\Rar Encoder Workspaces` under a random
`rainarchives-rar-` token. Each workspace has a durable token-bound `owner.lock`; the active Worker holds both
the directory and owner without delete sharing. Maintenance begins only after authenticated startup and runs
outside the request path, so an over-budget stale tree cannot block unrelated Worker features. One global
1,024-candidate/16,384-object/two-second budget covers the complete pass. Root ancestry, candidate directories,
and the owner claim are opened no-follow and must remain ordinary, non-reparse, one-link objects before any
cleanup. A live claim is skipped; only an inactive exact-name/exact-record tree from one complete bounded
inventory may be removed. Suspicious, unreadable, changed, or over-limit state is preserved with a fixed
warning. Encoder cancellation proves the exact process exited after kill; failure to prove containment tears
down the authenticated Worker rather than publishing a false terminal result. Workspace disposal propagates
cleanup failure after releasing owned handles. An exact-PID subprocess regression proves live preservation,
hard-kill residue, fresh-process cleanup, and preservation of an unrelated sibling. Because WinRAR and Rain
run as the same account and WinRAR reopens pathnames, this is an accidental-failure and crash-recovery boundary,
not hostile same-user filesystem containment.

Local qualification uses an isolated, signature-checked copy extracted from the official 7.23
installer solely as a test dependency. It proves Unicode files, zero-byte data, an empty directory,
all four presets, exact staged readback, source preservation, exact-PID workspace recovery, and a real protocol
1.23 Worker operation. That test binary is not copied into source or packages. A packaged
child-process run on a machine whose production path contains qualifying 7.23+, broader RAR method
interoperability, manual GUI use, clean-machine licensing, signing, and release acceptance remain
separate gates. The current machine's signed 7.20 production installation remains a typed
`UpdateRequired` state. Encryption, split volumes, SFX, update/rebuild, existing-destination replacement,
arbitrary switches, and password command-line use remain closed.

## Protocol 1.22 support diagnostics and containment dependency decision

The fixed-field support-diagnostics response adds no runtime, native, logging, telemetry, database,
serialization, UI, or package dependency. It reuses Contracts, the authenticated control pipe, the
existing operation/recovery constants, and Client validation. Its exact dependencies are
`operation-activity-v1` and `request-cancellation-v1`; minor 21 advertises neither the feature nor
its message. The schema intentionally contains only enums, integers, and booleans, including
`IsSameUserSecuritySandbox=false`, so it does not introduce a support-bundle/logging library or a
free-form text provenance problem.

The containment decision also adds no dependency: Rain retains the existing unelevated same-user
Worker, exact launched-PID authentication, and Windows one-process/2-GiB kill-on-close Job Object as
a lifecycle/crash boundary. AppContainer/brokered handles are not enabled because no maintained
broker protocol is proven compatible with the current fully qualified path authority, pinned
handles, provider stream model, private named pipe, atomic App/Worker package, and solo-maintenance
budget. This is an explicit limitation, not a claim that process isolation is a same-user filesystem
sandbox. A future different-principal broker would be a new deployment/security dependency gate.

## Protocol 1.21 independent nested recovery reopen dependency decision

Independent reopen adds no codec, native, database, logging, or UI dependency. It reuses the
protocol-1.11 nested recovery store and no-follow Security handles, SHA-256 evidence, existing
`OwnedTemporaryArchiveSource`, provider content detection, session/resource reservations, binary
secret slots, and request cancellation. The feature depends on nested-workspace recovery, nested
archive open, secret slots, source-fingerprint validation, and request cancellation; minor 20 advertises neither
the feature nor its request/response. Reopen is copy-only: a fresh exact recovered source is copied,
flushed, detected, and registered as an independent depth-zero session. It does not adopt/mutate the
recovery path, reconstruct ancestry, or require a destructive disposition journal.

## Protocol 1.20 extraction recovery dependency decision

The extraction-staging journal, local startup inventory, and explicit read-only inspection add no
runtime, native, codec, database, logging, UI, or package dependency. They use the existing Windows
handle-relative Security boundary, SHA-256, bounded JSON/control framing, authenticated Worker/Client
transport, request cancellation, no-replace extraction transaction, and fixed local recovery root.
The negotiated feature depends exactly on `new-destination-extraction`,
`source-fingerprint-validation`, and `request-cancellation-v1`; minor 19 advertises neither the
feature nor either message. No format/write capability, package composition, licence, notice, or
native-maintenance gate changes. The immutable protocol-1.18 M3 package remains historical evidence
and is not relabelled as a protocol-1.20 package.

## Protocol 1.19 operation activity dependency decision

The bounded activity inventory adds no runtime, native, codec, logging, persistence, database, or UI
dependency. It is implemented entirely in Contracts, Worker, and Client over the existing
authenticated control pipe, operation events, request cancellation, concurrent collections, and
bounded in-memory lifecycle state. The exact protocol dependencies are `operation-events-v1` and
`request-cancellation-v1`; minor 18 advertises neither the feature nor its message. The retained
protocol-1.18 artifact contains zero `7z.dll`, `7za.exe`, `SharpSevenZip.dll`, `unrar.dll`, or
`unrar.exe`. No licence, notice, package-composition, format capability, or native-maintenance gate
changes in this slice.

## .NET built-in compression

The framework is not the sole read engine: its public surface does not cover the required 7z/RAR breadth or the complete encrypted/multipart matrix. It is, however, the selected provider for protocol-1.7/1.8 unencrypted ZIP creation and protocol-1.12 new-copy ZIP rebuilding. Official .NET 10 documentation states that [`ZipArchiveMode.Create`](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchivemode?view=net-10.0) requires a writable destination and does not require seeking, [`ZipArchive.CreateAsync`](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchive.createasync?view=net-10.0) creates asynchronously over a stream, and the [`ZipArchive` stream constructor](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchive.-ctor?view=net-10.0) supports `leaveOpen`. Those properties fit Rain's caller-owned guarded staging stream and keep path/commit authority outside the codec provider. Explicit trailing-slash zero-length entries provide the required empty-directory representation without giving the provider any filesystem enumeration authority.

The BCL writer maps Rain's `Store`, `Fast`, `Balanced`, and `Maximum` presets to `NoCompression`, `Fastest`, `Optimal`, and `SmallestSize`. ZIP timestamps use DOS local-time semantics; official [`ZipArchiveEntry.LastWriteTime`](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchiveentry.lastwritetime?view=net-10.0) documentation records the 1980-01-01 through 2107-12-31 range and two-second resolution, so Rain converts source UTC timestamps to local time and clamps out-of-range values with one structured warning. This choice does not provide AES, filename encryption, split output, 7z/RAR, or update-in-place capabilities.

Local source evidence covers all four presets, empty output, Unicode and zero-byte files, exact source-length enforcement, cancellation after authoritative `Running`, source replacement, destination races, provider failure, timestamp conversion/clamping, cleanup, BCL byte readback, and reopen/list/integrity through Rain's managed reader. A retained proof at `C:\Users\aliss\AppData\Local\Temp\RainArchives.ZipExternalProof.1ff5b5faf8fb4a73be3efc824ed5bb99` ran the corrected production CLI/worker creation route for every preset, then used official 7-Zip Extra 26.02 `x64/7za.exe` (SHA-256 `35D4D69D7CD6CB44558F208C3B1334268013F9DAF82D2DDA848893A1C30C59C2`) to test and extract each archive. Root, nested, and Unicode entry paths extracted with source-identical SHA-256 values. Output hashes are Store `F7D29F56F997B05F8D544CB5816DADE1947614793D0F5FEC95D3494DF2D7A059`, Fast `7D4E2E7169CF73AAD9007DEBB050081D629B052A960801B1A3A0132740EA689F`, Balanced `6480A2EE1A868B706B2C166C2073880C977477C23DC7CE0E6E591AFE7DA1B21B`, and Maximum `E9679C197615D30E1E380AEEB6E547537E854DD6BADB60E5B1444611EA08F018`. A second proof at `C:\Users\aliss\AppData\Local\Temp\RainArchives.ZipLoopbackSmbProof.13cd2278a3e84fad8b663f6d8be8c16f` reads its source and commits its destination through `\\localhost\C$`; official 7-Zip accepts the 449-byte output (SHA-256 `803B8755E50B65C59259DD967AC6C1D6A4A2BDA41F47390F6F488AAA5A124366`). Loopback exercises the Windows redirector but is not actual remote-server or interruption evidence. These are retained exact-source-snapshot datasets predating creation journaling, not current-source, broad multi-tool, or package acceptance. The slice adds no third-party runtime package and therefore no new third-party license or notice obligation. A five-run-per-route/preset local creation-operation baseline is retained and detailed in `docs/benchmarks.md`; every exact measured output is byte-verified through Rain and accepted by a hash-pinned owned copy of official 7-Zip, so it is externally qualified for that deterministic corpus but remains neither a current-source result, competitor comparison, nor broad compatibility evidence.

Protocol-1.8 source-tree planning relies on Windows handle APIs rather than .NET path recursion. Official Microsoft documentation defines [`GetFileInformationByHandleEx`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex), its directory information classes, and the [`FILE_ID_BOTH_DIR_INFO`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_both_dir_info) records returned for handle-anchored enumeration. Rain opens every child relative to its pinned parent, rejects reparse attributes, and compares a nonzero enumerated file ID with the reopened handle identity. An unavailable/zero enumeration ID cannot provide that one binding and remains a filesystem compatibility gate; reopened-handle snapshots and final exact-manifest revalidation still apply.

Local protocol-1.8 evidence covers mixed explicit-file/directory inputs, root and empty-directory preservation, deterministic order independent of request order, Unicode, exact progress, the 4,096-entry boundary, metadata/depth/path limits, same/overlapping roots, source/destination overlap, archive-path collisions, nested reparses, child file-ID substitution, source-tree mutation both during copy and after provider finalization, cancellation, provider failure, cleanup, downgrade refusal, CLI routing, and same-worker recovery. A retained production CLI/worker proof at `C:\Users\aliss\AppData\Local\Temp\RainArchives.ZipTreeExternalProof.31b230c5112844f8bc8708f0e063154b` produced a 605-byte Maximum ZIP with SHA-256 `30F0691747243344A93363341AC82B975F8533BDC69151E879B883DB0D487669`; official 7-Zip Extra 26.02 listed three directories and two files, passed integrity testing, preserved the empty directory, and extracted the tree file (`81400C7DB70124172D2B7D3DB922021DBC1F20D7E35A33AD4326A2077E7CF671`) plus explicit file (`BE76591527EC39E4BA632A4AAA2BB4357002049CCF7370DB9EAEA5B012879408`) with matching SHA-256 values. The exact Windows `C:\Windows\System32\tar.exe` used as a second independent reader reports `bsdtar 3.8.4 - libarchive 3.8.4`, is 92,176 bytes with SHA-256 `9B77D4C912F2EDAE8C241D0ECE1094D2AC068B084269CEAF85D7C7B085D2AE86`, lists the same five material paths, preserves `assets/empty/`, and extracts both regular files with those same hashes. A loopback UNC proof at `C:\Users\aliss\AppData\Local\Temp\RainArchives.ZipTreeLoopbackSmbProof.2208f165abc44c149c2966b0cd4ae857` read the source tree and committed through `\\localhost\C$`; official 7-Zip accepted the 403-byte output with SHA-256 `C4889F79A39899BF44041ECB8A4EB91CD0BC448117CBC224CCB8DB485DA99ABB`, and the extracted source hash matched `1D83A0830862CB8724A23E850408B726B4C8725FE690589C6716A717532CB3D5`. Loopback is not actual second-machine SMB evidence.

Protocol-1.12 Rebuild Copy uses the same BCL writer only after Rain's reader provider has checksum-validated each retained entry into a caller-supplied producer stream. The provider never receives arbitrary source or destination paths. One closed plan can delete exact session-bound persistent keys and map pinned ordinary single-link files to add/replace targets; every retained and mapped file is recompressed with the selected preset into a distinct absent destination. Rain retains one deny-write/delete lease per unique mapped source through final validation and commit, then reopens the path and rechecks identity. After durable flush, it reads the staged ZIP through the exact staging handle and compares the complete material manifest before publication; a corrupt, unreadable, or mismatching provider output is removed without publishing a destination. Local source evidence covers mixed edits, directories, empty/zero-byte/Unicode output, all presets, deterministic ordering, exact path/collision and aggregate resource policy, encrypted/multipart/special-entry refusal, misleading declared sizes, expansion limits, mapped-source and opened-archive mutation, admission/copy/final-validation cancellation, output ceiling, runtime low-space transition, destination race, exact-PID Worker death, same-worker recovery, CLI and WPF-model routes, original SHA-256 preservation, and zero ordinary-failure residue. A test-owned copy of official 7-Zip Extra 26.02 `x64/7za.exe`, revalidated at 1,334,784 bytes and SHA-256 `35D4D69D7CD6CB44558F208C3B1334268013F9DAF82D2DDA848893A1C30C59C2`, independently tests and extracts representative mixed Store/Fast/Balanced/Maximum outputs; all expected retained/replaced/added paths and file hashes match and deleted paths remain absent. This qualifies one deterministic corpus only and adds no shipping dependency or notice obligation.

The current recovery layer adds no codec dependency. Before destination staging, it writes an exclusive local journal containing at most five append-only, 256-KiB-bounded, SHA-256-checked full-state frames. Automated evidence covers every legal phase, transition refusal, immutable identities, checksum corruption, a torn tail, unknown content, a 1,024-entry inventory ceiling, local-root enforcement, exact cleanup, and non-destructive preservation. A real launched worker killed after partial ZIP progress leaves no final/staging output but retains the real operation ID, destination, staging identity, and `Writing` phase; a second worker reports only the count and does not contact or delete the destination. SHA-256 is corruption detection, not same-user authentication, and no startup inventory record authorizes cleanup.

## Managed Milestone 3 writers (protocol 1.17/1.18)

.NET 10 `System.Formats.Tar.TarWriter` is the selected TAR encoder. Rain passes only normalized
archive-relative names, `RegularFile`/`Directory` entries, exact lengths, UTC modification times,
and already-open source streams. Windows attributes and pre-Unix timestamps are rejected because
they are not part of the qualified TAR metadata contract. The writer is finalized once while the
caller-owned guarded destination remains open. Rain then parses the complete staged TAR, checks its
footer and exact path/kind/length/time manifest, and compares each decoded regular-file SHA-256 with
the digest collected during the authoritative source copy. Conservative space/output estimation is
format-specific: the ZIP formula is unchanged, while TAR charges 512-byte headers, payload padding,
two EOF blocks, and bounded PAX/extended-header reserve.

.NET 10 `GZipStream` supplies TAR.GZ and raw GZIP compression. GZIP-backed creation supports
Store/Fast/Balanced/Maximum, explicitly completes the outer stream, and validates the reopened
trailer and full decoded payload before publication. SharpCompress 0.50.4 `BZip2Stream.CreateAsync`
plus `FinishAsync` supplies TAR.BZ2 and raw BZIP2 at the one qualified Balanced preset. Its validator
opens one member only, requires the decoder to consume the complete staged archive, checks the
canonical footer, and rejects trailing bytes or appended valid members. These routes add no package:
SharpCompress was already the pinned MIT-licensed runtime provider and the BCL writers are framework
components.

Raw GZIP/BZIP2 is a distinct one-source contract, not a multi-entry archive API: exactly one ordinary
regular source file, no directory or additional file, a normalized synthetic member name derived
from the destination stem, one-entry/64-GiB bounds, and the same Security-owned journal/staging/
readback/source-revalidation/no-replace publication. TAR-family explicit/tree creation retains the
4,096-entry/64-GiB bounds. Provider code receives no source or destination filesystem paths.

Current deterministic evidence covers provider length/finalization/cancellation failure, same-length
payload corruption, unsupported kinds/metadata, TAR boundary/PAX estimation, source mutation,
destination race, low-space and recovery seams, exact cleanup, Rain reopen/list/test/extract/hash,
and hash-pinned official 7-Zip Extra 26.02 test/extraction for TAR.GZ, TAR.BZ2, GZIP, and BZIP2.
Uncompressed TAR also passes its qualified external TAR reader path. The retained self-contained
package `artifacts/RainArchives.M3AutomatedPackage.1.18.1C85F6D9` repeats ZIP plus all five new
managed formats collectively: direct Client creation covers TAR/TAR.GZ/TAR.BZ2/GZIP/BZIP2, the
in-process CLI covers ZIP/TAR.GZ/BZIP2, and representative WPF-model creation covers ZIP/TAR.GZ/GZIP.

The retained protocol-1.18 package has a 75-sample mixed-format benchmark with 90 hash-pinned 7-Zip checks. Its package-bound physical tranche adds a fresh offline Windows Sandbox all-format run, typed non-elevated ACL
denial, two actual NTFS exhaustion points, all 15 supported exFAT preset/provenance cases, and real
local 8.3/`subst`/alternate-loopback-UNC probes. These results qualify only their named local paths and
identities; they add no runtime package and do not stand in for Recovery Center interaction,
actual second-machine SMB, blocked UNC, DFS/no-file-ID, product-owner accessibility, or release gates.
The product owner directly observed exact-package ZIP creation/reopen/integrity and Rebuild Copy with
the expected warning; their submitted placeholder staging query proves no cleanup state. A separately hash-bound installed GNU suite (tar 1.35, gzip 1.14, bzip2 1.0.8) lists/
extracts TAR/TAR.GZ/TAR.BZ2 and integrity-tests/decompresses raw GZIP/BZIP2 from the retained package;
WinRAR 7.20 independently integrity-tests/extracts all six outputs. Distinct `bsdtar`/libarchive 3.8.9
also lists/extracts the simple containers and three exact-package PAX layouts. Combined with pinned
official 7-Zip and Windows libarchive 3.8.4, this closes the bounded M3 external-reader matrix. Every
payload hash matches, the archives remain immutable, and every exact launched external process
settles. The staged PAX timestamp is exact while SharpCompress's public descriptor may differ by one
100 ns tick. A fixed-timestamp repeat differs only in BCL-generated
`./PaxHeaders.<process-id>/` helper names/checksums, so material order/content/metadata are deterministic
but PAX container bytes are not reproducible across Worker PIDs. Those installed external executables
are evidence tools only and add no shipping
dependency or notice obligation; open-ended version/method/layout breadth remains M8 release work.

No approved current dependency can write XZ: SharpCompress 0.50.4 reports XZ decompression only and
`SupportsCompression=false`. BCL ZIP and SharpCompress writer APIs expose no qualified ZIP content
or filename encryption. TAR.XZ/raw XZ and ZIP encryption therefore remain disabled unless a new
managed/native dependency passes a separate source, license, package, security, cancellation,
interoperability, and maintenance gate. Decoder availability never authorizes RAR encoding.

## Windows download provenance

Microsoft's [`File Streams`](https://learn.microsoft.com/en-us/windows/win32/fileio/file-streams) documentation defines named streams as separate streams associated with a file, and the official [filesystem functionality comparison](https://learn.microsoft.com/en-us/windows/win32/fileio/filesystem-functionality-comparison) distinguishes filesystems that support named streams. Microsoft also states that [`IAttachmentExecute::Save`](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iattachmentexecute-save) may invoke virus scanners or other trust services which can alter or delete the target and may attach evidence in an NTFS ADS. Rain therefore does not select `IAttachmentExecute` for deterministic engine-level propagation to a staged archive. The implemented creation path uses Rain's handle-relative direct canonical ADS writer only after unanimous source-zone validation and final state revalidation. Local NTFS evidence proves precreation, delete-pending retention, durable write/readback, close, same-volume rename preservation, and ordinary/hard-kill cleanup. Current exFAT evidence proves every supported creation preset commits without ADS, reports `provenance.not_applied`, preserves the source/provenance stream, and reopens/extracts to the exact payload hash. This is an engineering policy, not a claim that direct ADS writing is equivalent to Attachment Execution Services. Actual SMB behavior, the pre-disposition kill window, partial-write injection, and post-rename ambiguity remain gates. These Windows APIs add no third-party redistribution or license obligation.

## SharpCompress 0.50.4

The exact `0.50.4` tag resolves to commit `c083c6efd843a844b0c8f7878787360e815be781`; source-level claims in this section were checked against that revision rather than a moving branch.

- Verified: exact tagged source exists at <https://github.com/adamhathcock/sharpcompress/tree/0.50.4> and carries the MIT license copied into `licenses/SharpCompress-LICENSE.txt`.
- Verified from exact tagged source: archive entry `WriteToAsync` accepts `ExtractionOptions` and wraps the decoded stream with checksum validation when `CheckCrc` is true. A raw `OpenEntryStreamAsync` does not by itself apply that optional wrapper. Rain Archives therefore uses provider copy for extraction/test and reserves raw streams for preview.
- Verified from the restored 0.50.4 API/source: provider translation uses SharpCompress `ArchiveException`, `ArchiveOperationException`, and `InvalidFormatException`, plus the framework `CryptographicException`; the version does not expose the exception names assumed by the original draft.
- Verified from exact tagged source and local API use: sequential TAR/compressed-TAR and raw GZIP reads use `ReaderFactory.OpenAsync` and `MoveToNextEntryAsync`; the reader copy path does not add the optional raw-GZIP checksum wrapper used by archive-entry extraction. Rain therefore computes raw GZIP CRC32 plus decoded size and compares both with the trailer. Before the first integrity/extraction copy from TAR.GZ, TAR.BZ2, or TAR.XZ, Rain performs one bounded full validation pass over the outer compression stream and caches only a successful result for that source-locked session. Raw BZIP2/XZ use the restored `CompressionProviderRegistry` decoder seam. Ascending sequential copies retain one forward reader; backward access, cancellation, destination failure, or decoder failure resets it. Initial indexing and retained-reader scans enforce a 64 GiB aggregate decoded-work ceiling, avoiding the previous reopen-and-rescan O(n²) behavior for provider-order operations.
- Verified: the public project documents managed ZIP/7z/RAR/TAR and compression-stream reading, async APIs, and writer support. Those are upstream claims, not local conformance evidence.
- Risk evidence: upstream issue #1270 documented decompression-bomb/crash cases in 0.47.3 and then-current source; advisory GHSA-6c8g-7p36-r338 documents traversal through upstream directory extraction helpers in versions through 0.47.4. Rain Archives pins a later version, never calls provider directory extraction, runs parsers out of process, and enforces its own runtime output guard. Exact 0.50.4 regression fixtures and a deterministic 5,000-case malformed-archive run now cover the known architectural paths, but coverage-guided parser testing is still required.
- Local evidence: the locked package restored, compiled, and passed real-worker conformance for extensionless/ordinary/AES-256/ZipCrypto/corrupt/truncated/split ZIP, including encrypted extraction, a 65,536-entry ZIP64 selected-last-entry test, and a runtime-generated sparse ZIP64 whose central directory is beyond 4 GiB; solid, content-encrypted, header-encrypted, and split-list-only 7z; solid, header-encrypted, truncated, and split-list-only RAR, including checked solid and encrypted extraction; TAR/TAR.GZ/TAR.BZ2/TAR.XZ; and raw GZIP/BZIP2/XZ. The sparse ZIP64 lists/tests/extracts through the real worker and was independently accepted by official 7-Zip Extra 26.02. Checked split ZIP/7z/RAR sets list, while all multipart integrity/extraction is intentionally refused with no output until every volume can be leased, fingerprinted, and validated. Deliberate raw-GZIP CRC corruption and outer TAR.GZ/TAR.BZ2/TAR.XZ checksum/footer corruption fail integrity and extraction without final or staging output; valid controls still pass. Reader reuse/reset tests cover forward order, backward order, destination failure, cancellation, and disposal. Ten deterministic truncation/bit-flip cases run by default across ZIP, 7z, RAR, raw GZIP, and TAR.GZ; an opt-in 500-round run completed 5,000 cases while checking worker health after every case. Focused regressions prove malformed ZIP disk references and overflowing RAR variable integers return structured `InvalidArchive` results and leave the worker usable. Missing final ZIP/7z/RAR volumes fail closed. Interchanged middle volumes can expose metadata with the current library, so list-only policy remains authoritative. Wrong/missing-password and corrupt/truncated failures use structured, secret-free errors. A 200,000-entry queued-open test proves negotiated request cancellation, bounded completion, secret disposal, session-slot recovery, and continued worker health. Separate real-worker tests suspend the exact owned worker to prove bounded cancel-response and worker-close teardown instead of indefinite waits. Direct WorkerHost protocol tests each run 32 early failed password-bearing Opens or pre-handoff operations without client cleanup and then prove fresh secret-slot capacity; a real queued-operation cancellation and a pre-write client cancellation also prove settlement. The retained package evidence additionally lists, integrity-tests, extracts, and handle-relatively commits through loopback UNC source/destination paths using `GetDiskFreeSpaceExW`, with checked output hashes and zero staging residue.
- Fresh package audit on 2026-08-26: `dotnet package list --project RainArchives.slnx --vulnerable --include-transitive --no-restore` queried nuget.org after a successful locked restore and reported no known vulnerable direct or transitive package for all 15 solution projects. This feed result is time-bound and does not replace parser threat modeling, malformed-input tests, or future release-day audit.
- Protocol-1.5 preview evidence: real-worker tests compare decoded hashes for checked ZIP, solid/content-encrypted/header-encrypted 7z, ordinary/header-encrypted non-solid RAR, TAR/compressed-TAR, GZIP, BZIP2, and XZ fixtures. SharpCompress can defer password/decode failure until the first stream read, so Rain now translates those read-time exceptions through the same stable secret-free error model as provider open. Solid RAR remains an explicit exception: upstream documents its sequential-reader requirement, and [open upstream PR #1364](https://github.com/adamhathcock/sharpcompress/pull/1364) tracks single-entry extraction failures for solid RAR. The checked 0.50.4 direct-entry stream fails, while a safe sequential workaround would need a separate aggregate skipped-work budget that the entry-stream contract does not expose. Rain therefore withholds `OpenEntryStream` for solid RAR but retains checked integrity testing and extraction; the opened descriptor emits `archive.preview_unavailable` through the existing warning shape. This is a capability gate, not a claim that RAR preview is technically impossible. Seven additional real-worker tests now prove the client-side seekable bridge over that same complete-entry lease: exact seek/read bytes, read-only and path-hidden publication, lease/byte admission recovery, changed-directory refusal, cache disposal, and cancellation cleanup with continued worker health. No new codec or protocol dependency was added.
- Protocol-1.6/1.11 nested evidence: nested materialization deliberately uses the provider's checksum-validating `CopyEntryToAsync` path rather than raw `OpenEntryStreamAsync`, then redetects the immutable temporary child from content. Real-worker tests cover three nested ZIP levels, depth refusal, header-encrypted nested 7z wrong/correct password recovery, malformed-child recovery, provenance inheritance, provider-copy cancellation, declared and actual byte limits, global active-byte admission, exact close/shutdown cleanup, and solid-RAR refusal before decode. A later reliability slice places those children under one random token-claimed worker workspace, proves exact source-free/source-owning cleanup, inventories at most 1,024 matching startup candidates without deletion, and emits count-only/path-free warnings for token-verified stale or suspicious state. Its packaged-worker test hard-kills only the exact launched PID after nested materialization in an isolated temporary root, verifies the owner claim is unlocked and the child archive is byte-identical, and proves a second worker reports without deletion. Protocol 1.11 adds path-free list/inspect and explicit exact-revision/count/byte discard for only the verified inactive ordinary shape. Unit, adversarial, in-process protocol, real packaged-Worker, and exact-PID post-first-deletion crash tests cover active/change/extra/ADS/reparse/hard-link/count/size refusal, exact replay, pre-mutation cancellation, and conservative preservation of a partial tree after restart. This validates the selected managed paths only. It does not prove every encoder/method combination, bound skipped decode work for every solid implementation, authenticate the metadata revision against a malicious same-user process, authorize automatic stale-workspace cleanup, or provide nested restore/reopen.
- Protocol-1.11 standalone-consumer evidence: the WPF Recovery Center now adds a separate path-free nested-workspace tab over the existing neutral Client contract. Focused model tests cover exact-evidence authority, default-No refusal, downgrade, cancellation/drain, selection/refresh invalidation, and failure without retry; STA rendering covers bindings and accessibility metadata; and a real packaged production Worker test drives the view model through list, inspect, exact-evidence confirmation, discard, and empty refresh. Direct predecessor-package interaction opens both recovery tabs and observes empty state. Product-owner direct creation/rebuild observation is now complete, but a direct current Recovery Center observation and product-owner Narrator/keyboard/contrast/mixed-DPI acceptance of the current creation flows remain open.
- Protocol-1.21/1.22 lifecycle evidence adds no third-party dependency. Unit, adversarial, real Worker/
  Client/CLI, golden-frame, downgrade, retained-binary, mixed-package, and self-contained package tests
  prove exact-evidence copy-only recovered-source reopen as a depth-zero root plus fixed-field support
  diagnostics. An exact-PID kill after the copied source is flushed proves unchanged original hash/
  review evidence, path-free restart warning, preserved copied residue, and successful fresh ordinary-
  Worker inspect/reopen/list. The 773-test gate and a separate immutable six-surface security scan pass.
  This does not authorize automatic nested cleanup, extraction disposition, restored former ancestry,
  typed decision continuations, retry, AppContainer/broker deployment, or release acceptance.
- ZIP64 provider-boundary evidence: a standards-valid per-entry size/offset-extra fixture lists, integrity-tests, and extracts through the worker. A separate standards-valid disk-start-sentinel fixture with disk number zero is accepted by .NET `ZipArchive` and official 7-Zip Extra 26.02, but SharpCompress 0.50.4 fails internally while opening it. Rain therefore returns `UnsupportedFormat` / `archive.unsupported_zip64_disk_start` for that exact representation instead of misreporting corruption or surfacing the provider exception. Nonzero disk references remain `InvalidArchive`, and missing/truncated ZIP64 extras remain `archive.invalid_zip_central_directory`. The retained disk-start proof is `C:\Users\aliss\AppData\Local\Temp\RainArchives.Zip64DiskOnlyProof.312c468918fc49ad84d219136dc6c14e\zip64-disk-only.zip`, SHA-256 `2EBE6B016BA482108B27588CF26D70677DBE4769C894A575D180C522604EA4C8`.
- Numeric multipart evidence: one integration case splits the checked ZIP bytes deterministically into 13 adjacent volumes named `.001` through `.013`. SharpCompress 0.50.4 lists the complete set across the `.009`/`.010` boundary; Rain exposes only `List | ReadEncrypted | ReadVolumes`, refuses integrity/extraction without output, rejects the set when `.013` is absent, and successfully reopens the complete set in the same worker afterward.
- Classic multipart evidence: the checked `.z01` through terminal `.zip` corpus is generated independently by the [DotNetZip 2025 v2025.02.15 upstream release](https://github.com/DinoChiesa/dotnetzip-2025/releases/tag/v2025.02.15), version 2025.2.3.0 at commit [`866343fa8508d120ebc4cb0f3a6cfe95b7e6987c`](https://github.com/DinoChiesa/dotnetzip-2025/tree/866343fa8508d120ebc4cb0f3a6cfe95b7e6987c), then tested by official 7-Zip. Rain lists it when opened from either `.z01` or the terminal `.zip`, exposes the same exact list-only capabilities, rejects fake extension-only ZIP input, fails closed when the terminal or a middle volume is absent, and recovers in the same worker. DotNetZip remains fixture-only tooling under its checked Apache-2.0 license; it is not a product dependency.
- Independent multi-disk ZIP64 evidence: official 7-Zip Extra 26.02 generated 22 numeric volumes totaling 22,557,773 physical bytes from a deterministic sparse 4,296,015,872-byte source entry. Official `7za l -slt` reports `Zip64 NTFS`, and `7za t` completes successfully. Rain's final packaged CLI lists the same one-entry set through the real worker with the exact 4,296,015,872-byte decoded size, then refuses integrity/extraction as `archive.multipart_operation_deferred` with no destination or staging output. The retained proof is `C:\Users\aliss\AppData\Local\Temp\RainArchives.NaturalZip64Proof.76a99f64bccd4800991f30a33b6b7a33`; its 22-volume manifest SHA-256 is `F8F77D697BEAA833A4605D936F6FD96CCCCE06C1D57F306403CEFEE26FE1B5FB`. A separate forced-DotNetZip ZIP64 attempt produced headers that official 7-Zip rejected and is explicitly not counted as acceptance evidence.
- Protocol/provider volume identity evidence: protocol 1.4 carries negotiated `Single`, `ConfirmedMultipart`, or `Unknown` state while retaining the legacy boolean only as a coherent projection. The worker omits the field under a runtime minor-3 negotiation, rejects incoherent provider capabilities, and refuses unknown operations before admission. The current client requires the field only when negotiated and independently enforces list-only unknown/multipart state. Checked ZIP, 7z, RAR, TAR/compressed-TAR, GZIP, BZIP2, and XZ corpus runs assert exact identities; malformed ZIP terminal states classify as unknown at the bounded detector and are rejected by preflight before a production session opens.
- Still unverified locally: hard interruption while SharpCompress is inside synchronous parser work, synchronous bounded ZIP-tail reads on hostile network filesystems, sustained coverage-guided fuzzing, compressed/encrypted/solid physical multi-gigabyte variants beyond the checked stored-ZIP case, broader ZIP64 layouts, managed support for the valid disk-start-sentinel layout, provider-accepted multipart naming/tool variants beyond the checked sets, retained binaries/golden frames for the older v1.4-v1.11 history, bounded solid-RAR preview/nested skipped-work handling, true wire-range reads, path-based external-preview provenance/lifetime behavior, stable reconstruction of a former multi-level nested ancestry, automatic nested cleanup, and release-scale solid random-selection performance. Protocol 1.21 already supplies copy-only independent reopen of one exact recovered source; the partial-discard decision intentionally preserves uncertain state and adds no unsafe durable completion inference. Creation-specific open gates are physical blocked-UNC cancellation, actual second-machine SMB destination/interruption/provenance, DFS and filesystems or servers without usable IDs or ADS. A final audit found no current single-output staging/commit/recovery transition lacking an exact-PID or deterministic checkpoint proof; any newly introduced unqualified transition reopens that gate. Retained-package true NTFS disk-full, standard-user ACL, exFAT provenance, clean-machine startup, real local 8.3/mapped-drive/alternate-loopback-UNC paths, the bounded official-7-Zip/Windows-libarchive/GNU/WinRAR/libarchive-3.8.9 simple and PAX corpora, and externally qualified mixed-corpus performance are separately accepted; the exact staged PAX timestamp is preserved while SharpCompress's public descriptor may differ by one 100 ns tick. Product-owner direct creation/reopen/integrity and Rebuild Copy observation now passes; direct current-package Recovery Center/support-diagnostics GUI and accessibility, remote/device, installer, signing, and release gates remain open. Encryption, XZ creation, 7z creation, split output, and existing-destination replacement remain closed dependency/product/transaction gates rather than unimplemented advertised capabilities.

## SharpSevenZip 2.0.109 candidate

- Dependency state verified: `Directory.Packages.props` records the audited candidate version, but no project has a `PackageReference` to SharpSevenZip. Its managed assembly and bundled native files are therefore absent from restore, build, publish, and runtime output.
- Package provenance verified: the signed NuGet package is 4,648,441 bytes with SHA-256 `F2B3BF16138D814FD03D8F706E6C77C163E4B31B7E29A21FF1FB2399C7776262`. `dotnet nuget verify --all` accepted its NuGet.org repository signature. Its `.nuspec` identifies exact source commit [`105964f1c060481a17d20d084aacaecace92ffab`](https://github.com/JeremyAnsel/SharpSevenZip/tree/105964f1c060481a17d20d084aacaecace92ffab), and the net8.0 assembly product version embeds that same commit. The downloaded exact-commit source ZIP is 4,163,122 bytes with SHA-256 `D437ED0085AD63E72BC1CBD9AA5DF8EA4E2229CB2CD125CDD578D4252C2E625C`.
- Package contents verified: the runtime assets are the net48, net8.0, and netstandard2.0 managed assemblies/XML documentation, an MSBuild target, `LICENSE`, `README.md`, and bundled x86/x64 `7z.dll` files. The net8.0 assembly is 1,881,088 bytes with SHA-256 `47F0CD2CF1B490ADEA5D57C15E057DE6A11E203FB430E5CBC417E6338D029E9`, version `2.0.109.0`, no strong-name public key, and only framework assembly references. The package target copies both native architectures by default; any Rain reference must set `ExcludeSevenZipAssemblies=true` and stage only Rain's separately verified x64 binary.
- Native payload verified: the bundled x64 `7z.dll` is 1,906,688 bytes, version 26.02, SHA-256 `69FD4DF057985C40E510E2FAC182881C7F85E90AA13EC703F763A8FDB2CE61F8`, and is byte-identical to `7z.dll` extracted from the official 26.02 x64 installer. The bundled x86 DLL is not a Rain shipping asset; its recorded SHA-256 is `C6989259A78960805B8B646843D2EF3A8A19E5533359E8A252AAD2F3CC78C844`.
- License evidence verified: the `.nuspec`, packaged `LICENSE`, and exact source `LICENSE` all identify LGPL-3.0-or-later; the packaged license is the complete LGPLv3 text. The source also carries complete GPLv3 and LGPLv3 texts. The README's sentence “As required by GNU GPL 3.0” is imprecise, but it does not replace those controlling package/source declarations. If adopted, Rain must keep the wrapper as a replaceable separate assembly, permit reverse engineering for debugging modifications, preserve notices, and provide the applicable LGPLv3/GPLv3 text plus corresponding wrapper source in the release compliance bundle. This is an engineering compliance interpretation, not legal advice.
- Loader behavior verified from exact source and .NET 10 execution: `SetLibraryPath` accepts a caller-selected path and the net8 build uses `NativeLibrary.Load`. A .NET 10.0.10 probe set the exact absolute official DLL path before creating any wrapper object; even with another `7z.dll` in both the current directory and the first `PATH` directory, the process loaded only the selected official path. A missing selected path failed before any 7-Zip module loaded. Rain must configure this once before concurrent provider use and must reject relative, missing, current-directory, `PATH`, registry, or user-selected alternatives in its own adapter.
- Read conformance verified on .NET 10 with official 7-Zip 26.02: listing, integrity testing, and selected-entry stream extraction passed across the checked ordinary/AES-256/ZipCrypto/split ZIP, solid/content-encrypted/header-encrypted/split 7z, and ordinary/solid/header-encrypted/split RAR corpus. This was a focused SharpSevenZip provider-to-caller extraction probe, not protocol-1.5 preview qualification. Unicode names survived all applicable formats, and the wrapper enumerated all checked split volumes. Throwing `OperationCanceledException` from a bounded output stream stopped a stored 3 GiB extraction after 4 MiB in 45.7 ms, and a follow-up archive integrity test in the same process passed.
- Write conformance verified on .NET 10: 7z creation with encrypted headers, split-volume 7z creation, integrity verification, rename, and deletion all succeeded against official 7-Zip 26.02. These are dependency probes, not exposed Rain creation capabilities.
- Correctness blocker verified: the checked truncated ZIP returned `true` from wrapper `Check()`, while official `7z.exe t` reported `Unexpected end of archive` and exit code 2 for the same bytes. Exact source shows that the native property enum contains `ErrorFlags`, `WarningFlags`, and `Warning`, but the wrapper neither maps those archive properties nor incorporates them into `Check()`. Unmodified 2.0.109 therefore cannot be Rain's authoritative integrity reader.
- Other adapter gaps verified: `ArchiveFileInfo` omits per-entry packed size; a missing password for a header-encrypted 7z can surface as an empty/failed listing rather than a typed password request; a wrong content password surfaced as a generic data error; byte progress is reduced to percentage events; progress events are not cancellable; and a stream-thrown cancellation is translated to a generic wrapper HRESULT. The adapter must use Rain-owned cancellation-aware streams and token state for cancellation translation and needs explicit password/error behavior backed by fixtures.
- Update-path restriction verified from exact source: wrapper update methods create a sibling `.~` file but unconditionally delete the input in `finally` before moving the temporary output. Rain must never invoke those methods on a user-owned archive. Any future use is limited to a complete Rain-owned staging copy followed by Rain's separately validated commit protocol.
- Decision: SharpSevenZip remains the preferred starting point for a thin official-`7z.dll` adapter, but **the unmodified 2.0.109 package is rejected as an authoritative reader**. It may be reconsidered only through a newer audited upstream release or a minimal pinned Rain fork that exposes per-entry packed size and archive warning/error flags, makes integrity fail on native open warnings, and provides deterministic password/cancellation translation. A fork would add LGPL modified-source distribution and maintenance obligations, so it requires a separate decision gate. Until then, a SharpSevenZip `PackageReference` remains absent and the native runtime gate stays off; managed reads remain authoritative and native creation remains deferred.

### Corrected-adapter decision gate

This gate is a future implementation decision, not authorization to add a package, fork, or native binary now. Prefer a newer upstream release if it closes every required gap. Approve a pinned Rain fork only if upstream does not and the native provider remains valuable after measured comparison with the managed path.

The minimum acceptable adapter delta is deliberately narrow:

1. Surface native archive `ErrorFlags`, `WarningFlags`, and warning text without discarding unknown bits; make integrity fail on every native error flag, including unexpected end, header/data/CRC errors, unsupported required features, and data-after-end unless a separately documented policy explicitly permits that case.
2. Surface nullable per-entry packed size from the native property set. Unknown must remain unknown rather than being reported as zero.
3. Convert header-open, content-decode, and password outcomes into Rain's stable error model using native status plus operation context. Never infer a wrong password solely from exception text.
4. Translate a wrapper exception into cancellation only when Rain's operation token is already signalled and the failure came from the Rain-owned bounded stream or callback path. Preserve unrelated failures.
5. Keep extraction stream-only into Rain-owned guarded output. Keep directory extraction APIs forbidden, and permit archive updates only against a complete Rain-owned staging copy.
6. Configure one normalized absolute app-private DLL path before provider concurrency. Disable the package's automatic native-copy target and fail closed if the selected binary is absent or has the wrong recorded identity.

Acceptance requires the exact truncated-ZIP regression to fail integrity as official 7-Zip does; the complete checked ZIP/7z/RAR corpus to retain listing, test, selected-stream, Unicode, encryption, solid, and multipart behavior; the 3 GiB cancellation and follow-up-health probes to pass; packaged current-directory/`PATH` substitution probes and missing-DLL behavior to pass; and worker crash/cleanup tests to leave no committed or staged output. A fork also requires a reproducible source commit, reviewable minimal patch, locked restore, modified-source release bundle, all applicable license texts, and an owner decision accepting its ongoing maintenance cost.

## Official 7-Zip 26.02 candidate

- Verified: official downloads and source are published at <https://www.7-zip.org/download.html>; license terms are at <https://www.7-zip.org/license.txt>.
- Exact x64 installer evidence: `7z2602-x64.exe` is 1,657,896 bytes with SHA-256 `6745FA76DC2EA031596D8678F6F6B99C3C1B435B4164A63485ADBBC7B8D82EF0`. Its extracted `7z.dll` exactly matches the SharpSevenZip package x64 payload recorded above. The official 26.02 source archive SHA-256 is `C7502DD4557481F52CCF1B3E680329F1FDD207E79A25544AFEB3106325474944`.
- Fixture-only provenance: official 7-Zip Extra 26.02 was downloaded from the official site. The archive SHA-256 is `081DF9E9311DFD9C9E0E98C1C80180B99BB51E4CB24156B5F3057FE3C259D70A`, `x64/7za.exe` is `35D4D69D7CD6CB44558F208C3B1334268013F9DAF82D2DDA848893A1C30C59C2`, and its exact `License.txt` is `39D2187B942CC88818DD959E53C6B7DDDD152097A42061467C4D56050168D646`. The tool generated fixtures only and is not redistributed by Rain Archives.
- Distribution design: dynamic app-private `native\win-x64\7z.dll` only. Never probe current directory, `PATH`, registry installations, or user-selected DLLs.
- Gate: binary/source hashes, exact x64 identity, exact-path loading in the presence of current-directory/`PATH` candidates, and missing-path fail-closed behavior are now recorded. Before redistribution, copy the exact official 7-Zip license information and source-availability material into the release compliance bundle. Before enabling the provider, repeat loader-policy checks through the packaged Rain adapter and complete full worker-corpus, Unicode, corrupt-input, and forced-process-failure conformance.
- RAR creation is available only through the separately installed, licensed, signed WinRAR 7.23+
  gate described above. Decoder availability alone is never permission to implement, bundle, or
  expose a RAR encoder.

## libarchive

Libarchive remains rejected as a Rain runtime provider for the first provider set. Its broad read API is attractive, but Windows native packaging, callback marshalling, per-architecture servicing, and a second native security/update surface do not solve a requirement that the selected managed plus 7-Zip plan cannot already cover. Reconsider only if measured format correctness or stream behavior leaves a documented gap.

The Windows-provided `bsdtar` used for the ZIP-tree conformance check above is fixture-only tooling, not linked, packaged, or invoked by Rain Archives. The official [libarchive 3.8.4 release](https://github.com/libarchive/libarchive/releases/tag/v3.8.4) identifies that version as a signed bug-fix release, and the exact tag's [README](https://github.com/libarchive/libarchive/blob/v3.8.4/README.md) states that `bsdtar` is implemented using libarchive and that ZIP is a supported read format. The exact tag's [COPYING file](https://raw.githubusercontent.com/libarchive/libarchive/v3.8.4/COPYING) governs source/binary redistribution. Because Rain redistributes no libarchive binary or source, this host-tool proof adds no Rain product notice/source obligation; shipping libarchive later would require a new exact-binary/source/license audit.

## Gate-changing evidence

Change the engine decision only for reproducible evidence: conformance failures against required fixtures, unacceptable cancellation/solid performance, a security advisory affecting the pinned path, unmanageable redistribution obligations, or a materially simpler supported official API. Format-count marketing alone is not sufficient.
