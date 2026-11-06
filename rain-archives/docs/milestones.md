# Implementation milestone sequence

Every milestone normally starts only after the previous exit gate is evidenced. Source presence, a helper-level unit test, or a deterministic screenshot is not enough by itself. On 2026-08-23 local time, the product owner first authorized Milestone 2 implementation to proceed in parallel while the remaining manual checks were scheduled, then accepted the Narrator/keyboard flow after completing ZIP selection and extraction without a mouse. The later physical CachyOS/Samba success and interruption run closes the final Milestone 1 remote-SMB gate; creation-specific SMB qualification remains a separate Milestone 3 requirement.

## 1. Foundation and dependency proof

Scope: versioned contracts, provider boundary, worker launch/lifecycle, secure ZIP-focused list/test/new-destination extraction, CLI harness, adversarial policy, package/license audit, and reproducible baseline measurements. Narrow managed-fixture proofs for 7z, RAR, TAR families, and raw streams may validate the provider boundary in M1, but they do not establish broad compatibility or authorize multipart extraction. Non-goals: merge extraction, creation, modification, native gate enablement without proof, product UI acceptance, Rain Explorer changes.

Exit: all criteria in `docs/milestone-1.md`, including restored exact packages, full build/tests, packaged clean-machine run, manual Windows matrix, and no unresolved high/critical issue.

## 2. Secure read compatibility

Scope: broaden the narrow M1 proofs into multi-tool, variant-aware conformance for 7z, RAR, TAR and compressed TAR, GZIP/BZIP2/XZ; encrypted/solid/corrupt behavior; an explicit single/confirmed-multipart/unknown volume-set identity model; selected entry streaming suitable for preview; bounded nested-archive opt-in. Non-goals: public writes or shell integration.

Progress through 2026-08-24: six production-grade M2 source slices are complete. Protocol 1.4 adds negotiated `volume-set-identity`; providers report `Single`, `ConfirmedMultipart`, or `Unknown`; worker and client enforce coherent fail-closed capabilities; minor-3 wire output omits the new field; and checked ZIP/7z/RAR/TAR-family corpus tests prove the shipping classifications. Protocol 1.5 adds authenticated, bounded complete-entry preview leases over separate pipes, exact worker-PID plus nonce proof, actual decoded-byte enforcement, password handoff, per-session cleanup, and acknowledged per-lease cancellation that reaches provider reads. The third slice changes no wire shape: checked payload hashes qualify preview across the managed ZIP/7z/non-solid-RAR/TAR/compression-stream corpus, deferred provider read failures receive stable errors, and solid RAR explicitly remains test/extract-only until a safe bounded sequential-preview path exists. The fourth adds provider-neutral `archive.preview_unavailable` rationale through the existing descriptor-warning shape whenever a proven single-volume session can extract but cannot direct-preview. Protocol 1.6 is the fifth slice: explicit one-entry nested opening uses provider integrity-copy into an immutable handle-relative worker-owned source, content detection, depth/per-entry/chain/global byte limits, disk and ratio guards, two-slot password settlement, provenance inheritance, request cancellation, client descendant ownership, and exact close/shutdown cleanup. Solid RAR, multipart, and unknown-volume parents remain safely refused. The sixth slice changes no wire shape: `RainArchives.Client` materializes an authenticated complete-entry lease into a bounded random delete-on-close file and exposes only a read-only seekable stream after completion and length validation. A separate reliability hardening slice groups nested sources beneath a lazily created token-claimed worker workspace and performs bounded, non-destructive startup inventory with count-only/path-free descriptor warnings. An isolated packaged-worker kill/restart test proves stale token and child-byte persistence plus non-destructive reporting by the next worker. Protocol 1.11 now adds the first explicit nested-workspace review/discard slice: path-free list/inspect, exact metadata revision/count/byte confirmation, strict no-follow identity/shape/link/ADS limits, immediate pre-delete revalidation, bounded cancellation semantics, and a real authenticated exact-PID kill after first mutation that leaves partial state suspicious and preserved. The standalone WPF Recovery Center now consumes that contract without adding filesystem authority. This does not complete M2 or M4: broader multi-tool/version conformance, true wire-range reads, path-based external-preview/provenance handoff, actual N/N-1 binaries/golden fixtures, nested restore/reopen, and the remaining exit evidence below are still open.

Later M4 progress supersedes only the dated nested-reopen item above: protocol 1.21 can copy and
redetect one exact recovered source as an independent root. Stable former-ancestry restoration,
automatic cleanup, and the remaining M2 conformance work are still open.

Exit: per-format fixtures from multiple external tools, password/solid/multipart tests, exact capability discovery, checksum semantics, cancellation limits, mutation invalidation, and compatibility report.

## 3. Creation and modification

Scope: staged ZIP/TAR-family creation, approved 7z creation, presets, compression/encryption controls, add/replace/delete by complete rewrite, split creation where proven, source/destination self-overwrite refusal, and rollback. RAR creation stays closed unless a new official licensed encoder path receives explicit product approval and passes its own provider, containment, package, and interoperability gate; protocol 1.23 records the subsequently approved conditional route.

Progress through 2026-08-24: the first two production-grade M3 protocol slices, creation-provenance hardening, an initial WPF new-ZIP flow, an initial WPF archive-creation recovery center, and the durable creation-recovery foundation are complete in source. Protocol 1.7 feature `zip-create-v1` creates a new unencrypted ZIP from zero to 4,096 explicit regular files totaling at most 64 GiB with `Store`, `Fast`, `Balanced`, or `Maximum`. Protocol 1.8 adds separate feature `zip-create-directories-v1` and a distinct request so the 1.7 wire shape stays unchanged. The new route may mix explicit files with one or more directory roots, includes root and empty directories, sorts material entries deterministically, and applies one 4,096-entry/64-GiB regular-file-byte budget after worker-owned expansion. A separate writer-provider seam receives only validated directory metadata, already-open source streams, and a caller-owned destination stream. Rain validates archive-relative names and Windows source paths, enumerates pinned non-reparse directory handles, binds enumerated file IDs where available, holds stable source identities, captures and finally revalidates an exact tree manifest, enforces conservative output/disk limits, writes a local five-phase recovery journal before the journal-bound deletion-marked sibling staging file, durably flushes, and commits by handle-relative no-replace rename. Bounded startup inventory reports unresolved stale/torn/suspicious local records without contacting destinations or deleting those candidates. Protocol 1.9 adds path-free fresh inventory and explicit read-only inspection of one opaque record: journal, parent, staging, and final evidence are revalidated without content reads or mutation, and no result grants cleanup/retry authority. Protocol 1.10 adds an explicitly confirmed, exact-revision disposition contract and CLI for commit, discard, accept-published, or acknowledge-none outcomes. It revalidates identity/length/link/reparse evidence through pinned handles, durably records intent before mutation and completion after the postcondition, refuses stale/conflicting action, and makes same-action replay idempotent. Completed receipts now use a bounded newest-256/30-day replay policy with one count-prune after each resolution and at most 256 exact metadata-tree deletions per startup. Count pruning requires complete bounded enumeration; every deletion reparses and exclusively pins an exact ordinary two-file, single-link, identity-matched tree and never opens the recorded destination. When every regular source has the same strictly parsed Mark-of-the-Web zone, Rain captures and finally revalidates bounded ADS state, retains a precreated handle-relative staging ADS across base-file deletion disposition, writes only canonical zone data, durably verifies it, and preserves it through commit. Mixed/ambiguous provenance is omitted with one sanitized warning; all-absent/all-unsupported state stays silent. Client, worker, CLI, downgrade-refusal boundaries, deterministic progress/cancellation, collisions, nested reparse refusal, source/provenance mutation, destination races, timestamp clamping, provider failure, cleanup, empty output, all four presets, Rain-managed round trips, the initial WPF files/folders/preset flow, the read-first and explicit-confirmation WPF recovery model, journal/receipt corruption and torn-tail handling, real worker-kill/restart recovery reporting, recovery evidence classification, deterministic disposition interruption/replay, retention/replay expiry, and exact-PID authenticated Worker/IPC termination at both commit checkpoints have automated evidence. The completed actual second-machine gate proves M1 extraction behavior only; it does not substitute for the still-open M3 creation-specific SMB matrix or make this a packaged M3 acceptance claim. The separate product-owner Narrator/keyboard gate is accepted against the exact final M1 package, not the new current-source recovery window.

Protocol 1.12 completes the first narrow add/replace/delete slice as `zip-rebuild-copy-v1`. It consumes one already-open eligible single-volume unencrypted ZIP, uses exact persistent entry keys for deletion, maps explicit regular files to closed add/replace targets, and checksum-validates every retained entry while streaming directly into the provider writer. All retained and mapped material is recompressed into a distinct absent ZIP with the selected preset under one checked 4,096-entry/64-GiB budget. The original is revalidated before the journaled no-replace commit and is never renamed, overwritten, deleted, or used as staging. Typed Client, `rebuild-zip` CLI, and WPF Rebuild Copy routes are implemented. Focused evidence covers closed-plan/collision/resource policy, malformed and minor-11 protocol refusal, source/mapped-file mutation, misleading sizes, expansion, cancellation, destination race, runtime low space, exact-PID Worker death and conservative recovery, same-worker health, original SHA-256 preservation, no ordinary-failure residue, authoritative reopen, and a real packaged-Worker WPF-model mixed edit. Pinned official 7-Zip Extra 26.02 independently tests and extracts representative mixed Store/Fast/Balanced/Maximum outputs and every expected file hash/path matches. This remains source/deterministic-corpus evidence, not broad package, physical SMB/filesystem, or manual GUI acceptance.

Protocol 1.13 completes M3-A Slice 1 as additive feature `zip-rebuild-rename-v1`. A retained regular file is identified by its exact persistent normalized path and duplicate ordinal, and its validated final path is resolved together with deletion, addition, and replacement before staging. One key cannot be renamed, deleted, or replaced more than once; simultaneous swaps remain valid when the final namespace is unique. Typed Client/Worker, repeatable CLI arguments, and a WPF Rename review column use the distinct route while old no-rename calls remain on protocol 1.12. Real mixed round trips, all four presets, Rain reopen/list/test/extract, official 7-Zip Extra 26.02, cancellation, destination race, low-space, exact-PID crash/recovery, original hash, retained 1.12 binary compatibility, and a separate read-only review pass. The 1.13 checkpoint is 509 automated tests and canonical fingerprint `4A27632C1A32086663B972414A1F859BB40C9EBBAFEF53CB188AFA53B53FAD41`; tree editing remains the next bounded slice.

Protocol 1.14 completes M3-A Slice 2 as additive feature `zip-rebuild-tree-edit-v1` plus a distinct bounded Worker-owned source-folder review query. Exact leased-snapshot revisions authorize retained subtree move/delete; mapped folders retain roots and empty directories through pinned non-reparse manifests; exact file and tree edits resolve into one collision-checked namespace before staging. CLI and WPF expose explicit folder review without enumerating through WPF, including Worker-returned bounded descendant/file counts before confirmation. Real Worker/Client/CLI/WPF-model, official 7-Zip, Windows `bsdtar`, mutation/reparse/hard-link/ADS, cancellation/race/low-space/exact-PID recovery, original-hash, retained 1.12/1.13 binary compatibility, and separate read-only remediation review pass. The 1.14 checkpoint is 556 automated tests and canonical fingerprint `4B5F3A57BFD10C6919EB34C8E35C2330D22A7643996EA4EA4F4AF27EA1B0F10A`; explicit ZIP-representable metadata editing is the next bounded slice.

Protocol 1.15 completes M3-A Slice 3 as additive feature `zip-rebuild-metadata-v1`. Typed targets resolve retained keys or mapped final path/kind only after the closed file/tree plan. Editing is limited to one exact timezone-less raw ZIP DOS last-write tuple and the ReadOnly/Hidden/System/Archive low-word mask, with Directory derived from kind; unsupported fields remain explicitly unpreserved. CLI uses strict invariant syntax and WPF provides truthful escaped current/final review without becoming metadata authority. BCL and official 7-Zip provider proofs, every allowed mask and timestamp boundary, metadata-plus-namespace operations, mismatch/downgrade/frame refusal, cancellation/crash/recovery, original hashes, retained 1.12/1.13/1.14 compatibility, and separate remediation review all pass. The 1.15 checkpoint is 573 automated tests and canonical fingerprint `6F595F0D914E156685767181E063E455826B4621400395628D91C9948F1F4F78`; typed write capabilities, reusable presets, and final package productization were the next bounded slice.

Protocol 1.16 completes M3-A Slice 4 as additive feature `zip-write-capabilities-v1`. The typed
capability query reports global creation support, exact opened-session rebuild eligibility, supported
operations, four immutable built-in presets, current bounds, and explicit false values for
encryption, filename encryption, split output, existing-destination replacement, and in-place
update. CLI and WPF gate before picker/source/destination access, and the shared App-owned preset
store persists only supported compression and metadata defaults with strict validation, reparse
refusal, per-user locking, atomic last-known-good replacement, and visible non-secret fallback
warnings. Defaults apply only to newly mapped entries and explicit per-entry edits win. The final
self-contained package is `artifacts/RainArchives.M3AAutomatedPackage.1.16.8E6E9D17`; its mandatory
packaged-Worker flow passes create and every completed rebuild route. The retained compatibility
artifact is `artifacts/RainArchives.ProtocolCompatibility.1.16.8E6E9D17`, with 10/10 current/retained
1.12-1.16 matrix checks. The final checkpoint is 599 automated tests (307 unit/WPF, 29 adversarial,
263 integration) and canonical fingerprint
`8E6E9D171C82841E804C03285D86B119122A61A339F6D00331054BE916AC6258` across 128 inputs. Locked
restore, vulnerability inventory, formatter, Release build, package composition/flow, and
independent remediation review pass. M3-A automated phase is complete; Milestone 3 remains partial
because manual GUI/accessibility, clean-machine, physical filesystem/SMB, installation, signing,
and publication acceptance remain `manual_pending` and are not transferred from historical evidence.

### Completed autonomous tranche: Milestone 3 managed creation completion

Protocols 1.17 and 1.18 complete every remaining autonomously implementable managed creation item
from the finite Section 13 manifest. Protocol 1.17 adds separate Store-only TAR explicit/tree
requests and TAR capabilities. Protocol 1.18 adds TAR.GZ and TAR.BZ2 explicit/tree requests, raw
GZIP and BZIP2 exact-one-source requests, and managed-format capability discovery. Providers remain
path-free; Security owns pinned source planning, format-specific conservative output estimates,
per-file digests, guarded staging, the five-phase journal, full format/footer/trailing-data readback,
source/provenance revalidation, and one handle-relative no-replace commit. TAR-family creation keeps
the 4,096-entry/64-GiB policy; raw formats admit exactly one ordinary file under the 64-GiB bound.

Typed Client, CLI, and capability-gated WPF-model routes are complete. The App still references only
Contracts and Client. Real Worker tests reopen/list/test/extract every new format through Rain,
compare exact source/output hashes, validate pinned official 7-Zip Extra 26.02 interoperability,
exercise cancellation/mutation/race/low-space/recovery/cleanup behavior, and preserve existing or
raced destinations. Protocol-1.16 ZIP shapes remain unchanged. A narrow retained protocol-1.17
binaries-only artifact proves current/1.17 negotiation, TAR creation, and pre-access refusal for
1.18 features in both directions; it is not relabelled as an exact 1.17 source checkpoint.

The retained Milestone 3 local self-contained package is
`artifacts/RainArchives.M3AutomatedPackage.1.18.1C85F6D9`. Final verification passes 358 unit/WPF,
29 adversarial, and 293 integration tests (680 total), zero failures/skips, against canonical
production fingerprint `1C85F6D929DE5F940E1357C72C210A8647DFD2E9F38A0A2B0B0832127F5CE844`
across 132 inputs. Locked restore, all-project no-known-vulnerability inventory, formatter,
zero-warning Release build, package product flow, external conformance, and independent read-only
review pass.

The remaining decisions are closed honestly: ZIP content/name encryption has no approved managed
writer; TAR.XZ/raw XZ have no approved compressor; 7z creation remains disabled behind the failed
native adapter/license/package/maintenance gate; RAR creation remained forbidden at this checkpoint; split output lacks
a multi-output publication/recovery contract; and existing-destination replacement/overwrite/
in-place update remains a deliberate no-replace refusal. These are not missing code and are not
advertised. A source-at-package-time ZIP probe and exact-package 75-sample/90-external-validation mixed-format
benchmark pass against `1C85F6D9`. Package-bound fresh offline Sandbox startup/all-six-format creation,
non-elevated ACL denial, early and near-final true NTFS exhaustion, all 15 supported exFAT preset/
provenance cases, and local 8.3/mapped-drive/alternate-loopback-UNC paths now pass. Milestone 3 remains
partial even though the product owner subsequently observed exact-package ZIP creation/reopen/
integrity and Rebuild Copy with the expected warning. Recovery Center, staging inventory,
rebuilt-output manifest/hash, product-owner current-flow accessibility, actual second-machine SMB/
blocked-UNC/DFS/no-file-ID, and release acceptance remain `manual_pending`; the autonomous managed
implementation and safe package-bound automated/performance/available-physical tranche are complete.

One additional package-bound conformance corpus passes GNU tar 1.35 for TAR/TAR.GZ/TAR.BZ2 and GNU
gzip 1.14/bzip2 1.0.8 for raw streams; WinRAR 7.20 independently passes all six outputs. A distinct
`bsdtar`/libarchive 3.8.9 build passes the four simple archive containers plus three exact-package PAX
TAR-family layouts with a long Unicode path, empty directory, zero-byte file, and fractional timestamp.
Hash-bound explicit tool paths, exact extracted payload hashes, immutable archives, exact launched-
process settlement, and zero Rain recovery/staging/process residue pass. Together with pinned official
7-Zip and Windows libarchive 3.8.4, this closes the bounded M3 external-reader gate; open-ended breadth
remains Milestone 8 release hardening. SharpCompress's public descriptor differs from the exact staged
PAX timestamp by one 100 ns tick, and that reader limitation is retained explicitly. A fixed-source-
timestamp repeat confirms material order/content/metadata but not byte-reproducible PAX containers,
because BCL helper header names include the Worker PID.

One source-snapshot conformance proof runs all four explicit-file presets through the production CLI/worker and then official 7-Zip Extra 26.02 test/extraction; root, nested, and Unicode entry paths reproduce every source hash. A second official-7-Zip proof covers mixed explicit-file/directory-tree creation, a Unicode filename, and an empty directory. Windows `bsdtar` 3.8.4/libarchive 3.8.4 independently lists and extracts that same tree ZIP with both hashes and the empty directory preserved. Separate loopback proofs read and commit both explicit-file and directory-tree creation through `\\localhost\C$`; official 7-Zip accepts each output. Exact-process tests kill the launched worker during a real 128 MiB Maximum-compression copy with a prepared provenance stream and after durable finalization while a 4,096-entry tree undergoes final manifest validation; neither case publishes a destination or leaves delete-pending staging. The copy-kill additionally leaves a valid operation/destination/staging-identity-bound `Writing` journal which a second worker reports without deletion. A deterministic runtime-low-space transition likewise fails closed and cleans staging. The retained earlier operation probe remains bound to its exact historical fingerprint; the `1C85F6D9` package-time source rerun covers 40 measured ZIP operations plus partial-progress cancellation, and its exact-package mixed-corpus probe covers 75 measured operations across all 15 supported format/preset cases. All 140 measured/warm-up/cancellation outputs across the two retained probes pass the hash-pinned owned official 7-Zip path, Rain content verification, and cleanup gates. Package-bound clean-machine/storage/local-alias evidence and the product-owner GUI observation are detailed in `docs/milestone-3-manual-windows.md`. Remaining before the full Milestone 3 product exit: direct Recovery Center state, DFS/no-file-ID filesystem conformance, physical blocked-UNC cancellation, actual second-machine SMB creation/interruption/provenance/partial-write behavior, product-owner current-flow accessibility, and release acceptance. Encryption/XZ/native-7z/split/replacement are separately closed gates, not promised completion items. The later protocol-1.23 RAR route does not retroactively change this protocol-1.18 evidence.

The subsequent Recovery Center consumer slice adds a separate path-free nested-workspace tab over protocol 1.11. Only one fresh verified assessment enables Discard; the default-No prompt repeats the exact opaque ID/revision/count/bytes; refresh, selection change, failure, cancellation, malformed evidence, or uncertain completion clears authority. Automated STA rendering checks converter resources, OneWay evidence bindings, and accessibility metadata, while a packaged production Worker integration test drives the real view model through list, inspect, exact-evidence confirmation, discard, and empty refresh. This is source evidence, not current-source package/manual acceptance.

### Completed autonomous tranche: M3-A Managed ZIP Rebuild Completion

M3-A is the finite managed-only phase defined in `CONTINUATION.md` Section 13. Protocol-1.13 retained-file rename/move, protocol-1.14 bounded directory-tree add/move/delete, protocol-1.15 explicit ZIP-representable metadata editing, and protocol-1.16 typed write capabilities are complete. Reusable managed-ZIP presets and current-source local package qualification also pass. It remains limited to eligible single-volume unencrypted ZIP input and distinct previously absent output; every rebuild operation preserves its opened original and uses the existing staged journal/readback/no-replace authority.

M3-A deliberately excludes encryption, TAR/7z/RAR writing, native-provider adoption, split output, existing-destination replacement, in-place update, Rain Explorer, shell/installer/signing/publication, and physical/manual acceptance. Those exclusions kept the phase autonomously executable without silently deciding licensing, destructive replacement, multi-output recovery, or product-owner gates. Every predeclared slice, current/older protocol compatibility, full automated/adversarial/real-Worker evidence, external ZIP conformance, scoped independent review, final local package flow, fingerprint, documentation, and residue check passed. M3-A automated phase is complete; Milestone 3 remains partial and manual/physical gates stay explicitly pending.

Exit: deterministic round trips with external tools, cancel/disk-full/crash tests at each rewrite stage, final-commit durability evidence, no original archive loss, and capability/UI wording that says Rebuild where applicable.

## 4. Worker reliability and operation lifecycle

Scope: truthful operation admission/activity policy, bounded cancellation and shutdown, crash recovery and user-reviewable reconciliation, orphan extraction-staging and nested-source ownership, structured redacted diagnostics, protocol compatibility tests, and an AppContainer/brokered-handle/same-user containment decision. Bounded local-only startup inventory exists for nested workspaces, ZIP-creation journals, and protocol-1.20 extraction journals; none contacts recorded destinations. Recovery inventory never cleans unresolved/unverified state, while verified archive-creation `Completed` journal/receipt metadata has a bounded newest-256/30-day retention policy enforced through exact handle-relative deletion. Protocol 1.9 provides explicit read-only creation-artifact inspection with conservative `CommitIntent` evidence. Protocol 1.10 supplies the user-confirmed, identity-revalidated, idempotent creation-disposition slice with durable intent/completion receipts and an explicit CLI. Protocol 1.11 separately supplies path-free list/inspect and explicit discard for only a narrowly verified inactive nested workspace. Protocol 1.19 fixes a 32-admitted/one-running/64-terminal activity policy with cancel-only controls; protocol 1.20 adds read-only extraction-staging inspection; protocol 1.21 adds fresh exact-evidence copy-only independent nested recovery reopen while retaining conservative partial-discard preservation; and protocol 1.22 adds fixed-field support diagnostics plus the explicit same-user Worker/Job containment decision. Typed decision continuations, retry, pause, reorder, automatic cleanup, overwrite, and extraction disposition remain closed until a future immutable operation descriptor and mutation-unambiguous recovery proof exists. Remaining acceptance work is physical blocked-network/network-filesystem behavior, direct current-package GUI/accessibility, installer/update, signing, and release qualification. Non-goals: background service or account/cloud dependency.

Progress through 2026-08-26: protocol 1.19 completes the first bounded activity foundation. One
additive query reports path-free live and retained terminal lifecycle state with an exact
32-active/queued, one-concurrent, newest-64-terminal policy. Records contain only typed IDs/enums/
timestamps, numeric progress, warning count, and cancelability. The Client rejects malformed bounds,
ordering, timestamps, lifecycle, and progress by aborting its owned transport. Pause, retry,
reordering, and restart persistence remain explicitly unsupported. The exact protocol-1.18 source/
binary artifact and 17-case current/retained matrix prove prior managed operations and minor-18
pre-access refusal. This is not a durable queue, Activity Center, recovery authority, or M4 exit.
The clean source gate is 365 unit/WPF, 29 adversarial, and 307 integration tests (701 total), zero
failures/skips, at 132 inputs and fingerprint
`F800A94D031F999F74B9D5AB157855560507B42EBBA62997E3DC71B41CF48FC2`.

Progress through 2026-08-27: protocol 1.20 completes the read-only extraction-staging recovery
foundation. The tracked extraction transaction durably records five phases before and around its
no-replace commit. Startup reads only the bounded local journal root and returns path-free opaque
summaries; explicit one-record inspection alone may contact/disclose a destination. Security uses
pinned exact-case no-follow handles and requires stable nonzero identities, ordinary type, one-link
files, exact ADS shape, 2,000,000-object/64-GiB/depth bounds, and a deterministic revision hashing
both default file data and allowed provenance-stream data. Torn tails and every active, changed,
excessive, malformed, inaccessible, or ambiguous state remain incomplete/non-actionable and are
preserved. Client independently validates bounds, phase/state/evidence consistency, assessment, and
false mutation/retry/cleanup flags. Ordinary terminal delivery converges the journal; exact-PID
transport loss preserves real `Writing` or published evidence for restart inspection. Minor 19 and
the retained exact 1.18 binary refuse the new requests before access while prior operations remain
compatible. This is not extraction disposition, retry, cleanup, restore, or M4 exit.
The completed gate is 398 unit/WPF, 29 adversarial, and 320 integration tests (747 total), zero
failures/skips, with a zero-warning Release build, formatter, locked restore, no-known-vulnerability
inventory, 17/17 retained-binary compatibility cases, and two clean read-only re-audits. The exact
current identity is 135 inputs at
`4806DA8CD71AA4C4C42BF9BA8DF3A2DBCE3F817DB9CD1D5CC29154F8DD12B591`. Actual second-machine SMB,
blocked UNC, DFS, no-file-ID, and non-ADS filesystem behavior remains `manual_pending`.

Progress through 2026-08-30: protocols 1.21 and 1.22 complete the autonomously implementable M4
source/package tranche. Independent nested recovery reopen accepts no path and acts only on fresh
workspace revision/count/bytes plus exact source ID/length. Security exclusively reopens and
revalidates liveness, stable identity, ordinary type, one-link/no-reparse/no-ADS shape, depth,
ancestry, bounds, and content evidence before copying through pinned handles. The Worker flushes and
redetects a new owned copy, then registers it as a depth-zero root; it never mutates or exposes the
recovery source. A post-copy-flush exact-PID kill proves unchanged original hash/evidence, path-free
restart warning, preserved copied residue, and successful fresh reopen/list. Nested discard retains
no unsafe completion inference: partial/smaller/missing/ambiguous state remains preserved or not
found, never automatic success or retry.

The lifecycle policy remains 32 admitted, one executing, unspecified ordering, 64 retained terminals,
and cancel-only controls. Terminal cancel refusal and 33rd-operation secret settlement are tested.
Typed password/conflict/risk phases remain forward-compatible enum values only; password uses
one-shot slots, conflict/risk is local fail-closed preflight, and retry/pause/reorder/restart
persistence remain unsupported. Fixed-field support diagnostics contain only enums, integers, and
booleans and explicitly report that same-user Worker isolation is not a filesystem sandbox. The
current decision retains the authenticated exact-PID unelevated Worker plus 2-GiB/one-process
kill-on-close Job Object; no compatible maintained AppContainer/broker/deployment path is proven.
Cleanup is class-specific and exact-identity-bound; uncertain or suspicious state never receives an
age/count auto-delete rule. Extraction disposition remains closed because protocol 1.20 lacks an
exact per-entry identity/content manifest and deletion-progress ledger.

The final automated gate passes 407 unit/WPF, 29 adversarial, and 337 integration tests (773 total),
zero failures/skips, plus formatter, locked restore, no-known-vulnerability inventory, zero-warning
Release build, 17/17 retained-binary compatibility cases, exact-PID crash coverage, a zero-finding
immutable security scan, and the self-contained
`artifacts/RainArchives.M4AutomatedPackage.1.22.DCE2BC1E` package flow. The canonical source identity
is 135 inputs at `DCE2BC1E01DB8B563E161F1B58E87007ADF7FB6AFC5109CDF4C393B8AAD10C6C`.
Actual second-machine SMB, blocked UNC, DFS, no-file-ID/non-ADS filesystems, direct current-package
Recovery Center/support-diagnostics GUI and accessibility, installer/update, signing, and release
acceptance remain `manual_pending`; Rain Explorer remains unchanged.

Exit: worker crash/restart matrix, bounded cancellation and shutdown, protocol N/N-1 compatibility, secret inspection, resource containment, and recovery behavior that never deletes unproven user data.

## 5. Standalone WPF application

Scope: polished accessible archive browser, preview presentation and cache integration, create/update flows, presets, queue/activity UX, keyboard/touch/high-DPI/high-contrast behavior, settings, packaging. The current WPF shell has production contract-consumer foundations, but the full application milestone remains partial.

Progress through 2026-08-25: the first source-only preview presentation slice, the two-family Recovery Center consumer, and the ZIP Rebuild Copy file/tree surface are complete. The WPF shell activates a regular entry by mouse or Enter, discovers `OpenEntryStream` from the opened session, and presents at most 1 MiB of strict UTF-8 or BOM-marked UTF-16/UTF-32 as escaped read-only text. Requests are serialized as one active plus one newest pending item, cancellation drains stream/cache/secret ownership before replacement, password prompts close on cancellation, and wrong-password retry plus validated-session reuse pass through the packaged real worker. The Recovery Center presents separate creation and path-free nested-workspace tabs over typed Client contracts, with fresh-inspection authority, default-No confirmation, exact evidence forwarding, cancellation/drain, and no automatic retry. Rebuild Copy is honestly gated to an eligible opened ZIP, reviews explicit file and retained-tree edits plus Worker-reviewed source-folder additions, states that the original remains untouched, displays bounded folder counts before confirmation, uses shared progress/cancel state, and reopens only the completed copy. Binary, oversized, solid-RAR/no-stream, non-file, and active-content preview remain honest refusals/non-goals. This is not M5 exit evidence: current source still needs a refreshed package and manual Recovery Center/preview/rebuild accessibility matrix, non-text preview design, settings/preset UX, queue polish, visual polish, and the remaining broader creation/update surfaces.

Progress through 2026-08-30: Milestone 5A completes the bounded production Activity Center and
lifecycle UX foundation without a protocol change. One modeless surface consumes Contracts/Client
only and unifies extraction, integrity, creation, and rebuild operations. It shows validated phase,
progress, escaped live current item, bytes, elapsed/throughput/ratio where supplied, warning count,
typed terminal error category, and the exact 32-admitted/one-running/64-retained/unspecified-order
connection policy. Cancel is present only when the latest snapshot permits it. Pause, retry, reorder,
decision continuation, durable history, and restart persistence remain explicitly unavailable.
Fixed-field support diagnostics contain no paths, recovery IDs, secrets, provider/error text, or
mutation authority.

The refresh owner serializes activity and diagnostics at a bounded interval, coalesces event/UI work,
caps model state at 96, rejects stale responses, and cancels/drains before Client disposal. Loading,
empty, offline, unsupported, and truncated-history states are explicit. System-color resources,
access keys, deterministic focus order, automation names/help/live status, DPI-safe layout, and
recycling row/column virtualization establish the automated accessibility/performance base. The gate
passes 412 unit/WPF, 29 adversarial, and 339 integration tests (780 total), the full source gates, a
separate read-only review, and the self-contained
`artifacts/RainArchives.M5AAutomatedPackage.1.22.08A4C999` production-model flow at 141 inputs and
`08A4C999CB51DB9A2DCC7D32B8D845AC65932908CFB8EB750D9E1441A7A4EDC6`. Manual current-package
visual/Narrator/keyboard-only/High Contrast/mixed-DPI and clean-machine acceptance are not inferred.

Milestone 5B completes the bounded standalone archive-browser/navigation tranche without changing
protocol 1.22. One reusable Contracts/Client-only model owns canonical root/folder navigation,
breadcrumbs, Back/Forward branching, Up/root, Unicode current-folder search, deterministic duplicate
ordering/selection, explicit loading/empty/offline/unsupported/error/stale states, and persistent-key
resolution only across an authoritative same-source/fingerprint reopen. It validates all pages and the
complete hierarchy before adoption, clears old authority after an uncertain prior-session close,
indexes at most 500,000 descriptors, materializes 512 rows, and retains 256 history locations. The
production surface adds access keys, Alt/Ctrl navigation, deterministic focus restoration, screen-
reader names/help/live status, system-color semantics, Per-Monitor V2 layout, and recycling row/column
virtualization. Archive-controlled labels are escaped, preview stays inert bounded text, and no
provider/exception/path/secret/recovery or mutation authority enters the browser.

The remediated M5B gate passes 429 unit/WPF, 29 adversarial, and 341 integration tests (799 total), including a
50,000-descriptor/512-row model measurement, STA production-XAML/accessibility assertions, malformed/
downgrade/offline/stale-source refusal, exact-source history reset/restore, real Worker Unicode
duplicate preview/extract/integrity, package-backed browser/reopen flow, an accepted reverse-ordered
256-level hierarchy, refusal of a 4,352-level reverse-ordered chain, and explicit cycle refusal across
32,768 reverse-parent-ordered entries without replacing prior state. Current production identity is
142 inputs / 2,333,543 bytes at `41BB378A224046A505D54F955243BCD8570519FDF798018E09541425941D3C56`;
the local package is `artifacts/RainArchives.M5BAutomatedPackage.1.22.41BB378A`, with manifest SHA-256
`121CF5703435064DE4602DF3E5ACBCD19A1D760E32C045B59005447B018AB65F`. The earlier scoped workbench
scan remains bound to its pre-history-cap snapshot. Independent verification of the later `93D944FC`
checkpoint found one App-side recursive parent-walk stack-exhaustion blocker; the current package
replaces that recursion with bounded iterative O(n) validation and passed a separate fix-workflow
bypass/regression review plus the complete gate. Independent verification accepted the remediated
source/package after reproducing the deep-chain/cycle cases, all 799 tests, exact source/package
identities, and zero residue. The `93D944FC` package remains immutable predecessor evidence. Manual
current-package visual/Narrator/keyboard-only/High Contrast/mixed-DPI, clean-machine, installer/update,
signing, release, and physical M4 gates remain pending.

Progress through 2026-08-31: Milestone 5C completes the bounded Settings/Presets and managed
create/rebuild UX foundation without changing protocol 1.22. The Contracts/Client-only App surface
persists only one format enum, seven compression-preset enums, one boolean, and the already-bounded
ZIP user presets. The 32-KiB versioned settings schema and 128-KiB/64-user preset schema reject
unknown, missing, duplicate, malformed, invalid-Unicode/enum, truncated, oversized, future, reparse,
hard-link, and ADS state. Validated backup/last-known-good, corrupt/unsupported/unsafe/unavailable,
deterministic explicit reset, per-user serialization, fresh `CreateNew` staging, durable flush, atomic
replacement/backup, exact cleanup, partial-independent-document failure, and close/shutdown
cancellation/drain are explicit. Recovered App settings are read-only hints until Reset. Ordinary ZIP
preset Save revalidates under the store mutex, while Create/Rebuild carry and recheck the exact load
state before Save/Remove; invalid state leaves built-ins usable without replacing prior bytes/object.

One production dialog now presents ZIP, TAR, TAR.GZ, TAR.BZ2, GZIP, and BZIP2 creation from current
typed capability state; eligible ZIP Rebuild Copy consumes its hint only after a fresh session-bound
capability query. Format-specific preset/directory/input bounds and raw one-ordinary-file semantics
remain authoritative. Final review requires a distinct absent destination, states that existing/raced
targets are preserved and every source/original remains untouched, and describes modification as a
full guarded rebuild to a new copy. Encryption, filename encryption, split volumes, overwrite,
replacement, retry, pause, reorder, decision continuation, in-place update, native 7z, and RAR
creation remain unavailable. The system-color WPF surface adds access keys, focus order, automation
names/help/live status, DPI-safe scrolling, recycling virtualization, and explicit loading/saved/
reset/corrupt/offline/unsupported states without retaining provider/exception/untrusted text.

The complete M5C gate passes 456 unit/WPF, 29 adversarial, and 343 integration tests (828 total), zero
failures/skips, plus locked restore, all 15 no-known-vulnerability direct/transitive inventories,
formatter, zero-warning/error Release build, focused STA and real-Worker repeats, direct finite review,
and clean process/recovery/cache/fixture/nested/staging inventory. That accepted production identity is 148
inputs / 2,459,777 bytes at
`6942C0B02756DABBBB33DA7A664B8DDFEF6AFB12C003E35D59977E4C712DDA3E`. The accepted package is
`artifacts/RainArchives.M5CAutomatedPackage.1.22.6942C0B0`, with manifest SHA-256
`1E457D65694D57C8B411D86B381FBD3634C55ABFB4EBB7F192799611C77FA196`; it proves strict settings,
one user preset, fresh-capability create/rebuild, Rain/external reopen, original/source hashes, and
zero residue. The `126DC65F` package remains immutable rejected pre-remediation evidence, and the
accepted M5B package remains immutable predecessor evidence. Manual exact-current
visual/Narrator/keyboard-only/High Contrast/mixed-DPI, clean-machine, installer/update, signing,
release, and physical M4 gates remain pending. The proposed M5D tranche is bounded archive/entry
properties plus safe-preview UX; active content, generic open-with/export, new mutation semantics,
and release work remain separately gated.

Progress through 2026-08-31: Milestone 5D completes the bounded archive/selected-entry properties and
safe-preview UX tranche without changing protocol 1.22. One Contracts/Client-only session inspector
projects only validated format/capability state and optional kind, known size, mathematically valid
ratio, representable UTC timestamp/basic attributes, fixed link/encryption/integrity-presence,
duplicate occurrence, and normalized persistent-key fields. It rejects malformed descriptor,
capability, volume, enum, size, and key shapes before replacing prior authoritative state and clears
properties at stale/offline/closed boundaries. Provider IDs/text, method strings, link targets, raw
integrity/fingerprint values, exceptions, secrets, recovery IDs, cache paths, handles, command lines,
and mutation authority remain absent.

Independent verification found one finite entry-row rendering blocker: adding one to the accepted
`Int32` duplicate ordinal before widening made `int.MaxValue` negative in both visible and Narrator
output. The remediated row model shares one value widened to `Int64` before addition. Its focused
regression first reproduced the negative output, then proves exact positive `#2,147,483,648`, matching
Automation text, and unchanged archive-controlled CR/LF/bidi escaping.

The preview service and model now exchange typed text, binary, oversize, unsupported-encoding, and
unsupported-entry outcomes. Only strict UTF-8 or BOM-proven UTF-16/UTF-32 discloses an encoding;
NUL-bearing, invalid, ambiguous, and control-dense data remains inert. Explicit loading, text, binary,
oversize, unsupported, offline, stale, cancelled, and fixed-error states preserve the exact 1 MiB plus
one detection byte, one-active-plus-newest-pending, four-materialization, delete-on-close cache,
selection/source/session cancellation, stale-response rejection, and deterministic focus boundaries.
Active content, HTML/script, embedded objects/macros, shell/Open With, export, and external launch stay
closed. System-color XAML, access keys, ordered focus, automation names/help/live status, wrapping,
layout rounding, and existing recycling virtualization are automated evidence only.

The complete M5D gate passes 475 unit/WPF, 29 adversarial, and 345 integration tests (849 total), zero
failures/skips, plus locked restore, all 15 no-known-vulnerability direct/transitive inventories,
formatter, zero-warning/error Release build, 41 focused model/STA and two real-Worker/exact-package
repeats, a finite same-task read-only review with zero findings, and clean process/recovery/cache/
fixture/nested/staging inventory. That accepted M5D production identity is 150 inputs / 2,506,077 bytes at
`AE156AB8437974EF927A0049E231FEAA1F012F8EAF8DD17B439D48E608C3F3BD`. The exact package is
`artifacts/RainArchives.M5DAutomatedPackage.1.22.AE156AB8`, with manifest SHA-256
`A28ECFC93F37A1145CA2E265341ED6DD49DABFDC5B5594470B00252816F90F12`; its App tree is
`609 / 233,098,501 / AC61D11EFBEF2CA4B83D5DE972F4A5B9746CBC28860189E4232E643E3ABE2EB0`
and Worker tree is
`205 / 85,041,541 / 1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`.
The `49601147` M5D package remains immutable rejected pre-remediation evidence. Accepted M5C/M5B
packages and rejected `126DC65F` remain immutable predecessor evidence.

Milestone 5E completes the bounded Recovery Center and support-evidence UX tranche without changing
protocol 1.22. One Contracts/Client-only modal surface owns creation, nested-workspace,
extraction-staging, and fixed support-evidence models. Creation keeps only its exact disposition
contract; nested recovery keeps exact-evidence copy-only independent reopen and discard; extraction
stays strictly read-only. Inventories are path-free until explicit inspection, destination text is
escaped, selection/refresh/cancellation/malformed/offline boundaries clear authority, and the dialog
cancels and joins initialization plus every child request before returning. Reopened sources are fresh
depth-zero sessions with fixed presentation text; no temporary path or former ancestry is exposed.
Transferred one-shot slots settle on every pre-response Client exit. Dialog ownership ends only at a
successful result handoff, and MainWindow ownership ends only after the complete authoritative state
transition and notification commit; every earlier cancellation/failure closes the exact session once.
Ordinary open follows the same pre-commit compensation rule.
No generic delete/cleanup, retry, restore, merge, replacement, archive/provider/exception text,
secret, command line, or new mutation authority enters WPF.

The complete M5E gate passes 494 unit/WPF, 29 adversarial, and 350 integration tests (873 total), zero
failures/skips, plus locked restore, all 15 current direct/transitive advisory inventories, formatter,
zero-warning/error Release build, focused model/STA, real Worker, exact-PID crash/restart, and exact
package flows. Production identity is 152 inputs / 2,584,721 bytes at
`6E0AEC1419512030AD82FFF8241B94DD7A94116661113BCA9B937EEC1764B150`. The accepted M5E package is
`artifacts/RainArchives.M5EAutomatedPackage.1.22.6E0AEC14`, with manifest SHA-256
`5B8711FDA66572D72DC26F6F6A88C875860861159F5F0B98B914514FD9AD431F`; its App tree is
`609 / 233,165,429 / F6D83EFEC18C7D60F09A5D7FD9E79CDF29FD912464F16347582B51FC2157F88D`
and Worker tree is
`205 / 85,041,541 / 1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`.
The `973CB3C3` package remains immutable rejected identity-script evidence and `E20205D2` remains
immutable rejected pre-drain-fix evidence. The `80A394D5` package remains immutable rejected
pre-ownership-remediation evidence. A finite same-task post-fix security/reliability/
accessibility/lifecycle review found no blocking or non-blocking issue; it is not independent or
manual acceptance. No exact-M5E-package manual result transfers to the current remediated M5F package; visual/Narrator/keyboard-only/High Contrast/mixed-DPI,
clean-machine, installer/update, signing, publication, release, and physical M4 gates remain pending.
Milestone 5F completes that bounded tranche without changing protocol 1.22. One fixed-field command
context canonicalizes the current 512-row visible selection against the newest validated archive and
browser state, then backs Enter/double-click, window accelerators, and a five-command context menu for
only folder navigation, validated properties, bounded safe preview, integrity, and extraction. Stale,
offline, busy, downgraded, missing, substituted, or duplicate identity state fails closed. Open With,
export/external launch, shell execution, mutation, overwrite/replacement, retry, restore, generic
cleanup, and provider-specific actions remain absent. WPF still references only Contracts and Client;
the 500,000-index, 512-visible-row, 256-history, preview/cache, protocol paging, and Worker limits are
unchanged.

The inspector remediation keeps the same boundary while removing ambient inspector presentation. The
inspector starts hidden; only the authorized Properties or Safe Preview command reveals its matching
tab. A fixed keyboard/Automation-described Close control invalidates preview generation, cancels and
joins bounded preview work before collapse, then restores entry-list/selection focus. Late completion
cannot repaint the closed surface, and no window-global Escape binding is introduced.

The complete remediated M5F gate passes 505 unit/WPF, 29 adversarial, and 354 integration tests (888 total), zero
failures/skips, plus locked restore, all 15 current direct/transitive advisory inventories, formatter,
zero-warning/error Release build, focused model/STA, real Worker, exact-package product, and
retained predecessor measurement flows. Production identity is 153 inputs / 2,605,119 bytes at
`3FD7C6F42250A75ED8B62878D15CEB6FF4361D8DCDCC667B8B6134375C35DDAD`. The package is
`artifacts/RainArchives.M5FInspectorRemediationPackage.1.22.3FD7C6F4`, with manifest SHA-256
`9A0F153DE2AF4203D385EE21BB1ABF8022E8B068893A68024C1693ED504CFD5B`; its App tree is
`609 / 233,182,721 / 78945421A56A188ED64C6908574613DEA3BEE0C3DBCB43F1A89EFE24BAA336C6`
and Worker tree is
`205 / 85,041,541 / 1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`.
The pinned 20,546-entry fixture and raw package measurement remain separately hash-bound; no
competitor-speed or manual visual/accessibility claim is made. A finite Daybreak-max scan sealed five
surfaces of the 94811C7A predecessor with zero findings under the required no-subagent limitation; a
finite direct remediation review found no blocker in the new inspector lifecycle but is not independent
acceptance. The predecessor package remains immutable and retains only its own observed DPI scaling and
broad keyboard-navigation evidence; those observations do not transfer. Accepted M5E and every earlier
package remain immutable predecessor evidence. Exact-remediated-package visual/Narrator/keyboard-only/
High Contrast/mixed-DPI/clean-machine plus installer/update/signing/publication/release and physical M4
gates remain `manual_pending`.

Milestone 5G-A completes the bounded main-shell presentation tranche without changing protocol 1.22,
dependencies, providers, filesystem/recovery behavior, or command/mutation authority. Three centralized
palettes plus shared semantic spacing, typography, focus, control, card, DataGrid, and rounded-stroke
vector-icon resources back one archive-identity/search header, tiered commands, compact navigation and
breadcrumbs, the dominant virtualized eight-column entry list, an adaptive 320-380-DIP inspector, a
wrapping narrow-safe welcome surface, and one status live region. System mode follows Windows without
changing it, explicit Light/Dark remains application-local, and Windows High Contrast is authoritative.
Keyboard order follows visual order; row Automation text adds only validated typed safety/encryption
facts. Every M5F command, top access key, accelerator, no-global-Escape rule, inspector cancellation/drain/
focus lifecycle, 500,000/512/256 bound, preview/cache limit, original/no-overwrite rule, and
Contracts/Client-only App boundary remains intact.

The accessibility remediation gives every custom surface an explicit foreground role: High Contrast
uses Window/WindowText, Control/ControlText, and Highlight/HighlightText pairs through headers,
alternating and selected rows, row icons, browser buttons, and direct muted shell content. It removes
access markers from the three duplicated welcome actions, leaving the top Create and Recovery Alt+C/
Alt+R owners unchanged and preserving welcome Tab and Automation behavior.

The complete M5G-A remediation gate passes 511 unit/WPF, 29 adversarial, and 356 integration tests
(896 total), zero
failures/skips, plus locked restore, all 15 current direct/transitive advisory inventories, formatter,
zero-warning/error Release build, focused resource/model/STA tests, and the exact-package product flow.
Production identity is 158 inputs / 2,670,808 bytes at
`5AF06A6EFF889132AFB2389312EE585E52D3721EDEA12D6151B3185278AC812C`. The package is
`artifacts/RainArchives.M5GAAccessibilityRemediationPackage.1.22.5AF06A6E`, with manifest SHA-256
`FADBAF43E14C03D34C8105D282B23EC1D1963FC893DF7FFC42AE94006EFF83D8`; its App tree is
`609 / 233,238,233 / BB266C5E105FB731220D56BD31A75316FBA8BC68CC9B62277B8A38F6F5FA828B`
and Worker tree is
`205 / 85,041,541 / 1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`.
The immutable `BE985F08` package is rejected pre-remediation evidence and remains pinned by a separate
identity check; none of its accessibility status transfers.
Automated resource/XAML/UI Automation/min-size evidence and an accessibility-tree inspection are not
direct visual, Narrator, keyboard-only, High Contrast, 100/150/200%, mixed-DPI, or clean-machine
acceptance. Those exact-package gates remain `manual_pending`, and no predecessor result transfers.

Milestone 5G-B completes that finite six-window reconciliation. Activity Center, Settings/Presets,
Recovery Center, Create, Rebuild Copy, and Password use shared semantic dynamic palettes and bounded
secondary-window styles. One weak/idempotent `AppThemeManager` attachment reapplies native caption
theme at source creation and every app-theme change, and `AppDialogService` preserves the existing
modal/nonmodal owner and close/drain/session-handoff lifecycles. Activity keeps its grid non-live and
one polite status owner; Recovery exposes only the selected tab's assertive status. Create/Rebuild keep
bounded scrolling around fixed-height recycling grids, and Password keeps a bounded ellipsized name
while exposing the complete escaped name through Automation help and its tooltip. Settings' existing
five-row preset editor remains explicit and regression-locked rather than receiving a redundant layout
change. Access keys, tab order, default/cancel behavior, fixed/redacted text, capability authority,
4,096-entry and other existing limits, originals, and no-overwrite rules remain unchanged.

The complete M5G-B gate passes 515 unit/WPF, 29 adversarial, and 357 integration tests (901 total),
with zero failures/skips. Locked restore, all 15 direct/transitive vulnerability inventories, formatter,
and a zero-warning/error Release build pass. Production identity is 158 inputs / 2,689,478 bytes at
`20FF2288E30812A5863018FE1FDAD0C46821997465F910203CCEC0A421B8462C`. The immutable package is
`artifacts/RainArchives.M5GBSecondaryWindowPackage.1.22.20FF2288`; its 1,083-byte manifest is
`C49011AD025FE9329F9D0AAC07C7A8EDF94A9287E52DA8D4EA1A9B581222317C`, its App tree is
`609 / 233,249,953 / 8D551EC0E0F7F513EB21DBBB65DCCDFDCC1FA8349A15A0AC82864878B7DCC342`,
and its unchanged Worker tree is
`205 / 85,041,541 / 1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`.
The accepted gate bound its then-current source, manifest, trees, principal hashes, Contracts coherence,
and forbidden assets, then exercised the packaged Worker with unchanged original hashes and zero scoped
residue. A current identity-only regression now pins it as immutable historical evidence. A fresh bounded
read-only review found no actionable M5G-B defect. Windows.Graphics.Capture
again returned a uniform black frame and no accessibility text, so this is automated local evidence—not
direct visual, Narrator, keyboard-only, High Contrast, 100/150/200%, mixed-DPI, or clean-machine
acceptance. No Contracts, Client/Worker/Security/Core/provider, protocol, dependency, filesystem,
recovery, mutation, settings-schema, Rain Explorer, Phase 6, shell, install/update, signing, publication,
or system-setting work was started.

Milestone 5G-C completes the final bounded Phase-5 product-identity tranche without changing protocol
1.22, runtime dependencies, MainWindow/theme/secondary-window XAML, custom chrome, shell registration,
installation, signing, or any archive/recovery/settings/filesystem authority. The original deterministic
`Assets/RainArchives.ico` adapts the existing `Icon.Archive` geometry to a violet rounded tile with a
near-black edge and soft-light archive glyph. It contains 16/20/24/32/40/48/64/128/256-pixel 32-bit alpha
DIB frames and has exact SHA-256
`554013891F9E3F50F2993CE8ACF636CB80457A9CF8A81099F69A0C675A7720A0`. One project-relative
`ApplicationIcon` binding supplies the EXE resource, Windows shell extraction, and top-level window
icon fallback; focused tests prove the ICO directory/DIB/alpha/mask/pixel bounds, deterministic hash, absence
of a duplicate XAML Window icon, small/large shell handles, and live top-level-window icon fallback.

The complete M5G-C gate passes 518 unit/WPF, 29 adversarial, and 358 integration tests (905 total), with
zero failures/skips. Locked restore, all 15 direct/transitive vulnerability inventories, formatter, and a
zero-warning/error Release build pass. The canonical input algorithm now includes `.ico`, so production
identity is 159 inputs / 3,070,555 bytes at
`56FFA87711984983DD8AA52A9735A901ECCDE92A2EDD8A00ED2F4E17FB301491`. The immutable package is
`artifacts/RainArchives.M5GCProductIdentityPackage.1.22.56FFA877`; its 1,083-byte manifest is
`D8284B822C2475C61D40B155719A20522BD05CB67C12A5A2EEB8D74BB84CE74F`, its App tree is
`609 / 234,012,833 / D490AB5B36ECDD4D3C67384C86178A7A23467E99E1E4CE6F8459B4676A99F8B5`,
and its unchanged Worker tree is
`205 / 85,041,541 / 1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`.
The exact-package flow validates the icon-bearing EXE plus every manifest/tree/principal identity, then
retains the packaged browser/preview/properties/inspector/integrity/extraction flow with unchanged source
and zero scoped residue. A fresh read-only post-patch review found no source/package defect. This remains
automated local evidence, not current-package visual, Narrator, keyboard-only, High Contrast,
100/150/200%, mixed-DPI, or clean-machine acceptance. Phase 6 remains unauthorized.

Milestone 5G-D follows that immutable product-identity checkpoint after direct visual review exposed
ordinary WPF controls still using native/default presentation outside the earlier shell and window
styles. `Themes/Controls.xaml` now centralizes dynamic semantic templates and styles for text and password
inputs, combo boxes, menus, scrolling, check boxes, tabs, DataGrid surfaces, progress, expanders, tooltips,
status items, and labels. Light, Dark, and High Contrast palette lookup remains dynamic, every required
WPF template part is retained, and the prior product-specific Button, tab, DataGrid, and inspector styles
compose the shared layer. Runtime regressions explicitly cover generated DataGrid checkbox containers and
editable text cells because those column types bypass ordinary implicit element styles.

Settings now has one bounded outer pixel-scrolling route at compact height while its footer remains
available, and large grids retain bounded internal review space. The Create shell's design-time/default
copy is archive-neutral while runtime format-specific capability text remains unchanged. The three
post-create open-failure/cancellation fallbacks are likewise archive-neutral; no creation, reopen,
capability, settings, filesystem, recovery, or overwrite behavior changed. The complete M5G-D gate passes
522 unit/WPF, 29 adversarial, and 359 integration tests (910 total), with zero failures/skips. Production
identity is 160 inputs / 3,157,575 bytes at
`651729F471468F590BB2F89CE663C14B5140356227509B3C247A93C74481D740`. The immutable current package is
`artifacts/RainArchives.M5GDDesignSystemPackage.1.22.651729F4`; its 1,083-byte manifest is
`01BF21F943C46B54C953E2A2BE3C5D037BE1FB26F738DBE67CA6C8C46AB30137`, its App tree is
`609 / 234,037,929 / AD4D7D620C75771788083B29B7DEB92DE403D665336BBF5D96810FF921BC212A`, and its unchanged Worker tree is
`205 / 85,041,541 / 1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`.
The earlier `B11E7A80` M5G-D package is preserved as superseded pre-final evidence: runtime editing proof
then exposed the generated DataGrid text editor's remaining native-style bypass, which the final source
and package close. Same-task automated GUI inspection of theme switching, menus, and Settings scrolling
does not satisfy product-owner visual, Narrator, keyboard-only, actual High Contrast, DPI, or clean-machine
acceptance. Those exact-package gates remain `manual_pending`; Phase 6 remains unauthorized.

Exit: production bindings exercise success/refusal/pending/error paths, UI automation plus manual Windows acceptance, clean-machine packaging, accessibility review, and no codec or filesystem logic in WPF.

## 6. Rain Explorer provider integration

Scope: Rain-side virtual archive location adapter over Contracts/Client; tab/history/breadcrumb/search; preview streams; explicit nested-session navigation; copy/drag out; approved drag/copy in; activity bridging; invalidation/reopen; cache cleanup ownership. Do not share WPF view models or provider implementation types.

Exit: actual Rain Explorer tabs and production bindings pass navigation/history/search/preview/drag/cancel/password/source-change tests without duplicating archive logic or weakening Rain’s existing file operations.

## 7. Windows associations and shell integration

Scope: explicit opt-in associations, Open/Extract/Test verbs, default-app-safe registration and uninstall, icon/protocol packaging. Non-goals: always-running service or invasive shell extension unless a separate evidence gate proves it necessary.

M7-A automated checkpoint (2026-09-03): Rain Explorer now exposes an explicit Settings action that creates
only its owned per-user ProgID/Registered Applications capability map, then opens Windows Default apps for
the user-controlled choice. It registers ZIP, 7z, RAR, TAR and supported compressed-stream terminal
extensions, recognizes compound TAR suffixes, and deliberately does not claim generic `.001` files. It
never writes `UserChoice`, an extension default, WinRAR keys/verbs, or foreign registrations. Archive
process arguments now enter the existing Rain Archives virtual-location route while ordinary-file launch
semantics remain unchanged. The 24 focused tests and complete 286-test Rain Explorer suite pass; the Release
build has zero warnings/errors and the current direct/transitive advisory query is clean. Exact package,
installer, source, composition, and manual-pending identities are recorded in `docs/milestone-7.md`.

M7-B automated checkpoint (2026-09-03): virtual archive entries now use inert Windows file-type/folder
icons rather than Rain Explorer glyphs; the archive root composes with outer tab history so Back returns to
the source folder and Forward reopens the archive; and the owned archive ProgID uses a dedicated original
nine-size archive icon while Rain Explorer's application icon remains unchanged. Missing icon payload fails
before registry mutation. The focused 33-case and complete 293-case suites pass, Release remains zero-warning/
zero-error, direct/transitive advisory queries are clean, and exact package/installer evidence is recorded in
`docs/milestone-7.md`. Protocol 1.22 and all Rain Archives authority boundaries are unchanged.

M7-C automated checkpoint (2026-09-04): Rain Explorer now preserves the source identity and fixed actionable
reason after an archive refusal, visibly presents every then-qualified managed creation format, adds bounded
**Extract to...**, routes **Open with...** through the documented Windows chooser, and suppresses only exact
Rain-owned or duplicate-WinRAR projected menu entries. Malformed extension-only files remain refused. Exact
source/package/installer evidence and the remaining direct Windows shell matrix are recorded in
`docs/milestone-7.md`.

M7-D automated checkpoint (2026-09-05): protocol 1.23 fixes valid RAR5 directories without optional CRC
metadata and conditionally adds unencrypted single-volume RAR5 explicit/tree creation through only the user's
separately installed, licensed, signed x64 WinRAR 7.23-through-7.x encoder. The signed 7.20 installation on the
verification machine reports `UpdateRequired`; Rain neither bundles nor updates it. Concrete-provider
re-resolution, an exact executable lease, bounded post-handshake workspace maintenance, no-follow ownership,
verified child termination, complete encoder/SharpCompress readback, and exact-PID crash cleanup pass. The
Rain Archives gate is 934/934; Rain Explorer is 315/315 both normally and against the exact packaged Worker.
Current immutable package/installer identities and manual RAR creation/open/shell checks are recorded in
`docs/milestone-7.md`.

This is partial Milestone 7 evidence. Direct default selection/reversal, WinRAR context-menu preservation,
qualifying-installed-WinRAR RAR creation, installed stable-path behavior, install/upgrade/uninstall cleanup, additional Open/Extract/Test/Create
verbs, supported-Windows policy/account matrices, signing, publication, and release acceptance remain open.

Exit: install/upgrade/uninstall matrix across supported Windows versions, per-user/system policy behavior, long/quoted path tests, no orphaned registrations, and clean fallback when app files are absent.

## 8. Performance, compatibility, and release hardening

Scope: startup/list/extract/create/CPU/memory/cancellation benchmarks, competitor methodology, format corpus, fuzzing, SBOM/notices, signing/update provenance, telemetry-off verification, and release runbook.

Exit: reproducible results and thresholds, no unsupported speed claims, all licenses/source obligations packaged, signed artifacts verified from final publish output, and unresolved risks explicitly accepted or fixed.
