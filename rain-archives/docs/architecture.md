# Architecture decisions

## Process boundary

Rain Archives uses one on-demand worker per client. The worker owns archive sessions and operations; clients own presentation. A client-created named pipe carries bounded control frames, while password material uses a separate binary frame type. The minor-5 preview feature uses a separate client-created, read-only data pipe per lease rather than sharing or blocking the control channel. Full extraction, minor-6 nested-materialization data, and all ZIP/TAR/raw creation source enumeration, streams, staging, and output stay inside the worker, so payload bytes and untrusted tree traversal do not make a needless round trip through the UI.

The process boundary protects the UI from parser crashes. The Job Object adds kill-on-client-close, one-process, and a 2 GiB per-process memory limit. The client first requests a bounded graceful close, then uses Job Object/process termination as fallback. This does not stop a compromised same-user worker from reading files available to that user and is not a same-user filesystem sandbox. The Milestone 4 decision retains this same-user Worker/Job design as the current lifecycle and crash boundary. AppContainer or brokered filesystem handles are not enabled because no compatible maintained principal/ACL/handle-broker protocol has been proven across the current fully qualified path authority, provider streams, authenticated IPC, atomic App/Worker deployment, and solo-maintenance constraints. A stronger hostile-same-user threat model must reopen that as a separately designed boundary rather than relabel process isolation.

An archive session holds a deny-write/delete source lease. File identity, length, last-write time, and the SHA-256 of the first 64 KiB are also compared before reads and immediately before extraction commit. This catches replacement and ordinary mutation while avoiding a full rehash on every listing page. Protocol 1.4 and the provider boundary use an explicit `Single`, `ConfirmedMultipart`, or `Unknown` volume-set identity. `IsMultipart` remains only as the additive legacy projection and must be true exactly for `ConfirmedMultipart`. ZIP classification combines checked volume names with bounded EOCD, ZIP64 locator, disk, and spanning metadata; 7z classification checks the signature header and required next-header extent; RAR uses SharpCompress's parsed `IRarArchive.IsMultipartVolume()` signal, which follows the archive main-header volume flag. TAR-family and recognized raw compression streams are intrinsically single under their supported container model. Missing or malformed terminal/ZIP64 records are normally rejected before a session is opened; any provider result that still cannot prove completeness is `Unknown`, never silently `Single`.

The worker validates every provider's identity/capability combination before registering a session. `ConfirmedMultipart` currently advertises `ReadVolumes` but no stream/test/extract/rewrite operation, while `Unknown` advertises neither `ReadVolumes` nor any data operation. Both remain list-only: confirmed multipart operations are refused until every volume receives equivalent identity and lifetime protection, and unknown-set operations are refused until the provider proves a complete single source. The client independently rejects missing, unknown, or incoherent identity values whenever protocol feature `volume-set-identity` is negotiated.

## Provider boundary

Provider routing is internal and deterministic:

- SharpCompress is the managed read provider and also supplies the qualified path-free TAR/TAR.BZ2/BZIP2 writer implementations; .NET framework streams supply GZIP and `RainArchives.Providers.Zip` supplies ZIP writing.
- The official app-private `7z.dll` is intended to become authoritative for 7z and RAR after its adapter gate passes.
- No provider fallback happens during an operation.
- Provider exceptions are translated into stable archive error categories before crossing IPC.
- Providers may copy decoded bytes only into a caller-supplied stream. Rain Archives owns paths, staging, limits, and commit. Extraction and integrity testing request provider checksum validation; raw entry streams are used only by bounded preview leases and do not establish integrity.

Sequential formats retain a single forward reader for provider-order copies and reset it for backward selection, cancellation, output failure, or decoder failure. Both initial indexing and retained-reader movement consume an aggregate decoded-work budget. Raw GZIP integrity compares a Rain-computed CRC32 and decoded size with the trailer. Compressed TAR performs one bounded full validation of its outer GZIP/BZIP2/XZ stream before the first integrity/extraction copy and caches only successful validation for the source-locked session. This costs an additional pass but avoids treating successful inner TAR decoding as proof that the outer stream checksum is valid.

The native gate currently advertises no formats and cannot be accidentally selected. SharpSevenZip 2.0.109 passed package/source/license provenance, exact-path loading, and most of the checked .NET 10 plus official-7-Zip-26.02 corpus, but its integrity API accepted a ZIP that official 7-Zip marked truncated. It also drops native archive warning/error flags and per-entry packed size. The unmodified package is not eligible to become the authoritative reader. A newer audited upstream release or separately approved minimal fork must close those fields plus password/cancellation translation before packaged loader-policy and forced-process-failure checks can begin. If adopted, its default native-copy target is disabled and Rain stages one separately verified app-private x64 DLL. Wrapper directory extraction and direct user-archive update APIs are forbidden: extraction remains stream-only into Rain-owned guarded output, and modification operates only on a complete Rain-owned staging copy before Rain's commit protocol.

## ZIP creation boundary

Protocol 1.7 adds optional feature `zip-create-v1` and a separate provider-neutral writer registry. Protocol 1.8 adds `zip-create-directories-v1` as a distinct operation rather than changing the minor-7 request shape. The first writer is `RainArchives.Providers.Zip`, backed by .NET 10 `ZipArchive`; it is authoritative only for new unencrypted ZIP output. This keeps the initial write path managed, adds no third-party runtime dependency, supports explicit directory entries, and supports cancellation between source reads/writes through Rain-owned streams. Its disadvantages are deliberate: no AES or filename encryption, split output, in-place archive update, filesystem-link preservation, or 7z creation, and no claim that its compression ratio or speed beats another tool. Reconsider the provider only if external conformance, security, cancellation, output-size, or performance evidence shows a concrete gap; format-count marketing alone is not a gate-changing reason.

Protocol 1.12 adds `zip-rebuild-copy-v1` without changing any earlier creation request. It accepts one closed edit plan against an already-open, proven single-volume, unencrypted ZIP session and can delete exact persistent entry keys plus add or replace files at validated archive-relative paths. The Worker retains the opened source lease, fingerprint, password/session state, and exact entry snapshot. Retained entries move through the reader provider's checksum-validating copy callback directly into the writer stream. Each unique mapped file remains on one non-reparse, single-link, deny-write/delete lease through final validation and commit, followed by a path/identity recheck. Security rejects encrypted, multipart, duplicate/ambiguous, link/special, colliding, aliased, over-budget, or mutated state before publication. The selected preset recompresses every retained and new file into a new absent destination through the existing journal-bound sibling staging. After durable flush, Rain reads the staged ZIP back through the exact staging handle and compares entry count, order, path, kind, decoded length, attributes, and DOS-normalized timestamp with the closed manifest before the no-replace commit. This is a Rebuild Copy, not an in-place update: the original archive is never renamed, overwritten, deleted, or used as staging.

Creation is not tied to an opened archive session. The minor-7 request contains one new destination path, zero to 4,096 explicit `(source path, archive-relative path)` file mappings totaling at most 64 GiB, and one of `Store`, `Fast`, `Balanced`, or `Maximum`. The minor-8 request has the same fields plus at least one `(source directory, nonempty archive-relative root)` mapping; explicit files and every expanded directory/file share one 4,096-entry limit, while only regular-file bytes count toward the 64 GiB limit. Each selected root itself is emitted, every descendant directory is emitted so empty directories survive, and all entries are globally sorted by archive path for deterministic provider order. The worker validates and plans the entire request before staging. The writer receives no filesystem path and cannot choose enumeration or commit behavior.

For explicit files, Rain opens each ordinary absolute Windows source through pinned non-reparse ancestry, denies write/delete sharing, compares identity/length/time with the plan, and supplies only that stream plus validated metadata to the writer. For a directory root, Rain opens the complete ancestry without following reparses, enumerates each pinned directory handle with `GetFileInformationByHandleEx`, rejects reparse children, and reopens each child relative to its parent handle. When enumeration supplies a nonzero file ID, the reopened object's handle identity must contain the same file ID. The complete relative-path/kind/identity/length/timestamp manifest is captured during planning and reproduced after writer finalization; any extra, missing, renamed, type-changed, replaced, resized, or retimestamped object fails as `source.directory_tree_changed` before commit.

The output is one random-token, journal-bound sibling staging file beneath a pinned destination parent. Rain durably records its exact name before creation, creates it with native create-new semantics, and normally marks the exact handle for deletion immediately. A unanimous valid source zone selects one special setup path: before setting deletion disposition, Rain creates an empty ADS relative to the pinned parent, proves that it belongs to the staging file, and retains it with only delete sharing. This is necessary because Windows rejects a later path open of that ADS once the base file is delete-pending. Rain then gives a guarded form of the default stream to the provider, verifies every source snapshot before and after its copy, and durably flushes output. Exact source-tree and per-file provenance revalidation runs after provider completion; the canonical ADS is written, durably flushed, and read back through the retained handle before it is closed. Commit validates parent/file identity and link count, persists final length and commit intent, clears deletion disposition, then performs a handle-relative no-replace rename and records the committed phase. A destination race preserves the raced file; ordinary failure or cancellation closes the delete-pending handles. A successful rename is authoritative even if the worker dies before the terminal event, so callers must inspect rather than retry blindly. Precreating an ADS enlarges the tiny pre-disposition crash window by one bounded stream-create/identity-check step, while clearing and reapplying deletion disposition or using asynchronous `BackupWrite` are rejected because they weaken cleanup or violate the documented handle contract.

Creation download provenance is implemented as an internal five-state policy: present, absent, malformed, inaccessible, or unsupported. Every regular source is captured through its stable source lease, then reopened and compared after writer finalization. Valid-size ADS payloads retain exact length and SHA-256 state; oversized malformed streams retain their length plus a 4 KiB prefix hash so inspection stays bounded, and are never propagated. Rain propagates a zone only when every regular source has one identical, strictly validated `ZoneId`; an empty/directory-only archive, all-absent sources, or all-unsupported sources produce no stream or warning. Mixed zones, present-plus-missing/unsupported, malformed, or inaccessible state produces no output stream and at most one sanitized warning. A detected state change fails as `source.provenance_changed` or the stronger ordinary source-snapshot failure. Only canonical `[ZoneTransfer]` plus `ZoneId` data is written; URLs and arbitrary source ADS bytes never cross into the new archive. ADS preparation failure on the destination becomes one `provenance.not_applied` warning and uses the ordinary delete-pending transaction. Rain does not use `IAttachmentExecute::Save` for this deterministic engine path: [Microsoft documents](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iattachmentexecute-save) that it may invoke trust services which alter or delete the file, and it does not accept Rain's already validated zone as a direct input. Direct canonical ADS writing has the disadvantage that Rain does not claim equivalence with Attachment Execution Services or invoke its scanner/policy workflow. Reconsider this choice only if an official Windows API gains an exact-zone, handle-bound, non-mutating contract. Local NTFS source/output, exact rename, ordinary cleanup, cancellation-before-application, hard-kill-during-copy, and packaged-worker tests pass. Current exFAT creation across all supported presets commits with the explicit warning, no ADS, exact payload hashes, and preserved source provenance; actual SMB creation-provenance behavior remains a separate gate.

## Managed TAR and raw creation boundary

Protocol 1.17 adds separate TAR explicit/tree requests and capabilities without changing any ZIP
message. The TAR provider receives only validated `RegularFile`/`Directory` entries and open streams,
writes sequentially through .NET 10 `TarWriter`, accepts only `Store`, preserves qualified UTC/PAX
timestamp precision, and rejects pre-Unix timestamps and Windows attributes rather than importing ZIP
DOS semantics. Security reuses its source leases, namespace policy, provenance, five-phase journal,
guarded sibling, durable flush, source revalidation, and no-replace commit, but applies a TAR-specific
output estimate and readback profile.

Protocol 1.18 adds distinct TAR.GZ/TAR.BZ2 explicit/tree requests, raw GZIP/BZIP2 requests, and
managed-format capabilities. `GZipStream` supports all four presets; SharpCompress BZIP2 is qualified
only at Balanced. Raw requests accept exactly one ordinary regular source file, no directories or
multi-source mapping, and derive one normalized synthetic member name from the destination stem.
They are not squeezed through the multi-entry TAR/ZIP DTOs.

The provider-neutral staged manifest carries an expected SHA-256 for each regular file. Digests are
computed while Security owns the authoritative source copy and compared while the provider validator
fully decodes the exact staged handle. TAR validates complete structure and footer. Compressed TAR
also validates the outer stream. GZIP validates its trailer. BZIP2 opens one member only, requires the
decoder to consume the complete underlying stream, validates the canonical footer, and rejects junk
or appended valid members. Finalization uses a one-shot state transition so a failure cannot cause
cleanup to emit a second footer. Unsupported expected kinds are rejected before parsing.

Output estimation remains format-aware. ZIP keeps its established formula byte-for-byte. TAR-family
charges 512-byte entry headers, payload rounded to 512 bytes, two EOF blocks, and bounded PAX/
extended-header overhead; raw formats charge the single source plus conservative codec overhead.
The guarded stream and runtime free-space checks remain authoritative. Existing or raced destinations
are preserved at both admission and precommit, and publication remains exactly one no-replace rename.

This boundary intentionally does not generalize to multiple output paths. Split creation would need
a bounded volume manifest, aggregate resource checks, durable partial-publication journal, path-free
recovery, and crash/cancel proofs for every published subset. Existing-destination replacement would
need a separate typed conflict/backup/recovery contract and proven Windows replacement semantics.
Neither authority exists, so capability, CLI, and WPF surfaces report/refuse both. ZIP encryption and
XZ writing have no approved managed provider, and native 7z creation remains disabled. Protocol 1.23
adds one separate conditional RAR5 writer: the Worker verifies and invokes only the user's separately
installed, licensed, signed x64 WinRAR 7.23–7.x encoder at its exact Program Files path. It never bundles
or discovers the executable. Validated source streams are materialized under a random bounded private
workspace, the encoder output is tested and fully read back against the exact digest manifest, and only
then is it copied into the existing guarded single-output/no-replace transaction. Encryption, split,
SFX, update/rebuild, arbitrary options, and existing-destination replacement remain unavailable.

The Worker owns RAR provider authority rather than inferring it from a generic `Format` string. It resolves
the concrete verified provider at capability query, request admission, and again after the serialized
operation queue gate. The provider revalidates the exact executable and keeps a read lease that denies
write/delete replacement through all encoder invocations. Installation after Worker startup is a typed
`RestartRequired` state; removal or replacement closes the existing capability before materialization.
Each RAR bridge workspace has a random 256-bit name and matching durable owner record under the app-private
local root. A live Worker pins the directory and owner without delete sharing. Post-handshake maintenance runs
off the dispatch path under one global 1,024-candidate/16,384-object/two-second budget. It opens the stable root
ancestry and `owner.lock` no-follow, deletes only from one complete bounded inventory of an inactive exact-record
ordinary non-reparse one-link tree, skips active work, and preserves changed, unreadable, suspicious, or
over-limit state with a fixed warning. Encoder cancellation must prove the exact child exited; an unproven kill
tears down the Worker-owned connection rather than emitting a false terminal. Workspace cleanup failure is
reported after handles are released. This closes ordinary crash residue without claiming hostile same-user
filesystem isolation, which would require a separately reviewed broker/principal.

## Entry preview stream leases

Protocol 1.5 adds optional feature `entry-stream-leases` for complete single-file preview reads. The client creates a random `rainarchives-entry-` pipe with 256 bits of random name material, `PipeOptions.CurrentUserOnly`, and read-only direction. The authenticated control request carries that pipe name, a separate 256-bit bootstrap nonce, the session/entry IDs, an optional password slot, and a caller-selected decoded-byte ceiling. The worker accepts only the exact canonical pipe-name/nonce shape, connects locally, writes a hello containing a fresh non-reusable stream ID plus the nonce, and only then acknowledges the control request. The client checks the connected process with `GetNamedPipeClientProcessId` against the exact worker it launched and compares the fixed-size nonce in constant time. This prevents a same-user pipe-name race from silently becoming the preview peer; it does not give the worker a lower filesystem privilege or sandbox it from the user account.

The data protocol has a five-byte binary header and exactly four frame kinds: one 48-byte hello, non-empty data frames of at most 64 KiB, one eight-byte completion total, or one bounded structured error. No JSON control, password, source path, or command line carries entry contents. The client and worker each enforce the requested decoded-byte ceiling; the default is 64 MiB and the hard protocol maximum is 256 MiB. Declared entry size is an untrusted preflight hint: a value above the ceiling is refused before decode, while actual bytes are counted with checked arithmetic and can still fail at runtime if metadata understated them. The client independently checks aggregate bytes and the terminal completion total. A consumer or cache must publish a preview only after `Complete`; bytes observed before a terminal error are partial and untrusted.

Only a provider-advertised `OpenEntryStream` capability, a proven `Single` volume set, and a material file entry can receive a lease. The worker keeps the archive-session reservation and source fingerprint checks through terminal framing, serializes access through the session handle, and revalidates provider identity, capabilities, volume identity, entry count/order/kind/path whenever a password causes a reopen. At most eight leases are admitted and at most two preview decode executions run concurrently. Stream IDs are never reused on one connection.

Preview support is discovered from each opened descriptor, never inferred from the extension or a format-wide table. Current SharpCompress 0.50.4 sessions intentionally withhold `OpenEntryStream` for solid RAR because its safe path is sequential and protocol 1.5 has no bounded accounting for decoding preceding solid members; the same descriptor may still advertise integrity testing and extraction. Any proven single-volume session that advertises extraction without direct streaming receives structured warning `archive.preview_unavailable` through the existing descriptor shape. Clients hide or explain preview for that session rather than silently performing an unbounded fallback.

Client disposal, direct stream disposal, read cancellation, archive close, and client shutdown close the data pipe and send an authenticated `entry.stream.cancel` control request. The worker acknowledges against a per-lease cancellation source whose token reaches provider open/read calls, so cancellation does not depend on reaching the next pipe write. If that bounded acknowledgement cannot be obtained, the authenticated control transport and owned Job Object remain the containment fallback. Password bytes transfer to the stream task only after admission, are removed from the secret store exactly once, and are zeroed when the task ends; managed strings and provider internals still impose the documented best-effort zeroization limitation. A raw preview ending in `Complete` proves bounded transport completion and stable source identity, not CRC/checksum integrity.

### Client-owned seekable preview cache

The sixth M2 slice changes no wire shape. `RainArchives.Client.ArchivePreviewCache` consumes the authenticated complete-entry lease and materializes it into a caller-provided, precreated app-private directory. The directory must exist and must not be a reparse point. Admission defaults to 512 MiB of active materialized bytes and the protocol ceiling of eight active leases; callers may choose lower limits. Known declared length is reserved before copy, while unknown length reserves the requested stream maximum, and actual materialized length replaces that reservation only after success.

Each cache object is a 128-bit-random `rainarchives-preview-*` file opened with `CreateNew`, `FileShare.None`, asynchronous random-access support, and delete-on-close. Publication happens only after the source lease reaches authenticated terminal completion, the file is flushed, and any declared length equals the materialized length. Rain rewinds that same exclusive handle and places it behind the returned read-only seekable wrapper; it does not reopen the name, expose the underlying `FileStream`, or expose the cache path. Closing either the stream or lease closes the complete lease and releases its byte/slot reservation. Cache disposal cancels pending admission, waits for in-flight materializations, and closes every published lease. Failure and cancellation attempt source, owned-file, reservation, and slot cleanup independently so a cleanup exception cannot hide the primary failure.

The cache owns only the random files it creates. It never recursively deletes or enumerates the caller's root, and an unavailable or newly reparsed root fails with a stable destination-conflict error. These checks are not a transactional same-user seal: a hostile process under the same account can race a path-based directory lookup, inspect process memory, or interfere through privileges outside ordinary file opens. Random `CreateNew` names prevent replacement of an existing object, exclusive sharing rejects ordinary second readers/writers while the lease is live, and delete-on-close bounds normal residue. Path-based Shell preview handoff, provenance propagation, and a stronger directory-ownership/handle model remain separate gates. True protocol range reads are also deferred; repeatedly reopening and discarding a compressed prefix would add wire complexity without providing efficient random access.

### Standalone bounded text presenter

`RainArchives.App` layers a presentation-only text policy over `ArchivePreviewCache`; it does not add a format provider or wire contract. A metadata-known file above 1 MiB is refused before a lease is opened. Every other request asks the worker for at most 1 MiB plus one byte, so understated or unknown metadata cannot turn preview into an unbounded transfer. The decoder accepts strict UTF-8 (with or without BOM) and BOM-marked UTF-16/UTF-32. NUL-bearing, invalidly encoded, or control-dense payloads are reported as unavailable. The displayed value preserves CR/LF/tab but visibly escapes backslashes, controls, format/bidi characters, line/paragraph separators, and invalid UTF-16; no HTML, script, rich-text, executable, Shell, or external-handler path is invoked.

Preview requests use a serialized latest-wins pump: at most one request is active and one newest request is pending. Selecting another entry cancels the active request and replaces any older pending request, but the successor does not begin until the active cache/stream/password cleanup has settled. Opening another archive, starting an archive operation, navigation, closing the panel, and app disposal cancel and drain the pump. An encrypted preview creates a temporary secret slot only after a cancellable prompt; a refused-before-read result does not mark the session password as validated, and every temporary slot is explicitly discarded if the worker did not consume it. These rules are independently testable at the App/Client seam and do not belong in Rain Explorer UI types.

## Explicit nested archive sessions

Protocol 1.6 adds optional feature `nested-archive-open`. Nested work is never automatic: listing, search, preview, and ordinary open do not sniff or recurse into entry payloads. A caller must explicitly select exactly one material file entry in an already-open parent session. The parent must be a proven `Single` volume set and must advertise extraction. Confirmed multipart and unknown-volume parents remain list-only, directories and synthetic hierarchy entries are refused, and solid sources without a provider path that can avoid unbounded skipped-member decoding return `nested.solid_source_deferred`. The checked solid-RAR session therefore remains test/extract-only; this is a safety gate, not a format-name assumption.

The worker integrity-copies the selected entry through `IArchiveHandle.CopyEntryToAsync` into one worker-owned temporary source. It does not use the preview stream transport, does not accept an output path from the client, and does not send the temporary path over IPC. The copy is guarded by checked actual-byte accounting, declared-size preflight, expansion-ratio and free-space checks, cancellation inside the provider copy, and parent source-fingerprint checks before and after materialization. Defaults and hard ceilings are explicit: 64 MiB per request by default, 256 MiB per request, 256 MiB across one ancestry chain, 512 MiB reserved across all active nested sessions and in-flight nested opens, and three nested levels. The ordinary 16-session worker cap still applies. A reservation is made before decoding and reduced to exact materialized bytes only after successful completion, so understated metadata cannot bypass either the write guard or global accounting.

The worker lazily creates one 256-bit-random `rainarchives-worker-*` workspace only when its first nested-open request needs storage. A token-bound `owner.lock` is flushed before use and retained with exclusive sharing as the worker's liveness claim. Each `OwnedTemporaryArchiveSource` then creates a separate 256-bit-random `rainarchives-nested-*` child inside that pinned workspace with one fixed `source.archive`. The child root shares delete only with the already pinned workspace handle; ordinary extraction and standalone temporary roots retain the stricter non-delete-sharing parent pin. Creation, validation, immutable source reopening, and deletion remain handle-relative, and the completed source lease denies write/delete sharing while the child session is open.

Cleanup deletes only the exact file and directory identities Rain created; it never recursively follows a name or erases unexpected content. If a same-user process inserts an unexpected object, cleanup fails visibly and preserves that object, while byte reservations, password slots, and execution/session admission are still released. Ordinary failure, cancellation, child close, client close, and graceful worker shutdown remove exact owned child sources, and graceful worker shutdown removes the empty claimed workspace. A hard process termination can still leave the workspace and its children. Each later worker performs a bounded startup inventory of at most 1,024 matching direct children and classifies them as active, token-verified but unlocked potential stale state, or suspicious/unreadable state. Inventory is deliberately non-destructive: archive descriptors report count-only/path-free `worker.nested_recovery_required` or `worker.nested_inventory_incomplete` warnings, but no automatic recovery or deletion occurs. A valid liveness token, directory-name pattern, or unlocked owner file is not by itself deletion authority. An isolated packaged-worker test proves exact-PID hard-kill persistence and reporting by a second worker.

Protocol 1.11 feature `nested-workspace-recovery-v1` adds explicit path-free review and discard without making startup destructive. List returns only sorted opaque 256-bit IDs that a fresh exclusive inspection currently classifies as verified disposable, plus aggregate active/suspicious/incomplete counts. Inspecting one ID returns no path or filename; it returns the assessment and, only for verified disposable state, a SHA-256 metadata revision, source count, total bytes, and last-write time. A disposable workspace is either the exact inactive owner-plus-source shape or the exact empty ownerless shape left if an ordinary cleanup or prior discard reached its final two deletes. It must contain no extra object, reparse point, named stream, or multiply linked file; every workspace/owner/source object must have a stable nonzero filesystem identity. At most 16 source files, 256 MiB per source, and 512 MiB aggregate are reviewable. Identity-unavailable, active, changed, excessive, inaccessible, or unexpected state remains non-actionable and preserved.

Discard accepts no path. The caller must repeat the exact lowercase recovery ID, revision, source count, and total bytes returned by inspection; the CLI additionally requires `--confirm` with that same ID. Security reopens the temporary root and exact-case descendant names through handle-relative no-follow handles, compares the complete identity/shape/length revision, and repeats all checks immediately before mutation. Cancellation is honored until the first exact source deletion and then ignored through the bounded source-files, source-directories, owner, and workspace sequence. There is intentionally no durable intent receipt for nested discard: a process kill after mutation begins can leave a smaller suspicious tree, which later workers preserve rather than infer as completed. A missing tree after an uncertain result is not success evidence and is never automatic retry authority. A real authenticated Worker/IPC test kills the exact owned PID after the first deletion and proves `worker.disconnected`, conservative restart classification, and no follow-on deletion. Automatic nested cleanup remains forbidden.

The materialized child is a normal independent archive session detected from content. Its response binds a fresh child session ID to the parent session ID, source entry ID, depth, exact materialized bytes, and chain bytes. It inherits validated download provenance from the outermost source so later extraction can propagate `Zone.Identifier` without trusting the temporary file's own ADS. Parent and child password needs use distinct optional secret slots; both are settled on validation failure, queue cancellation, decode/open failure, or success, and password arrays are zeroed on every normal path subject to managed-runtime/provider limitations.

Protocol 1.21 feature `nested-workspace-independent-reopen-v1` separately reopens one recovered source without reconstructing its old parent chain. The request repeats the freshly inspected workspace revision/count/aggregate bytes plus the exact source ID and length. Security again exclusively pins and reparses the inactive workspace, validates the owner/source ancestry, liveness, stable identities, exact ordinary type, one-link/no-reparse/no-ADS shape, bounds, and revision, and holds the selected source without write/delete sharing while copying it into a new operation-owned temporary source. The new copy is flushed before detection and session registration. The response is a new independent root with depth zero and ancestry-chain bytes zero; it contains no former parent/source relationship or temporary path. The original recovery workspace is not mutated or adopted. An exact-PID kill immediately after the copied source flush proves the original source/evidence/hash remains unchanged, restart warning remains path-free, the copied source is preserved rather than guessed away, and a fresh ordinary Worker can inspect/reopen/list the original recovery at depth zero.

`ArchiveClient` records nested parentage only for sessions it opened successfully. Closing a parent through the client closes all known descendants deepest-first before closing the parent; cancellation that races a successful child response performs a compensating close. At the raw protocol layer a child is intentionally independent, so closing a parent alone does not implicitly invalidate an already-materialized child. A protocol-1.21 recovered session is deliberately a new root and may open new nested children from depth zero under the ordinary limits. Rain Explorer must persist a chain of archive fingerprints plus persistent source-entry keys for ordinary history and reopen; it must never infer a former chain from a recovery ID or persist an ephemeral child `EntryId` as durable location identity.

## Filesystem boundary

The security layer receives provider-neutral entry metadata and streams. Its M1 transaction is:

1. Require a nonexistent final destination with an existing, non-reparse parent.
2. Resolve the complete selection through parent-ID adjacency, with cancellation and provider-order output, then preflight every path/collision/resource limit.
3. Pin every existing component of the absolute destination-parent chain, then create the random sibling staging directory relative to the pinned parent with Windows `NtCreateFile` create-new semantics.
4. Reopen or create every staging descendant relative to verified directory handles with `OBJ_DONT_REPARSE` and `FILE_OPEN_REPARSE_POINT`. A replaced directory fails identity validation instead of redirecting a later write.
5. Ask the selected provider to checksum-copy each entry into a guarded handle-owned stream; validate actual decoded bytes, expansion ratio, filesystem-object charges, and free space while reporting progress. Apply timestamps and `Zone.Identifier` through the still-open file/parent handles. The runtime guard forces a new space check at least every 1,024 created objects even when few payload bytes have been written.
6. Flush and close output handles. Reopen every expected descendant relative to pinned handles and require the original file identity/type; require one hard link for every regular file; compare a filesystem enumeration with the exact expected manifest so extra, missing, type-changed, and reparse entries fail closed; repeat handle/identity validation; revalidate the source fingerprint; and require that the final destination is still absent.
7. Commit the staging directory with `NtSetInformationFile(FileRenameInformation)`, naming it relative to the still-pinned parent handle. The staging handle is opened with `DELETE`; replacement is disabled, so a destination that races into existence makes the kernel fail the commit rather than overwrite it.
8. Remove an operation-owned staging tree after ordinary failure or cancellation only after identity and no-reparse checks. Preserve replaced/reparsed staging and any unexpected path, hard link, named stream, unavailable identity, or otherwise suspicious shape for recovery rather than recursively deleting it.

Existing-directory merge, overwrite backup, and in-place archive replacement are later milestones and must not be approximated by direct writes. Protocol 1.12/1.13 new-copy rebuild uses the creation transaction below and never targets the original. A hard-killed worker may leave operation-owned staging, or may leave a fully renamed final destination when the kill occurs after commit but before terminal acknowledgement. Cross-restart reconciliation/cleanup must inspect durable ownership and operation state and must never rely on a filename pattern alone.

Protocol 1.20 adds the read-only extraction-recovery layer around that transaction without changing
the extraction request shape. Before staging, the Worker creates an exclusive local fixed-drive
journal whose random token determines the sibling staging name. Five append-only, 256-KiB-bounded,
SHA-256-checked full-state frames record `Admitted`, `Writing`, `Finalized`, `CommitIntent`, and
`Committed`. `Writing` binds the staging identity; later phases bind regular-file/directory counts,
the aggregate bytes of unnamed data plus allowed `Zone.Identifier` streams, named-stream count, and
a deterministic tree revision over exact-case paths, stable nonzero identities, lengths, every
regular-file SHA-256, and every allowed provenance-stream SHA-256. The journal remains live through
terminal delivery. A delivered terminal deletes only its exact local journal tree; lost transport
after publication preserves `Committed` evidence.

Startup inventory examines at most 1,024 direct local records and never contacts a recorded
destination. It validates the fixed operation/journal shape, nonzero enumeration identities,
checksums, strict JSON, immutable fields, and monotonic phases, then emits only sorted opaque IDs,
typed conservative state, timestamps, and bounded incomplete/suspicious counts. Explicit inspection
of one ID exclusively reparses the journal before opening exact-case destination ancestry and the
direct staging/final names through pinned no-follow handles. It rejects or conservatively reports
parent/root/object replacement, zero identity, wrong kind, reparse, hard link, unexpected ADS,
content/stream revision mismatch, excessive count/bytes/depth, access failure, liveness, and torn
tails. Both Worker and Client recompute the assessment; mutation, cleanup, and retry flags are fixed
false. There is no extraction disposition, automatic retry, recursive orphan deletion, restore,
replacement, or WPF action in this slice.

## Operation lifecycle, diagnostics, and owned-temporary convergence

Protocol 1.19 exposes a connection-lifetime snapshot rather than a durable queue. Start admission is
nonblocking up to 32 active/queued operations; one execution permit serializes provider work;
`QueueOrdering.Unspecified` is truthful because admission does not promise FIFO; and only the newest
64 terminal records are retained, for a maximum 96-record response. Starts are admitted before the
start response and then run in a background task. Terminal publication is authority, warnings remain
provisional, and `WorkerOperation.TryCancel` refuses a terminal operation under the lifecycle lock.
The activity response advertises only ID-targeted Cancel; pause, retry, reorder, and restart
persistence are false. A 33rd start fails closed before background ownership and settles any supplied
secret slot.

Start-operation requests intentionally are not registered in the request-cancellation table. Client
cancellation is therefore two-phase: before a start response, the Client completes its bounded
request-cancel/response wait and tears down only its authenticated owned Worker if the operation ID
cannot be recovered; after a successful start response, it sends `CancelOperation` for that ID and
waits for the authoritative terminal. This prevents an abandoned start from creating an unowned
operation without inventing response semantics. It is a documented containment boundary, not a
promise that provider work is cooperatively cancellable at every instruction.

`AwaitingPassword`, `AwaitingConflict`, and `AwaitingRiskApproval` are accepted enum values for
forward-compatible event validation only; the current Worker never emits them and there is no
decision-response wire. Passwords are one-shot binary secret slots and a wrong password is terminal.
Destination conflicts fail closed under absent-destination/no-replace policy, while current risk
approval is local WPF preflight. `ArchiveError.IsRetryable` is advisory metadata only: activity
`SupportsRetry` is false and no retry endpoint exists. A future continuation requires a durable
immutable operation descriptor, pre-mutation decision authority, expiry/replay rules, and proof that
the observed failure cannot hide a mutation or publication before any retry is enabled.

Protocol 1.22 feature `worker-support-diagnostics-v1` exposes only fixed enums, bounded integers, and
booleans for these policies. It reports queue/admission/retention bounds, supported controls,
automatic-recovery-disposition support, containment kind, and
`IsSameUserSecuritySandbox=false`. It contains no free-form message, path, filename, entry text,
provider string, exception text, secret, recovery ID, or mutation token. The CLI renders only those
typed fields. Standard Worker exceptions map to fixed messages and diagnostic UUIDs. Because
`ArchiveProviderException.Message` crosses the error seam, provider implementations must keep every
message fixed or policy-sanitized; generic exception text, filesystem paths, archive-controlled
control text, and provider stdout/stderr may not enter activity or future support evidence.

### Milestone 5A Activity Center presentation boundary

The standalone Activity Center is one connection-lifetime WPF presentation owner, not a new engine,
queue, recovery store, or protocol authority. `RainArchives.App` still references only Contracts and
Client. Main-window extraction, integrity, creation, and rebuild starts register their typed operation
ID/kind/display label with the shared model; authenticated operation events provide transient detail,
and protocol-1.19 snapshots remain the authority for retained membership, phase, progress, lifecycle
times, warning count, and cancelability. The model never receives a filesystem path or recovery
action. Live `CurrentEntryName` is escaped through `UntrustedDisplayText`; snapshots retain no entry
name or error text, and terminal presentation uses only the typed error category. Fixed-field
protocol-1.22 diagnostics are mapped one field at a time rather than reflected or serialized for
display.

The model runs no service and persists nothing. Its default one-second loop serializes activity then
diagnostics behind one gate, admits a 250 ms through 30 s testable interval, coalesces explicit refresh
signals, and never overlaps requests. Worker-event callbacks only update bounded locked state and
schedule at most one UI drain, so dispatcher delay does not block authenticated Client receipt. State
is capped at the protocol maximum of 96 rows; an unknown addition can evict only the oldest terminal
or incomplete placeholder, never an active operation. Every refresh is generation-bound, and
downgrade, malformed data, disconnect, or disposal fails to an explicit unavailable/offline state
without retry. Closing the modeless window removes only that view because the same connection-scoped
model authorizes the main-window Cancel action. App/client shutdown cancels and awaits the refresh
owner before Client disposal, then gives queued dispatcher work a bounded drain; delayed callbacks
observe disposal and cannot apply transport data.

Cancel is hidden unless the newest validated snapshot sets `CanCancel` for that exact operation ID.
A local pending state removes stale authority immediately, terminal events remove it authoritatively,
and a rejected/timed-out request is reported without blind retry. There is no pause, retry, reorder,
password/conflict/risk continuation, durable queue, or restart-history simulation. The Activity window
uses system colors, layout rounding, keyboard access keys, deterministic tab order, automation
names/help/live status, and recycling row/column virtualization. Those are enforceable automated
foundations; direct visual, Narrator, keyboard-only, High Contrast, mixed-DPI, and clean-machine
acceptance remain package-bound manual gates.

### Milestone 5B archive browser presentation boundary

The production browser remains a Contracts/Client consumer, not an archive index, filesystem, or
mutation authority. `MainWindowViewModel` lists sequential protocol pages of at most 4,096 records,
requires stable total counts and advancing exact offsets, rejects duplicate IDs across pages, and
prepares a replacement browser index before touching the current session. The browser then validates
the exact descriptor count, nonempty unique IDs, metadata bounds, parent existence/directory type,
acyclic hierarchy and maximum depth, and negotiated persistent-key shape before adopting anything.
Hierarchy validation uses an iterative functional-parent walk with one reusable path and visited/depth
maps. Each descriptor is visited at most once, a reverse-ordered cycle remains an explicit cycle, and
the 257th level is refused before increment; no descriptor ordering can create a recursive WPF stack.
The full presentation index is capped at 500,000 descriptors; one page materializes at most 512 row
models, breadcrumbs remain protocol-depth bounded, and history retains only its newest 256 locations.
The WPF grid enables row and column recycling virtualization. These are UI/resource bounds, not a
reason to raise protocol or Core limits.

`EntryId` identifies one record only inside its current session. Root and folder navigation,
Back/Forward branching, Up, breadcrumbs, current-folder Unicode ordinal-ignore-case search, paging,
and selection are deterministic presentation operations over that ID. A persistent
`(NormalizedPath, DuplicateOrdinal)` key can resolve history or selection only after an explicit
same-canonical-source reopen whose complete protocol fingerprint—length, last-write time, file
identity, and prefix SHA-256—matches, with the persistent-key feature negotiated by both sessions and
the prior browser in `Ready` or `Empty`. The new session is fully listed and prepared before the old
session close starts. Once close starts, a refusal, timeout, malformed response, or non-authoritative
close clears the old session/browser authority; it is never revived from cached rows. A failed second
open before that close preserves the still-authoritative old session. Changed fingerprint, different
source, stale/error/offline state, or downgrade resets to root with no restoration.

Archive-controlled names, paths, format/method labels, breadcrumbs, status, and automation names are
rendered through the shared visible-escape boundary; provider identity, warning/error messages,
exception text, temporary paths, secrets, and recovery identifiers do not enter browser state. The
only content preview remains the existing 1 MiB strict-text, control/format-escaped, read-only WPF
surface. Navigation never sniffs nested archives or invokes active content. Extraction, integrity,
creation, rebuild, recovery, source-fingerprint, secret-slot, staging, and commit decisions continue
through Client/Worker/Security. Keyboard accelerators, focus restoration, automation names/help/live
status, Per-Monitor V2 layout, system-color browser resources, explicit loading/empty/offline/
unsupported/error/stale states, and STA/source tests establish automated behavior. They do not
replace current-package visual, Narrator, keyboard-only, High Contrast, mixed-DPI, or clean-machine
acceptance.

### Milestone 5C settings, presets, and managed create/rebuild boundary

The Settings and Presets center remains inside the App and the WPF project still references only
Contracts and Client. App-private settings storage does not move archive/source/destination,
fingerprint, secret-slot, staging, recovery, provider, or commit authority into the presentation
layer. `ui-settings.v1.json` is a fixed 32-KiB schema containing one creation-format enum, seven
compression-preset enums, and one boolean; there is no free-form string member. Strict UTF-8 JSON
deserialization rejects unknown, missing, duplicate, malformed, invalid-enum, truncated, oversized,
and unsupported-version state. A validated backup may become explicit last-known-good state without
rewriting the primary, but those recovered hints are read-only and ordinary Save remains disabled
until explicit Reset. `zip-presets.v1.json` retains at most 64 records and has an exact 128-KiB file
ceiling; its bounded NFC name and strict required/unknown/duplicate-member rules remain unchanged.

`AppSettingsStore` and `ZipPresetStore` share the private
`%LocalAppData%\SailHub\Rain Archives\settings` ancestry but have distinct fixed files, backups, and
per-user mutexes. Every existing ancestry/file object must be ordinary, non-reparse, and ADS-free;
files must have one link and stable volume/file identity. A save writes a random `CreateNew` sibling,
durably flushes it, rechecks its exact identity plus destination/backup shape, and uses atomic
replacement or first move. Cleanup may delete only that exact staged identity. The two documents are
independently atomic rather than one transaction, so partial Save/Reset remains explicit and requires
review/reload. Ordinary ZIP preset Save re-reads and validates the current document while holding the
same mutex. The Create and Rebuild dialogs carry that exact state and recheck immediately before each
Save/Remove; non-Ready/non-Missing state leaves built-ins usable but keeps mutation closed until the
Settings Center performs explicit Reset. These path-based checks do not create a hostile same-user
sandbox or transactionally seal the directory namespace.

The Settings model serializes work, coalesces overlapping commands, generation-binds capability
responses, and cancels/drains storage/capability/UI work on close or App shutdown. Loading, defaults,
saved, reset, backup-recovered, corrupt, unsupported, unsafe, unavailable, offline, downgraded, error,
and closed states are explicit fixed presentation text. The system-color window adds access keys,
logical focus order, automation names/help/live status, DPI-safe scrolling, and recycling
virtualization over bounded format/preset rows.

Saved format/preset values are hints only. The create route queries the current authenticated Worker,
offers only ZIP/TAR/TAR.GZ/TAR.BZ2/GZIP/BZIP2 formats and presets in that response, then repeats
format-specific validation at start. TAR-family directory support stays capability-driven; raw
GZIP/BZIP2 require exactly one ordinary file. Eligible ZIP Rebuild Copy repeats a session-bound ZIP
capability query and clamps the saved rebuild preset before use. Both reviews require a distinct absent
destination, preserve an existing/raced target and every source/original, and describe modification as
a full guarded rebuild to a new copy. Encryption, filename encryption, split volumes, overwrite,
replacement, retry, pause, reorder, decision continuation, in-place update, native 7z, and RAR
creation have no control or simulated state.

### Milestone 5E Recovery Center and support-evidence boundary

The production Recovery Center is one modal owner over four Contracts/Client-only models: creation,
nested workspace, extraction staging, and support evidence. Each inventory remains local, bounded,
path-free, and non-destructive. A destination can be contacted and shown only after explicit
inspection of one opaque record, and the returned text is visibly escaped. Opaque recovery/source
identifiers authorize only the exact typed request that already accepts them; the App never derives a
filesystem path from an ID or receives a temporary workspace/cache path.

Creation retains the four protocol-1.10 disposition choices, fresh action-time evidence, and
default-No confirmation. Nested recovery retains only protocol-1.11 exact-evidence discard and
protocol-1.21 copy-only independent reopen. A reopened source becomes a new depth-zero session with a
fixed presentation title and no former ancestry; adoption prepares its complete bounded listing,
authoritatively closes any prior session, and closes the exact new session if adoption cannot finish.
The recovered workspace remains preserved after reopen. Extraction inspection remains protocol-1.20
read-only evidence: the surface has no disposition, cleanup, retry, restore, merge, or replacement
command. Selection change, refresh, malformed/downgraded/offline response, cancellation, failure, or
uncertain action completion clears presentation authority rather than simulating success or retry.

The dialog owns one initialization task plus all four child request owners. Close/disconnect requests
cancellation, then drain joins initialization and every child before returning to the shell; delayed
callbacks cannot apply transport data afterward. A password-required independent reopen may request
one fresh binary secret slot after an initial no-secret attempt, settles it through Client ownership,
and never retains or displays secret data. Protocol-1.22 support evidence is mapped field by field from
fixed enums, integers, and booleans; no fallback state is inferred. Provider/exception text,
archive-controlled text, command lines, generic deletion, and mutation authority never enter the
surface. System colors, access keys, ordered focus, Automation name/help/live-status metadata,
layout rounding, and recycling virtualization are automated foundations, not manual package-bound
accessibility acceptance.

### Milestone 5G-A main-shell presentation boundary

The App owns one semantic WPF resource layer: Light, Dark, and solid High Contrast palettes feed
shared spacing, typography, focus, control, card, DataGrid, and rounded-stroke vector-icon resources.
System mode reads the current per-user Windows app-theme preference without changing it; explicit
Light/Dark selection is application-local, and Windows High Contrast remains authoritative through
dynamic `SystemColors`. Theme application changes only App resource dictionaries and the App-owned
title-bar treatment. It does not write a Windows setting or add a settings-schema field.

Every custom background now has a separate semantic foreground role. High Contrast pairs Window
surfaces with `WindowText`, muted Control surfaces with `ControlText`, and selected Highlight rows
with `HighlightText`; headers, alternating rows, browser buttons, direct muted shell content, and row
icons all consume those pairs. Light and Dark map the same new roles to their prior colors, so this is
an accessibility repair rather than a visual redesign.

The main window composes one archive-identity/search header, tiered icon-and-text commands, a compact
navigation/breadcrumb strip, a dominant virtualized eight-column entry list, a wrapping two-column
welcome surface, one 320-380-DIP adaptive inspector, and one polite status live region. Keyboard order
follows that visual hierarchy. Entry-row Automation names combine visibly escaped entry text with
already validated typed safety and encryption facts; they never surface provider/error/path/secret or
other raw archive fields. Top command access keys, accelerators, native control semantics, recycling
virtualization, 500,000/512/256 browser bounds, and the M5F inspector reveal/cancel/drain/focus lifecycle
remain unchanged. The duplicate welcome-copy markers were removed so no-session Create and Recovery
have one active Alt+C/Alt+R owner while welcome actions remain ordinary tab stops with the same
Automation names and help.

This layer consumes only Contracts and Client. It introduces no protocol, dependency, provider,
filesystem, recovery, mutation, overwrite/replacement, retry, shell, or external-launch authority.
Resource, XAML, UI Automation, min-size, package, and product-flow tests are automated foundations;
exact-package visual, Narrator, keyboard-only, High Contrast, 100/150/200%, mixed-DPI, and clean-machine
acceptance remain separate manual gates.

### Milestone 5F selected-context and measured-interaction boundary

The App keeps one immutable presentation snapshot for selected-entry commands. It accepts only the
newest validated `ArchiveDescriptor`, ready/empty browser state, host-busy bit, current 512-row page,
and selected `EntryId` values. The current page is indexed once, duplicate identities are refused,
stale or absent identities are dropped, and a same-ID substituted view model is replaced by the
canonical current row. Navigation, properties, bounded safe preview, integrity, and selected-entry
extraction all re-resolve that snapshot immediately before calling their existing Contracts/Client
route. Window accelerators, Enter/double-click activation, and the row context menu share those same
commands and capability gates. No WPF type crosses IPC and no provider, filesystem, recovery, retry,
overwrite, mutation, shell, Open With, export, or external-launch authority is added.

Context status, menu labels, Automation names, and help text are fixed presentation strings. Existing
archive-controlled entry text continues through the visible-escape boundary and is never copied into
the command-status diagnostic. Mouse selection synchronizes before menu opening; selection/source/
session or browser-state change refreshes the context, rejects stale preview completion, and preserves
deterministic focus. The DataGrid retains recycling row/column virtualization, system colors, layout
rounding, and the existing 500,000-index, 512-visible-row, and 256-history limits.

The archive inspector is not ambient browser chrome. It starts collapsed and only a current authorized
Properties or Safe Preview command reveals its matching tab. One fixed Close control is keyboard-
reachable and Automation-described; it first invalidates the preview generation, cancels active and
pending preview work, joins the bounded pump, then clears and collapses the inspector and requests focus
for the current entry list/selection. The surface remains visible while that drain is outstanding, and
a late completion cannot repaint it. Session/source/state transitions reuse the same fail-closed
invalidation. There is no window-global Escape binding that could intercept dialog or operation
cancellation.

The package measurement is an automated production-model observation, not a competitor claim or
manual UI acceptance. A deterministic 20,546-entry ZIP is bound by exact length and SHA-256. One
warmup and three fresh packaged-Worker opens measure process-cold open/list; the retained final process
then exercises Unicode search, every root page transition, 320 folder/root round trips, 1,024
selection changes, 32 preview replacements plus drain, and five steady Worker samples. The evidence
attributes the WPF model to VSTest and the exact Worker PIDs separately, records runtime/machine facts,
raw and min/median/max summaries, retains all established limits, waits for every exact PID to exit,
and verifies zero Worker-root, preview-cache, or staging files.

Owned temporary convergence is class-specific rather than a generic recursive cleaner:

- Parser/index/session state is in memory and owns no durable temporary tree. Entry-stream pipes and
  buffers close with their lease/session/Worker; at most eight stream leases are active.
- Preview files are random `CreateNew`, exclusive, delete-on-close objects capped at eight leases and
  512 MiB. Disposal closes exact handles; it never enumerates or recursively cleans the cache root.
- Activity retention is in memory and count-bounded to 64 terminal records; it contains no path,
  error text, entry text, or secret.
- Extraction, creation, and rebuild normally remove only their exact identity-validated staging and
  journal state after settled terminal delivery. Creation `Completed` journal/receipt metadata alone
  has bounded newest-256/30-day retention with exact no-follow maintenance. Extraction has no
  disposition or automatic retention deletion.
- Nested storage is capped by 16 sessions/sources and 512 MiB aggregate. Normal child/session/Worker
  close deletes exact identities only; hard-kill residue is inventoried at most 1,024 records and is
  never automatically deleted or restored.
- Every startup inventory is local-only, bounded, path-free first, and non-destructive. It never
  contacts recorded destinations. Active, changed, malformed, identity-unavailable, excessive,
  inaccessible, partial, hard-linked, reparsed, streamed, or ambiguous state is intentionally
  preserved regardless of age or count; no filename, token, journal, or missing tree alone grants
  generic cleanup authority.

These are bounded active-allocation, replay-metadata, and inspection-work policies. Unverified disk
evidence intentionally has no destructive age/count retention deadline: imposing one would violate
the preservation rule by deleting precisely the states that cannot prove ownership or completion.
Such residue converges only through a future explicit, freshly revalidated disposition contract.

The protocol-1.7/1.8 creation transaction is separate from the extraction-tree transaction:

1. Validate the raw destination and every raw source/archive path before Windows normalization can hide a trailing-dot/space alias. Require an existing non-reparse destination parent and an absent final name.
2. Resolve case-insensitive NFC collisions, duplicate parent spelling, and file/directory-prefix conflicts. For protocol 1.8, reject same/overlapping selected roots, an explicit source located lexically inside a selected tree, and a destination located lexically inside a selected tree. Enforce 4,096 total material entries after expansion, 64 GiB of regular-file source bytes, a 64 MiB planning-metadata budget, the archive path/depth limits, a conservative output ceiling, and free-space limits.
3. Capture the destination-parent identity, then create an exclusive operation directory beneath Rain's local fixed-drive recovery root. Durably append a checksummed `Admitted` frame before any destination staging file exists. Its random 256-bit token determines both the recovery ID and exact sibling staging name.
4. Pin destination ancestry and create that journal-bound sibling file with native create-new/open-reparse-point/write-through semantics. Mark the handle with reversible deletion disposition before provider work, then durably append `Writing` with the staging file identity.
5. Reopen each explicit source through pinned, non-reparse ancestry with write/delete sharing denied. For directory roots, enumerate each pinned native directory handle and open each child relative to that handle; reject every reparse and bind the enumerated file ID to the reopened handle when Windows supplies one. Compare identity, length, and last-write time before and after the writer consumes exactly the planned length.
6. The selected writer emits explicit directory entries and regular files only to Rain's guarded destination stream. Actual output bytes, disk reserve, progress, cancellation, source-byte totals, and aggregate entry totals remain Rain-owned.
7. Complete and durably flush the ZIP and any canonical provenance stream. For protocol 1.8, re-enumerate each source root under the same limits and require the exact planned path/kind/identity/length/timestamp manifest. Verify destination parent/file identity and link count, recheck final-name absence, then append `Finalized` with exact staging length followed by durable `CommitIntent`.
8. Clear deletion disposition and perform a handle-relative no-replace rename. If commit fails after disposition clear, Rain attempts to restore deletion before closing. After a successful rename Rain appends `Committed`; an inability to persist that final frame preserves the prior journal and becomes a sanitized operation warning rather than falsely undoing the already-published archive.

Each frame contains the full current state and is bounded to 256 KiB; a journal has at most five frames. SHA-256 detects accidental corruption and torn tails retain only the last complete valid phase, but the journal is not an authentication boundary against another process running as the same user. The exclusive journal handle is a liveness claim while the operation runs. Startup examines at most 1,537 direct children of the local recovery root so retained completed receipts cannot consume the separate 1,024-record attention/publication bound. It validates exact token/name/phase transitions and reports only counts. It never opens, stats, or otherwise contacts a recorded destination, so an offline SMB server cannot enter worker startup through this feature. Unknown content, checksum failure, invalid transitions, and partially usable state are preserved and reported; no unresolved or unverified inventory result is automatic deletion authority.

Protocol 1.13 adds retained regular-file rename/move without changing the transaction. The closed Security planner resolves each exact persistent key to its leased source descriptor, rejects delete/replace/rename overlap, normalizes every final path through the shared Windows archive-name policy, permits simultaneous swaps only after building the complete final namespace, and preserves retained timestamps/basic attributes. The writer still checksum-copies retained bytes directly from the open provider handle and compares the finalized staged ZIP with the exact renamed count/order/path/kind/length/attribute/timestamp manifest. Worker and Client use a distinct negotiated message; a downgraded side refuses before payload or filesystem access. CLI and WPF only construct typed directives and never become source, destination, or provider authority.

Protocol 1.14 adds a bounded read-only directory plan and a distinct tree-edit start route without changing minor-12 or minor-13 messages. Security derives explicit/synthetic directory nodes and opaque subtree revisions from the exact leased snapshot, validates echoed revisions immediately before planning, and resolves retained subtree move/delete with exact file operations and mapped additions in one namespace. Incremental node/path budgets, component tries, and stable ordinal radix ordering keep planning bounded by snapshot/directive/path input instead of rescanning every subtree. A separate Worker-owned source-folder review route pins each selected root, refuses reparse and multi-link files, counts all roots under one entry/byte budget, and returns only canonical roots plus descendant/file counts. WPF gates before opening its picker and never enumerates a selected tree; CLI and WPF remain typed review surfaces only. Final writing, readback, journal, source revalidation, and no-replace commit remain the same transaction authority.

Protocol 1.15 adds a distinct metadata-edit start route without changing older rebuild messages. Each directive is typed as one retained persistent key or one mapped final path/kind, and Security resolves it only after the closed file/tree namespace plan using the same NFC/case collision keys. Editable state is deliberately limited to an exact timezone-less ZIP DOS last-write tuple and the ReadOnly/Hidden/System/Archive low-word mask; Directory is derived from entry kind. Client and Worker mirror exact shape, calendar, mask, duplicate, count, path, and frame validation. The ZIP provider writes these values and reopens the completed staging stream to compare the exact normalized tuple and forced-kind low word before commit. CLI and WPF remain typed review surfaces and do not substitute filesystem/provider UTC data for unavailable raw ZIP-local state. Comments, extra fields, ACLs, owners, ADS, creation/access times, POSIX modes, links, original methods, and arbitrary external attributes remain outside authority and are not claimed as preserved.

Protocol 1.16 adds the separate `zip-write-capabilities-v1` query and
`archive.zip.write.capabilities` response. A global query reports managed-writer creation support;
an opened-session query reports exact session-bound rebuild eligibility. The response enumerates the
supported add/replace/delete/file-rename/tree-edit/metadata operations, four immutable built-in
compression presets, current material-entry/decoded-byte bounds, and explicit false values for
encryption, filename encryption, split output, existing-destination replacement, and in-place
update. Client and Worker validate ownership, session state, negotiated dependencies, and the
requested operation before source or destination access. CLI and WPF use the typed result at both
preflight and write boundaries, so missing or downgraded capability state is an honest refusal.

The reusable preset surface is App-owned only. `ZipPresetStore` persists supported compression and
metadata defaults in a 128-KiB/64-user document at
`%LocalAppData%\SailHub\Rain Archives\settings\zip-presets.v1.json`; it is
not Worker, Security, provider, or archive-operation authority. Built-ins remain immutable. User
names/defaults are bounded and NFC/ordinal-ignore-case validated, unsafe Unicode is refused,
reparse roots/files are refused, one per-user mutex serializes writers, and random CreateNew
siblings are flushed and atomically replaced with at most one last-known-good backup. Failed saves
preserve the prior document, while malformed, oversized, future, missing, or interrupted state
falls back to built-ins with a visible non-secret warning. Metadata defaults apply only to mapped
entries and explicit per-entry edits take precedence; secrets, paths, recovery IDs, and untrusted
archive text never enter the store.

Ordinary failure/cancellation leaves neither a final archive nor staging file and removes only an exact identity-verified single-journal tree. Unexpected recovery-directory content makes cleanup fail closed and remain visible as suspicious state. Exact-worker tests prove that process death during a real 128 MiB compressed copy and after durable writer finalization while the maximum-entry tree is in final manifest validation leaves no final archive and no delete-pending staging file; the first case additionally leaves a parseable `Writing` record carrying the real operation ID, destination, and staging identity, and a second worker reports it without deletion or path disclosure. A kill between native file creation and deletion disposition may leave the recorded sibling with only `Admitted`; a kill after durable `CommitIntent` but before rename may leave the staging identity; and a kill after rename may leave the final destination with either `CommitIntent` or `Committed` but no delivered terminal event.

Protocol 1.9 feature `archive-creation-recovery-inspection-v1` exposes two evidence-only requests. Listing reruns the bounded local inventory and returns sorted opaque IDs plus path-free phase/count data; it still does not contact destinations. Inspecting one strict lowercase-hex ID exclusively reopens and reparses that journal before destination access, then Security pins the ordinary non-device destination-parent ancestry without following reparses, compares the parent identity, and observes only the direct staging/final names with no-follow handles. Regular-file identity, phase-bound length, kind, reparse state, and hard-link count produce a conservative assessment; a torn tail always remains incomplete, `Admitted` can never establish ownership, and conflicting/multiple artifacts never become cleanup authority. The client independently validates paths, enums, lengths, phase/state compatibility, and recomputes the assessment; malformed evidence aborts the authenticated transport. Inspection remains read-only and grants no implicit authority.

Protocol 1.10 feature `archive-creation-recovery-disposition-v1` adds one explicit reconciliation request with four closed actions: commit an exact `CommitIntent` staging artifact, discard one identity-bound staging artifact, accept one exact published final, or acknowledge that no owned artifact remains. The request contains no arbitrary path or overwrite target; it repeats the opaque ID and exact inspected journal phase, sequence, and UTC record timestamp. Both Client and Security reject malformed revisions, and Security exclusively reopens/reparses the journal before comparing that revision. It then pins and rechecks destination ancestry, freshly classifies staging and final artifacts, and keeps the exact staging file open with write/delete sharing denied while revalidating identity, phase-bound length where required, kind, reparse state, and single-link ownership immediately before mutation. Commit is a handle-relative no-replace rename; discard marks only that exact open handle for deletion; accept and acknowledge mutate no destination artifact. Parent path/identity is checked immediately before and after the action, and the final artifact assessment must match the action's postcondition.

Each recovery operation directory may contain one bounded `disposition.bin` with at most two append-only, SHA-256-checked full-state frames. `Intent` is durably flushed before any artifact mutation; `Completed` is appended only after the postcondition is re-inspected. A durable same-action intent makes restart idempotent: the worker recognizes an already-applied commit/discard, appends completion, and later replay returns `AlreadyCompleted` even when the destination path is no longer available. A different action or revision conflicts; malformed receipt data remains suspicious and is never mutation authority. An empty or valid truncated first frame and a torn tail after a valid intent are recoverable by truncating to the last valid boundary before continuing.

Completed receipts are omitted from the attention inventory and retained as a bounded replay cache: at most the newest 256 remain eligible, and any receipt older than 30 days is eligible regardless of count. A successful new resolution attempts one count-prune; worker startup performs one pass capped at 256 deletions and 1,537 examined operation children. If enumeration is truncated, age pruning may still use each candidate's fully validated completion time, but count/rank never becomes deletion authority. Every candidate is reopened exclusively relative to the pinned local root, reparsed, required to contain exactly `journal.bin` plus `disposition.bin`, and required to have matching enumeration identities, ordinary non-reparse type, and one hard link per file. Security re-enumerates and revalidates the exact tree immediately before marking the receipt, journal, and then-empty operation directory for deletion by handle. It never opens the recorded destination. Cancellation is checked before mutation and then deferred through this small deletion sequence; a race or failure can at worst leave a smaller suspicious metadata tree, never trigger destination mutation. Expired replay returns path-free `recovery.not_found`. The normal steady state remains at the count bound; a pre-existing larger inventory converges through bounded startup passes rather than making startup deletion work unbounded.

The worker serializes destination probes and disposition. Cancellation first uses `request.cancel`; a bounded control acknowledgement and then a separate two-second original-response deadline end in closing the client-owned kill-on-close Job Object if synchronous filesystem I/O remains stuck. That is process containment, not proof that a raw protocol consumer or every SMB redirector is bounded. Deterministic checkpoint tests are now paired with exact-PID external termination through the authenticated Client/Worker request path. A non-shipping wrapper calls the production `WorkerProgram`; it differs only by supplying an internal synchronous callback that signals after durable intent or after artifact mutation and then blocks. One exact authenticated-Worker kill leaves the staging artifact and no final; the other leaves the byte-exact final and no staging artifact. The interrupted client reports `worker.disconnected`, a fresh ordinary Worker completes each intent once, and later replay succeeds after the destination parent moves. This closes the Worker/IPC micro-checkpoint gate without exposing a command-line or environment probe switch in the shipping Worker. The disadvantage is that the first process image is the test wrapper rather than the shipping apphost, so package-composition changes still require normal packaged-worker qualification; an exact shipping-binary test would replace this evidence if a safe inherited-handle checkpoint mechanism becomes necessary. Blocked-UNC inspection/disposition cancellation remains required.

The precommit checks are detection, not a transactional seal against another malicious process with the same user token. Empirical Windows probes showed that denying write sharing on a retained directory handle does not prevent child creation, and retaining a file handle does not prevent a new hard link. Microsoft also documents directory oplocks as advisory for directory-content changes in [`FSCTL_REQUEST_OPLOCK`](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_request_oplock). If hostile same-user processes enter scope, the design gate is a broker running as a different principal with private ACLs and handle-relative cleanup—not another path scan or share-mode flag.

The handle-relative implementation follows the documented Windows [`NtCreateFile`](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntcreatefile) `RootDirectory` model, [`OBJECT_ATTRIBUTES`](https://learn.microsoft.com/en-us/windows/win32/api/ntdef/ns-ntdef-_object_attributes) flags, and [`NtSetInformationFile`](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntsetinformationfile) rename contract. Directory-tree enumeration uses official [`GetFileInformationByHandleEx`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex) information classes and the documented [`FILE_ID_BOTH_DIR_INFO`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_both_dir_info) layout. The source, staging, and destination-parent handles remain pinned for their required lifetimes. Path-string overlap admission cannot prove every 8.3, mapped-drive, DFS, or alternate-UNC alias, and a filesystem that supplies no usable enumeration file ID weakens enumeration-to-open binding; both remain explicit adversarial/compatibility gates backed by final manifest revalidation rather than claimed solved.

## Enforceable security invariants

- Archive-controlled paths never become absolute output paths. Traversal, rooted/UNC/device/drive-relative names, alternate data streams, Windows reserved names, trailing-dot/space aliases, case/Unicode collisions, duplicates, and file/directory prefix conflicts are either rejected or represented as explicit non-extractable safety issues before output creation.
- Archive links never become filesystem links during extraction. Symbolic links, junctions/reparse points, and hard-link metadata are refused; existing destination ancestry is opened without following reparses, and every created regular file must still have exactly one hard link before commit.
- Providers never receive a user-selected extraction destination and never perform directory extraction. They emit metadata or checksum-validated decoded bytes into Rain-owned bounded streams only.
- Writer providers never receive source or destination filesystem paths. They consume already-open stable source streams and emit only to a Rain-owned guarded staging stream; Rain alone owns path policy, resource checks, cleanup, and no-replace commit.
- Declared sizes, compressed sizes, counts, CRCs, offsets, and ratios are untrusted hints. Checked arithmetic, actual-byte ceilings, object/depth/count limits, free-space rechecks, checksum semantics, and cancellation remain authoritative at runtime.
- No partial extraction tree is presented as success. A new-destination operation publishes only through the identity-bound no-replace commit after exact-manifest and source revalidation; ordinary failure/cancellation removes only proven operation-owned identities, while suspicious mutations are preserved for recovery.
- Source identity is held and rechecked for every data operation. An archive is never allowed to overwrite itself, and an underlying mutation invalidates the session rather than mixing metadata or bytes from different source states.
- ZIP creation sources are ordinary absolute Windows regular files or explicitly selected directory roots opened through pinned non-reparse ancestry. Regular-file identity, length, and last-write time match the plan before and after copy. Every directory tree remains reparse-free and must reproduce the exact bounded path/kind/identity/length/timestamp manifest after ZIP finalization; the final destination is absent until one durable handle-relative commit.
- ZIP Rebuild Copy retains only exact session-bound entry keys, integrity-copies those entries from the still-leased original, and validates the source archive again immediately before commit. One global post-edit count/decoded-byte budget covers retained and mapped material, while actual streamed bytes, expansion, output, disk, cancellation, and new-source provenance remain authoritative throughout.
- Password material never appears in command lines, JSON control frames, ordinary logs, telemetry, filenames, or exception messages. Secret-slot ownership is single-use and every admitted path settles it; zeroization is best effort because managed strings, copies, and provider internals cannot be proven erased.
- Nested archives are opened only by explicit request. Depth, per-entry, ancestry-chain, global-active-byte, ordinary session, decoded-output, ratio, disk, and cancellation limits all apply before another parser session is registered.
- Nested temporary storage is worker-owned, lazily created beneath a token-bound exclusive liveness claim, and never exposed over IPC. Startup inventory is bounded and non-destructive; it may report only classification counts and never treat a name pattern as deletion authority. Explicit discard requires a user-reviewed path-free revision/count/byte tuple, complete no-follow identity/shape validation, and exact revalidation immediately before bounded deletion; unverified, active, changed, partial, or suspicious state is always preserved. Independent recovery reopen additionally repeats the exact selected source ID/length, copies from a freshly pinned verified source, and registers only a new depth-zero session without mutating or exposing the recovery tree.
- Archive-creation recovery startup remains local-only and path-free. Destination access requires an explicit opaque-ID inspection or disposition request. Inspection is read-only and grants no authority. Disposition requires a user-selected closed action, exact inspected journal revision, fresh identity/length/link/reparse evidence, a durable pre-mutation intent, and a verified postcondition; it accepts no arbitrary path, overwrite target, automatic choice, or generic retry. Automatic maintenance may delete only a fully verified expired/count-excess `Completed` journal/receipt tree through exclusive handle-relative no-follow deletion; it never treats unresolved, truncated, suspicious, active, or destination state as cleanup authority.
- Extraction-recovery startup is independently local-only and path-free. An explicit opaque-ID inspection is the only route that may contact or disclose one recorded destination, and it grants no mutation, cleanup, retry, restore, or replacement authority. Pinned inspection requires stable nonzero identity, ordinary no-reparse shape, one-link files, only the exact expected provenance ADS, bounded objects/bytes/depth, and content-bound default-stream plus provenance-stream revision evidence; active, torn, changed, excessive, inaccessible, or ambiguous state is preserved.
- Worker isolation contains parser crashes and bounds owned process memory/lifetime; it does not sandbox filesystem reads from the same user, defeat a malicious same-user process, guarantee parser correctness, or by itself remove files left by a hard kill. Fixed support diagnostics report this limitation explicitly rather than converting it into a security claim.
- Download provenance is inherited from the validated outer source and applied where the destination filesystem supports ADS. Failure to apply provenance is explicit; it never silently changes archive integrity or path-safety decisions.

## Protocol compatibility

Protocol 1.23 keeps the control pipe's five-byte frame header: one frame-type byte and one little-endian 32-bit payload length. JSON control payloads are limited to 1 MiB and depth 32, reject unknown members, and secret payloads are limited to 16 KiB. Typed payloads serialize once into a bounded buffer, so an oversized object fails before any frame bytes are written instead of first allocating an unbounded intermediate JSON document. Listing pages also stop at a conservative encoded-byte budget; a single entry too large for one response produces a structured resource-limit failure. Secret frames contain only a slot GUID and bytes. Major-version mismatch fails; later minor versions remain additive and feature-negotiated. Minors 1-16 retain the established required, read, recovery, ZIP create/rebuild, and ZIP capability features. Minor 17 adds distinct `tar-create-v1`, `tar-create-directories-v1`, and `tar-write-capabilities-v1`. Minor 18 adds distinct TAR.GZ/TAR.BZ2 explicit/tree requests, raw GZIP/BZIP2 requests, and `managed-write-capabilities-v1`. Minor 19 adds only `operation-activity-v1`, dependent on operation events and request cancellation. Minor 20 adds `extraction-staging-recovery-inspection-v1`, dependent on new-destination extraction, source-fingerprint validation, and request cancellation. Minor 21 adds `nested-workspace-independent-reopen-v1`, dependent on nested-workspace recovery, nested archive open, secret slots, source fingerprint validation, and request cancellation. Minor 22 adds `worker-support-diagnostics-v1`, dependent on operation activity and request cancellation. Minor 23 adds distinct `rar-create-v1`, `rar-create-directories-v1`, and `rar-write-capabilities-v1`; the write capability is conditional and does not imply the encoder is present. A client refuses operations newer than the negotiated minor locally without sending a frame; a Worker negotiated below a message's minor rejects it before payload deserialization or source/destination access, and dependency-incomplete negotiation is refused. Exact typed JSON, unknown/missing-member, oversize, downgrade, and framing fixtures lock the new bounded routes, while retained 1.12-1.18 binaries prove common operations and local refusal against current source. Minor 22 omits every RAR creation feature. This preserves each older negotiated shape without weakening current-minor validation.

The managed-creation compatibility evidence extends this additive sequence through minor 18.
The retained 1.12-1.16 checkpoints continue to pass their common-operation matrix. The narrow
`artifacts/RainArchives.ProtocolCompatibility.1.17.64081B70` checkpoint contains genuine minor-17
Contracts/Client/Worker binaries plus a harness and fixture, but no exact source snapshot; it is
truthfully binaries-only. Current↔1.17 tests prove ZIP/TAR common operations, real TAR creation,
and local pre-access refusal of every minor-18 compressed/raw/capability route. The exact
`artifacts/RainArchives.ProtocolCompatibility.1.18.1C85F6D9` checkpoint additionally retains the
132-input source snapshot, private Worker, Client/Contracts binaries, harness recipe, and managed
pair proof. The current compatibility class passes 17/17 retained-binary cases: current Client
against the retained 1.18 Worker refuses activity, extraction recovery, independent reopen, and
diagnostics locally while prior operations continue, and retained 1.18
Client against the current Worker negotiates only minor 18 and completes its managed route. Capability responses are advisory: the owning Client/Worker
revalidates negotiation, provider authority, request shape, source, and destination at the write
boundary. No UI type crosses IPC, and no capability response grants filesystem or provider authority.

Requests use one contiguous connection sequence; responses echo the matching request ID and sequence. Request IDs cannot be reused during a connection, and a connection is capped at 1,000,000 accepted IDs. The client bounds ordinary pending requests to 256 and control requests to 8. Once a frame write starts it uses the connection lifetime rather than caller cancellation, preventing a cancelled half-frame from corrupting later traffic.

For a negotiated cancellable archive open, the client keeps the original request pending and sends `request.cancel` on the control lane. The worker registers the target cancellation source before accepting the next frame, lets cancellation/control messages bypass the regular dispatch gate, and sends the original request's structured authoritative result even when Open was still queued behind the dispatch gate. A control cancellation is surfaced only after the worker accepts it or the owned transport/Job is torn down; a false acknowledgement means the start result remains authoritative and is not discarded. The client and worker each admit at most eight concurrent control requests. Every valid password-bearing Open settles its slot on success, cancellation, or ordinary failure; discard after consumption is harmless. If completion wins and returns a session, the client compensates by closing that session before surfacing cancellation. Cancel, discard, compensating-close, and worker-close acknowledgements have real two-second bounds; expiry invalidates the transport, closes the authenticated pipe, releases pending slots, and closes the Job Object that owns only that launched worker. Post-write request waiters stay attached to their pending-response records; response-loop shutdown fails those records explicitly, avoiding a response-versus-shutdown-token race. The synchronous SharpCompress archive-construction call itself remains non-interruptible; cancellation is checked before/after it and through Rain-owned metadata/index construction.

Entry-stream setup is also request-cancellable until its response. After handoff, `entry.stream.cancel` uses the same separately admitted control lane and a stream-specific cancellation source. Closing the data pipe remains an immediate independent signal; the control acknowledgement is what interrupts a provider call before its next write. Closing an archive first disposes and settles every client-owned lease for that session. Worker shutdown cancels all lease sources and drains their tracked tasks before session handles or admission semaphores are disposed.

Password-bearing extraction and integrity starts use an explicit ownership handoff. Client-local validation and pre-response send failures discard the slot. The worker discards on every validation, session, capability, reservation, queue, or response failure before task handoff. After the background task exists, that task owns the slot and unconditionally settles it on queued cancellation, provider failure, or completion. Start-operation requests are not registered in the Worker request-cancellation table. Caller cancellation first uses the Client's bounded request-cancel/start-response settlement; if the start response wins, the Client obtains the operation ID and sends `CancelOperation`, while an unresolved pre-response start ends through transport abort of only that Client-owned Worker. It does not race a client secret discard against a successfully handed-off operation.

Events use contiguous per-operation sequences and validated phase transitions. The client authorizes an operation ID only from a successful start response; malformed output or events for unknown operations fail the transport and terminate the owned worker. While the client remains active, every validated event enters a bounded presentation queue in order; the queue waits when full instead of dropping progress, warnings, or terminal events. Terminal waiters are completed on the transport loop before the terminal event is enqueued, so presentation backpressure cannot prevent the authoritative terminal result from settling. Presentation subscribers must return promptly or post work to their UI dispatcher because a persistently blocked callback intentionally propagates bounded backpressure to later IPC reads. Explicit client disposal cancels presentation dispatch and does not promise to drain callbacks that the caller abandoned. Terminal phases must use terminal events, post-terminal events fail the transport, the first terminal state is preserved, a terminal result can be consumed once, and retired state is bounded.

Protocol 1.19 projects those successfully sent events into one bounded path-free activity snapshot.
The response reports the exact policy—32 active/queued admissions, one concurrent execution, newest
64 terminal records, unspecified non-reorderable queue presentation—and explicitly reports pause,
retry, reordering, and restart persistence as unsupported. Records omit paths, entry IDs/names,
warnings/errors, secrets, recovery IDs, provider details, and archive-controlled text. Event frames
and snapshots are ordered by the same authenticated write gate; membership/history transitions use a
separate lock, and the Client independently validates IDs, UTC time order, phase/terminal coherence,
numeric progress, sorting, and both active and terminal bounds. This is connection-lifetime status,
not durable recovery, retry authority, or an App/Rain Explorer Activity Center.

The worker admits at most 16 sessions, 32 queued operations, and eight entry-stream leases; it allows at most two concurrent archive-open/index jobs, two preview decode executions, and one decompression operation. Cancellation and close control messages use separately bounded admission so they cannot sit behind a long open/index queue. Close waits for accepted session reservations; shutdown cancels queued/running operations and streams and drains tracked tasks before releasing sessions.

Entry IDs are stable only for the life of an open session. Duplicate names retain separate IDs. When `persistent-entry-keys` is negotiated, every descriptor carries an explicit normalized internal path and deterministic duplicate ordinal; those keys remain stable across reopen for an unchanged archive fingerprint and provider ordering even though session entry IDs change. Persistent navigation must use archive fingerprint plus that key, never an old entry ID.

Hierarchy reconstruction has its own depth and synthetic-directory budgets; provider-entry limits alone are not treated as a sufficient memory bound. Path-collision diagnostics use constant-time de-duplication and bounded reporting.

Archive names, methods, paths, warnings, and errors cross the core boundary as raw semantic values. Presentation clients must pass archive-controlled text through the shared visible-escape formatter before terminal or WPF display. This removes terminal controls, bidi/format controls, line separators, invalid UTF-16, and ambiguous backslashes without changing IDs, search, selection, or extraction behavior.

## Project dependency rules

- Contracts depends only on the framework. It contains DTOs, identifiers, errors, capabilities, and framing.
- Core depends only on Contracts. It owns provider-neutral detection, metadata/indexing, fingerprints, source leases, and separate read/write provider interfaces.
- Security depends on Core and Contracts. It owns Windows name policy, resource accounting, provenance, staging, and commit.
- Provider projects depend on Core and Contracts, never Client, Worker, WPF, or Security. `Providers.SharpCompress` implements reads plus qualified TAR/compressed/raw writers; `Providers.Zip` implements the framework-backed ZIP writer. Providers remain path and commit agnostic.
- Worker depends on Contracts, Core, Security, and provider projects. It is the only shipping process that loads codec packages/native code.
- Client depends only on Contracts. CLI, WPF, and future Rain Explorer code depend on Client and Contracts.
- Rain Explorer must not reference providers, Security, Worker implementation types, or WPF types from Rain Archives.

## Standalone package layout

The WPF compile graph remains limited to Client and Contracts. Its publish target separately publishes Worker and its managed/runtime dependencies under an app-private `worker\` subtree, and the App launches only the absolute `worker\RainArchives.Worker.exe` path beneath `AppContext.BaseDirectory`. It does not search the current directory, `PATH`, or an installed copy. Keeping App and Worker self-contained outputs in separate subtrees avoids same-name framework-file conflicts and keeps the future native provider inside the worker trust boundary. The tradeoff is duplicated .NET runtime payload and a larger package; framework-dependent or shared-runtime optimization is a later measured release choice, not an implicit change.

The updater/installer must treat the App root and worker subtree as one versioned package and must never leave a mixed pair runnable. Single-file, trimming, and AOT publishes currently fail closed because those modes have not been validated for Worker loading, reflection, codecs, cancellation, or crash diagnostics.

The current autonomous Milestone 3 package is retained at
`artifacts/RainArchives.M3AutomatedPackage.1.18.1C85F6D9`. Its self-contained `win-x64` App launches
only the private packaged Worker, keeps App/Worker Contracts assemblies byte-coherent, and contains
no `7z.dll`, `7za.exe`, or `SharpSevenZip.dll`. The mandatory local flow covers
ZIP/TAR/TAR.GZ/TAR.BZ2/GZIP/BZIP2 creation collectively: direct Client creation covers
TAR/TAR.GZ/TAR.BZ2/GZIP/BZIP2, the in-process CLI covers ZIP/TAR.GZ/BZIP2, and representative
WPF-model creation covers ZIP/TAR.GZ/GZIP. It also covers the existing ZIP rebuild surface and
validates authoritative Rain reopen/test/extract, exact hashes, original-byte preservation, and
staging cleanup. The App tree is 609 files / 232,216,977 bytes at aggregate
SHA-256 `C61749B2CC94A4E66606D3615DF88C44A2D95F599EE1F2B78C812BAC1BCFCFB0`; its private Worker subtree
is 205 files / 84,699,121 bytes at
`36C64503CC9EA9FD6CF0F6C390F75F8871BEF9BFCA5461456CE645CBACB2966F`. Manifest SHA-256 is
`6A8CB71E3924E04451299E9C4C4AB2F022D9648FCFE8113186656C6F0D0FF47A`. The exact package passes
the automated all-format product flow, the externally checked mixed-format local benchmark, fresh
offline Sandbox creation for all six formats, non-elevated ACL denial, early/near-final real NTFS
exhaustion, 15 supported exFAT preset/provenance cases, and real local
8.3/mapped-drive/alternate-loopback-UNC paths. Its bounded external-reader matrix includes pinned
official 7-Zip, Windows libarchive 3.8.4, GNU tar/gzip/bzip2, WinRAR 7.20, and distinct libarchive
3.8.9 simple/PAX layouts. The writer/staged validator retains the exact fractional PAX timestamp;
SharpCompress's public descriptor may differ by one 100 ns tick. BCL PAX helper header names include
the Worker PID, so deterministic entry planning does not claim byte-reproducible TAR containers.
The product owner later completed direct exact-package ZIP creation/reopen/integrity and Rebuild Copy
with the expected metadata warning. The submitted staging command retained a literal placeholder, so
no cleanup inventory is inferred. Direct Recovery Center, installation, signing, publication,
product-owner accessibility, and creation-specific remote SMB/blocked-UNC/DFS/no-file-ID claims
remain separate.

The immutable historical M5G-C product-identity package is
`artifacts/RainArchives.M5GCProductIdentityPackage.1.22.56FFA877`, bound to 159 canonical production
inputs / 3,070,555 bytes at
`56FFA87711984983DD8AA52A9735A901ECCDE92A2EDD8A00ED2F4E17FB301491`. Its 1,083-byte manifest SHA-256
is `D8284B822C2475C61D40B155719A20522BD05CB67C12A5A2EEB8D74BB84CE74F`; the App tree is 609 files /
234,012,833 bytes / `D490AB5B36ECDD4D3C67384C86178A7A23467E99E1E4CE6F8459B4676A99F8B5`, and the private Worker remains
205 files / 85,041,541 bytes /
`1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`. Its historical package flow pins
its source-at-package-time identity, manifest, both trees, principal assemblies, byte-equal Contracts,
and forbidden assets,
checks the icon-bearing EXE through Windows shell extraction, then drives packaged-Worker open,
navigation, safe preview, properties, drained inspector close/focus, integrity, and selected extraction
with unchanged original hash and zero scoped residue. It contains no `7z.dll`, `7za.exe`, or
`SharpSevenZip.dll`.

M5G-C adds no data or command authority. One deterministic 381,038-byte ICO adapts the existing
`Icon.Archive` geometry to the accepted violet/near-black/soft-light palette across nine 32-bit alpha
frames from 16 through 256 pixels. The App project binds it once through the relative
`Assets\RainArchives.ico` `ApplicationIcon`; no Window-level duplicate, runtime generation, custom title
bar, shell association, installer metadata, or new dependency exists. The canonical production input
set now includes `.ico`, so the source identity binds the product artwork as well as its project path.
Binary-structure tests validate every directory/DIB field, frame dimension, alpha/AND mask, palette,
nonempty bound, and the exact icon hash; Windows tests extract small/large EXE icons and verify the live
top-level window receives the executable icon without a XAML override.

The current M5G-D design-system package is
`artifacts/RainArchives.M5GDDesignSystemPackage.1.22.651729F4`, bound to 160 canonical production inputs /
3,157,575 bytes at `651729F471468F590BB2F89CE663C14B5140356227509B3C247A93C74481D740`.
The complete source/package gate passes 522 unit/WPF, 29 adversarial, and 359 integration tests
(910 total) with zero failures or skips.
Its 1,083-byte manifest SHA-256 is
`01BF21F943C46B54C953E2A2BE3C5D037BE1FB26F738DBE67CA6C8C46AB30137`; the App tree is 609 files /
234,037,929 bytes / `AD4D7D620C75771788083B29B7DEB92DE403D665336BBF5D96810FF921BC212A`, and the private Worker remains
205 files / 85,041,541 bytes /
`1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`. The current exact-package flow
also binds the live canonical source, all seven principal hashes, Contracts byte equality, executable
icon resources, private-Worker composition, and the forbidden-native-asset rule before running the same
packaged browser flow with an unchanged input and zero scoped residue.

M5G-D adds presentation authority only. The App merges the selected palette, then
`Themes/Controls.xaml`, then product-level `DesignTokens.xaml`; every shared control template resolves
semantic colors dynamically so a live theme change reaches existing controls and popup content. Required
WPF template parts remain present. Product-specific Button, tab, DataGrid, and inspector resources compose
that control layer. Generated DataGrid checkbox display/edit elements and editable text cells receive
explicit shared styles at the four authorized Rebuild columns because those column types construct
elements outside normal implicit-style lookup. Settings owns one outer pixel-scrolling content route with
bounded inner grids and a stationary footer. Create's default XAML labels and the three post-create open
fallbacks are format-neutral, while runtime format labels, capability decisions, command flow, settings
schema, distinct-absent destination rule, and no-overwrite behavior are unchanged. The preserved
`B11E7A80` package records the pre-final state before runtime proof exposed the generated text-editor
bypass; it is superseded evidence and is not accepted as current.

M5G-B added no data or command authority. Six secondary windows consume shared dynamic semantic surface,
foreground, border, focus, tab, card, and recycling-grid styles. `AppThemeManager` owns a weak,
idempotent per-window caption attachment that applies after HWND creation, reapplies on theme changes,
and detaches on close; `AppDialogService` attaches every production modal/nonmodal path without changing
ownership or results. Activity has one polite live status and a non-live grid. Recovery has one selected-
tab assertive status while inactive inventory/guidance stays non-live. Create/Rebuild use bounded outer
scrolling with fixed-height virtualized review grids; Password visually bounds the escaped archive name
while retaining its complete escaped tooltip/Automation description. Existing access keys, tab/default/
cancel behavior, Settings rows 0-4, cancellation/drain/session handoff, redaction, secret clearing,
capability checks, limits, originals, and no-overwrite behavior remain intact.

`artifacts/RainArchives.M5GBSecondaryWindowPackage.1.22.20FF2288` remains immutable accepted predecessor
evidence. Its manifest, source, App/Worker trees, and principal hashes are pinned by a separate
historical check without asserting that its source identity is current.

`artifacts/RainArchives.M5GAAccessibilityRemediationPackage.1.22.5AF06A6E` remains immutable accepted
predecessor evidence. Its manifest, source, App/Worker trees, and principal hashes are pinned by a
separate historical check without asserting that its source identity is current.

`artifacts/RainArchives.M5GAPolishPackage.1.22.BE985F08` remains byte-exact rejected
pre-remediation evidence. Its High Contrast muted/selected foreground roles and duplicated no-session
welcome mnemonics are not accepted or transferred to the current package; a retained integration
check pins its original manifest, trees, and principal hashes without relabelling it current.

The predecessor M5F inspector-remediation package is
`artifacts/RainArchives.M5FInspectorRemediationPackage.1.22.3FD7C6F4`, bound to 153 canonical production
inputs / 2,605,119 bytes at
`3FD7C6F42250A75ED8B62878D15CEB6FF4361D8DCDCC667B8B6134375C35DDAD`. Its 1,082-byte manifest SHA-256
is `9A0F153DE2AF4203D385EE21BB1ABF8022E8B068893A68024C1693ED504CFD5B`; the App tree is
609 files / 233,182,721 bytes /
`78945421A56A188ED64C6908574613DEA3BEE0C3DBCB43F1A89EFE24BAA336C6`, and the private Worker remains
205 files / 85,041,541 bytes /
`1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`. The exact package flow verifies
hidden startup, explicit Properties and Safe Preview reveal, drained close, focus restoration, reopen,
source hash, exact process exit, and zero scoped Worker/cache/staging residue. The earlier
`artifacts/RainArchives.M5FAutomatedPackage.1.22.94811C7A` and its deterministic measurement under
`artifacts/RainArchives.M5FMeasurement.1.22.94811C7A.02` remain immutable predecessor evidence, but the
package is not the current Phase-5 candidate because manual review found its inspector permanently
visible with no Properties close affordance. The predecessor's observed DPI scaling and broad keyboard
navigation do not transfer to the remediated package. The accepted M5E package remains immutable
predecessor evidence. The `973CB3C3` package is immutable rejected evidence of a slash-normalization
mistake in its manifest source/App-tree identities, and `E20205D2` is immutable rejected pre-drain-fix
evidence. The `80A394D5` package is immutable rejected pre-ownership-remediation evidence. The accepted
M5D/M5C/M5B packages and their already documented rejected predecessors remain immutable. No historical
manual accessibility or clean-machine result transfers to the current package.

Milestone 5D adds no protocol, provider, filesystem, or mutation authority. The App session inspector
consumes only Contracts/Client state and builds its complete fixed projection before replacing the
authoritative view. It accepts optional sizes, ratios, timestamps, attributes, duplicate occurrence,
link/encryption/integrity presence, and persistent key only when their existing typed fields are valid;
one-based visible and Automation occurrence values share an `Int64` value widened before addition;
provider identifiers/text, method strings, raw integrity/link/fingerprint values, exceptions, secrets,
recovery IDs, paths, handles, and command lines are absent. The safe-preview service returns typed
text/binary/oversize/unsupported outcomes, exact source-read/byte facts, and an encoding enum only after
strict UTF-8 or BOM-proven UTF-16/UTF-32 decoding. It preserves the existing 1 MiB plus one detection
byte, one-active-plus-newest-pending, four-materialization, exclusive delete-on-close cache, stale-
generation, cancellation, drain, and source/session ownership boundaries. Active content, external
launch, shell/Open With, implicit export, and new mutation semantics remain closed.

## Standalone shell status

The WPF project proves that bounded root/folder browsing, breadcrumbs, Back/Forward/Up history, Unicode search, deterministic duplicate selection, persistent-key same-source reopen, extraction, integrity testing, password prompts, progress, cancellation, worker lifecycle, fixed archive/entry properties, typed bounded inert preview, capability-gated managed creation and ZIP Rebuild Copy, bounded Settings/Presets, the four-surface Recovery Center, unified Activity Center, and canonical selected-entry context commands can consume the public Client boundary. Source-level noninteractive WPF-model routes cover ZIP/TAR/TAR.GZ/TAR.BZ2/GZIP/BZIP2 creation plus add/replace/delete, retained-file rename, tree edits, source-folder addition, explicit ZIP metadata, navigation/invalidation, property projection, preview-state transitions, connection-lifetime lifecycle presentation, strict local settings state, current-capability default clamping, all three recovery families, fixed support evidence, context-command invalidation, and explicit inspector reveal/cancel-drain-close/focus restoration; they reopen only authoritative completed destinations/sessions and preserve originals. The current protocol-1.22 M5G-D self-contained package proves App/private-Worker composition, deterministic EXE/shell/window identity, centralized semantic shell/window/control presentation, popup theme switching, generated DataGrid element bridges, Settings scroll reachability, caption attachment lifecycle, deliberate live regions, bounded layouts, collision-free keyboard metadata, and the remediated inspector/product lifecycle with hashes and cleanup. M5G-C, M5G-B, M5G-A, and M5F preserve prior identity/secondary/main/inspector checkpoints, while 94811C7A separately retains deterministic performance evidence plus the broader context navigation/properties/preview/integrity/extraction flow. The accepted M5E package separately preserves creation/nested/extraction recovery plus diagnostics; accepted M5D, M5C, and M5B packages preserve properties/preview, settings-backed create/rebuild, and browser/lifecycle evidence. The retained protocol-1.18 package proves representative WPF-model ZIP/TAR.GZ/GZIP creation and its historical fresh offline clean-machine startup. Product-owner direct observation covers that retained package's ZIP creation/reopen/integrity and Rebuild Copy/reopen with the expected warning. Product-owner visual/Narrator/keyboard-only/actual High Contrast/100/150/200%/mixed-DPI qualification of the current M5G-D package remains separate; same-task automated GUI inspection and prior-package DPI/keyboard observations do not transfer.

The creation dialog admits files atomically, admits folders only for formats that negotiate tree creation, exposes ZIP, TAR, TAR.GZ, TAR.BZ2, GZIP, and BZIP2 from typed capabilities, refuses an existing destination, applies only supported presets, reports progress through the shared activity model, and reopens a successful archive. Raw formats visibly require exactly one source file. The Rebuild Copy dialog remains gated to an eligible opened ZIP, reviews explicit file/tree edits, final metadata, preset, and new destination, displays bounded Worker-owned source-folder counts, distinguishes Rename from Replace, states that the original remains untouched, uses the same activity/cancel model, and reopens only after authoritative completion. Both flows keep overwrite, in-place replacement, encryption, split, XZ, 7z, and RAR creation visibly unavailable. The preview presenter consumes only per-session `OpenEntryStream` capability, discloses only typed/proven decoding state, and leaves active-content rendering, export, and external path handoff disabled.

The Recovery Center references only Contracts and Client and owns creation, nested-workspace, extraction-staging, and fixed support-evidence models under one modal lifetime. Creation disposition and nested discard/reopen remain exact-evidence, default-No, freshly revalidated actions. Extraction remains an inspection-only surface with every mutation/retry/cleanup flag required false. The support tab maps only the protocol-1.22 fixed schema and grants no recovery authority. Refresh, selection change, declined confirmation, failure, cancellation, stale/malformed evidence, or uncertain mutation clears authority; no model retries or substitutes an action. A supplied nested password slot is Client-owned from call entry through local validation and lifecycle-gate admission; every pre-response exit discards it once, while success remains Worker-consumed. A recovered nested source is adopted only as its fresh depth-zero session, never as a filesystem path. The dialog service owns that session until a result is successfully handed off, and MainWindow owns it until the full state transition and notifications commit; every earlier cancellation or failure closes it once. Ordinary open uses the same pre-commit compensation rule. Dialog initialization plus all child requests are cancelled and joined before control returns. No WPF model crosses IPC, and App still has no reference to Worker, providers, Core, or Security. Automated STA, real-Worker, exact-PID, and exact-package cases cover the complete surface without claiming manual accessibility acceptance.

It remains a partial Milestone 5 application: richer non-text rendering and external preview remain closed, stable former-ancestry restoration, extraction-staging disposition, broader per-format method/estimate UX, any separately approved replacement/multi-output flow, installer/update atomicity, and the broader runtime GUI matrix remain later gates. Managed settings/presets, all managed create formats, eligible ZIP rebuild, Activity Center, bounded standalone browser, fixed properties, safe preview, explicit inspector lifecycle, consolidated recovery/support presentation, existing-command context interaction, the M5G-A/M5G-B shell/window system, the M5G-C deterministic Windows identity, the M5G-D centralized control system and Settings scroll route, and predecessor-bound measured packaged large-list/cold-start/memory behavior are implemented. Exact-package clean-machine and local disposable storage/alias results remain bound to their named predecessor packages, and product-owner direct creation/rebuild observation is complete within that retained evidence boundary. Direct current-M5G-D-package visual/Narrator/keyboard-only/actual High Contrast/100/150/200%/mixed-DPI/clean-machine acceptance, creation-specific remote SMB/blocked-UNC/DFS/no-file-ID/non-ADS behavior, installation, signing, publication, and release remain `manual_pending`.
