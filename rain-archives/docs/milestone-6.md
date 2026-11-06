# Milestone 6 — Rain Explorer integration

Status: **M6-A through M6-exit automated checkpoints accepted; implementation stopped before Phase 7** as of
2026-09-02. Direct integrated Windows GUI/assistive/clean-machine acceptance remains
`manual_pending`.

The sealed Rain Archives predecessor is protocol 1.22 at source fingerprint
`651729F471468F590BB2F89CE663C14B5140356227509B3C247A93C74481D740`. Its immutable package is
`artifacts/RainArchives.M5GDDesignSystemPackage.1.22.651729F4`. That package remains Phase-5 evidence;
no acceptance result transfers automatically to an integrated Rain Explorer package.

## Binding architecture

- The integration adapter is Rain Explorer code. Rain Archives does not reference Rain Explorer.
- Rain Explorer production code may reference only `RainArchives.Contracts` and
  `RainArchives.Client`. It must not reference `RainArchives.App`, `RainArchives.Worker`, Core,
  Security, provider projects, SharpCompress archive APIs, or implementation-specific DTOs.
- One lazy application-owned client connection hosts multiple archive sessions. Each archive tab owns
  or leases one root session and settles its entry-stream, preview-cache, nested-session, operation,
  and close responsibilities explicitly.
- Providers receive no Rain Explorer WPF types. Rain Explorer view models consume a Rain-owned neutral
  location/result model and keep ordinary filesystem mutation services separate.
- Protocol 1.22 is sufficient for M6-A. No protocol change is permitted merely to simplify UI code.
  Additive protocol work is considered only after a demonstrated semantic gap.

For development builds, Rain Explorer may use a configurable `RainArchivesRoot` MSBuild property that
defaults to the canonical sibling repository and may copy a freshly built Worker output as a runtime
artifact. No machine-specific absolute path may be committed. Rain Explorer project references remain
Contracts and Client only; a build-only Worker dependency is not an API reference. Before an integrated
release, this temporary source-root arrangement must be replaced or sealed by a hash-pinned package/feed
and exact Worker/package identity checks. Do not vendor the current 85-MiB Worker tree into Git.

## M6-A: read-only archive locations

M6-A proves the location, session, and ownership boundary before operations or mutation are added.

### Scope

- Open a filesystem archive as a real Rain Explorer tab. Extension matching is only an affordance;
  Worker content detection is authoritative.
- Aggregate `ListEntriesAsync` in bounded pages (normally 512; never above
  `ProtocolConstants.MaximumPageSize`) into a Rain-owned index capped at 500,000 descriptors.
- Validate offsets, totals, `NextOffset`, page growth, descriptor identity, normalized paths, and
  duplicate ordinals before replacing authoritative tab state.
- Reconstruct immediate-child hierarchy including implicit directories without collapsing duplicate
  material entries. Reject cycles, impossible parentage, depth beyond 256 components, and unbounded
  materialization.
- Support archive root/folder navigation, breadcrumbs, Back, Forward, Up, refresh, tab duplication,
  tab groups, local ordinal Unicode filename filtering, and bounded recursive filename search. Search
  never decodes file contents or nested archives.
- Represent loading, ready, empty, unsupported, password-required, offline, stale, and fixed-error
  states explicitly. Archive-controlled and provider-controlled text is escaped or replaced with fixed
  product copy before display.
- On source fingerprint change, `SourceChanged`, Worker loss, malformed response, or uncertain reopen,
  fail closed and require explicit reopen. Never mix old and new `EntryId` values.
- On tab replacement/close or application shutdown, cancel in-flight presentation work, then close the
  exact owned session with a dedicated short bounded cleanup token that is not the already-cancelled UI
  token. If close cannot be acknowledged within that bound, client shutdown/Job containment is the
  final cleanup boundary; the adapter does not retry the close as a new mutation.

### Persistent location identity

Rain Explorer persists a strict, versioned archive-location envelope bounded to 32 KiB. The envelope
contains the canonical source path, exact opened `ArchiveFingerprint`, normalized internal directory
path, and—only when a material entry must be resolved—the negotiated persistent path plus non-negative
duplicate ordinal. A nested location later adds an ordered bounded chain of parent fingerprint,
persistent source-entry path/ordinal, and child fingerprint segments.

The protocol's 32,767-character path ceiling is larger than this persistence budget. A location that
cannot fit the fixed 32-KiB envelope remains browseable for the current session but is explicitly
non-restorable/non-duplicable and is never truncated, hashed into an ambiguous token, or partially
persisted. Serialization applies smaller per-field and aggregate UTF-8 byte limits and fails closed
before settings/project state is changed.

`ArchiveSessionId`, `EntryId`, operation IDs, secret-slot IDs, cache paths, Worker paths, provider IDs,
raw warnings, and exceptions are never persistent location data. Persistence is disabled when
`persistent-entry-keys` was not negotiated. Restore reopens the source, compares the exact stored
fingerprint, and resolves fresh ephemeral IDs; mismatch produces stale state rather than silent
navigation.

### Closed capabilities in M6-A

- No archive-entry shell launch, Open With, terminal, active-content preview, shell thumbnail, or
  physical path fabrication.
- No copy/cut/paste, OS file-drop payload, drag-in/out, rename, delete, properties mutation, undo,
  recycle, pinning, or filesystem watcher treatment for virtual entries.
- No extraction, integrity operation, password prompt, preview stream, or explicit nested open until
  their bounded follow-up slices are accepted. The underlying descriptor capability may be retained
  internally but does not grant UI authority by itself.
- No archive creation/rebuild changes, overwrite, replacement, retry, in-place update, encryption,
  split output, 7z/RAR creation, or new native dependency.
- Existing direct Rain Explorer archive extraction/compression code is not reused for virtual tabs.
  It is removed or quarantined only when its Rain Archives replacement is proven.

### M6-A acceptance

Automated tests must cover:

1. strict location serialization/round-trip, size/depth/version/unknown-field refusal, and absence of
   ephemeral IDs;
2. paged aggregation, malformed offsets/totals, cancellation, cap enforcement, and stale-result
   rejection;
3. explicit and implicit directories, duplicate files, case/NFC collisions, deep paths, and cycles;
4. root/folder navigation, Back/Forward branching, Up, breadcrumbs, refresh, search, duplicate tab,
   close/reopen, and session/project restoration;
5. fingerprint mismatch and `SourceChanged` without old/new session mixing;
6. exact session close on success, cancellation, navigation replacement, tab close, and app shutdown;
7. virtual-entry refusal at shell, preview, drag/drop, clipboard, filesystem mutation, undo, terminal,
   pin, and watcher boundaries;
8. unchanged ordinary Home, Drives, folder, split-pane, group, and session behavior;
9. one real protocol-1.22 Worker flow that opens, lists, navigates, closes, and leaves no Worker/cache/
   staging residue.

Manual Windows acceptance must use a disposable archive and verify double-click open, breadcrumbs,
Alt+Left/Right/Up, search, refresh, tab duplication/grouping, restart restore, stale-source handling,
keyboard/focus behavior, and the absence of physical-only commands. It must also repeat an ordinary
folder workflow to catch regressions.

### M6-A automated checkpoint — 2026-09-02

The implementation is an uncommitted Rain Explorer working-tree delta over clean base
`0bc3fb090bef4d93e0db5bb6d66813b80e8385f7` (`master`). The exact 15-file changed-input set contains
541,661 raw bytes and has SHA-256
`D4543C0ACB433EF596EEF8AB13204E6E59CB4B45708C08CD0A3B93EB58E27FAF`. That digest is computed over
the ordinal repo-relative path, NUL, decimal raw byte length, NUL, and raw file bytes for every
tracked-modified or untracked M6-A input. No commit, push, PR, install, signing, package publication,
or release action occurred.

Independent verification rebuilt the current source and passed all 116 Rain Explorer tests with zero
failures/skips. The Release application build passed with zero warnings/errors. The current direct and
transitive NuGet advisory query reported no known vulnerable package. A real protocol-1.22 Worker test
opened a ZIP whose path contains spaces and Unicode, listed/navigated it, serialized its persistent
location, closed the exact session, disposed the client, and left no Worker, testhost, M6-A temporary
fixture, cache, or staging residue. The development output contains byte-identical App/Worker Contracts
assemblies and no `7z.dll`, `7za.exe`, `7zz.exe`, or `SharpSevenZip.dll`.

The accepted automated surface includes strict malformed/oversized/versioned location refusal,
authoritative `ArchivePathRules` normalization, bounded short-page aggregation, total material plus
implicit-node limits, cycle/depth/parent validation, duplicate preservation, root/folder history and
search, stale fingerprint and `SourceChanged` cleanup, overlapping-open generation rejection,
downgrade behavior without inferred stable keys, exact recently-closed-tab drain, final client/Job
cleanup, and fail-closed physical-command/preview boundaries. Direct Windows visual, keyboard-only,
Narrator, High Contrast, DPI, restart restore, stale-source interaction, ordinary-folder regression,
and clean-machine checks are not inferred from this automated checkpoint and remain `manual_pending`.

### M6-B automated checkpoint — 2026-09-02

M6-B extends the same uncommitted Rain Explorer working tree over base
`0bc3fb090bef4d93e0db5bb6d66813b80e8385f7` without changing protocol 1.22. The cumulative exact
18-file changed-input set contains 618,457 raw bytes and has SHA-256
`DAF3D191214F30C177C198E375239A0A9203F3B8E60767923769ABA64B657A7E`, using the same ordinal
path/NUL/decimal-length/NUL/raw-byte algorithm as M6-A. No commit, push, PR, installer, package,
signing, publication, or release action occurred.

The accepted implementation reads one selected material archive file through
`ArchiveEntryStreamLease` with an exact 1 MiB plus one-byte ceiling. It publishes only after the
authenticated terminal frame, accepts strict UTF-8 or BOM-proven UTF-16/UTF-32, visibly escapes
control/format/bidi text, and exposes only fixed loading/text/binary/oversize/unsupported/password/
offline/stale/cancelled/error states. It creates no cache or external path and never routes a virtual
entry into physical image, PDF, media, Shell-thumbnail, Open With, export, or execution handlers.

The latest-wins pump owns at most one active and one newest pending request. Request completion,
generation invalidation, cancellation-source lifetime, and exact lease disposal have one serialized
ordering; a successor is not admitted before predecessor cleanup. Navigation, replacement, tab close,
Worker loss, and application shutdown cancel and drain the pump. `SourceChanged` closes the exact
session and marks the entire location stale. Authenticated transport failure invalidates the exact
shared client, marks every affected tab offline only after preview drain, and permits explicit reopen
through a fresh client without mixing old entry IDs.

Independent verification passed all 127 Rain Explorer tests with zero failures/skips. The Release
application build passed with zero warnings/errors; the current NuGet advisory query reported no known
vulnerable package; and scoped formatter verification passed for every new Phase-6 source/test file.
The project still has its pre-existing repository-wide formatting debt, so a whole-project formatter
result is not relabelled as an M6-B regression or acceptance gate. A real protocol-1.22 Worker flow
opened, browsed, and inertly previewed a spaces-and-Unicode ZIP, disposed the exact lease/session/client,
and left no test-created Worker, testhost, fixture, cache, or staging residue. The build contains
byte-identical App/Worker Contracts at
`A2D7D6A173637B0B91E47E80625EF362FF5577290D440A5BE2B4227F1B8CAE4D`, Rain Explorer DLL
`F272E6F292D8D21D6AE20F3E6B8F4F8F075B31A9B01FF1086A2708F4BF5660B3`, and no gated 7-Zip
native asset. Direct visual, keyboard-only, Narrator, High Contrast, DPI, restart/stale-source,
ordinary-folder, and clean-machine acceptance remains `manual_pending` for a future exact integrated
package.

### M6-C automated checkpoint — 2026-09-02

M6-C extends the same uncommitted Rain Explorer working tree over base
`0bc3fb090bef4d93e0db5bb6d66813b80e8385f7` without changing protocol 1.22. The cumulative exact
27-file changed-input set contains 817,265 raw bytes and has SHA-256
`9F3672F88A01983FB8A49A9AC4959F2141347C502057FED3B4C884207A964709`, using the same ordinal
path/NUL/decimal-length/NUL/raw-byte algorithm as M6-A and M6-B. No commit, push, PR, installer,
integrated package, signing, publication, or release action occurred.

The accepted operation adapter re-resolves the newest visible selection against the current session,
fingerprint, entry identity, capability, volume state, and negotiated features before each request. It
preflights the exact bounded start frame before secret allocation and again before dispatch. Integrity
testing is read-only. Selected extraction accepts only an explicit distinct absent destination and uses
the Rain Archives Worker transaction; a pre-existing or raced destination is preserved, and virtual
entries never enter Rain Explorer's legacy physical extraction path.

Rain Explorer's Activity surface receives only fixed typed state plus bounded escaped entry display
text. It presents queued/validating/running/finalizing/terminal state, bytes, entries, elapsed time,
throughput, ratio, warnings, and cancel-only control without retaining provider messages, raw paths,
exceptions, or secrets. Cancellation targets the exact operation ID and waits for authoritative
terminal settlement; if the five-second UI bound expires, exact-client teardown/Job containment occurs
before session or tab ownership is released. Source change marks the tab stale, Worker/protocol loss
invalidates the exact shared client, and close/application shutdown drains all callbacks.

Encrypted operations use one-shot secret slots. Caller password arrays are bounded by UTF-8 bytes,
cleared immediately after slot creation or on every pre-slot refusal/cancellation path, and never enter
ordinary logs, activity text, command lines, environment variables, or persistence. The WPF prompt
clears visible, unmanaged, non-transferred, failed-transferred, unused-captured, and caller-owned
representations on their exact ownership paths.

Independent verification passed all 149 Rain Explorer tests with zero failures/skips. The Release
application build passed with zero warnings/errors; the current direct/transitive NuGet advisory query
reported no known vulnerable package; scoped formatter verification passed for every touched Phase-6
production/test C# file; and `git diff --check` found no error. The real protocol-1.22 Worker flow
opened and previewed a spaces-and-Unicode ZIP, integrity-tested it, extracted the exact selection to a
new destination, compared output hashes, preserved the source and a destination-conflict sentinel, and
left no test-created Worker, testhost, fixture, recovery, preview-cache, nested-workspace, or staging
residue. Independent residue review exposed a test-only empty `password-refused-*` directory leak; the
test now removes only its own verified empty GUID destination, and the 17 already-created empty,
non-reparse directories were removed by exact path after two-phase validation.

The final development output contains byte-identical App/Worker Contracts at
`A2D7D6A173637B0B91E47E80625EF362FF5577290D440A5BE2B4227F1B8CAE4D`, Rain Explorer DLL
`1F8F5612D4888B8177A8E040350DDC64A951102D80C6A16EE81BB1AA50DF4976`, Client DLL
`C9FA54B215AC3B325AFC9B9D318669DCBD1E7C99A5F633605A1C8EC5EC14A9F5`, Worker DLL
`5D565A9272D8CD5004A90D8456D9C7DCE1F60230BA777D8575CD85D276EBC0FE`, and no `7z.dll`,
`7za.exe`, `7zz.exe`, or `SharpSevenZip.dll`. The user's installed Rain Explorer process was observed
and deliberately left untouched. Direct integrated visual, keyboard-only, Narrator, High Contrast,
DPI, restart/stale-source, ordinary-folder, and clean-machine acceptance remains `manual_pending` for
a future exact integrated package.

### M6-D automated checkpoint — 2026-09-02

M6-D extends the same uncommitted Rain Explorer working tree over base
`0bc3fb090bef4d93e0db5bb6d66813b80e8385f7` without changing protocol 1.22 or any Rain Archives
source. The cumulative exact 28-file changed-input set contains 907,670 raw bytes and has SHA-256
`93EFF0EA30F531A4995DDFDBBB93BD714E7C7CB0C938DC62050C4D13415061B7`, using the same ordinal
path/NUL/decimal-length/NUL/raw-byte algorithm as M6-A through M6-C. No commit, push, PR, installer,
integrated package, signing, publication, or release action occurred.

One explicit command/Enter/double-click path now opens one currently visible material file through the
existing authenticated nested-session route. Admission re-resolves the current parent session,
fingerprint, entry identity, safety, single-volume state, random-access capabilities, feature set,
depth, declared bytes, chain bytes, and connection-wide active-byte reservation before creating a
one-shot child password slot. It never uses an extension as authority, recursively scans or decodes
children during listing/search, fabricates a physical child path, or routes nested content through
Rain Explorer's legacy archive code.

The adapter owns an ordered root-to-child session chain and closes descendants before ancestors.
Child presentation is adopted only after the authenticated response and complete bounded index pass
validation; cancellation during child indexing, malformed responses, fingerprint mismatch, source
change, Worker loss, branch replacement, tab close, and application shutdown compensate every
partially owned session, reservation, and secret. Back/Forward/Up and breadcrumbs retain archive
depth, while opening a replacement branch prunes only now-closed descendant history.

Persistent archive locations are strict 32-KiB version-2 envelopes while version-1 root locations
remain readable. Each nested segment records the exact parent fingerprint, normalized persistent
source-entry path plus duplicate ordinal, and child fingerprint without runtime session, entry,
secret, cache, or workspace identity. Restore reopens every segment with fresh IDs and never falls
back to an ancestor. Root fingerprints compare exactly. Nested temporary file identity and timestamp
are intentionally ephemeral, so nested comparison uses length and prefix hash only under the already
validated immutable parent fingerprint and persistent entry key. Encrypted parent entries are
truthfully refused before secret allocation because the current prompt owns only the child-password
role; a password-protected child supports explicit wrong/correct retry. A persisted encrypted child
chain contains no secret and therefore stops in fixed password-required state rather than silently
restoring a different location.

Independent verification passed the 11 focused M6-D cases and all 160 Rain Explorer tests with zero
failures/skips. The Release application build passed with zero warnings/errors; the current direct and
transitive NuGet advisory query reported no known vulnerable package; scoped formatter verification
passed for every M6-D production/test C# file; and `git diff --check` found no error. Coverage includes
depth-three success/depth-four refusal, per-entry/chain/global byte ceilings, downgrade/solid/
multipart/list-only/encrypted-parent refusal before secret use, wrong/correct child password and
zeroization, cancellation after child ownership but before index adoption, malformed/source-changed
responses, fresh-ID duplicate-ordinal restore, fingerprint mismatch, exact close order, and strict
unknown/oversize/version persistence refusal.

The real protocol-1.22 Worker flow opens three extension-independent nested levels from a
spaces-and-Unicode `.data` root, navigates/searches/previews at nested depth, integrity-tests and
extracts the exact nested selection, compares hashes, preserves the original, restores the depth-three
location with fresh sessions, then leaves zero test-created Worker, testhost, fixture, recovery,
Preview Cache, nested workspace, or staging residue. Final development output contains byte-identical
App/Worker Contracts at
`A2D7D6A173637B0B91E47E80625EF362FF5577290D440A5BE2B4227F1B8CAE4D`, Rain Explorer DLL
`016D66A62113C80E36C4245F37276A96172CE9FE5C3703DA2655C2D137FF230C`, Client DLL
`C9FA54B215AC3B325AFC9B9D318669DCBD1E7C99A5F633605A1C8EC5EC14A9F5`, Worker DLL
`5D565A9272D8CD5004A90D8456D9C7DCE1F60230BA777D8575CD85D276EBC0FE`, and no gated 7-Zip
native asset. The user's installed Rain Explorer process remained untouched. Direct integrated visual,
keyboard-only, Narrator, High Contrast, DPI, restart/password-restoration, ordinary-folder, and
clean-machine acceptance remains `manual_pending` for a future exact integrated package.

### M6-E automated checkpoint — 2026-09-02

M6-E extends the same uncommitted Rain Explorer working tree over base
`0bc3fb090bef4d93e0db5bb6d66813b80e8385f7` without changing protocol 1.22 or any Rain Archives
source. The cumulative exact 34-file changed-input set contains 1,078,510 raw bytes and has SHA-256
`104F6C3B5D5DFD6088C1394564C0744B36F1D96532BDD319B2B4E21C2345562A`, using the same ordinal
path/NUL/decimal-length/NUL/raw-byte algorithm as M6-A through M6-D. No commit, push, PR, installer,
integrated package, signing, publication, or release action occurred.

The platform gate closed honestly: protocol 1.22 does not bind the Worker's committed destination
identity, and WPF's drag/drop wrapper preserves delayed `GetData` delegation but does not expose a proven
`IDataObjectAsyncCapability` start/in-operation/end-operation lifetime to Rain Explorer. Production
archive copy/drag-out therefore remains disabled with fixed capability text and starts zero extraction
work. No eager extraction, timer cleanup, ordinary clipboard probe, hover, context-menu query, drag
threshold, or format discovery can create transfer output.

The disabled production gate is backed by a qualified internal seam rather than a stub. One bounded
owner serializes admission and re-entrant rendering, revalidates the exact session/fingerprint/current
selection/capability/volume state, uses the authenticated Worker for selected extraction, and publishes
only an exact ordinary-object manifest under a fixed per-user transfer root. Aggregate object, byte,
character, and UTF-8 path ceilings are explicit. Exact identities are pinned during review and cleanup;
deletion uses exact handles and preserves suspicious, changed, partially removed, or uncertain output as
path-free evidence. Cancellation, normal release, late-render settlement, cleanup exceptions, password
ownership, app drain, and replacement admission settle deterministically.

Independent verification passed 65 focused transfer/OLE/adapter cases and all 192 Rain Explorer tests
with zero failures/skips. The Release build has zero warnings/errors, the direct/transitive NuGet
advisory query reports no known vulnerable package, `git diff --check` is clean, and scoped formatter
verification passes for the new M6-E source. The broader repository's unrelated historical formatting
debt remains outside this gate. One real protocol-1.22 Worker flow publishes a nested selected file,
verifies output and unchanged-source hashes, cleans the exact transfer tree, and leaves production copy
capability disabled. Direct integrated visual, keyboard-only, Narrator, High Contrast, DPI,
ordinary-folder, and clean-machine acceptance remains `manual_pending` for a future exact package.

### M6-F automated checkpoint — 2026-09-02

M6-F extends the same uncommitted Rain Explorer working tree over base
`0bc3fb090bef4d93e0db5bb6d66813b80e8385f7` without changing protocol 1.22 or Rain Archives production
source. The cumulative exact 44-file changed-input set contains 1,326,298 raw bytes and has SHA-256
`E637C248717555533151C3959B7A5847F4DFD5E4489AD262643263B4DEAF3108` under the established ordinal
path/NUL/decimal-length/NUL/raw-byte algorithm. No commit, push, PR, installer, integrated package,
signing, publication, or release action occurred.

Rain Explorer now queries the authenticated Worker before exposing managed creation. ZIP, TAR, TAR.GZ,
TAR.BZ2, GZIP, and BZIP2 use only Worker-advertised formats, presets, source shapes, and limits. Raw GZIP
and BZIP2 accept exactly one ordinary file; the four tree-capable formats preserve nested Unicode files,
zero-byte files, and explicit empty directories. Explorer passes only validated top-level physical roots
and never enumerates selected directories or writes archive bytes. Existing destinations, source aliases,
reparse roots, stale selections, capability changes, and unsafe interactive operation phases fail closed.

Eligible unencrypted single-volume ZIP sessions can rebuild a full distinct absent copy only after exact
session, fingerprint, source, feature, and capability revalidation. The original is never targeted for
write, staging, deletion, replacement, or retry. Creation and rebuild publish fixed typed lifecycle state
to Rain Activity and expose cancellation only. The legacy in-process ZIP writer and its path-based partial
output deletion were removed; physical extraction remains deliberately unchanged until M6 exit proves its
Worker-backed replacement.

Independent verification passed the 47 focused managed-write cases and all 239 Rain Explorer tests with
zero failures/skips. The Release build has zero warnings/errors, the current direct/transitive NuGet
advisory query reports no known vulnerable package, `git diff --check` has no error, and scoped formatter
verification is clean for the cumulative changed C# set. The real protocol-1.22 Worker flow creates and
round-trips every exposed format, exercises all four directory-tree routes, integrity-tests and extracts
their exact manifests, rebuilds an eligible ZIP, and preserves every source/original hash. App/Worker
Contracts are byte-identical at
`A2D7D6A173637B0B91E47E80625EF362FF5577290D440A5BE2B4227F1B8CAE4D`; Rain Explorer DLL is
`E4AD234E66AF175BA3E37C3490B0704FCAA89EDED589867B1910F98581B6A823`; Client DLL remains
`C9FA54B215AC3B325AFC9B9D318669DCBD1E7C99A5F633605A1C8EC5EC14A9F5`; Worker DLL remains
`5D565A9272D8CD5004A90D8456D9C7DCE1F60230BA777D8575CD85D276EBC0FE`; and gated 7-Zip native
assets remain absent. The user's installed Rain Explorer process was untouched. Direct visual,
keyboard-only, Narrator, Light-theme, High Contrast, DPI, ordinary-folder, and clean-machine acceptance
remains `manual_pending` for the future exact integrated checkpoint.

### M6-exit automated checkpoint — 2026-09-02

M6 exit removes the last Rain Explorer-owned archive decoder/extractor. Physical-selection `Extract here`
now processes at most 16 ordinary files sequentially in current visible order, content-probes each file
through a temporary authenticated Worker session, and requests full extraction with an empty entry
selection. The physical context menu enables `Extract here` for every bounded ordinary-file selection, so
extensionless files reach Worker content detection and extensions remain neither required nor trusted.
Normal Open, double-click, and Enter remain extension-hinted archive affordances. Each source receives one
deterministic absent sibling destination. Existing or raced destinations are preserved; Explorer does not
merge, overwrite, retry, delete partial output, or register path-based Undo. Fingerprint, capability,
volume identity, operation event, exact-ID cancellation, terminal, and session/client teardown authority
stays in the accepted Contracts/Client route. Ordinary encrypted and header-encrypted archives use
one-shot secret slots, immediate caller-buffer clearing, and one explicit password attempt. The first and
later sequential password callbacks are regression-proven on the originating WPF dispatcher after
asynchronous Worker-like completion. Header-encrypted open reuses the password-opened session for
extraction; a wrong password consumes one slot without retry or extraction.

The cumulative exact 47-file changed-input set contains 1,408,763 raw bytes and has SHA-256
`CB0DC84FC3A19D43F73E9ECB7E527F7094A968F54787635F816DF9BD0240AD65`. Production has no
`System.IO.Compression` extraction call or SharpCompress reference/package, and WPF project references
remain Contracts plus Client only. Focused tests pass 31/31; all 262 Rain Explorer tests pass with zero
failures/skips; Release build is zero-warning/zero-error; current advisory, formatter, and
`git diff --check` gates pass. Real-Worker cases cover no/misleading extensions, nonarchives, corrupt and
multipart refusal, ZIP/7z/RAR/TAR.GZ/GZIP/BZIP2, AES and header encryption, wrong-password no-retry,
ordered multi-selection, full-directory expansion, destination/source immutability, exact before/after
recovery inventories, shutdown cancellation, and zero process/temp/staging residue. A separate read-only
review found one header-open cancellation classification gap, which was fixed and regression-tested, then
reported no remaining blocking finding.

The current self-contained `win-x64` package is
`artifacts/RainExplorer.M6ExitIntegratedPackage.1.2.3.CB0DC84F`. The 455-file / 250,820,035-byte app tree
hash is `038671F5F5EC4EF7B91241E29571874ADCC167C98549604335A7321C88D23285`; its private 205-file Worker
tree hash is `D1A666333CD9588F8C71C439F729BB66886CDED5805DD90E852A8A9055CE7F49`. App/Worker Contracts are
byte-identical at `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`; SharpCompress and all
Worker-only assemblies are absent from the app root, and forbidden native 7-Zip assets are absent. Ten
package-bound flows pass using the package-byte-identical RID App assembly and exact private Worker.
`package-manifest.json` retains the complete identity and verification record. The earlier `C243D42E`
package remains untouched as superseded pre-remediation evidence. No installer, install, association,
signing, publication, release, commit, push, or PR occurred; installed PID 26520 was untouched.
Direct GUI, keyboard-only, Narrator, Light/High Contrast, DPI, ordinary-folder, physical-network, and
clean-machine acceptance remains explicitly `manual_pending`.

## Follow-up slices

- **M6-B — automated checkpoint accepted:** bounded text/seekable preview using
  `ArchiveEntryStreamLease` and, only where
  required, `ArchivePreviewCache`; strict inert presentation; one active plus one newest pending
  request; generation-safe replacement cancellation; exact lease/cache cleanup; fixed
  loading/text/binary/oversize/unsupported/password/offline/stale/cancelled/error states; and no
  external path, Shell preview, Open With, export, execution, or active-content handoff. It must retain
  the M6-A source/session fingerprint and row-identity checks, leave physical preview behavior
  unchanged, remain Contracts/Client-only, and keep protocol 1.22 unless a separately proven semantic
  gap is found.
- **M6-C — automated checkpoint accepted:** integrity and selected/new-destination extraction mapped
  into Rain `ActivityService`, exact
  terminal/cancellation/password-slot settlement, no merge/overwrite, and source-change handling.
- **M6-D — automated checkpoint accepted:** explicit nested open and nested history/reopen ownership
  within existing depth/byte limits, with the encrypted-parent two-role prompt intentionally closed.
- **M6-E — automated checkpoint accepted, capability disabled:** the delayed-render/OLE lifetime proof
  is unavailable in current WPF, so no archive copy/drag-out is exposed; the bounded internal ownership,
  exact-manifest, handle-cleanup, and real-Worker seam is retained and regression-tested.
- **M6-F — automated checkpoint accepted:** capability-gated managed creation and distinct-copy ZIP
  rebuild integration; drag/copy in remains closed unless a complete staged-rewrite UX is separately
  approved.
- **M6 exit — automated checkpoint accepted:** duplicate physical extraction authority is removed and the
  exact integrated package is retained. Manual GUI/assistive/theme/DPI/network/clean-machine acceptance
  remains pending. Phase 7 remains separately authorized work.
