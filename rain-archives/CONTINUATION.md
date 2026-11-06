# Rain Archives canonical continuation

Status date: 2026-09-05 (America/Toronto)

Latest user-requested checkpoint: **extraction conflict diagnosis and UI repair**, Rain Explorer
**2.0.0**, protocol **1.24**. See [extraction-sync-conflict.md](docs/extraction-sync-conflict.md).
The reported ZIP is valid. Live checks found temporary extracted files hard-linked into Google
Drive's upload cache. The unchanged Worker refuses those links; Explorer now distinguishes this
from a destination appearing before commit. The misleading-message repair is complete, but reliable
extraction into actively synced folders is **not fixed**. A copied ZIP extracted through the exact
packaged Worker outside that synced tree; all 773 file hashes and 240 directories match.

Retained predecessor: **RAR long-path regression repair**, Rain Explorer **2.0.0**,
protocol **1.24**. See [rar-long-path-fix.md](docs/rar-long-path-fix.md) for the source/package identity,
reproduced real-Worker failure, fix and verification. Native private-workspace operations now use
extended-length paths without depending on registry or executable-manifest opt-ins. Fixed creation
diagnostics identify RAR preparation failures without exposing provider text or paths.
The preceding [performance repair](docs/rar-performance.md) retains its pinned per-path checks and
full publication protections. Its recorded timings belong only to that predecessor build.
The prior [creation/theme repair](docs/creation-ui-remediation.md), its negotiated 65,536-entry tree
budget, explicit-root/rebuild/raw limits and earlier immutable evidence remain unchanged.

This is the canonical entry point for a fresh Rain Archives task. Read this file before
changing source. Then read the linked architecture, security, testing, format, and milestone
documents for the area being changed.

The workspace currently has **no Git repository**. The live filesystem is authoritative.
Do not infer a clean/dirty state, initialize Git, commit, push, or create a pull request unless
the user explicitly authorizes that exact action in a later task. The canonical source
fingerprint below is the reproducible identity for the current production/benchmark inputs.

If this file conflicts with production source or an executable test, inspect the discrepancy.
Source and current runtime evidence outrank prose, but the discrepancy must be corrected rather
than silently ignored. Historical package and benchmark evidence applies only to its named
source fingerprint.

The current user message always controls. The authorization below applies only when that message
explicitly requests implementation or continuation and contains no narrower read-only, planning,
diagnostic, or other non-mutating restriction.

When the user starts a fresh task and says to continue from this document, that authorizes the
production implementation described in **Exact next action**. Section 13 may define either one
slice or a finite multi-slice phase. Reading files, rebuilding, packaging, or rerunning existing
tests is preflight evidence, not completion. The task must implement the named production behavior
and verify it end to end. When Section 13 defines a phase, completing one slice is a checkpoint, not
permission to stop: update the checkpoint, preserve its evidence, and continue automatically to the
next predeclared slice whenever no genuine safety or authorization blocker exists. Replace the
exact next action only after the entire finite phase manifest is complete. Independent manual gates
must be recorded as pending without pausing unrelated authorized implementation, and this section
must not be turned into a manual-only pause unless the user explicitly asks for one.

## 1. Original product goal

Rain Archives is SailHub's Windows archive manager and the single archive subsystem intended for
both:

1. A fast, polished, approachable standalone Windows archive application with advanced controls,
   no account, cloud dependency, advertising, trial interruption, or unnecessary background
   service.
2. Deep Rain Explorer integration in which archives behave as honestly like folders as the format
   permits: tabs, breadcrumbs, history, search, previews, extraction/copy/drag-out, supported
   add/replace/delete, activity reporting, invalidation, and explicit read-only/writable state.

The standalone application and Rain Explorer must consume the same archive engine and stable
   contracts. Rain Explorer UI types must never enter the archive core. Compression codecs must not
   be implemented from scratch. RAR reading remains managed; conditional RAR5 creation is approved
   only through the user's separately installed, licensed, exact signed WinRAR 7.23-through-7.x
   encoder and the protocol-1.23 gate described below. Rain never bundles that encoder.

The complete product must eventually cover secure detection, listing, streaming, selected/full
extraction, integrity testing, passwords, solid archives, realistic multipart support, very large
archives, bounded nested archives, corrupt/partial behavior, creation, staged rewrite-based
modification, progress, cancellation, conflict/password decisions, crash-safe publication,
cleanup, a shared operation model, WPF UX, Rain Explorer integration, Windows associations/shell
verbs, compatibility/performance hardening, packaging, licensing, signing, and release evidence.

## 2. Binding architecture decisions

These decisions are already represented in production code and tests. Do not casually replace
them while implementing an unrelated feature.

1. **Platform:** .NET 10, C#, WPF, Windows x64. The SDK is pinned by `global.json` to 10.0.301
   with latest-patch roll-forward. A platform change needs compelling measured evidence and a
   migration plan compatible with Rain Explorer.
2. **One shared engine, multiple clients:** `RainArchives.Contracts`, `RainArchives.Client`, and
   provider-neutral Core models are the integration boundary. The standalone WPF App, CLI, and
   future Rain Explorer provider consume those boundaries rather than duplicating archive logic.
3. **On-demand out-of-process Worker:** untrusted archive parsing, decompression, extraction,
   integrity, creation, and recovery mutation run in `RainArchives.Worker`, not in WPF or Rain
   Explorer. There is no permanent service.
4. **Authenticated, versioned IPC:** the Client creates a random per-launch named pipe and
   bootstrap proof, verifies the launched Worker PID, negotiates protocol/features, and owns the
   Worker in a kill-on-close Job Object. The current wire version is **1.23**. Changes to wire
   semantics require a protocol decision, downgrade tests, and compatibility fixtures.
5. **Provider/adaptor model:** format codecs sit behind Core provider contracts. Clients do not
   reference SharpCompress, BCL ZIP internals, native 7-Zip, Worker, or Security.
6. **Managed reader is current shipping authority:** SharpCompress 0.50.4 is the active reader
   adapter. It is not trusted with extraction paths; Rain copies decoded streams into guarded,
   Rain-owned outputs.
7. **Qualified single-output writers are the active write authority:** BCL `ZipArchive`, BCL
   `TarWriter`/`GZipStream`, and SharpCompress `BZip2Stream` write only their qualified unencrypted
   ZIP/TAR/TAR.GZ/TAR.BZ2/GZIP/BZIP2 shapes through a caller-owned guarded staging stream. They do
   not own source enumeration, original source paths, commit, rollback, or recovery. Conditional
   RAR5 creation uses the separate Worker-only WinRAR adapter: it receives validated streams,
   materializes them in a token-owned private bridge workspace, holds the verified encoder against
   replacement, independently tests/readbacks the output, and then returns bytes to the same guarded
   no-replace transaction. Missing, old, untrusted, unsupported, or newly installed-after-start
   encoders keep the capability closed with a typed reason.
8. **Native 7-Zip gate remains disabled:** `RainArchives.Providers.SevenZip` advertises no formats.
   SharpSevenZip 2.0.109 and official 7-Zip 26.02 are audited candidates/evidence tools, not current
   runtime dependencies. No `7z.dll` or `SharpSevenZip.dll` is shipped.
9. **Native assets, if ever approved, stay Worker-private and replaceable:** load one exact
   app-private architecture-specific binary by absolute path. Never search `PATH`, the current
   directory, registry, or an installed 7-Zip. App and Worker must update atomically as one package.
10. **WPF dependency boundary:** `RainArchives.App` references only Contracts and Client. It must
    not reference Worker, Security, Core, or provider projects. WPF view models do not cross IPC.
11. **Filesystem authority stays in Security/Worker:** providers receive validated archive-relative
    metadata and already-open streams. They do not receive user source/destination filesystem paths.
12. **New-destination staged commit:** extraction and current creation publish only to a previously
    absent destination through operation-owned staging, exact validation, durable flush, and
    no-replace commit. Merge extraction, overwrite, and in-place updates are not implied.
13. **Modification means complete staged rebuild:** future add/replace/delete must write a complete
    Rain-owned replacement and atomically commit it. UI wording must say Rebuild when that is the
    actual operation. Never run an adapter's delete-before-final-move update path on a user archive.
14. **Capabilities are truthful and per session/format:** multipart or unknown volume sets are
    list-only today. A library-wide advertised feature must not make a specific opened archive
    appear writable, streamable, testable, or extractable when it is not safely supported.
15. **Nested work is explicit:** no listing, search, preview, or open action recursively sniffs
    archive entries. A caller explicitly selects one material entry and receives a bounded child
    session. Temporary paths never cross IPC.
16. **Recovery is local, path-free first, and conservative:** startup inventory may report
    counts/opaque IDs, and unresolved or suspicious state is preserved. Mutation of user-facing
    recovery artifacts requires fresh, exact evidence and an explicit user-confirmed action. The
    narrow RAR bridge exception may automatically remove only an inactive exact-name/exact-owner
    ordinary non-reparse workspace left by a killed Worker; active or unverified state is preserved
    with a fixed warning. No blind retry is permitted after an uncertain result.
17. **Secrets use binary secret slots:** passwords never appear in command-line arguments or normal
    JSON frames/logs. Temporary slots must be settled on all success, refusal, cancellation, and
    failure paths. SharpCompress still forces one immutable managed-string copy; do not claim full
    zeroization.
18. **No cloud/account/telemetry dependency:** engine operations remain local and on demand. Do not
    add advertising, account gates, or a background service as implementation shortcuts.
19. **Rain Explorer stays a Contracts/Client consumer:** its authorized Phase-6/7 integration uses
    neutral DTOs and client methods only. Never couple Rain Explorer UI types into Worker, Security,
    Core, or providers, and never restore a second archive implementation in Explorer.

Detailed rationale and disadvantages are in [docs/architecture.md](docs/architecture.md),
[SECURITY.md](SECURITY.md), and
[docs/dependency-evidence.md](docs/dependency-evidence.md).

## 3. Current solution and dependency graph

```text
RainArchives.App (WPF)
    -> RainArchives.Client
    -> RainArchives.Contracts

RainArchives.Cli
    -> RainArchives.Client
    -> RainArchives.Contracts

Rain Explorer (future, separate repository)
    -> RainArchives.Client
    -> RainArchives.Contracts

process boundary: authenticated versioned named-pipe IPC

RainArchives.Worker
    -> RainArchives.Contracts
    -> RainArchives.Core
    -> RainArchives.Security
    -> RainArchives.Providers.SharpCompress
    -> RainArchives.Providers.Zip
    -> RainArchives.Providers.SevenZip (disabled gate)

RainArchives.Core -> RainArchives.Contracts
RainArchives.Security -> Core + Contracts
each provider -> Core + Contracts
```

Test projects:

- `RainArchives.UnitTests`: Core/Security/App/client-seam and STA WPF tests.
- `RainArchives.AdversarialTests`: hostile path/archive/recovery fixtures.
- `RainArchives.IntegrationTests`: real packaged Worker, IPC, native/process, filesystem, WPF
  view-model, and crash/restart behavior.
- `RainArchives.RecoveryCrashProbe`: non-shipping wrapper that calls production Worker/Security
  paths with synchronous test-only checkpoints. The shipping Worker has no probe switch.
- `RainArchives.Benchmarks`: startup/listing, extraction, creation, large-file, memory/CPU, and
  cancellation probes. Historical results remain tied to their exact fingerprints.

## 4. Protocol and enforced bounds

Current protocol is **major 1, minor 22**:

| Minor | Added negotiated feature |
|---:|---|
| 1 | required secret slots, events, new-destination extraction, source fingerprinting, provenance |
| 2 | request cancellation |
| 3 | persistent normalized-path/duplicate-ordinal entry keys |
| 4 | `Single` / `ConfirmedMultipart` / `Unknown` volume identity |
| 5 | authenticated bounded complete-entry stream leases |
| 6 | explicit bounded nested archive sessions |
| 7 | new unencrypted explicit-file ZIP creation |
| 8 | separate bounded directory-tree ZIP creation request |
| 9 | path-free creation-recovery list and explicit read-only inspection |
| 10 | exact-revision creation-recovery disposition |
| 11 | path-free nested-workspace list/inspect/exact-evidence discard |
| 12 | new-copy-only ZIP add/replace/delete rebuild |
| 13 | retained regular-file rename/move during ZIP rebuild |
| 14 | bounded retained-tree edit plus Worker-owned source-folder review |
| 15 | explicit ZIP-representable metadata editing |
| 16 | bounded global/session ZIP write capability discovery |
| 17 | Store-only TAR explicit/tree creation and TAR capability discovery |
| 18 | TAR.GZ/TAR.BZ2 explicit/tree creation, raw GZIP/BZIP2 creation, managed capabilities |
| 19 | bounded path-free connection-lifetime operation activity inventory |
| 20 | local extraction-staging recovery inventory and explicit read-only artifact inspection |
| 21 | fresh exact-evidence nested-workspace source reopen as an independent root session |
| 22 | bounded fixed-field support diagnostics and explicit containment-policy disclosure |

Important hard ceilings from `ProtocolConstants`:

- Control frame: 1 MiB; JSON depth: 32; secret payload: 16 KiB.
- Open sessions: 16; active/queued operations: 32; concurrent operations: 1; retained terminal
  activity: 64; maximum activity response records: 96; pending requests: 256; control requests: 8.
- Entry stream leases: 8; frame: 64 KiB; default request: 64 MiB; maximum: 256 MiB.
- Nested archive depth: 3; per request: 256 MiB; ancestry chain: 256 MiB; active aggregate:
  512 MiB.
- ZIP creation/rebuild: 4,096 material entries and 64 GiB of decoded/new-source data; rebuild uses at
  most 4,096 combined directives and 512 KiB of combined UTF-8 path/name payload.
- TAR-family creation: 4,096 material entries and 64 GiB of regular-file source data. Raw GZIP/BZIP2
  creation: exactly one ordinary regular source file, at most 64 GiB.
- App-only ZIP preset storage: 128 KiB document, 64 user presets, 64 Unicode scalar values and 256
  UTF-8 bytes per name.
- Creation, nested-workspace, and extraction-staging recovery inventories: at most 1,024 published
  records each. Extraction inspection additionally bounds one observed tree to 2,000,000 objects
  and 64 GiB.
- Maximum path characters: 32,767; maximum page size: 4,096.

Do not raise a bound merely to make a failing fixture pass. Any change requires resource, frame,
overflow, cancellation, downgrade, and compatibility analysis.

## 5. Milestone state

Only **Milestone 1 is fully accepted**. Later milestones contain production-grade slices but have
not met their complete exit criteria.

| Milestone | Current status |
|---|---|
| 1. Foundation/dependency proof | **Accepted.** Protocol-1.3 self-contained package, automated suite, clean Windows Sandbox, physical storage/ACL/exFAT, product-owner keyboard/Narrator/mixed-DPI/contrast/password/extraction checks, and physical CachyOS/Samba extraction success/interruption evidence are recorded. |
| 2. Secure read compatibility | **Partial.** Six read/preview/nested source slices, protocol-1.11 nested recovery review/discard, and protocol-1.21 exact-evidence independent recovery reopen exist. Broader version/tool conformance, safe multipart operations, true range reads, external preview, older golden binaries/frames, and stable multi-level navigation restoration remain. |
| 3. Creation/modification | **Partial product milestone; managed writers plus conditional RAR5 creation are implemented.** Protocol 1.7/1.8 creates ZIP; 1.12-1.15 rebuilds an eligible ZIP to a distinct absent destination; 1.16 adds ZIP capabilities; 1.17 adds TAR; 1.18 adds TAR.GZ, TAR.BZ2, raw GZIP/BZIP2, and managed capabilities; 1.23 adds conditional unencrypted single-volume RAR5 explicit/tree creation through the user's separately installed, licensed, exact signed WinRAR 7.23-through-7.x encoder. The existing immutable protocol-1.18 package still binds the six managed-format performance/physical evidence. The protocol-1.23 route retains absent-destination/original-preservation, exact digest readback, a concrete provider gate, executable replacement lease, and token-owned crash-cleaned workspace. This machine's signed WinRAR 7.20 is truthfully `UpdateRequired`; no binary is bundled or auto-updated. ZIP/RAR encryption, XZ and native 7z creation, split output, RAR rebuild/update, and overwrite/replacement/in-place remain closed. Recovery Center inspection, product-owner Narrator/keyboard-only/High Contrast/mixed-DPI qualification, actual second-machine SMB/blocked-UNC/DFS/no-file-ID, current qualifying-installed-WinRAR GUI/child-process acceptance, and release acceptance remain `manual_pending`. |
| 4. Worker reliability/lifecycle | **Autonomously implementable source/package scope complete; formal milestone remains `manual_pending`.** Protocol 1.19 fixes truthful 32-admitted/one-running/64-retained activity and cancel-only controls; 1.20 adds non-destructive extraction-staging inventory/inspection; 1.21 adds exact-evidence copy-only nested recovery reopen as a fresh depth-zero session while partial discard remains conservatively preserved; and 1.22 adds fixed-field redacted support diagnostics plus the explicit same-user Worker/Job containment decision. Typed decision continuations, retry, pause, reorder, automatic cleanup, and extraction disposition remain closed because current evidence cannot make them safe. The 773-test gate, exact-PID crash matrix, retained-binary/downgrade evidence, independent security scan, and local self-contained package pass. Physical network/filesystem, direct GUI/accessibility, installer, signing, and release acceptance remain separate. |
| 5. Standalone WPF App | **Partial; bounded Milestone 5A Activity Center, 5B archive browser, 5C Settings/Presets plus managed create/rebuild, 5D properties/safe preview, 5E Recovery Center/support evidence, 5F existing-command context/performance, 5G-A main-shell presentation, 5G-B secondary-window reconciliation, 5G-C product identity, and 5G-D control-design-system tranches complete.** The Contracts/Client-only browser supplies validated navigation/search/history/selection and bounded virtualization. Activity Center remains truthful to the 32-admitted/one-running/64-retained/unspecified-order policy with snapshot-authorized Cancel only. Settings persist only fixed bounded UI/default fields and validated ZIP presets; all six managed create formats and eligible ZIP Rebuild Copy repeat fresh capability validation. The session inspector projects fixed validated archive/entry fields and typed inert preview outcomes. The four-surface Recovery Center preserves exact creation/nested authority, read-only extraction evidence, fixed diagnostics, and full cancellation/drain without path or secret authority. One canonical current-selection context backs only navigation/properties/safe-preview/integrity/extraction mouse and keyboard commands. M5G-A centralizes the Light/Dark/High Contrast main shell; M5G-B applies that semantic system, bounded layouts, deliberate live regions, recycling grids, and weak native-caption theme attachment to Activity, Settings, Recovery, Create, Rebuild, and Password without changing authority. M5G-C binds an original deterministic nine-frame application icon through the standard WPF application icon path. M5G-D centralizes semantic ordinary-control templates, closes generated DataGrid element fallbacks, and gives Settings one bounded scroll route without changing authority. Current package-backed Worker/browser/preview/operation/settings/create/rebuild/recovery/context, inspector, icon, and control-presentation flows pass. Localization/touch, installer/update/release work, and exact-M5G-D-package Narrator/keyboard-only/real DWM/actual High Contrast/100/150/200%/mixed-DPI/visual/clean-machine acceptance remain. |
| 6. Rain Explorer integration | **Automated implementation checkpoint complete; formal milestone remains `manual_pending`.** M6-A through M6-F and the M6-exit duplicate-authority removal run in the uncommitted Rain Explorer working tree over base `0bc3fb090bef4d93e0db5bb6d66813b80e8385f7`. Read-only archive locations, bounded preview, integrity/selected extraction, nested navigation/reopen, truthful delayed-transfer refusal, managed creation/rebuild, and Worker-owned physical extraction are integrated without restoring a second codec implementation. Direct integrated visual/accessibility, physical-network, clean-machine, install, signing, and release gates remain. |
| 7. Associations/shell integration | **Partial; M7-D valid-RAR open and conditional RAR creation remediation is automated/package complete.** M7-A's explicit owned per-user archive registration, M7-B's pathless entry icons/outer navigation/dedicated archive icon, and M7-C's Extract to/Open With/menu/refusal UX remain. Valid RAR5 directories without optional CRC now list instead of triggering the misleading pre-read refusal. Protocol 1.23 lets Rain Explorer add a seventh RAR row when a qualified encoder is available and otherwise shows the exact fixed disabled reason. Production still never writes `UserChoice`, extension defaults, WinRAR keys, or foreign verbs, and it never bundles/updates WinRAR. Direct Open With/context-menu/extraction/default/reversal, qualifying-WinRAR GUI creation, install/upgrade/uninstall cleanup, signing, and publication remain `manual_pending`. |
| 8. Performance/compatibility/release hardening | **Partial evidence only.** Current-source ZIP and exact-package all-managed-format `1C85F6D9` local baselines, corpus tests, package/license work, and fuzz smoke exist, but cross-machine/cold-cache thresholds, broader tool/layout matrices, and final release gates do not. |

See [docs/milestones.md](docs/milestones.md) for milestone exit criteria.

## 6. Implemented features

### Reading, detection, and browsing

- Content-first bounded detection and provider confirmation for ZIP, 7z, RAR, TAR, TAR.GZ,
  TAR.BZ2, TAR.XZ, GZIP, BZIP2, and XZ.
- Hierarchy reconstruction with synthetic directories, stable per-session IDs, and negotiated
  persistent normalized-path/duplicate-ordinal keys for future navigation history.
- Metadata for path/kind/sizes/timestamps/attributes/method/checksum/encryption where the provider
  supplies trustworthy values; untrusted display text is escaped for CLI/WPF.
- Paginated listing with encoded response-byte limits.
- The production WPF browser validates the bounded complete session index before adoption, canonicalizes
  archive-relative folder locations, sorts duplicate entries deterministically, and materializes only
  the current 512-row page. Its index, navigation history, and visible page are capped at 500,000
  entries, 256 locations, and 512 rows. Search is ordinal Unicode case-insensitive within the current
  folder; Back/Forward branch conventionally, Up records a new location, and root Up is disabled.
- Reopen restoration requires persistent-key support plus exact canonical source path, length,
  last-write time, file identity, and prefix SHA-256 equality. A source/session change, failed or
  unauthoritative close, downgrade, offline/error result, or mutation clears presentation authority;
  it never invents a filesystem path or treats display text as identity.
- Integrity testing and selected/full extraction for proven single-volume supported sessions.
- AES-256/ZipCrypto ZIP reads; content/header-encrypted 7z; checked header-encrypted RAR.
- Checked solid 7z/RAR extraction/integrity behavior. Solid RAR deliberately lacks preview and
  nested-open capabilities because bounded skipped-member decoding is unresolved.
- Multipart ZIP/7z/RAR detection/listing with explicit list-only capabilities and structured
  refusal of test/extract/preview.
- ZIP64 checked layouts: 65,536-entry, central directory beyond 4 GiB, per-entry size/offset extras,
  classic split, and official multi-disk ZIP64 listing.
- Corrupt/truncated/malformed errors are structured and keep the Worker usable in checked cases.

### Streaming, preview, and nested archives

- Authenticated secondary pipes for complete-entry leases with exact Worker PID and nonce proof,
  64 KiB framing, length limits, checksum-validating provider reads where applicable, cancellation,
  and cleanup.
- Client-owned bounded seekable preview cache uses random `CreateNew`, exclusive, delete-on-close
  files and publishes only a read-only wrapper over the already-open handle; no cache path is part
  of the public contract.
- WPF bounded text preview: 1 MiB plus one detection byte, strict UTF-8 or BOM-marked UTF-16/32,
  active-content refusal, control/bidi escaping, latest-wins request serialization, password retry,
  and cleanup.
- Explicit nested opening through provider integrity-copy, content redetection, depth/byte/global
  limits, provenance inheritance, parent/child ownership, and descendant close cleanup.

### Secure extraction and operation lifecycle

- New-destination extraction only; no merge or overwrite.
- Rain-owned staging, conservative destination-space preflight/runtime checks, guarded streamed byte
  and expansion-ratio accounting, object-count space refresh, exact output validation, durable
  flush, and no-replace final commit.
- Selected-entry and full extraction, current/aggregate progress, bytes, file counts, throughput,
  elapsed time, ratio, ordered warnings, terminal state, and cancellation.
- Source deny-write/delete lease plus identity/length/time/prefix-hash mutation checks.
- Canonical Mark-of-the-Web propagation for valid extraction sources; untrusted URL fields are
  discarded.
- Worker/client cancellation control lane, bounded response/close acknowledgements, and owned
  Worker teardown when a synchronous request cannot settle.

### Creation and recovery

- New, previously absent, unencrypted ZIP creation from explicit files and/or bounded non-reparse
  directory roots.
- Protocol-1.17 Store-only TAR creation from explicit files and/or bounded non-reparse directory
  roots, with regular-file/directory-only metadata, UTC/PAX timestamps, Unicode, zero-byte files,
  and empty-directory preservation.
- Protocol-1.18 TAR.GZ and TAR.BZ2 creation over the TAR policy plus raw GZIP/BZIP2 creation from
  exactly one ordinary regular source file. GZIP-backed formats support Store/Fast/Balanced/Maximum;
  BZIP2-backed formats support Balanced. Raw member names derive from the destination stem.
- Provider-neutral staged manifests carry exact per-file SHA-256 values collected through the
  authoritative source-copy stream. Format-specific readback verifies full structure, decoded
  payload hashes, footer/trailer and trailing-data rules before publication; BZIP2 refuses appended
  valid members as well as junk.
- Store, Fast, Balanced, and Maximum presets; zero-byte files, Unicode, empty ZIP, root and empty
  directory preservation, deterministic tree order, timestamp conversion/clamping.
- Maximum 4,096 entries / 64 GiB source bytes, case-insensitive NFC collision checks, source/dest
  overlap refusal, pinned source enumeration, final exact identity manifest revalidation.
- Unanimous valid source-zone propagation into canonical output `Zone.Identifier`; mixed/invalid
  state produces no propagated stream and at most one sanitized warning.
- Protocol-1.12 ZIP Rebuild Copy applies one closed delete/add/replace plan to an already-open,
  eligible single-volume unencrypted ZIP and recompresses a distinct absent destination while the
  original remains byte-identical. Retained entries use provider checksum-copy; mapped files keep
  stable deny-write/delete leases through final validation and commit.
- Protocol-1.13 `zip-rebuild-rename-v1` adds exact persistent-key regular-file rename/move as a
  separate additive operation. Delete/add/replace/rename resolve simultaneously, including safe
  swaps, before staging; final renamed paths participate in the same collision policy, escaped
  progress, metadata preservation, and exact staged-manifest readback.
- The staged rebuilt ZIP is durably flushed and read back through its exact handle; entry count,
  order, path, kind, decoded length, attributes, and DOS-normalized timestamps must match the closed
  manifest before the handle-relative no-replace commit.
- Five-phase local creation journal: `Admitted`, `Writing`, `Finalized`, `CommitIntent`, `Committed`.
- Path-free creation-recovery inventory, explicit inspection, four closed actions (commit exact
  staging, discard exact staging, accept exact published final, acknowledge none), exact-revision
  stale refusal, durable Intent/Completed receipt, idempotent same-action replay, and bounded newest
  256 / 30-day completed-receipt retention.
- Path-free nested-workspace list/inspect/discard for only a freshly verified inactive exact shape.
  Exact revision/count/bytes, no-follow identities, one-link/no-ADS/no-reparse limits, pre-delete
  revalidation, pre-mutation cancellation, and conservative partial-crash preservation are enforced.
- Path-free nested-workspace recovery reopen for one freshly inspected exact source. Protocol 1.21
  requires the exact workspace/source/revision/count/byte evidence, revalidates liveness, depth,
  ancestry, identity, type, link, reparse, ADS, and content length, copies through pinned handles,
  redetects the copy, and returns a new independent depth-zero session. It never exposes or adopts
  the recovered temporary path and never mutates the original recovery workspace.
- CLI review surfaces for both recovery families.
- WPF Recovery Center with separate creation and nested tabs. Nested listing/inspection/confirmation
  never disclose a temporary path; only fresh `VerifiedDisposable` evidence enables Discard; the
  confirmation defaults to No and forwards exact evidence once; failure/cancellation/refresh/
  selection change clears authority; close cancels and drains both view models.

### Standalone WPF shell

- Open archive and use a reusable Contracts/Client-only browser with root/folder navigation,
  breadcrumbs, Back/Forward/Up, current-folder Unicode search, deterministic selection and duplicate
  occurrence, bounded paging/index/history, truthful read/write capability summary, and exact-source
  persistent-key reopen resolution.
- Explicit no-archive/loading/empty/offline/unsupported/error/stale-source surfaces, system-color
  resources, DPI-safe layout, recycling virtualization, Alt+Left/Right/Up, Ctrl+Home/F, page
  accelerators, deterministic focus restoration, automation names/help, and polite live status.
- Selected/full extraction, integrity test, progress/activity status, cancellation, password prompts,
  and fixed error states remain integrated without moving source, secret, staging, or mutation
  authority into WPF.
- Versioned bounded Settings and Presets center for one managed-format hint, per-format create and ZIP
  rebuild preset hints, one advanced-review boolean, and validated user ZIP presets. Strict storage
  states, explicit reset, current-capability clamping, serialized work, and close/shutdown drain are
  presentation-only and persist no archive path, entry text, secret, recovery/operation data, provider
  text, temporary path, handle, or mutation authority.
- Typed managed-format capability discovery drives ZIP, TAR, TAR.GZ, TAR.BZ2, GZIP, and BZIP2 in the
  same creation dialog. Folders are enabled only for archive formats, raw formats require one file,
  only provider-qualified presets are selectable, the final review requires a distinct absent
  destination, and successful creation reopens the authoritative output.
- Capability-gated Rebuild Copy dialog for explicit deletion/add/replacement/rename plus bounded
  source-folder add, retained-subtree move/delete, and explicit metadata review, shared preset and new
  destination selection, honest original-untouched wording, shared progress/cancel, and reopen only
  after authoritative completion.
- Typed bounded safe preview plus one four-surface Recovery Center for creation, nested-workspace,
  read-only extraction-staging evidence, and fixed support diagnostics.
- Central semantic Light/Dark/High Contrast resources, shared spacing/type/control/card/DataGrid/icon
  tokens, one identity/search header, tiered commands, compact navigation, dominant eight-column
  virtualized list, wrapping welcome surface, adaptive on-demand inspector, visual-order focus, and
  one status live region. System mode follows Windows without changing it; explicit Light/Dark is
  app-local and High Contrast is authoritative. Custom Window, Control, and Highlight surfaces use
  their paired system foregrounds, and only the top no-session Create/Recovery actions own Alt+C/Alt+R.
- The same dynamic semantic system now covers Activity, Settings, Recovery, Create, Rebuild, and
  Password with bounded layouts, explicit keyboard/Automation metadata, deliberate live owners,
  recycling review grids, and weak/idempotent native-caption theme attachment.
- A shared dynamic-resource control layer now owns text/password inputs, combo boxes and popups, menus,
  scrolling, check boxes, tabs, DataGrid surfaces and generated editors, progress, expanders, tooltips,
  status items, and labels. Settings has one compact-height outer scroll route with bounded inner grids;
  Create shell copy and post-create open fallbacks are archive-neutral without changing runtime format
  capability text or operation behavior.
- Per-Monitor V2 manifest and prior product-owner acceptance of mixed-DPI, contrast theme,
  keyboard-only selection/extraction, and Narrator flow against the accepted M1 package. Those
  observations do not transfer to the current M5G-D package.

### Diagnostic CLI

The CLI supports list, test, extract, capability/preset diagnostics, fixed-field support diagnostics,
create-zip, create-tar,
create-tar-gz, create-tar-bz2, create-gzip, create-bzip2, rebuild-zip including explicit file/tree/
metadata edits, creation-recovery list/inspect/apply, and nested recovery list/inspect/discard/reopen.
Passwords are stdin/secret-slot data for read routes, never command-line arguments; creation has no
password/encryption option.

## 7. Exact current checkpoint

Current source identity:

- Protocol: **1.24**.
- Canonical production/benchmark fingerprint:
  **`DB6BBEDA3A758E14F865C6A0BAD6CDF74091685F13B127E4E5AD1F894BD7E5F0`**.
- Fingerprint input count: **165**; input bytes: **3,272,992**.
- Input set: the five root build files plus non-`bin`/`obj` `.cs`, `.csproj`, `.xaml`, `.manifest`,
  and `.ico` files under `src` and `benchmarks`, ordinal-sorted by slash-normalized relative path.
- No Git commit exists for this state.

Current extraction-diagnostic scoped gate: **350 Explorer + 41 extraction/recovery/provenance =
391 distinct passing cases**, zero failures/skips; all 350 Explorer cases were repeated with the
exact packaged Worker configured. The current Worker tree is byte-identical to the RAR long-path
predecessor; the Explorer UI changed. The reported copied ZIP also passed exact-package extraction
outside the synced destination tree, with complete payload, directory and provenance comparison.
This is not a full Archives integration/adversarial rerun or a cloud-sync compatibility fix.
See [extraction-sync-conflict.md](docs/extraction-sync-conflict.md) for the current package, limitations,
preserved user state, and retained test residue.

Retained RAR protocol-1.24 scoped gate: **536 unit/WPF + 29 adversarial + 32 focused RAR integration +
336 Rain Explorer = 933 passing cases**, zero failures/skips in the final runs. The 336 Explorer
cases were repeated against the exact packaged Worker, and both new real-Worker long-path cases
passed against that same payload. Locked restore, scoped whitespace checks, Release build and
current 18-project direct/transitive advisory inventories pass. This is not a full integration-suite
rerun; predecessor protocol/package matrices and benchmarks remain predecessor-bound.
Exact package and limits: [rar-long-path-fix.md](docs/rar-long-path-fix.md).

Retained protocol-1.23 verification completed on 2026-09-05:

- Locked restore: pass in ordinary Windows context.
- Fresh NuGet direct/transitive vulnerability inventory against nuget.org: every solution project
  reports no known vulnerable package in the current sources on 2026-09-05.
- `dotnet format ... --verify-no-changes`: pass.
- Release solution build: pass, **0 warnings / 0 errors**.
- Unit/WPF: **530 passed, 0 failed, 0 skipped**.
- Adversarial: **29 passed, 0 failed, 0 skipped**.
- Real Worker/IPC/process integration: **375 passed, 0 failed, 0 skipped**.
- Total: **934 passed, 0 failed, 0 skipped**.
- M7-D fixes RAR5 directories with absent optional CRC metadata and adds separately negotiated conditional
  unencrypted single-volume RAR5 creation through one exact qualified user-installed WinRAR encoder. Concrete
  provider re-resolution, executable leasing, bounded post-handshake no-follow workspace maintenance,
  verified child-process termination, independent encoder test plus exact digest readback, and exact-PID crash
  cleanup pass. The current signed 7.20 production installation remains `UpdateRequired`; no encoder binary is
  stored or shipped.
- Milestone 5G-A centralizes Light, Dark, and High Contrast palettes plus WPF spacing, typography,
  focus, control, card, DataGrid, and round-stroke vector-icon resources. System mode follows the
  per-user Windows app-theme preference, explicit Light/Dark stays application-local, and Windows
  High Contrast always selects the solid SystemColors palette. No system setting is changed.
- The remediation adds foreground roles for every custom surface. High Contrast pairs Window with
  WindowText, Control with ControlText, and Highlight with HighlightText through headers, alternating
  and selected rows, row icons, BrowserButton states, and direct muted main-shell content. Light/Dark
  retain their prior colors.
- The main archive browser now has one archive-identity/search header, visually grouped primary/work/
  review commands, icon-and-text navigation, a bounded dominant entry list, an on-demand
  320-380-DIP inspector, one status live region, and a narrow-safe wrapping welcome surface.
  Keyboard order follows visual order; all M5F commands/accelerators, inspector cancellation/drain/
  focus restoration, provider/capability truth, virtualization, and resource bounds remain intact.
  Entry-row Automation text now includes validated safety and encryption state without raw provider
  or archive fields. The three welcome copies are marker-free, leaving top Alt+C/Alt+R unchanged while
  preserving welcome Tab stops, Automation names, and help.
- Milestone 5G-B applies the semantic dynamic palette and bounded secondary-window styles to Activity,
  Settings, Recovery, Create, Rebuild, and Password. A weak/idempotent caption attachment applies after
  HWND creation, follows every app-theme change, and detaches on close without changing modal/nonmodal
  ownership. Activity keeps one polite status owner and a non-live grid; Recovery keeps one selected-tab
  assertive status. Create/Rebuild retain fixed-height recycling review grids inside bounded scrolling,
  and Password retains the complete escaped archive name in Automation help/tooltip while bounding the
  visible label. Access keys, tab/default/cancel behavior, Settings rows 0-4, redaction, cancellation/
  drain/session handoff, authority, limits, originals, and no-overwrite rules remain unchanged.
- Milestone 5G-C adds the original deterministic `RainArchives.ico` derived from the existing
  `Icon.Archive` visual language: nine alpha-capable 32-bit DIB frames at 16/20/24/32/40/48/64/128/
  256 pixels, violet/near-black/soft-light colors, 381,038 bytes, and SHA-256
  `554013891F9E3F50F2993CE8ACF636CB80457A9CF8A81099F69A0C675A7720A0`. Standard
  `ApplicationIcon` binding supplies the executable shell icons and top-level window class fallback;
  no custom title bar, shell integration, runtime generation, dependency, or `MainWindow.xaml` override
  was needed. Focused tests parse every frame and pixel bound, extract large/small executable icons, and
  verify the launched top-level window resolves both icon sizes.
- Milestone 5G-D merges `Controls.xaml` between the selected palette and product tokens. Dynamic semantic
  templates cover ordinary controls and popup content, while product Button/tab/DataGrid/inspector styles
  compose that base. Required template parts remain intact. Explicit Rebuild styles cover the two
  generated checkbox and two generated editable-text columns that bypass implicit element styles.
  Runtime STA tests exercise states, editing containers, Light/Dark/High Contrast popup refresh, and
  compact Settings scroll extent/end reachability with a stationary footer. The Create shell's default
  labels and three post-create open fallbacks are format-neutral; runtime capabilities and behavior are
  unchanged. Same-task GUI inspection exercised dark menus and Settings wheel scrolling, but is not
  product-owner visual or accessibility acceptance.
- Milestone 5F adds one immutable fixed-field context authority derived only from the newest validated
  session, browser state, host-busy bit, current 512-row page, and canonical current `EntryId` values.
  Enter/double-click, window accelerators, and the five-item row menu share its existing navigation,
  properties, bounded safe-preview, integrity, and selected-extraction commands. Handlers re-resolve
  the current primary/selection before Client use; substituted, duplicate, missing, stale, offline,
  downgraded, or busy state grants no command. Status/help/Automation text is fixed and path/provider/
  archive/secret/recovery-free. Open With, export/launch, shell, mutation, overwrite, retry, restore,
  generic cleanup, and provider-specific actions remain absent.
- Context preview replacement owns at most one active plus the newest pending request and remains
  cancellable/drainable without blocking Worker event receipt. Selection/session/source changes clear
  stale authority and presentation; the post-drain clear rejects a completion that had already passed
  its generation check. System colors, access keys, focus restoration, Automation metadata, DPI-safe
  layout, and recycling virtualization retain the existing 500,000-index, 512-visible-row,
  256-history, protocol-paging, preview-cache, and Worker-containment limits.
- The properties/safe-preview inspector now starts hidden and only an authorized Show Properties or Safe
  Preview command reveals its matching tab. Its fixed keyboard-accessible Close control invalidates the
  preview generation, cancels active/pending work, joins the bounded preview pump before collapse, and
  restores focus to the current entry list/selection. A late completion cannot repaint the closed
  inspector. No global Escape binding was added, and session/busy/offline/stale transitions retain the
  existing fail-closed cancellation and state behavior.
- Exact-package measurement pins a deterministic 20,546-entry / 4,421,858-byte ZIP at SHA-256
  `6CAF8851DF604339E577D63DB0D8545829C76DA790866112E6B54190A9258060`. One warmup and three measured
  fresh packaged-Worker opens are followed by 64 page transitions, 320 folder/root round trips, 1,024
  selections, 32 preview replacements/drain, and five 100 ms steady samples. Cold open/list measured
  625.7876/712.0909/772.6138 ms minimum/median/maximum; steady Worker working set was
  89,157,632-89,935,872 bytes, private bytes 55,873,536-56,336,384, and handles 320. All four exact
  PIDs exited with zero Worker/cache/staging files. Raw canonical evidence is the 6,302-byte
  `artifacts/RainArchives.M5FMeasurement.1.22.94811C7A.02/measurement.json`, SHA-256
  `F5AEB7113900F8373001BEEB76021D118B8FA4BCC09C8E11EF47C514FB55016B`; it is a local observation,
  not a competitor or manual UI claim.
- Milestone 5E consolidates creation, nested-workspace, extraction-staging, and fixed support evidence
  in one Contracts/Client-only Recovery Center. Inventories remain local, bounded, path-free, and
  non-destructive until explicit inspection. Creation retains only exact disposition authority;
  nested recovery retains exact-evidence independent depth-zero reopen/discard; extraction remains
  strictly read-only. Selection, refresh, malformed/downgraded/offline response, cancellation,
  failure, or uncertain completion clears action authority. No generic cleanup/delete, retry,
  restore, replacement, provider/exception text, archive text, secret, command line, or new mutation
  control enters WPF.
- Recovery Center close/disconnect cancels and joins its initialization task plus all four child
  request owners. A recovered source is adopted only as a freshly listed independent session with a
  fixed title and no filesystem source authority; failed, cancelled, or unclaimed adoption closes
  that exact session. Ownership remains with the dialog service until successful result handoff and
  with the MainWindow until the full authoritative state transition, including notifications, has
  completed. Both ordinary and recovered-session adoption compensate exactly once on any earlier
  cancellation or failure. Support presentation maps only protocol-1.22 fixed enums/integers/booleans.
  Production XAML supplies system colors, access keys, ordered focus, Automation name/help/live
  status, DPI-safe layout, and recycling virtualization without claiming manual accessibility.
- Milestone 5D adds one Contracts/Client-only session inspector with archive and selected-entry
  properties plus a bounded safe-preview center. Archive properties are fixed projections of the
  validated session descriptor and current capability state. Entry properties admit only validated
  kind, known sizes, mathematically valid ratio, representable UTC timestamp/basic attributes, fixed
  link/encryption/integrity-presence state, duplicate occurrence, and normalized persistent key. They
  expose no provider identifier/text, method string, raw integrity value, link target, source
  fingerprint, exception, secret, recovery ID, cache path, handle, command line, or mutation authority.
- Preview results are typed as decoded text, binary, oversize, unsupported encoding, or unsupported
  entry. Text accepts strict UTF-8 or BOM-proven UTF-16/UTF-32 only; NUL, invalid, and control-dense
  payloads remain inert refusals. The presentation has explicit idle/loading/text/binary/oversize/
  unsupported/offline/stale/cancelled/fixed-error states, discloses encoding only from the typed
  decoder, and states the exact 1 MiB display plus one detection byte, one-active-plus-newest-pending,
  four-materialization-slot, delete-on-close cache policy. Selection/source/session change cancels and
  drains work, generation checks reject stale responses, and closing restores deterministic list
  focus. Active content, export, shell/Open With, and automatic external launch remain absent.
- Production XAML uses system colors, access keys, ordered focus, automation names/help/live status,
  layout rounding, wrapping, and the existing recycling browser grid. STA coverage validates the
  inspector without claiming manual Narrator, High Contrast, keyboard-only, mixed-DPI, or visual
  acceptance. Protocol 1.22 and every Client/Worker/Security ownership boundary are unchanged.
- Milestone 5C adds a Contracts/Client-only Settings and Presets center plus one managed create/rebuild
  presentation for ZIP, TAR, TAR.GZ, TAR.BZ2, GZIP, BZIP2, and eligible ZIP Rebuild Copy. The App still
  references only Contracts and Client. Saved defaults are local hints: every operation route repeats
  the current authenticated capability query and clamps format/preset selection before any start.
  Raw GZIP/BZIP2 require exactly one ordinary file; directory support and input limits remain the
  Worker's advertised authority. Review text states that the destination must be distinct and absent,
  raced/existing objects are preserved, every source/original stays untouched, and modification is a
  full guarded rebuild to a new copy. Encryption, filename encryption, split output, overwrite,
  replacement, retry, pause, reorder, decision continuation, in-place update, native 7z, and RAR
  creation remain unavailable.
- The UI-settings schema is version 1 and fixed-field: one managed-format enum, seven compression
  enums, and one boolean, with no free-form string slot. It rejects missing, duplicate, unknown,
  malformed, unsupported-version, invalid-enum, invalid-Unicode, truncated, and over-32-KiB state.
  Validated backup recovery is explicit read-only last-known-good state, never silently repairs the
  primary, and cannot ordinary-save until explicit Reset.
  The existing ZIP preset document retains its 64-user-preset, 64-scalar/256-UTF-8-byte name, metadata,
  and exact 128-KiB bounds and now also rejects duplicate or missing required JSON members. A literal
  128-KiB valid document is accepted and the next byte is refused without deriving either boundary
  from the production constant.
- Both App-private documents use the per-user LocalAppData boundary, a per-store named mutex, verified
  ordinary no-reparse/one-link/no-ADS directory/file objects, a random `CreateNew` sibling, durable
  flush, and atomic replace-or-first-move. Cleanup targets only the exact staged identity. Save and
  reset report partial failure honestly because the two independent documents are not a transaction;
  malformed, future, oversized, unavailable, or unsafe storage disables ordinary save until explicit
  Reset. `ZipPresetStore.Save` revalidates that closed state under its mutex, while both create and
  rebuild dialogs carry the exact load state and recheck immediately before Save/Remove. Built-ins
  remain usable for the current operation, but fixed safe warning text directs repair to Settings and
  Presets; neither dialog can replace the refused bytes/object. Settings work is serialized, stale
  capability responses are rejected, and close/App shutdown cancels and drains work before Client
  disposal or UI callbacks.
- The system-color Settings window includes access keys, ordered focus, automation names/help/live
  status, DPI-safe scrolling, and recycling virtualization over bounded rows. Loading, defaults,
  saved, reset, recovered, corrupt, unsupported, unsafe, unavailable, offline, downgraded, error, and
  closed states are explicit. No provider/exception text, temporary path, archive-controlled string,
  secret, recovery authority, operation history, file identity, or handle is persisted or presented as
  diagnostics. Automated STA evidence is not manual Narrator, High Contrast, keyboard-only, mixed-DPI,
  or visual acceptance.
- A separate post-gate read-only remediation review traced every production ZIP preset Save/Reset
  caller, the Settings Center save gate, dialog state refresh, fixed warning provenance, App project
  references, and protocol constant. It found no remaining policy, authority-boundary, untrusted-text,
  cleanup, or same-user-containment claim issue. The hard-link cases additionally retain and compare
  exact volume/file identity and link count. This same-task review is not independent product-owner or
  manual accessibility acceptance.
- Milestone 5B replaces the plain entry surface with a reusable browser model and production WPF
  surface over existing protocol-1.22 Contracts/Client APIs. The model validates the entire bounded
  page sequence and parent graph before adopting a session, refuses duplicate/malformed IDs and
  inconsistent persistent-key downgrade shapes, and exposes no provider, temporary-path, recovery,
  secret, or mutation authority. It caps the index at 500,000 entries, visible materialization at 512
  rows, and history at 256 locations; a focused 50,000-descriptor model observation indexed in 64.7
  ms while retaining 512 visible rows.
- Independent verification of the `93D944FC` checkpoint found that App-side hierarchy validation
  recursively followed a complete reverse-ordered parent chain before applying its depth limit. The
  current validator uses one iterative reusable path plus visited/depth maps: every descriptor is
  visited at most once, large cycles remain explicit cycle refusals, and the 257th resolved level is
  rejected before the only increment. Focused regressions accept exactly 256 reverse-ordered levels,
  reject 4,352 levels, and reject a 32,768-entry reverse-parent cycle with `InvalidDataException` while
  preserving the prior authoritative title/state/selection. Protocol and WPF authority are unchanged.
- Navigation uses canonical archive-relative directory IDs, deterministic directory-first ordering,
  explicit duplicate occurrence, conventional branching Back/Forward history, root-bounded Up, and
  Unicode ordinal case-insensitive current-folder search. Selection and focus restoration are stable
  for the session. Reopen restoration occurs only with persistent keys and exact equality of canonical
  source path plus length/time/file-identity/prefix-hash fingerprint; all other reopen, close,
  mutation, stale, downgrade, offline, and error boundaries reset authority safely.
- Production XAML uses system colors, layout rounding, wrapping controls, recycling row/column
  virtualization, bounded pager controls, keyboard accelerators, automation names/help, and polite
  status announcements. Archive-controlled text is visible-escaped; active-content preview remains
  refused. This is automated evidence, not manual Narrator, High Contrast, mixed-DPI, or visual
  acceptance.
- Milestone 5A adds a production modeless Activity Center over Contracts/Client only. It combines
  extraction, integrity, creation, and rebuild operations; preserves authoritative terminal state;
  exposes Cancel only when the latest validated snapshot authorizes it; and keeps pause, retry,
  reorder, decision continuation, and restart persistence visibly unavailable. Live event entry text is
  passed through the shared visible-escape formatter; retained snapshots and diagnostics add no path,
  entry, warning/error message, recovery ID, secret, provider exception, or mutation authority.
- Refresh is one sequential bounded activity/diagnostics request at a time, normally once per second,
  with coalesced event-to-UI delivery, a 96-record model ceiling, stale-generation rejection, and
  cancellation/drain before Client disposal. Offline, unsupported, loading, empty, and truncated-
  history states are explicit. DataGrid row/column recycling, system-color resources, access keys,
  focus order, automation names/help, and polite status announcements provide the automated
  accessibility foundation without claiming manual visual or assistive-technology acceptance.
- Protocol 1.21 adds only `nested-workspace-independent-reopen-v1`. One explicit request repeats a
  fresh protocol-1.11 review revision, workspace source count/bytes, selected source ID/length, and
  recovery ID. Security exclusively reparses and revalidates the inactive exact-case workspace,
  stable identities, ordinary type, one-link/no-reparse/no-ADS shape, liveness, bounds, and content
  revision before copying one source through pinned handles. The Worker durably flushes a new owned
  copy, redetects it from content, and registers a fresh independent root session at depth and chain
  bytes zero. The recovered workspace is unchanged and its path never crosses IPC. A crash after the
  copy flush preserves the original evidence/hash and only the copied operation-owned source; a new
  Worker reports the stale workspace path-free, then a fresh inspect/reopen/list succeeds at depth
  zero. Partial nested discard still has no durable completion inference: any smaller or missing
  reviewed tree after uncertainty remains suspicious/not-found, never an assumed success or retry.
- Protocol 1.22 adds only `worker-support-diagnostics-v1`. Its response is a bounded fixed schema of enums,
  integers, and booleans: protocol/feature count, queue/admission/retention policy, unsupported
  controls, automatic-recovery-disposition support, containment kind, and explicit
  `isSameUserSecuritySandbox=false`. It contains no message, path, filename, entry, recovery ID,
  provider text, exception text, secret, or archive-controlled string. CLI output is derived only
  from those typed fields and grants no recovery authority.
- Operation lifecycle policy is now explicit and tested: at most 32 starts are admitted without
  blocking the start response, one executes, ordering is `Unspecified`, and the newest 64 terminals
  are retained in memory. The 33rd request fails closed and settles its password slot. Cancel is the
  only supported activity control; terminal operations refuse cancellation. Start requests do not
  register request-cancellation IDs: cancellation before the start response uses bounded transport
  abort of that Client-owned Worker, while cancellation after an operation ID uses `CancelOperation`.
  `AwaitingPassword`, `AwaitingConflict`, and `AwaitingRiskApproval` remain forward-compatible enum
  values only and are never emitted. Passwords remain one-shot secret slots, conflict/risk decisions
  remain local fail-closed preflight, `IsRetryable` is advisory, and pause/retry/reorder/restart
  persistence remain false with no endpoint.
- The extraction-staging disposition gate remains closed. Protocol 1.20's aggregate journal proves
  count/bytes/ADS/revision but does not contain a durable per-entry identity manifest or a deletion-
  progress ledger. Reopening this gate requires an exact bounded per-entry handle-relative identity/
  type/link/reparse/ADS/content manifest, deterministic completion ledger, explicit intent/receipt,
  identity revalidation at every mutation, idempotent replay, and exact-PID crash proofs. No current
  response grants cleanup, retry, merge, replacement, restore, or automatic reconciliation.
- The containment decision is the existing unelevated same-user Worker plus authenticated pipe,
  exact launched PID, bounded graceful close, 2 GiB/one-process kill-on-close Job Object, and exact
  owned-process fallback. This is a lifecycle/crash boundary, **not a same-user filesystem security
  sandbox**. AppContainer or brokered-handle deployment is not enabled: the current path/handle,
  provider, IPC, atomic package, and solo-maintenance model has no proven compatible broker contract.
  Reconsider only with a separately maintained principal/ACL/handle-broker and deployment design.
- A separate immutable security scan (`e255a57b-9dde-43bd-b44d-c426500b16fa`) covered six scoped
  surfaces and completed with full reported coverage and **0 findings**. It is bound to snapshot
  `codex-security-snapshot/v1:sha256:8fafb141100e72fb2d5817dbe48d9ec0caad9313f96ce0220a87306fde1b214e`;
  build/test output directories changed during the scan, but production source did not. The scan was
  performed parent-only because this task prohibited further subagent routing. Provider exception
  messages remain an explicit invariant: `ArchiveProviderException.Message` crosses the error seam,
  so every provider-originated message must stay fixed/redacted and never include a path, secret,
  archive-controlled control text, or generic exception string.
- A scoped M5F security scan (`bc166098-03ae-4b4a-a3a4-62b17929f304`) used Daybreak Blue at max
  reasoning, TAC1, and the required sequential parent fallback because this task explicitly prohibited
  subagents. It sealed five current-selection authority, WPF/accessibility, preview lifecycle,
  information-exposure, and package/performance surfaces with **0 findings**. The workbench records a
  directory-change warning because the final package-test and deterministic-measurement evidence was
  written while it ran; the canonical production identity remained exactly
  `94811C7A626839524DA43826E6AD238BAF29E85EDECFC7E8C3D581B8C217C641`, and the
  final test/artifact tail received a direct read-only check. This result is retained for the immutable
  predecessor and is not relabelled as review of the inspector remediation, independent acceptance, or
  manual accessibility evidence.
- A finite direct inspector-remediation review traced initial visibility, every reveal/hide caller,
  generation invalidation, active/pending cancellation, pump join, late completion, source/session/state
  invalidation, fixed Automation text, scoped focus restoration, App project references, package
  composition, and forbidden Escape/authority/dependency paths. It found **0 blocking or non-blocking
  issues** in that bounded scope. This same-task review is not independent acceptance or manual visual,
  Narrator, keyboard-only, High Contrast, mixed-DPI, or clean-machine evidence.
- A separate scoped M5B security scan (`48ec3d6f-ccd0-4a5a-86dc-259f00f60571`) reviewed six browser,
  WPF, session-transition, and package-test surfaces with **0 findings** at snapshot
  `codex-security-snapshot/v1:sha256:4ec1097ced945eaa805ecf0eaa19830286f61c723b450e16304407948a14c935`.
  The final 256-location history cap was added after that immutable snapshot. Later independent review
  found the recursive hierarchy blocker above; the current iterative remediation completed the
  security-fix workflow's source-to-sink, caller, sibling-path, checked-arithmetic, O(n) resource, and
  bypass/regression review plus focused/package/full gates. The earlier scan must therefore retain its
  snapshot qualification rather than being relabelled as a scan of the remediated tree. Independent
  acceptance reproduced the iterative 4,352-level/32,768-cycle proof, all 799 automated tests, source
  and package identities, and clean residue. Visible/manual assistive-technology acceptance remains
  pending and is not transferred to a later package.
- A requested scoped M5C security-workbench registration did not return a scan ID or immutable
  snapshot and was terminated without claiming a result. A finite direct read-only review then covered
  settings schema/parser, local object safety and atomic commit, preset validation, capability refresh,
  cancellation/drain, create/rebuild selection, fixed-text error handling, XAML accessibility, App
  project references, and the exact package flow. It found **0 blocking or non-blocking findings** in
  that scope. This is direct source review, not an external workbench scan or manual accessibility
  acceptance.
- A separate finite M5D read-only pass traced the new fixed-field property projections, typed preview
  outcomes and decoder, cancellation/generation/disposal paths, selection/focus transitions, XAML
  bindings and automation metadata, App references, package flow, and forbidden text/active-content
  sinks. It found **0 blocking or non-blocking findings** in that bounded scope. This same-task source
  review is not an external workbench scan, independent acceptance, or manual accessibility evidence.
- Independent M5E verification found pre-dispatch secret-slot settlement gaps on the two session-gated
  Client routes and pre-transfer recovered-session ownership gaps across dialog and MainWindow handoff.
  The remediation places validation and lifecycle-gate waits inside the transferred-slot settlement
  boundary, sends no open/reopen frame on pre-dispatch cancellation or validation failure, and closes
  every untransferred session exactly once. Deterministic real-protocol and STA/model regressions cover
  pre-cancel, gate cancellation, local validation, drain/callback failure, cancellation at handoff,
  compatibility-wrapper close, preview-drain failure, notification failure, ordinary open, and
  successful zero-close transfer. A finite same-task post-fix review found no adjacent bypass in those
  routes; it is not independent acceptance or manual accessibility evidence.
- Independent verification then found one finite presentation overflow: `ArchiveEntryViewModel` added
  one to an accepted non-negative `Int32` duplicate ordinal before widening, so `int.MaxValue` rendered
  as a negative visible/Narrator occurrence. Both outputs now share one value widened to `Int64` before
  addition. A focused boundary regression first reproduced `#-2,147,483,648`, then proves exact
  `#2,147,483,648`, the same positive Automation name, and unchanged control/bidi escaping. A separate
  post-fix bypass/regression pass found no sibling App occurrence renderer; the properties model already
  used the safe widened form. This remediation review is not independent acceptance.
- The current/retained protocol class passes **17/17** cases. The exact 1.18 artifact at
  `artifacts/RainArchives.ProtocolCompatibility.1.18.1C85F6D9` retains all 132 source inputs, the
  205-file Worker, Client/Contracts binaries, rebuildable harness recipe, fixture, and managed pair
  proof. A 1.18 peer refuses operation activity, extraction-recovery inspection, independent nested
  recovery reopen, and support diagnostics before frame/filesystem access while all prior managed
  creation operations continue to pass in both directions. Minor 20 omits independent reopen and
  diagnostics; minor 21 negotiates reopen but not diagnostics. Dependency-incomplete negotiation,
  mixed App/Worker package composition, unknown/missing members, oversize payloads, and malformed
  fixed fields fail closed.
- The retained protocol-1.18 M3 checkpoint separately passed 358/29/293 tests after its physical/
  alias/external-tool tranche. It remains bound only to fingerprint `1C85F6D9`; `eng`, documentation,
  tests, and retained evidence are intentionally outside both canonical production identities.
- Native-handle and MSBuild timestamp checks that the Codex filesystem sandbox denied were rerun as
  the identical ordinary Windows commands; they passed without weakening code, policy, or tests.
- The protocol-1.22 full gate also retains the checked corpus and the product owner's
  two identical 385-byte manual ZIPs under the package-bound acceptance artifact. No checked fixture
  or historical package was rewritten.
- Final MSBuild and compiler-server shutdown completed successfully. One read-only sample observed a
  concurrent `testhost`; it exited independently, and the final scoped snapshot found no Rain Archives
  App, Worker, recovery crash probe, CLI, or test host. The user-owned exact-package process was not
  signalled or stopped. The workspace contains zero top-level package staging entries, and the live
  App-data Preview Cache plus ArchiveCreation and Extraction recovery leaves each contain zero
  descendants. Of 213 preserved Rain Archives Temp parents, 56 were touched on 2026-09-02. Two older
  parents retain current-day residue: `RainArchives.FinalPackageProbe.10ce8a7a637b4f1bb669456bbe90413c`
  has 14 empty directories, and `RainArchives.ZipRebuildTreeEditEndToEndTests` has one empty directory.
  They contain zero files and were inventoried without deletion. No unrelated process, historical/
  manual/tooling root, retained package, or filesystem object was stopped or removed.

Protocol-1.23 M7-D current Rain Archives local package gate:

- Isolated self-contained `win-x64` package:
  `artifacts/RainArchives.M7DRarPackage.1.23.0D84243C`.
- Manifest: **1,199 bytes**, SHA-256
  **`DF47FAD343B6DCBDE2B1FFB42489C7CF7D19C154CA9D2CB6398C07AE2F442ADB`**.
- App tree: **611 files / 234,227,669 bytes**, aggregate SHA-256
  **`4995B32081A66E95A77A0B914876256146D094F9630ED22A4E3B64F4BEA34BCD`**.
- Private Worker tree: **207 files / 85,185,437 bytes**, aggregate SHA-256
  **`A4A83D8B51E704B95BB588FF6F21432E6E7A0B37F882A6E1193F7FCC12D2DACC`**.
- App executable/DLL/Client/Contracts SHA-256:
  `A565C435B523F656416ADB7736A0D005BB75B3D733379D2D443A7F7794F2E5C9`,
  `D46C8C2B03A7CE9062A771B06B232F261DF640C658D5BF9A436569E37E3A8988`,
  `38FF68D7EB7043F8200F4821C43B0289D41AA8009632129EE598AAD5B35A9AFB`, and
  `E71B945555031A9933EB0631BE260CFCA5CF9AA0257BEEF658BAD9319CB398EF`.
- Worker executable/DLL/Contracts SHA-256:
  `C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF`,
  `42406932E8EF96F56DDAA1BBD0E82D595442E2D4815E6274547FFC9BEFB53C2B`, and
  `E71B945555031A9933EB0631BE260CFCA5CF9AA0257BEEF658BAD9319CB398EF`.
- The package test binds source, manifest, both trees, principals, Contracts coherence, Worker-only provider
  placement, and the valid directory-without-CRC RAR flow. The reported 550,475,088-byte RAR also lists
  18,418 entries through this exact Worker and remains byte-identical. The package contains zero `Rar.exe`,
  `WinRAR.exe`, `7z.dll`, `7za.exe`, or `SharpSevenZip.dll`.

Protocol-1.22 Milestone 5G-D historical local package gate:

- Isolated self-contained `win-x64` package:
  `artifacts/RainArchives.M5GDDesignSystemPackage.1.22.651729F4`.
- Manifest: **1,083 bytes**, SHA-256
  **`01BF21F943C46B54C953E2A2BE3C5D037BE1FB26F738DBE67CA6C8C46AB30137`**.
- App tree: **609 files / 234,037,929 bytes**, aggregate SHA-256
  **`AD4D7D620C75771788083B29B7DEB92DE403D665336BBF5D96810FF921BC212A`**.
- Private Worker tree: **205 files / 85,041,541 bytes**, aggregate SHA-256
  **`1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`**.
- App executable/DLL/Client/Contracts SHA-256:
  `A565C435B523F656416ADB7736A0D005BB75B3D733379D2D443A7F7794F2E5C9`,
  `9C60B64D4A678AA0D15B2DF3E5B73A77275331E6E479CE28259E770FEB251AF5`,
  `53B933055CC5043FE8C5B15DB0B61C783AFCBBE429DE1E6EF443CD317498292D`, and
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`.
- Worker executable/DLL/Contracts SHA-256:
  `C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF`,
  `3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8`, and
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`.
- The exact-package test binds the live 160-input/3,157,575-byte source including icon and control
  resources, manifest, both trees, all principal hashes, Contracts byte equality, executable shell icons,
  and forbidden-asset rules, then drives the packaged Worker product flow with unchanged inputs and zero
  scoped residue. The package contains zero `7z.dll`, `7za.exe`, or `SharpSevenZip.dll`.
- `artifacts/RainArchives.M5GDDesignSystemPackage.1.22.B11E7A80` remains immutable superseded pre-final
  evidence at source `B11E7A808ADF9FD4E0329613F5C69C9A5FC2C88ED9EED9A3ADAE7FEF24BB679F`,
  manifest `4C742CE334FF2D7C37C7606EBDAA5F6A8C9B66E5B97535C5A2F3D978316A6918`, and App tree
  `609 / 234,036,197 / 1C3A27D0314F97EBC4F262AD952DCF7CB6C307163A0CD97B3F7FB9C170A29EEF`.
  Runtime editing proof exposed its generated DataGrid text-editor fallback; it is not the current package.
- Same-task automated GUI inspection exercised dark menus and actual Settings wheel scrolling in the owned
  Release App. That is finite automation, not product-owner visual, Narrator, keyboard-only, actual High
  Contrast, 100/150/200%, mixed-DPI, or clean-machine acceptance. Those exact-package gates remain
  `manual_pending`; no predecessor observation transfers.

Protocol-1.22 Milestone 5G-C historical local package gate:

- Isolated self-contained `win-x64` package:
  `artifacts/RainArchives.M5GCProductIdentityPackage.1.22.56FFA877`.
- Manifest: **1,083 bytes**, SHA-256
  **`D8284B822C2475C61D40B155719A20522BD05CB67C12A5A2EEB8D74BB84CE74F`**.
- App tree: **609 files / 234,012,833 bytes**, aggregate SHA-256
  **`D490AB5B36ECDD4D3C67384C86178A7A23467E99E1E4CE6F8459B4676A99F8B5`**.
- Private Worker tree: **205 files / 85,041,541 bytes**, aggregate SHA-256
  **`1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`**.
- App executable/DLL/Client/Contracts SHA-256:
  `A565C435B523F656416ADB7736A0D005BB75B3D733379D2D443A7F7794F2E5C9`,
  `B5827F9F439EE9D672BD3040AB797A286B0C6067104704936B31A740879CE25C`,
  `53B933055CC5043FE8C5B15DB0B61C783AFCBBE429DE1E6EF443CD317498292D`, and
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`.
- Worker executable/DLL/Contracts SHA-256:
  `C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF`,
  `3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8`, and
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`.
- The historical exact-package test pins its 159-input/3,070,555-byte source-at-package-time identity,
  manifest, both trees, all principal hashes, Contracts byte equality, executable large/small shell
  icons, and forbidden-asset rules, then drives the packaged Worker product flow with unchanged inputs
  and zero scoped residue. The package contains zero `7z.dll`, `7za.exe`, or `SharpSevenZip.dll`.
- Its automated icon/resource/window-class evidence remains valid historical product-identity evidence,
  but direct review exposed the broader native/default control-presentation gap addressed by M5G-D. It is
  not the current visual-acceptance candidate.

Protocol-1.22 Milestone 5G-B historical local package gate:

- Isolated self-contained `win-x64` package:
  `artifacts/RainArchives.M5GBSecondaryWindowPackage.1.22.20FF2288`.
- Manifest: **1,083 bytes**, SHA-256
  **`C49011AD025FE9329F9D0AAC07C7A8EDF94A9287E52DA8D4EA1A9B581222317C`**.
- App tree: **609 files / 233,249,953 bytes**, aggregate SHA-256
  **`8D551EC0E0F7F513EB21DBBB65DCCDFDCC1FA8349A15A0AC82864878B7DCC342`**.
- Private Worker tree: **205 files / 85,041,541 bytes**, aggregate SHA-256
  **`1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`**.
- App executable/DLL/Client/Contracts SHA-256:
  `9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26`,
  `4BDE3284E4CF14FFAC46F0DC9C3361C61072B1BE05C3C7A55C6965F4040090C7`,
  `53B933055CC5043FE8C5B15DB0B61C783AFCBBE429DE1E6EF443CD317498292D`, and
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`.
- Worker executable/DLL/Contracts SHA-256:
  `C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF`,
  `3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8`, and
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`.
- The historical identity check pins its then-current 158-input/2,689,478-byte source, manifest, both
  trees, all principal hashes, Contracts byte equality, and forbidden-asset rules without asserting
  that the live source still has that identity. Its sealed Worker browser/preview/properties/inspector/
  integrity/selected-extraction flow retains unchanged-source and zero-residue evidence. The package
  contains zero `7z.dll`, `7za.exe`, or `SharpSevenZip.dll`.
- A separate historical check pins the exact accepted M5G-A package without binding its source to the
  live workspace. The rejected `BE985F08` package remains separately pinned and rejected.
- A fresh bounded read-only post-patch review found no actionable issue. Package-bound
  Windows.Graphics.Capture returned a uniform black frame and no accessibility text, and activation
  reported `GetCursorPos` access denied. Automated resource/XAML/STA/UIA evidence is not direct visual,
  Narrator, keyboard-only, High Contrast, 100/150/200%, mixed-DPI, or clean-machine acceptance. Those
  exact-package gates remain `manual_pending`; no predecessor observation transfers.

Protocol-1.22 Milestone 5G-A historical local package gate:

- Isolated self-contained `win-x64` package:
  `artifacts/RainArchives.M5GAAccessibilityRemediationPackage.1.22.5AF06A6E`.
- Manifest: **1,083 bytes**, SHA-256
  **`FADBAF43E14C03D34C8105D282B23EC1D1963FC893DF7FFC42AE94006EFF83D8`**.
- App tree: **609 files / 233,238,233 bytes**, aggregate SHA-256
  **`BB266C5E105FB731220D56BD31A75316FBA8BC68CC9B62277B8A38F6F5FA828B`**.
- Private Worker tree: **205 files / 85,041,541 bytes**, aggregate SHA-256
  **`1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`**.
- App executable/DLL/Client/Contracts SHA-256:
  `9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26`,
  `D0E7A321DBA964C5F66DFE41CE2B235E37523CAB6F733CF5F4FF7EE0BEA8E9BD`,
  `53B933055CC5043FE8C5B15DB0B61C783AFCBBE429DE1E6EF443CD317498292D`, and
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`.
- Worker executable/DLL/Contracts SHA-256:
  `C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF`,
  `3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8`, and
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`.
- The retained historical-package test binds that package's source identity, manifest, both trees, all
  principal hashes, Contracts byte equality, and forbidden-asset rules without asserting that its
  source is current. The package contains zero `7z.dll`, `7za.exe`, or `SharpSevenZip.dll`.
- `artifacts/RainArchives.M5GAPolishPackage.1.22.BE985F08` is immutable rejected pre-remediation
  evidence. A separate test pins its original manifest/tree/principal identities; its High Contrast
  role mismatch and duplicate welcome mnemonics are not accepted or transferred.
- That package's accessibility tree exposed the named shell and marker-free welcome actions, but
  Windows.Graphics.Capture returned a uniform blank frame. Automated layout/resource/UI Automation
  inspection is not direct visual, Narrator, keyboard-only,
  High Contrast, 100/150/200%, mixed-DPI, or clean-machine acceptance. Those exact-package gates
  remain `manual_pending`; no predecessor result is transferred.

Protocol-1.22 Milestone 5F local package gate:

- Isolated self-contained `win-x64` package:
  `artifacts/RainArchives.M5FInspectorRemediationPackage.1.22.3FD7C6F4`.
- Manifest: **1,082 bytes**, SHA-256
  **`9A0F153DE2AF4203D385EE21BB1ABF8022E8B068893A68024C1693ED504CFD5B`**.
- App tree: **609 files / 233,182,721 bytes**, aggregate SHA-256
  **`78945421A56A188ED64C6908574613DEA3BEE0C3DBCB43F1A89EFE24BAA336C6`**.
- Private Worker tree: **205 files / 85,041,541 bytes**, aggregate SHA-256
  **`1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`**.
- App executable/DLL/Client/Contracts SHA-256:
  `9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26`,
  `54AF1FBFCD72390B31023DDCFF4FF7FA164AEF985271471B7097AD642DD7CA52`,
  `53B933055CC5043FE8C5B15DB0B61C783AFCBBE429DE1E6EF443CD317498292D`, and
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`.
- Worker executable/DLL/Contracts SHA-256:
  `C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF`,
  `3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8`, and
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`.
- The exact package tests bind the live 153-input/2,605,119-byte source identity and every manifest,
  tree, principal-file hash, and forbidden-asset rule. Through the packaged Worker and production model
  they verify hidden startup, explicit Properties and Safe Preview reveal, drained close, selection/
  focus restoration, reopen, original hash, exact PID exit, and zero scoped Worker/cache/staging
  residue. This is local automated evidence only; it is not clean-machine or manual visual/
  accessibility acceptance.
- `artifacts/RainArchives.M5FAutomatedPackage.1.22.94811C7A` remains exact immutable predecessor evidence,
  but manual acceptance found its inspector permanently visible and Properties had no close affordance,
  so it is not the current Phase-5 package. The user's observed DPI scaling and broad keyboard navigation
  on that predecessor do **not** transfer to the remediated package. Its deterministic measurement remains
  bound only to 94811C7A.

The accepted Milestone 5E package remains immutable predecessor evidence:

- `artifacts/RainArchives.M5EAutomatedPackage.1.22.6E0AEC14`
- manifest SHA-256 `5B8711FDA66572D72DC26F6F6A88C875860861159F5F0B98B914514FD9AD431F`
- App tree `609 / 233,165,429 / F6D83EFEC18C7D60F09A5D7FD9E79CDF29FD912464F16347582B51FC2157F88D`
- Worker tree `205 / 85,041,541 / 1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`

The identity-script-rejected package
`artifacts/RainArchives.M5EAutomatedPackage.1.22.973CB3C3` remains immutable evidence. Its manifest
used unnormalized Windows separators for the source/App aggregates and therefore is not an accepted
canonical package. The pre-drain-fix package
`artifacts/RainArchives.M5EAutomatedPackage.1.22.E20205D2` also remains immutable rejected evidence.

The independently rejected pre-ownership-remediation package
`artifacts/RainArchives.M5EAutomatedPackage.1.22.80A394D5` remains immutable evidence of the secret-slot
and recovered-session handoff gaps. It is not relabelled or accepted as the accepted M5E package.

The pre-remediation M5D package
`artifacts/RainArchives.M5DAutomatedPackage.1.22.49601147` remains immutable rejected evidence of the
checkpoint whose entry-row visible and Automation occurrence could overflow at `int.MaxValue`. It is
not relabelled or accepted as the current M5D package.

The accepted Milestone 5C package remains immutable predecessor evidence:

- `artifacts/RainArchives.M5CAutomatedPackage.1.22.6942C0B0`
- manifest SHA-256 `1E457D65694D57C8B411D86B381FBD3634C55ABFB4EBB7F192799611C77FA196`
- App tree `609 / 233,062,821 / C7DA0DB768CC32C38B375A5C67F9F5F3ED7D92A5606C015DDE595075E67AA4BA`
- Worker tree `205 / 85,041,541 / 1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`

The pre-remediation M5C package
`artifacts/RainArchives.M5CAutomatedPackage.1.22.126DC65F` remains immutable evidence of the checkpoint
that independent verification rejected for its conflicting 256-KiB production preset limit and
ordinary-save repair bypasses. It is not relabelled or accepted as the current M5C package.

The accepted Milestone 5B package remains immutable predecessor evidence:

- `artifacts/RainArchives.M5BAutomatedPackage.1.22.41BB378A`
- manifest SHA-256 `121CF5703435064DE4602DF3E5ACBCD19A1D760E32C045B59005447B018AB65F`
- App tree `609 / 232,942,737 / BE3BC9D2B19C02906797B4DDFB4E66F305258CA9F211BF7A5D3915191476BAD9`
- Worker tree `205 / 85,041,541 / 1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`

Protocol-1.22 Milestone 5B local package gate:

- Isolated self-contained `win-x64` package:
  `artifacts/RainArchives.M5BAutomatedPackage.1.22.41BB378A`.
- Manifest: **1,083 bytes**, SHA-256
  **`121CF5703435064DE4602DF3E5ACBCD19A1D760E32C045B59005447B018AB65F`**.
- App tree: **609 files / 232,942,737 bytes**, aggregate SHA-256
  **`BE3BC9D2B19C02906797B4DDFB4E66F305258CA9F211BF7A5D3915191476BAD9`**.
- Private Worker tree: **205 files / 85,041,541 bytes**, aggregate SHA-256
  **`1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`**.
- App executable/DLL/Client/Contracts SHA-256:
  `9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26`,
  `8AD76AC756D485084468D93BCEC9AE7EDB0F3570D5B09229515E928D6EEA32E1`,
  `ABC6B96B4692040BDB928F9A670BC6225F524950CB51D37F0CA92541C9A93603`, and
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`.
- Worker executable/DLL/Contracts SHA-256:
  `C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF`,
  `3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8`, and
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`.
- The package gate binds the live 142-input fingerprint and drives packaged Worker browser/list,
  preview/reopen, iterative hierarchy refusal, creation, integrity, extraction, rebuild, Activity Center state/cancellation,
  original/output hashes, and exact cleanup through production models. This is local automated
  evidence, not a transfer of
  historical Narrator, High Contrast, mixed-DPI, clean-machine, installer, signing, or release
  acceptance.

The pre-remediation protocol-1.22 Milestone 5B package at
`artifacts/RainArchives.M5BAutomatedPackage.1.22.93D944FC` and the Milestone 5A package at
`artifacts/RainArchives.M5AAutomatedPackage.1.22.08A4C999` remain immutable predecessor evidence; they
are not relabelled as the current browser package.

Retained protocol-1.22 Milestone 4 local package gate:

- Isolated self-contained `win-x64` package:
  `artifacts/RainArchives.M4AutomatedPackage.1.22.DCE2BC1E`.
- Manifest: **1,083 bytes**, SHA-256
  **`8D2286A940E9468C0D919CE728399B61B2CA4C1F525BEFF3895839E42BD6002A`**.
- App tree: **609 files / 232,823,153 bytes**, aggregate SHA-256
  **`C378FB9DC2C1DA893825E6CCE2877A298E9155B07DFAFA3E5F2CCAE392F0FB22`**.
- Private Worker tree: **205 files / 85,041,541 bytes**, aggregate SHA-256
  **`1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`**.
- App executable/DLL/Client/Contracts SHA-256:
  `9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26`,
  `7C2B363A1FC1BAC142A40341BA11CAE83C2DE370B7B1B96E60F429BA9B9A5510`,
  `ABC6B96B4692040BDB928F9A670BC6225F524950CB51D37F0CA92541C9A93603`, and
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`.
- Worker executable/DLL/Contracts SHA-256:
  `C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF`,
  `3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8`, and
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`.
- The package-composition/product-flow gate verifies its package-bound 135-input fingerprint, exact manifest,
  principal hashes, App/Worker Contracts coherence, private Worker placement, fixed support
  diagnostics, independent nested recovery reopen, unchanged original/copy hashes, and exact cleanup.
  It is local automated evidence, not GUI, clean-machine, physical-network, installer, signing, or
  published-release acceptance.

Retained protocol-1.18 Milestone 3 local package gate:

- Isolated self-contained `win-x64` package:
  `artifacts/RainArchives.M3AutomatedPackage.1.18.1C85F6D9/app`.
- Manifest SHA-256:
  **`6A8CB71E3924E04451299E9C4C4AB2F022D9648FCFE8113186656C6F0D0FF47A`**.
- App tree: **609 files / 232,216,977 bytes**, aggregate SHA-256
  **`C61749B2CC94A4E66606D3615DF88C44A2D95F599EE1F2B78C812BAC1BCFCFB0`**.
- Private Worker tree: **205 files / 84,699,121 bytes**, aggregate SHA-256
  **`36C64503CC9EA9FD6CF0F6C390F75F8871BEF9BFCA5461456CE645CBACB2966F`**.
- App/Worker Contracts SHA-256:
  **`BFDC6FB719D71BF4B85CC7471F6B95116F5B66E5A26C5EFCFADB7D9B848E2E91`**.
- App executable SHA-256:
  **`9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26`**;
  private Worker executable:
  **`C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF`**.
- App DLL SHA-256:
  **`AC856DFD5CC82F04B45B34D4708791391DACC0F77284FC3B331D943FC20CCE3C`**;
  Client DLL:
  **`5403CA7B8238A134A87D717EBE4A698E119EC5726254B524B7E3CC9E7CB8A671`**;
  Worker DLL:
  **`FE8CC4D51F8C873574A79BEB89B27F18C1D1FB503D2B2C1D70210AD2E7CDB178`**.
- Exact forbidden assets `7z.dll`, `7za.exe`, and `SharpSevenZip.dll`: **0**. The disabled managed
  SevenZip gate assembly remains inside Worker only; provider-selection regressions prove managed
  SharpCompress read authority for 7z/RAR while the gate advertises confidence zero.
- Mandatory packaged production flow covers ZIP, TAR, TAR.GZ, TAR.BZ2, GZIP, and BZIP2 collectively
  using only the private Worker: Client covers all five new managed formats, the in-process CLI
  covers ZIP/TAR.GZ/BZIP2, and representative WPF models cover ZIP/TAR.GZ/GZIP. It passes capability,
  creation, Rain reopen/integrity/extract/hash, original preservation, and cleanup. The same manifest
  passes current-source ZIP and exact-package all-managed-format performance probes. Exact-current
  fresh offline Sandbox startup/all-six-format creation, standard-user ACL denial, early and near-final
  true NTFS exhaustion, all 15 supported exFAT preset/provenance cases, and real local alias paths now
  pass. Explicit hash-bound GNU tar 1.35/gzip 1.14/bzip2 1.0.8 and WinRAR 7.20 suites accept the
  simple managed corpus; distinct `bsdtar`/libarchive 3.8.9 also accepts those containers and three
  long-Unicode/empty-directory/zero-byte/fractional-time PAX package layouts. Together with pinned
  official 7-Zip and Windows libarchive 3.8.4, the finite M3 external-reader gate passes. The public
  SharpCompress timestamp descriptor differs from the exact staged fractional PAX timestamp by one
  100 ns tick, an explicit read-metadata limitation. BCL helper header names include the Worker PID,
  so material planning is deterministic but byte-reproducible PAX containers are not claimed. The
  product owner directly observed package-bound ZIP creation/reopen/integrity and Rebuild Copy with
  the expected warning. Staging inventory, rebuilt-output manifest/hash, direct Recovery Center,
  accessibility, remote SMB/blocked-UNC/DFS/no-file-ID, install, sign, and publication acceptance
  remain separate. Open-ended tool/layout breadth remains Milestone 8 release hardening.

Protocol compatibility evidence:

- Retained 1.12-1.16 checkpoints remain historical exact evidence.
- `artifacts/RainArchives.ProtocolCompatibility.1.17.64081B70` contains genuine minor-17 Contracts,
  Client, Worker, a framework-dependent harness, and fixture. It proves TAR common operations and
  current/1.17 pre-access refusal for minor-18 routes in both directions.
- `artifacts/RainArchives.ProtocolCompatibility.1.18.1C85F6D9` retains the exact 132-input source
  snapshot at `1C85F6D9`, its 205-file private Worker, captured Client/Contracts binaries, a
  rebuildable retained-client recipe, and managed-format proof outputs. It contains no prohibited
  native provider asset.
- The complete current/retained class passes **17/17** cases. Current Client with the retained 1.18
  Worker keeps prior managed operations and refuses activity, extraction recovery, independent
  nested recovery reopen, and diagnostics locally; retained 1.18 Client with the current Worker
  negotiates minor 18 and completes ZIP/TAR/TAR.GZ common operations.
  The 1.17 artifact remains deliberately **binaries-only**: no exact source snapshot or source
  fingerprint survived, so none is claimed.

Retained protocol-1.11 local package gate completed on 2026-08-24:

- Isolated self-contained `win-x64` package:
  `artifacts/RainArchives.Protocol111RecoveryCenterPackage.20260824.01/app`.
- Package identity: **609 files / 231,060,953 bytes** and aggregate SHA-256
  **`A51087FD342AAFF8F3F252B68C05106FE3D4BB2E0352ED7A0490BD0166182D90`**.
  The App root is 404 files / 146,997,656 bytes; private `worker/` is 205 files /
  84,063,297 bytes.
- `RainArchives.exe` SHA-256:
  **`9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26`**.
  Private `worker/RainArchives.Worker.exe` SHA-256:
  **`C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF`**.
  Both report 1.0.0.0; both Contracts copies hash to
  **`6F52E6828C6EBA4F4D171153201E355F424DC9741A02785C767F22803CC5D39E`**.
- Exact forbidden packaged assets `7z.dll`, `7za.exe`, and `SharpSevenZip.dll`: **0**. The
  production App launched only the absolute private packaged Worker path.
- Production WPF package evidence: checked ZIP open/list, Unicode search, integrity, selected
  extraction with exact source hash, plain UTF-8 text preview, creation-dialog admission, and
  path-free Recovery Center list/Inspect/default-No/re-Inspect/confirmed exact-evidence Discard/
  refreshed-empty flow.
- Exact packaged-Worker production ViewModel evidence: AES-256 wrong/correct password retry,
  secret-array zeroing, text-preview hash, validated session reuse without a third prompt, Balanced
  ZIP creation/reopen/preview, exact source-entry hash, and zero staging residue.
- Recovery fixture evidence: opaque ID `cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc`,
  revision `2767bd1fe247e87a782726c880ef3aacbd6d0558659a15fce9d8f830968fa354`,
  one source / 31 bytes. Neither UI nor confirmation exposed a temporary path. Confirmed discard
  removed only the exact tree, refreshed to zero reviewable/active/suspicious records, did not retry,
  and preserved the only sibling sentinel at SHA-256
  `7F3015AC654D3CAC04E6DE9181508BEE888D115996534C047EF3FBC0186E7D92`.
- Final close: no packaged App, Worker, testhost, dotnet, or staging residue.
- Detailed commands, fixture hashes, automation limits, and manual-versus-automated boundaries are
  recorded in [docs/testing.md](docs/testing.md).

The completed protocol-1.12 slice adds:

- Protocol minor 12, negotiated `zip-rebuild-copy-v1`, typed bounded DTOs, operation/capability
  contracts, and client-local plus Worker-side minor-11 refusal.
- A provider-neutral closed planner, retained-entry producer callback, stable mapped-source leases,
  built-in ZIP writer route, journaled staged transaction, complete staged-manifest readback, and
  no-replace commit that never mutates the original.
- Typed Client API, `rebuild-zip` CLI, and the capability-gated standalone WPF Rebuild Copy dialog.
- Unit, adversarial, Worker/Client/CLI/WPF-model, race, crash, low-space, cancellation, and official
  7-Zip Extra 26.02 conformance coverage across all four presets.
- Independent read-only audit fixes for output-manifest trust, typed-frame transport poisoning,
  canonical archive-path validation, mapped-source stability, and invalid user-edited WPF paths.

The protocol-1.12 checkpoint was **not** republished as a self-contained product package or qualified on
a clean machine, through direct manual Rebuild Copy interaction, or for current accessibility. The
retained protocol-1.11 package above does not contain this slice and its acceptance must not be
transferred.

The completed protocol-1.13 Slice 1 checkpoint adds:

- A distinct `zip-rebuild-rename-v1` request, message kind, feature dependency, typed Client route,
  Worker pre-deserialization downgrade refusal, repeatable CLI `--rename` option, and WPF Rename
  review column that remains visibly distinct from Replace.
- Exact persistent-key regular-file rename/move, safe simultaneous swaps, one-action-per-key rules,
  closed case/NFC/prefix/directory collision refusal, retained timestamp/basic-attribute handling,
  escaped final-path progress, and complete renamed-manifest readback through the unchanged staged
  transaction and no-replace commit.
- Real mixed rename/delete/add/replace Client/Worker/CLI/WPF-model round trips, cancellation,
  destination race, runtime low-space, exact-PID Worker crash/recovery, original-hash preservation,
  authoritative reopen/list/test/extract, and official 7-Zip Extra 26.02 test/extraction for all four
  presets. A separate read-only Slice 1 review found no high/critical defect; its one accepted WPF
  mixed-operation evidence gap was regression-tested before the checkpoint.
- Exact protocol-1.12 and protocol-1.13 self-contained compatibility checkpoints are retained under
  `artifacts/`. Current Client/common-operation tests against the retained 1.12 Worker, retained 1.12
  Client against the current Worker, and the retained 1.13 Client/Worker self-test pass. This is
  binary compatibility evidence, not a product package or manual acceptance.

The completed protocol-1.14 Slice 2 checkpoint adds:

- Distinct `zip-rebuild-tree-edit-v1` plan/start routes and a Worker-owned bounded
  `zip-rebuild-source-folder-review-v1` query. The tree plan returns explicit/synthetic directory
  state, bounded descendant/material counts, and exact snapshot revisions; source-folder review uses
  pinned non-reparse enumeration, single-link file validation, global entry/byte accounting, and no
  WPF filesystem enumeration.
- One closed namespace resolves retained subtree move/delete, exact file delete/replace/rename, and
  mapped file/tree additions before staging. Overlap, cycles, self-descendants, stale revisions,
  file/directory prefixes, case/NFC aliases, and multiply transformed entries fail closed. Planning
  enforces 4,096-node/directive and 512-KiB path budgets incrementally and uses bounded component
  tries plus ordinal radix ordering instead of repeated subtree rescans.
- CLI and WPF expose explicit Add folder, Move folder, and Delete folder review. The WPF folder picker
  is disabled before construction on downgrade; every selected root plus the new candidate is sent
  to the Worker and the returned bounded descendant/file counts remain visible before confirmation.
- Real Worker/Client/CLI/WPF-model, cancellation, mutation, reparse, hard-link, ADS, destination race,
  low-space, exact-PID crash/recovery, authoritative Rain reopen/list/test/extract, official 7-Zip,
  Windows `bsdtar`, and retained 1.12/1.13 binary compatibility checks pass. A separate read-only
  remediation review found no actionable issue.
- Exact checkpoint `artifacts/RainArchives.ProtocolCompatibility.1.14.4B5F3A57` retains the
  self-contained `win-x64` Client harness, Worker, fixture, and source snapshot. Its pair self-test
  passed with protocol 1.14 and byte-identical source preservation. This is a local compatibility
  checkpoint, not an installable product package or manual acceptance.

The completed protocol-1.15 Slice 3 checkpoint adds:

- Distinct `zip-rebuild-metadata-v1` transport and typed retained-key or mapped-final-path targets,
  resolved only after the closed file/tree namespace plan. Client and Worker mirror exact shape,
  calendar, allowed-mask, duplicate-target, directive, path-payload, and frame bounds before source
  or destination access.
- Exact timezone-less raw DOS timestamps (`1980-2107`, valid calendar, even seconds `00-58`) and the
  low-word ReadOnly/Hidden/System/Archive mask, with Directory derived only from entry kind. The BCL
  provider writes and reads the exact tuple/mask, and complete staged readback rejects any mismatch.
- Strict CLI syntax and a compact WPF current/final review labelled **Archive time (timezone
  unavailable)**. WPF does not relabel provider UTC projection as raw ZIP-local state, mapped file
  and directory rows track edited targets and retained-tree moves, and untrusted display paths are
  escaped without changing raw edit/IPC values.
- Focused all-mask/timestamp-boundary provider proof, real Worker metadata-plus-namespace round
  trips, Rain reopen/integrity/extraction, BCL and official 7-Zip readback, cancellation,
  exact-worker crash/recovery, original-hash preservation, malformed/downgrade/frame refusal, and
  exact corrupted-readback rejection. Retained 1.12/1.13/1.14 compatibility checks pass.
- A separate read-only review found four product/compatibility issues and a follow-up found two
  mapped-target normalization/lifecycle issues; every accepted issue was fixed with regression
  coverage, and the final focused review found no remaining actionable issue.
- Exact checkpoint `artifacts/RainArchives.ProtocolCompatibility.1.15.6F595F0D` retains the
  self-contained `win-x64` Client harness, Worker, fixture, and source snapshot. This is a local
  compatibility checkpoint, not an installable product package or manual acceptance.

## 8. Acceptance evidence already earned

### Accepted Milestone 1 package (historical exact snapshot)

- Source fingerprint: `95754497E21D10A57F99C37E8DC239DDEA20626A96A36694E558A1A763CFA859`
  across 79 inputs.
- Self-contained bundle:
  `C:\Users\aliss\AppData\Local\Temp\RainArchives.M1FinalBundle.4651c78f811c4224b8f9e7270dbaa817`.
- Bundle manifest SHA-256:
  `40354B74DD36D728AC7C7A09961FEA7C04AD20817C084768A173087957F7403C`.
- Strict clean-machine evidence:
  `C:\Users\aliss\AppData\Local\Temp\RainArchives.M1FinalCleanMachineEvidence.8cad551636ea4aa4adf3f0d5e970c7de\strict-final`.
- Evidence SHA-256:
  `3FA36A7C8FFB0CFA4BFADDF094B6AD0F0DFDDB99FD67CB351D5BF5B03F6764FC`.
- Fresh eligible Windows Sandbox: 20/20 checks, 13 packaged CLI operations, immutable manifest,
  app-private runtime, no installed/app-private 7-Zip, no fixture-password leakage, zero residue.
- Product owner manually accepted mixed-DPI, contrast-theme behavior, protected password flow,
  keyboard-only ZIP selection/extraction, and Narrator navigation against the accepted package.
- Physical CachyOS/Samba 4.24.6 extraction source/destination success hash-matched; a disconnect after
  real progress ended in authoritative failure, no final destination, one attributable staging tree,
  and no Rain process. This closes the M1 remote-SMB gate only, not M3 creation SMB acceptance.

### Other retained evidence

- Physical non-sparse 3 GiB stored ZIP: full integrity, hash-identical extraction, bounded Worker
  memory, cancellation after real progress, and no final/staging residue.
- Disposable Windows storage: early/near-final NTFS exhaustion, standard-user ACL denial, and exFAT
  extraction/provenance-warning checks.
- Official 7-Zip 26.02 and Windows bsdtar independently accepted the checked new-ZIP outputs.
- Real exact-PID Worker kills cover extraction/creation windows, durable creation intent/post-rename
  reconciliation, and nested discard after first exact source deletion.
- Deterministic malformed run: 5,000 cases across ZIP/7z/RAR/GZIP/TAR.GZ with Worker health checks.
- Historical security scan recorded zero critical/high and eleven medium/low findings; all eleven
  were fixed with focused regressions. This is not an independent current-source security scan.

See [docs/milestone-1.md](docs/milestone-1.md),
[docs/testing.md](docs/testing.md), and
[docs/benchmarks.md](docs/benchmarks.md) for exact snapshot-qualified evidence.

## 9. Dependency versions, identities, and licensing

Direct package versions are centrally pinned and every project uses `packages.lock.json`.

| Dependency | Role/state | Exact identity |
|---|---|---|
| .NET SDK | Build | 10.0.301, commit `96856fd726`; MSBuild 18.6.4 |
| .NET host/runtime used for verification | Runtime | Host 10.0.9, commit `901ca94124`, win-x64 |
| SharpCompress | Active managed reader | 0.50.4; upstream commit `c083c6efd843a844b0c8f7878787360e815be781`; restored `.nupkg` SHA-256 `045AC4886EAAD54FCB0E9785CDA312178E7140579FA4BEBD3E5238456CE2970B`; MIT |
| MSTest | Test-only | 4.3.3; restored `.nupkg` SHA-256 `F3D4F71514C5EEC4007EE03161F399843ADB2E5CE64FBBF33C784D67E14E45AE` |
| Microsoft.NET.Test.Sdk | Test-only | 18.9.0; restored `.nupkg` SHA-256 `3C19312754A299160BAFCB192104C57719849F05589B3AE5FED2E21DBCF4F5B0` |
| BenchmarkDotNet | Benchmark-only | 0.15.8; restored `.nupkg` SHA-256 `A7C3800476E3AB709B791E50686F1D98D80305282EFA305179753ED2AD76BB58` |
| SharpSevenZip | Audited candidate only; **not referenced/restored/shipped** | 2.0.109; source commit `105964f1c060481a17d20d084aacaecace92ffab`; signed `.nupkg` SHA-256 `F2B3BF16138D814FD03D8F706E6C77C163E4B31B7E29A21FF1FB2399C7776262`; net8 assembly SHA-256 `47F0CD2CF1B490ADEA5D57C15E057DE6A11E203FB430E5CBC417E6338D029E9`; LGPL-3.0-or-later |
| Official 7-Zip x64 `7z.dll` | Audited candidate only; **not stored/shipped** | 26.02; SHA-256 `69FD4DF057985C40E510E2FAC182881C7F85E90AA13EC703F763A8FDB2CE61F8`; LGPL/BSD/unRAR components |
| Official 7-Zip Extra x64 `7za.exe` | External evidence tool only | 26.02; SHA-256 `35D4D69D7CD6CB44558F208C3B1334268013F9DAF82D2DDA848893A1C30C59C2` |
| DotNetZip 2025 | Fixture generator only; not product dependency | release v2025.02.15, assembly 2025.2.3.0, commit `866343fa8508d120ebc4cb0f3a6cfe95b7e6987c`; Apache-2.0 |

NuGet lock `contentHash` values remain the restore authority for every transitive package. Do not
replace lock files with floating versions. A forced locked restore and vulnerability-feed check
must pass before release evidence.

`THIRD-PARTY-NOTICES.md`, `licenses/`, and
[docs/dependency-evidence.md](docs/dependency-evidence.md) are binding inputs to packaging. If
SharpSevenZip/7-Zip is adopted, ship complete LGPL/GPL/BSD/unRAR notices and corresponding-source
availability material, retain a replaceable wrapper, and permit license-required reverse
engineering/debugging. This is engineering compliance guidance, not legal advice.

## 10. Enforceable security invariants

Every future implementation and test must preserve these invariants:

1. Every archive and every entry name/metadata field is untrusted.
2. Detection is content-based where practical. An extension never grants format or capability
   authority.
3. Archive entry paths cannot escape a selected root through `..`, rooted/absolute paths, UNC,
   device paths, drive-relative syntax, ADS syntax, reserved device names, trailing-dot/space
   aliases, empty/dot components, or separator tricks.
4. Admission rejects case-insensitive and NFC-normalized collisions, duplicate ambiguity where the
   operation cannot represent it safely, and file/directory prefix collisions.
5. Existing destination reparses are never followed. Extraction/creation uses a new destination,
   operation-owned staging, handle-relative no-follow creation, exact manifest revalidation, and a
   no-replace final commit.
6. Archive links, symlinks, hard links, junctions, and reparses never become implicit filesystem
   traversal. Unsupported links are refused or represented as metadata, not followed.
7. Source directories are enumerated through pinned non-reparse handles. Reopened identities are
   compared where the filesystem provides IDs; exact final manifest revalidation remains required.
8. No user-selected archive/destination is deleted, overwritten, or modified based only on a path,
   filename pattern, owner token, journal, revision, or startup scan.
9. Checked arithmetic, declared/runtime byte counts, expansion ratios, entry/object/depth limits,
   aggregate decoded-work limits, disk-space reserves, and actual output ceilings apply before and
   during writes. Metadata cannot bypass actual streamed accounting.
10. Integrity/extraction uses checksum-validating provider paths where available; raw GZIP verifies
    CRC32/ISIZE and compressed TAR validates the outer stream before member copies.
11. Multipart and unknown-volume sessions remain list-only until every required volume can be
    identity-leased, fingerprinted, validated, and represented in the protocol.
12. Nested recursion is always user-explicit and bounded by depth, per-entry, ancestry-chain,
    aggregate-byte, disk, ratio, cancellation, and session limits.
13. Operation events remain ordered. Warnings are provisional; only the terminal event authorizes
    a committed/cancelled/failed conclusion. Unknown/post-terminal operation events abort transport.
14. Cancellation must settle request, stream, secret, staging, and reservation ownership. If the
    result of an irreversible action is uncertain, do not retry automatically.
15. Passwords never enter normal JSON, command lines, telemetry, exceptions, or ordinary logs.
    Zero mutable buffers where possible and explicitly document the unavoidable managed-string copy.
16. Untrusted display text is escaped for control, format/bidi, separator, backslash, and invalid
    UTF-16 characters before CLI/WPF presentation.
17. Mark-of-the-Web propagation accepts one strict `ZoneId` 1-4, emits canonical zone-only data,
    discards source URL/arbitrary ADS fields, and treats mixed/malformed/inaccessible state
    conservatively.
18. Worker isolation contains parser crashes and allows owned-process termination; it is not a
    same-user filesystem sandbox and must never be described as one.
19. Creation recovery startup is count/path-free and does not contact recorded destinations.
    Disposition requires fresh exact phase/sequence/time evidence, identity revalidation, default-No
    user confirmation, durable intent, verified postcondition, and conflict-safe replay.
20. Nested recovery remains path-free and never automatic. Discard requires one freshly inspected
    exact-case inactive ordinary tree, stable identities, one-link/no-ADS/no-reparse objects, at most
    16 sources, 256 MiB each, 512 MiB aggregate, exact revision/count/bytes, full pre-delete
    revalidation, and explicit default-No confirmation. Partial deletion becomes suspicious state.
    Independent reopen additionally requires the exact selected source ID/length and copies through
    pinned handles only after complete fresh evidence revalidation; it creates a new depth-zero root
    session and never mutates, adopts, or exposes the recovery source.
21. Completed creation receipts are only a bounded replay cache (newest 256, maximum 30 days).
    Pruning exclusively reopens/revalidates exact ordinary metadata trees and never opens/deletes the
    archive destination. Truncated enumeration cannot grant count/rank deletion authority.
22. No codec provider receives authority to choose extraction paths, enumerate arbitrary source
    paths, overwrite user archives, or publish final output.
23. Support diagnostics contain only fixed enums, numeric bounds, and booleans. They expose no path,
    archive/provider text, exception message, secret, recovery ID, or action authority, and they must
    truthfully report that same-user process isolation is not a filesystem security sandbox.

The fuller threat model and known limits are in [SECURITY.md](SECURITY.md).

## 11. Known limitations

- Worker runs unelevated under the same user token and can read what that user can read. The accepted
  current containment choice is the authenticated exact-PID Worker plus kill-on-close Job Object as a
  lifecycle/crash boundary only. AppContainer/brokered handles remain unsupported until a compatible
  maintained principal/ACL/handle-broker and atomic deployment contract is separately proven.
- SharpCompress can execute synchronous parser/decoder work that cannot be cooperatively preempted.
  Process termination contains a stuck owned Worker, but does not make the parser memory-safe.
- Multipart ZIP/7z/RAR is list-only; test, extraction, preview, and split creation are deferred.
- Solid RAR supports checked list/test/extract but not direct preview or nested open.
- A standards-valid ZIP64 central-entry disk-start sentinel is explicitly unsupported because
  SharpCompress 0.50.4 fails internally on it.
- Current write support is limited to one new, absent-destination, unencrypted ZIP, TAR, TAR.GZ,
  TAR.BZ2, GZIP, or BZIP2 output. Rain can rebuild one eligible opened single-volume ZIP with
  add/replace/delete, retained-file rename, bounded retained-tree move/delete, mapped folders, and
  explicit DOS timestamp/basic attributes. TAR-family supports explicit/tree input; raw GZIP/BZIP2
  accepts exactly one ordinary source file. There is no overwrite/replacement/in-place update,
  encryption, filename encryption, split output, TAR.XZ/raw XZ writer, or approved 7z writer.
- Current extraction is new-destination only. Merge/conflict decisions and extraction-staging
  reconciliation are not implemented.
- One exact recovered nested source can be reopened copy-only as a newly detected independent root.
  It does not reconstruct a former ancestry chain or authorize discard; discard remains narrow and
  irreversible, and smaller/missing/uncertain trees never imply completion.
- Recovery destination operations may block in synchronous network filesystem I/O; genuine
  blocked-UNC cancellation has not been physically qualified.
- Real local 8.3 source paths, a reversible mapped drive, and alternate `localhost`/numeric loopback
  UNC spellings pass exact-current package-bound create/reopen/test/extract/hash checks. DFS, remote
  aliases, and filesystems with missing/unstable IDs remain unproven; the latter have weaker
  enumeration-to-open binding.
- Preview is bounded text only. There is no image/media/document/Shell preview or safe external
  path handoff.
- WPF is an early functional shell: visual design is plain, managed-ZIP presets are reusable but
  broader settings are absent, and queue/activity/conflict surfaces are incomplete.
- The accepted historical release package is protocol 1.3. Protocol 1.11 and 1.16 retain separate
  historical local automated package proofs, and protocol 1.18 has the latest self-contained App/
  private-Worker checkpoint plus package-bound performance, fresh offline Sandbox, disposable
  storage, local alias evidence, and the bounded current-package external-reader matrix. Direct
  product-owner ZIP creation/reopen/integrity and Rebuild Copy observation now passes. Recovery
  Center state, accessibility, creation-specific remote SMB/blocked-UNC/DFS/no-file-ID coverage,
  installer, signing, and publication acceptance remain open and are not transferred. Open-ended
  tool/layout breadth remains Milestone 8 release hardening.
- Rain Explorer integration, associations, shell verbs, installer/update atomicity, signing, and
  publication are not implemented.
- Current `1C85F6D9` ZIP and all-managed-format performance numbers are warm local snapshot baselines, not
  cold-cache/cross-machine release thresholds or competitor superiority claims.

## 12. Unresolved findings and decision gates

1. **SharpSevenZip 2.0.109 cannot become authoritative unchanged.** Its `Check()` accepted a checked
   truncated ZIP that official 7-Zip rejected, and the wrapper drops native archive warning/error
   flags and packed-size metadata. Reopen only for a newer audited release or an explicitly approved
   minimal fork with typed warning/error/password/cancellation behavior.
2. **Native 7-Zip adoption remains a gate, not a TODO import.** It must pass exact loader policy,
   package/license/source obligations, forced crash containment, format/cancellation/stream tests,
   and App/Worker atomic packaging. Until then, the provider must advertise nothing.
3. **SharpCompress security/robustness history remains relevant.** Rain avoids upstream directory
   extraction, runs parsing out of process, and has regression/fuzz-smoke evidence, but sustained
   coverage-guided fuzzing and broader method/version conformance are still required.
4. **The current independent source security scan is clean within its immutable snapshot.** Scan
   `e255a57b-9dde-43bd-b44d-c426500b16fa` covered six surfaces with full reported coverage and zero
   findings. It was parent-only because further subagent routing was prohibited, and generated build/
   test directories changed while it ran; the report remains bound to its named immutable source
   snapshot rather than becoming a timeless approval. Provider-message provenance and physical gates
   remain explicit review targets.
5. **Nested partial-discard durability is an explicit conservative decision.** No durable intent is
   added because current discard cannot prove completion after a partial crash without turning a
   smaller/missing tree into unsafe authority. It preserves suspicious state and never retries.
   Protocol 1.21 separately reopens one exact source by copy only and performs no recovery mutation.
6. **Worker containment is decided for the current release architecture.** Same-user Worker plus Job
   Object is retained for crash/lifecycle containment and explicitly is not a filesystem sandbox.
   AppContainer/brokered handles are not enabled because no current compatible path/handle/provider/
   IPC/deployment/solo-maintenance broker contract is proven. A different-principal threat model must
   reopen this as a separately designed authority boundary.
7. **Broader historical protocol compatibility evidence is incomplete.** Exact retained 1.12-1.16,
   narrow genuine 1.17 binaries-only, and exact source/binary 1.18 checkpoints pass the current/
   retained matrix. Earlier v1.4-v1.11 binary/frame coverage and broader version-layout qualification
   remain separate; the 1.17 artifact has no exact retained source snapshot and is not relabelled.
8. **Package-bound manual/remote/release acceptance is open.** Protocol 1.18 has a retained automated
   self-contained App/private-Worker package checkpoint and passes source-at-package-time ZIP, exact-package
   mixed-corpus benchmark, fresh offline Sandbox, non-elevated ACL, true NTFS exhaustion, exFAT, and
   local 8.3/mapped-drive/alternate-loopback-UNC gates. Explicit GNU 1.35/1.14/1.0.8, WinRAR 7.20,
   distinct libarchive 3.8.9 PAX/simple, pinned official-7-Zip, and Windows-libarchive 3.8.4 corpora
   close the bounded M3 external-reader matrix. Product-owner direct ZIP creation/reopen/integrity
   and Rebuild Copy observation now passes with the expected warning. Recovery Center, staging
   inventory, rebuilt-output manifest/hash, product-owner current-flow accessibility, physical
   blocked-UNC, actual second-machine creation, DFS/no-file-ID filesystems, installer, signing, and
   publication remain separate. Open-ended version/method/layout breadth remains Milestone 8 release hardening.
   Do not claim the automated/physical 1.18 evidence, retained historical packages, or accepted M1
   extraction package proves those remaining routes.

Closed Milestone 3 capability decisions are evidence boundaries, not unresolved implementation:

- **ZIP encryption:** BCL/SharpCompress writers provide no qualified managed content/name encryption;
  no new dependency passed the security/licensing/package/interoperability/maintenance gate.
- **TAR.XZ/raw XZ:** SharpCompress 0.50.4 is decompression-only for XZ and no compressor is approved.
- **7z creation:** the disabled gate and package contain no native asset; unmodified SharpSevenZip
  accepted a truncated archive rejected by official 7-Zip and lacks required warning/error/packed-size,
  password, progress, and cancellation authority. No creation feature/message exists.
- **RAR creation:** protocol 1.23 conditionally authorizes only unencrypted single-volume RAR5
  explicit/tree creation through the user's separately installed, licensed, exact signed x64
  WinRAR 7.23-through-7.x encoder. Missing/old/untrusted/unsupported/restart-required states stay
  closed. Rain never bundles, downloads, updates, or discovers the encoder, and decoder availability
  never authorizes it. Encryption, SFX, split, arbitrary switches, rebuild/update, overwrite, and
  in-place mutation remain closed.
- **Split output:** deferred until a bounded multi-output contract, aggregate resource model, durable
  partial-publication journal, recovery protocol, crash matrix, and official corpus exist.
- **Existing-destination overwrite/replacement/in-place:** closed refusal. Admission and precommit
  require absence, publication is no-replace, a raced target is preserved, and the original is never
  staging or mutated. A future route requires separately approved typed conflict/backup/recovery and
  atomic-replacement proof; automatic retry after uncertainty remains forbidden.

## 13. Exact next action

### CURRENT — Extraction sync-conflict diagnosis and message repair, protocol 1.24

The UI repair is built at `artifacts/RainExplorer.ExtractionFix.2.0.0.20260905.01`; its installer is
unsigned and was not installed. The Worker and its one-link/no-replace/recovery policy are unchanged.
The underlying interoperability problem with active Google Drive backup remains open. Do not mark
the user's extraction issue fully fixed or treat intermittent synced-folder success as qualification.
The exact reported ZIP successfully extracted outside the synced tree; a hash-verified copy is retained
under this artifact's `verified-extraction/Vencord-main` directory.

The next engineering action is a bounded staging/lifetime design that prevents transient foreign links
without weakening object identity, no-follow, single-link, resource, durable-recovery, cancellation or
atomic no-replace publication guarantees. Do not silently accept Drive-cache paths, remove validation,
add automatic retry/cleanup, change sync settings, or substitute a blind sleep for that proof. The
user's approval to fix the reported issue remains in scope; it does not authorize unrelated phases,
installation, system changes, publication, or disposition of the original failed staging tree.
See [extraction-sync-conflict.md](docs/extraction-sync-conflict.md) for runtime evidence and the exact
test-owned residue: a cleanup command was rejected before execution, so no fixture cleanup is claimed.
The withdrawn WinRAR context-menu concern required no changes.

### Retained — RAR long-path regression repair, protocol 1.24

The requested repair is complete and the unsigned, uninstalled deliverable is
`artifacts/RainExplorer.RarLongPathFix.2.0.0.20260905.01`.
The next user action is to install that build and retry the reported RAR creation. Do not start
an automatic retry, installation, next feature phase, system change or publication. The fix was
verified with generated local nested-path fixtures, not by retrying the user's private folder.
Original preservation, exact readback, cancellation, disk reserves and limits are unchanged.
See [rar-long-path-fix.md](docs/rar-long-path-fix.md). Historical performance numbers do not transfer
to this package, and physical UNC/filesystem, manual GUI, installation and signing remain separate.

### Retained — M7-D valid-RAR opening and conditional RAR creation remediation

The product owner reported that a large valid RAR showed “Rain Archives rejected the archive-open request
before reading the file” and that RAR was absent from Rain Explorer's creation picker. The reported
550,475,088-byte RAR5, SHA-256
`28F6AAC54C43CE10A39C0E930861D658229D7359F3466503C30D956D3222F92B`, is valid and contains 18,418
entries. SharpCompress 0.50.4 threw while reading optional CRC metadata from one valid RAR5 directory; the
generic `ArgumentException` mapping then mislabeled the provider defect as a pre-access invalid request.
Directory CRC is now optional, required metadata failures stay structured, the fixed 73-byte regression opens,
lists, and extracts, and the full reported RAR lists through the exact packaged Worker without changing hash.
Rain Explorer also replaces that misleading generic fallback with fixed actionable copy.

Protocol 1.23 preserves every earlier feature and adds separate RAR explicit/tree/capability messages. RAR
creation is enabled only through the concrete Worker-owned WinRAR provider when the user's exact ordinary x64
`C:\Program Files\WinRAR\Rar.exe` is Authenticode-trusted to `win.rar GmbH` and version 7.23 through 7.x.
The encoder remains separately installed and licensed. Rain does not bundle, copy, download, update, or
discover it through `PATH`, current directory, or registry. This machine's signed 7.20 installation therefore
reports `UpdateRequired`; protocol 1.22 and earlier remain locally closed. Rain Explorer visibly shows the RAR
row with the exact disabled reason until a qualifying installation is present.

The initial writer is unencrypted, single-volume, new-destination-only RAR5 for bounded explicit files and
non-reparse trees. Capability query, admission, and post-queue execution each re-resolve the concrete provider.
The exact encoder is revalidated and leased without write/delete sharing. Sources are materialized only from
validated streams into a random token-owned private workspace; the encoder runs without a shell, must exit
successfully, passes its own archive test, and the resulting RAR must match the complete SharpCompress
path/kind/length/timestamp/content-digest manifest before Rain's existing guarded staged no-replace commit.
Encryption, passwords, split, SFX, arbitrary switches, update/rebuild, overwrite, replacement, and in-place
mutation remain closed.

Workspace maintenance begins after authenticated startup and outside request dispatch under one global
1,024-candidate/16,384-object/two-second budget. Root ancestry, candidates, and owner records are opened
no-follow and must be ordinary non-reparse one-link objects. Only one inactive exact-token tree from one
complete bounded inventory may be removed; active, suspicious, changed, unreadable, and over-limit state is
preserved with a fixed warning. Cancellation proves the exact encoder exited after kill; uncertainty tears
down the Worker connection instead of publishing a false terminal. Cleanup failure is propagated after owned
handles are released. Exact-PID proof covers live preservation, hard-kill residue, next-process cleanup, and
unrelated-sibling preservation. This is accidental-failure recovery under the declared same-user model, not a
hostile same-user filesystem sandbox.

The final Rain Archives gate passes 530 unit/WPF, 29 adversarial, and 375 integration tests: **934/934**, zero
failures/skips. All 16 direct/transitive advisory inventories, locked restore, formatter, and zero-warning/
zero-error Release build pass. Canonical production identity is 164 inputs / 3,260,519 bytes /
`0D84243CA1A09AE3BD77942EC66578E44E3EB5A7547411CCD40E224CD2460AB2`. The immutable package is
`artifacts/RainArchives.M7DRarPackage.1.23.0D84243C`; its 1,199-byte manifest is
`DF47FAD343B6DCBDE2B1FFB42489C7CF7D19C154CA9D2CB6398C07AE2F442ADB`. It contains zero `Rar.exe`,
`WinRAR.exe`, `7z.dll`, `7za.exe`, or `SharpSevenZip.dll` assets.

The complete Rain Explorer suite passes **315/315** both normally and with every real-worker route redirected
to the exact packaged Worker. Its Release build has zero warnings/errors, both advisory inventories are clean,
scoped formatting and `git diff --check` pass, and the cumulative uncommitted identity over base
`0bc3fb090bef4d93e0db5bb6d66813b80e8385f7` is 67 inputs / 1,661,839 bytes /
`266A39F2A2286D9116F41A5A11C2F587B758C2C953AEDBC9A3E902D1E2560C4C`. A fresh Daybreak Blue
max-reasoning review's bounded-maintenance, kill-proof, cleanup-reporting, no-follow-owner, and fixture-isolation
findings were remediated in the one permitted review pass; the 14 focused cases and complete gates pass. This
is same-task review evidence, not independent/manual acceptance.

The current Rain Explorer package is
`artifacts/RainExplorer.M7RarRemediationPackage.2.0.0.266A39F2.01`: manifest 6,749 bytes /
`6E9AE928C36F9E2F7C2FA718D24D6C6337B7054A825D168603553A409ECCF23E`; app tree 458 files /
251,080,369 bytes / `37B03FE1E36B2BF2D647831F3AEB43C6598FA0599D7692E46FFDE45F1F948628`; private Worker tree
207 files / 85,185,437 bytes / `B523EBE613581D176389C0C9EF6E6C6D77DDFC866E3D5485845E8555DE5AF1C8`.
The matching uninstalled Inno Setup 6.7.3 installer is
`artifacts/RainExplorer.M7RarRemediationInstaller.2.0.0.266A39F2.01/RainExplorer-M7-RAR-Remediation-Setup-2.0.0.exe`:
76,160,790 bytes / `AB7E8BE476990B2CC0EC0CFB86ED6353196AF2DD14B019D5231370B99CCE19CB`, version 2.0.0.0,
intentionally unsigned. Neither artifact was executed, installed, signed, or published; the installed
single-instance Rain Explorer and its Worker were not interrupted. Every predecessor remains immutable.

Exact next action: close the installed Rain Explorer, install or launch the exact `.01` candidate, and verify
the reported large RAR plus representative ZIP/7Z/RAR/TAR/GZIP/BZIP2/XZ-family inputs. Confirm the fixed
message for malformed extension-only files, **Open with...**, projected/legacy WinRAR menus, **Extract to...**,
dedicated archive icons, Back/Forward, association choice, and reversal. With signed WinRAR 7.20, confirm the
RAR row is visibly `UpdateRequired`. Manually install an official signed 7.23-or-later 7.x WinRAR, restart the
candidate, create disposable file/tree RARs, and independently reopen/test/extract them with exact hashes and
unchanged sources. Then record install/upgrade/uninstall, accessibility, clean-machine, and residue results.
Do not weaken content validation, auto-update/bundle WinRAR, infer GUI acceptance from automation, or begin
signing/publication without separate authorization.

See [docs/milestone-7.md](docs/milestone-7.md) for the bounded design and manual matrix.

### PREDECESSOR — M6 exit duplicate-extraction-authority removal and integrated package gate

The product owner explicitly authorized Phase 6 on 2026-09-02 after directly confirming the remediated
inspector behavior. The current sealed Rain Archives predecessor remains protocol 1.22, 160 canonical
inputs / 3,157,575 bytes at
`651729F471468F590BB2F89CE663C14B5140356227509B3C247A93C74481D740`, local self-contained package
`artifacts/RainArchives.M5GDDesignSystemPackage.1.22.651729F4`, and 910 automated tests
(522 unit/WPF, 29 adversarial, 359 integration). M5G-D's centralized semantic control layer, generated
DataGrid bridges, compact Settings scroll route, archive-neutral presentation fallbacks, runtime-state
proof, exact-package flow, and documentation tranche are complete.

M6-A's automated read-only location checkpoint, M6-B's bounded inert preview checkpoint, M6-C's
integrity/selected-extraction checkpoint, M6-D's nested navigation/reopen checkpoint, M6-E's truthful
capability-disabled delayed-transfer checkpoint, M6-F's managed creation/rebuild checkpoint, and the
M6-exit duplicate-authority/package checkpoint are complete
in the uncommitted Rain Explorer working tree over base
`0bc3fb090bef4d93e0db5bb6d66813b80e8385f7`. The retained M6-A identity is 15 changed inputs /
541,661 raw bytes / `D4543C0ACB433EF596EEF8AB13204E6E59CB4B45708C08CD0A3B93EB58E27FAF`.
The cumulative M6-B identity is 18 changed inputs / 618,457 raw bytes /
`DAF3D191214F30C177C198E375239A0A9203F3B8E60767923769ABA64B657A7E`, using ordinal repo-relative
path, NUL, decimal raw length, NUL, then raw bytes. The cumulative M6-C identity is 27 changed inputs /
817,265 raw bytes / `9F3672F88A01983FB8A49A9AC4959F2141347C502057FED3B4C884207A964709`
under the same algorithm. The cumulative M6-D identity is 28 changed inputs / 907,670 raw bytes /
`93EFF0EA30F531A4995DDFDBBB93BD714E7C7CB0C938DC62050C4D13415061B7` under that same algorithm.
The cumulative accepted M6-E identity is 34 changed inputs / 1,078,510 raw bytes /
`104F6C3B5D5DFD6088C1394564C0744B36F1D96532BDD319B2B4E21C2345562A` under that same algorithm.
The cumulative accepted M6-F identity is 44 changed inputs / 1,326,298 raw bytes /
`E637C248717555533151C3959B7A5847F4DFD5E4489AD262643263B4DEAF3108` under that same algorithm.
The cumulative accepted M6-exit identity is 47 changed inputs / 1,408,763 raw bytes /
`CB0DC84FC3A19D43F73E9ECB7E527F7094A968F54787635F816DF9BD0240AD65` under that same algorithm.
No commit, push, PR, installer, install, signing, publication, or release action occurred.

M6-B consumes `ArchiveEntryStreamLease` directly with an exact 1 MiB plus one-byte ceiling and creates
no preview cache or external path. Only strict UTF-8 or BOM-proven UTF-16/UTF-32 reaches inert text;
binary, oversized, unsupported, password, offline, stale, cancelled, and error outcomes use fixed
product copy. Control/format/bidi text is visibly escaped. Virtual rows never enter physical image,
PDF, media, Shell-thumbnail, Open With, export, or execution handlers. The serialized latest-wins pump
admits one active plus one newest pending request and settles exact lease disposal before a successor.
Completion, generation invalidation, and cancellation-source lifetime have one atomic ordering.
Navigation, replacement, tab close, Worker loss, and application shutdown cancel and drain the pump.
`SourceChanged` closes the exact session and marks the whole location stale; authenticated transport
failure invalidates the exact shared client and marks affected tabs offline after preview drain so an
explicit reopen receives a fresh client without reusing old entry IDs.

Independent M6-C verification passed 149/149 Rain Explorer tests with zero skips, a zero-warning/
zero-error Release build, no known vulnerable package in the current direct/transitive advisory query,
scoped formatter verification for every touched Phase-6 production/test C# file, and no
`git diff --check` error. The repository's unrelated historical formatting debt remains outside this
gate. The real protocol-1.22 Worker flow opens/browses/previews/integrity-tests a spaces-and-Unicode ZIP,
extracts the exact selection to a new destination, verifies output hashes, preserves the source and a
destination-conflict sentinel, and settles the exact operation/session/client. Fresh selection,
capability, volume, fingerprint, bounded-frame, destination, cancellation, event, current-item,
source-change, Worker-loss, and close/app-drain regressions pass. Password arrays and one-shot slots are
settled on every pre-admission, post-slot, transfer, cancellation, failure, and prompt ownership path.

Independent residue review found a test-only empty `password-refused-*` directory leak. The test now
removes only its own verified empty GUID destination and preserves unexpected contents as failing
evidence. The 17 already-created empty non-reparse directories were removed by exact path after
two-phase validation. Final test-created Worker/testhost/fixture/recovery/Preview Cache/nested/staging
inventories are zero; the user's installed Rain Explorer process was left untouched. App/Worker
Contracts are byte-identical at
`A2D7D6A173637B0B91E47E80625EF362FF5577290D440A5BE2B4227F1B8CAE4D`; Rain Explorer DLL is
`1F8F5612D4888B8177A8E040350DDC64A951102D80C6A16EE81BB1AA50DF4976`; Client DLL is
`C9FA54B215AC3B325AFC9B9D318669DCBD1E7C99A5F633605A1C8EC5EC14A9F5`; Worker DLL is
`5D565A9272D8CD5004A90D8456D9C7DCE1F60230BA777D8575CD85D276EBC0FE`; and no gated native
7-Zip asset is present. Direct integrated visual, keyboard-only, Narrator, High Contrast, DPI,
restart/stale-source, ordinary-folder, and clean-machine acceptance remains `manual_pending` and does
not transfer from Phase 5 or an earlier M6 checkpoint.

Independent M6-D verification passed the 11 focused nested cases and all 160 Rain Explorer tests with
zero skips, a zero-warning/zero-error Release build, no known vulnerable package in the current
direct/transitive advisory query, scoped formatter verification for every M6-D production/test C# file,
and no `git diff --check` error. The strict version-2 32-KiB location envelope retains version-1 root
compatibility and resolves each nested segment through exact parent identity plus normalized path and
duplicate ordinal with fresh runtime IDs. Root fingerprints compare exactly; nested temporary file IDs
and timestamps are ephemeral, so child comparison uses length plus prefix hash only under the already
validated immutable parent chain. Encrypted parent entries are refused before secret allocation until a
separate two-role prompt exists; child password retry is one-shot and zeroized, and persisted encrypted
children stop in fixed password-required state without fallback.

The real protocol-1.22 Worker flow opens three extension-independent nested levels from a
spaces-and-Unicode root, navigates/searches/previews, integrity-tests and extracts at nested depth,
hash-verifies output and the unchanged original, persists/reopens all three segments with fresh
sessions, then leaves zero Worker/testhost/fixture/recovery/Preview Cache/nested/staging residue. The
development output keeps App/Worker Contracts byte-identical at
`A2D7D6A173637B0B91E47E80625EF362FF5577290D440A5BE2B4227F1B8CAE4D`; Rain Explorer DLL is
`016D66A62113C80E36C4245F37276A96172CE9FE5C3703DA2655C2D137FF230C`; Client and Worker DLLs
remain `C9FA54B215AC3B325AFC9B9D318669DCBD1E7C99A5F633605A1C8EC5EC14A9F5` and
`5D565A9272D8CD5004A90D8456D9C7DCE1F60230BA777D8575CD85D276EBC0FE`; forbidden native 7-Zip
assets are absent; and the user's installed Rain Explorer process remained untouched.

The completed M6-E slice was bound to delayed-render **copy/drag out only** for current Rain Explorer
archive selections using `RainArchives.Contracts` plus `RainArchives.Client` and the already accepted
selected-extraction operation. Cut, move, link, paste/drag into an archive, generic export, Open With,
shell execution, overwrite/merge/retry, and archive mutation remain closed. A hover, mouse threshold,
context-menu open, clipboard query, or data-format probe must not extract anything. Only an explicit
copy command or a drop target's first authoritative data request may admit one exact transfer after
re-resolving the newest visible selection, session, fingerprint, capabilities, volume state, bounded
frame, and source identity. Re-entrant/concurrent render requests share one result and never start a
second extraction.

Transfer output must be a fresh random absent child under one fixed per-user local Rain Explorer
archive-transfer root. The Worker owns archive decoding, extraction path policy, resource checks,
staging, provenance, and no-replace publication; Rain Explorer never writes archive entry bytes or
constructs an entry-derived destination path. No file-drop path may be published until the Worker has
reached authoritative successful terminal state and the completed transfer root has passed a bounded
manifest/top-level-name/ordinary-object/reparse/link/identity review. The OLE/clipboard surface exposes
only the exact completed top-level transfer objects and copy effect. Internal recovery, Worker staging,
journal, cache, or source paths remain absent from UI, diagnostics, and data formats.

One transfer owner must cover operation ID, password slot, Activity entry, local root identity, rendered
data object, drag/clipboard lifetime, cancellation, and cleanup. Abandoned drag, clipboard replacement,
source change, Worker loss, failed render, navigation/tab/app close before handoff, and target refusal
cancel/drain the exact operation and remove only a fully revalidated operation-owned tree. Any unexpected
content, reparse, link-count/identity change, named stream, inaccessible object, or uncertain target
consumption makes cleanup fail closed and preserves evidence with fixed path-free status rather than
recursively deleting by name. Completed drag data must remain valid for the target's synchronous consume
window; clipboard ownership and app shutdown need an explicit bounded lifetime policy, not a timer-based
blind delete. Startup may inspect only this dedicated bounded root and must not acquire generic cleanup
authority.

M6-E automated acceptance requires deterministic non-OLE coordinator tests plus a Windows STA/OLE seam:
no render/no extraction; first-render-only admission; concurrent/re-entrant requests; exact selection and
duplicate/implicit-directory behavior; Unicode and unusual safe names; password success/refusal;
capability/downgrade/multipart/source-change refusal; cancellation before admission, during extraction,
after terminal but before publication, and on abandonment/app drain; copy-only effect; clipboard
replacement; target exception; destination race; manifest/object tamper preservation; exact Activity
state; one real Worker transfer with source/output hashes; and zero ordinary Worker/transfer/recovery/
cache/staging residue. If WPF/OLE cannot provide a provable delayed-render and ownership boundary, the
slice must remain capability-disabled and report the exact missing proof rather than ship eager temp
extraction or unsafe cleanup.

M6-E is accepted as a truthful capability-disabled platform checkpoint. Protocol 1.22 does not bind the
Worker's committed destination identity, and current WPF preserves lazy custom `IDataObject.GetData`
delegation but does not expose a provable `IDataObjectAsyncCapability`
start/in-operation/end-operation lifetime, so production `CanCopyArchiveSelection` remains false with
fixed text and starts zero extraction. The retained internal seam proves bounded serialized admission,
one first-render owner with re-entrant sharing, current session/fingerprint/selection/capability/volume
revalidation, authenticated Worker extraction, exact nested publication, aggregate object/byte/path
ceilings, two-pass pinned identity review, exact-handle cleanup, deterministic cancellation/password/
late-render/app-drain settlement, and preservation of uncertain evidence. Independent verification passed
65 focused transfer/OLE/adapter cases and all 192 Rain Explorer tests with zero failures/skips, a
zero-warning/zero-error Release build, no known vulnerable package in the current direct/transitive
advisory result, clean `git diff --check`, and clean scoped formatting for the new M6-E source. The
repository-wide formatter's unrelated historical debt remains outside this gate.

M6-F is accepted. Rain Explorer now queries protocol-1.22 Worker capabilities and creates only advertised
ZIP, TAR, TAR.GZ, TAR.BZ2, GZIP, and BZIP2 outputs at distinct absent destinations. Raw streams require one
ordinary file; directory-capable formats preserve an exact Worker-enumerated tree. Eligible current ZIP
sessions rebuild only a distinct full copy, with exact session/fingerprint/capability revalidation and an
unchanged original. Explorer writes no archive bytes, exposes no password/encryption/split/overwrite/
retry/continuation controls, and maps fixed typed progress plus cancellation into Rain Activity. The old
in-process ZIP writer and path-based partial-output deletion are gone. Independent verification passed the
47 focused cases and all 239 Rain Explorer tests with zero failures/skips, a zero-warning/zero-error
Release build, no current direct/transitive advisory, clean scoped formatting and `git diff --check`, and
the exact real-Worker file/tree/rebuild round trips. App/Worker Contracts remain byte-identical at
`A2D7D6A173637B0B91E47E80625EF362FF5577290D440A5BE2B4227F1B8CAE4D`; Rain Explorer DLL is
`E4AD234E66AF175BA3E37C3490B0704FCAA89EDED589867B1910F98581B6A823`; Client and Worker DLLs remain
`C9FA54B215AC3B325AFC9B9D318669DCBD1E7C99A5F633605A1C8EC5EC14A9F5` and
`5D565A9272D8CD5004A90D8456D9C7DCE1F60230BA777D8575CD85D276EBC0FE`. No gated native 7-Zip asset
is present, no actionable recovery or staging record remains, and the user's installed Rain Explorer
process was untouched.

M6 exit is accepted. Physical-selection `Extract here` now sends every bounded ordinary-file selection
through temporary authenticated Contracts/Client sessions. The physical context menu enables that command
for any bounded ordinary-file selection, so an extensionless file reaches Worker content detection; an
extension is neither required nor trusted. This change is scoped to `Extract here`: normal Open,
double-click, and Enter remain extension-hinted archive affordances. The Worker owns content detection,
listing, fingerprint/capability/volume validation, full extraction, traversal/collision/resource policy,
staging, no-replace commit, events, exact-ID cancellation, and terminal state. Up to 16 items run
sequentially in visible order, each with one deterministic absent sibling destination. Existing or raced
destinations are preserved; Explorer does not merge, overwrite, retry, delete partial output, or register
path-based Undo. Ordinary encrypted and header-encrypted ZIP/7z/RAR routes use one-shot secret slots with
immediate caller-buffer zeroization and no automatic wrong-password retry. Regression coverage proves that
the first and later sequential password callbacks return to the originating WPF dispatcher after
asynchronous Worker-like completion. Header-encrypted open uses the one password-opened session for
extraction, while wrong-password handling consumes one slot and never retries or extracts.

The legacy in-process `ZipFile`/SharpCompress extraction authority and Rain Explorer SharpCompress package
are gone. Production WPF references only Contracts and Client; source guards prevent decoder/writer
authority from returning. The final independent review found and closed one header-password-open
cancellation classification gap, then reported no remaining blocking finding. Focused verification passes
31/31 and the complete Rain Explorer suite passes 262/262 with zero skips. The Release build has zero
warnings/errors, locked-mode restore commands and current direct/transitive advisory checks succeed with no
known vulnerable package, cumulative formatter verification and `git diff --check` are clean, and exact
before/after recovery inventories plus process/temp scans show zero new scoped residue. Installed Rain
Explorer PID 26520 remained untouched.

The current local package is
`artifacts/RainExplorer.M6ExitIntegratedPackage.1.2.3.CB0DC84F`. Its 455-file / 250,820,035-byte app tree
has SHA-256 `038671F5F5EC4EF7B91241E29571874ADCC167C98549604335A7321C88D23285`; its private 205-file Worker
tree has SHA-256 `D1A666333CD9588F8C71C439F729BB66886CDED5805DD90E852A8A9055CE7F49`. App/Worker Contracts are
byte-identical at `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`; no SharpCompress is at the
app root, no Worker-only assembly is at the app root, and no gated native 7-Zip asset is present. Ten
package-bound flows use a byte-identical RID RainExplorer assembly and this exact private Worker for managed
create/rebuild, open/browse/preview/integrity/selected extraction, nested restore, physical ZIP/7z/RAR/
TAR.GZ/GZIP/BZIP2 extraction, encryption, refusal, recovery-inventory, source-hash, residue, and process
checks. `package-manifest.json` retains all identities and flags. The earlier
`artifacts/RainExplorer.M6ExitIntegratedPackage.1.2.3.C243D42E` package remains untouched as superseded
pre-remediation evidence.

M7-B now supersedes this no-active-slice statement only for the explicitly authorized, owned, per-user
archive-default registration, archive activation/history, pathless file-type icons, and archive-association
icon surface described above. Direct association/icon-cache refresh, reversal, WinRAR-context-menu, GUI,
keyboard-only, Narrator, Light/High Contrast, DPI, ordinary-folder, physical network-filesystem, and
clean-machine acceptance remains `manual_pending` and is not inferred from automation.

Do not begin additional Phase-7 verbs, localization/touch expansion, installer/update publication, signing,
or release work without fresh explicit authorization. Do not change Contracts,
Client/Worker/Security/Core/providers, protocol 1.22, capability/mutation authority, settings schemas,
filesystem/recovery semantics, originals, or no-overwrite rules merely to simplify Explorer
integration. Do not transfer M5F performance observations, M5G-A visual observations, or any older
manual result to `651729F4` or a future Rain Explorer package.

The remaining exact-M5G-D-package visual, Narrator, keyboard-only, real DWM/actual High Contrast,
100/150/200%, mixed-DPI, and clean-machine gates plus Milestone 4 physical network/filesystem gates are
separate `manual_pending` work. Automated resource/template/runtime-state checks and same-task GUI
inspection close none of those direct visual/assistive/device gates. M5G-C remains immutable historical
identity evidence, and `B11E7A80` remains immutable superseded M5G-D evidence; neither replaces this exact
package for the remaining Phase-5 release acceptance. Those manual gates do not transfer to M6-A and
must be repeated against any future integrated package.

### Completed checkpoint — M5F existing-command context and measured package interaction

One bounded `ArchiveCommandContext` canonicalizes only the newest validated session, state, capability,
visible page, and selection. Window accelerators, Enter/double-click, and the row menu invoke only
existing navigation, properties, safe preview, integrity, and selected extraction commands through
Contracts/Client. Duplicate current identities fail closed; stale, substituted, missing, offline,
downgraded, or busy state grants no action. Every handler re-resolves the canonical current row or
selection before use. Fixed labels/help/live status expose no archive/provider/path/secret/recovery
text, and no Open With, export/launch, shell, mutation, overwrite/replacement, retry, restore, generic
cleanup, provider-specific command, protocol change, or new Worker/Security authority exists.

The inspector remediation starts the Properties/Safe Preview surface collapsed, reveals only the
explicitly requested authorized tab, and supplies one fixed keyboard/Automation-described Close
control. Close invalidates preview generation, cancels active/pending work, waits for the bounded pump,
then collapses and restores entry-list/selection focus; late completion cannot repaint it. A window-
global Escape binding remains absent.

The complete remediated gate passes 505 unit/WPF, 29 adversarial, and 354 integration tests. Focused
evidence covers command authority and negatives, initial collapse, explicit open/close/reopen, close
during/loading/after preview, late completion, focus restoration, STA XAML/accessibility wiring, one
real-Worker flow, immutable predecessor flow, fresh exact-package remediation flow, and retained
large-list/process measurement. Package and measurement identities are recorded in the current
checkpoint. The earlier Daybreak-max scan remains bound to 94811C7A; a finite direct remediation review
found no issue but is not independent acceptance or manual desktop qualification.

### Completed checkpoint — M5E Recovery Center and support evidence

One production modal now owns four bounded Contracts/Client-only models. Creation keeps its four
exact-evidence disposition choices and default-No confirmation. Nested workspaces keep only
fresh-evidence discard and copy-only independent depth-zero reopen; the reopened workspace remains
preserved. Extraction staging is explicit read-only evidence with mutation/retry/cleanup capabilities
required false. Fixed support evidence maps only protocol-1.22 enums, integers, and booleans. No
recovery/temp/cache path, ID-derived path, provider/exception text, archive text, secret, command line,
generic cleanup/delete, retry, restore, replacement, or new authority enters the surface.

Refresh, selection changes, malformed/downgraded/offline state, cancellation, failures, and uncertain
completion invalidate presentation authority. Destination text appears only after explicit typed
inspection and is escaped. A recovered source is prepared as a fresh session with a fixed title; an
unclaimed or failed adoption closes only that exact session. Dialog close cancels and joins the
top-level initialization task and all four child owners. Production XAML uses system colors, access
keys, ordered focus, Automation names/help/live status, DPI-safe layout, and recycling virtualization.

The complete gate passes 494 unit/WPF, 29 adversarial, and 350 integration tests. Focused evidence
covers the recovery/STA surface plus deterministic secret-slot and session-ownership race cases, one
real-Worker unified flow, five exact-PID crash/restart cases, and one exact-package product flow. The
package binds every source/tree/principal identity, excludes rejected
native assets, drives all three recovery families plus diagnostics, preserves original hashes, and
leaves zero scoped residue. A finite same-task read-only security/reliability/accessibility/lifecycle
review found zero blocking or non-blocking findings; it is not independent acceptance or manual
accessibility evidence.

### Completed checkpoint — M5D archive/entry properties and safe preview

One production session inspector now projects only validated descriptor and capability fields. It
keeps stable browser selection/focus, clears properties across stale/offline/closed authority, refuses
malformed enum/size/key/capability/volume shapes before replacing prior state, uses checked duplicate
arithmetic, and omits values the contract does not prove. Provider IDs and method text, link targets,
raw integrity values, fingerprints, exception/provider text, secrets, recovery/cache paths, handles,
command lines, and mutation controls are absent.

The safe-preview model accepts only typed text/binary/oversize/unsupported outcomes from the bounded
preview service. Strict UTF-8 and BOM-proven UTF-16/UTF-32 are the only disclosed decodings. Binary,
NUL-bearing, invalid, ambiguous, and control-dense content remains inert; active content, embedded
objects/macros, shell/Open With, export, and external launch are closed. One active plus one newest
pending request, generation-bound adoption, cancellation/drain on selection/source/close, 1 MiB plus
one detection byte, four App materializations, and delete-on-close cache ownership remain explicit.

The complete gate passes 475 unit/WPF, 29 adversarial, and 345 integration tests. Focused repeats cover
41 model/STA cases and two real-Worker/exact-package flows. The exact package verifies fixed property
projection, UTF-8 text, binary/NUL, oversize, source hashes, cache cleanup, and Worker/staging cleanup.
A separate finite same-task read-only source review found zero blocking or non-blocking findings in the
bounded M5D scope; it is not independent acceptance or manual accessibility evidence.

### Completed checkpoint — M5C settings/presets and managed create/rebuild UX

The production Settings and Presets center is App-private and Contracts/Client-only. Its version-1
32-KiB settings document contains only one managed-format enum, seven compression-preset enums, and
one boolean; unknown, missing, duplicate, malformed, invalid-Unicode, invalid-enum, truncated,
oversized, or future-version documents fail closed. A validated backup may supply explicit
read-only last-known-good state without rewriting the primary; ordinary Save remains disabled until
explicit Reset. The existing exact-128-KiB/64-user ZIP preset store retains its NFC/scalar/UTF-8/
control/metadata/name-uniqueness bounds and now applies the same strict JSON-member semantics.

Both stores use the private LocalAppData chain, per-user serialization, ordinary no-reparse/one-link/
no-ADS object checks, fresh random `CreateNew` staging, durable flush, atomic replacement/backup, and
exact-identity cleanup. Save/reset truthfully report independent-document partial failure; corrupt or
unsafe state requires explicit reset. Ordinary preset Save validates the existing state again while
holding the store mutex; Create and Rebuild carry and recheck the exact state before every Save/Remove,
leave built-ins available, and cannot replace malformed, future, oversized, unavailable, or unsafe
bytes/objects. Capability refresh is serialized and generation-bound; close and App shutdown cancel
and drain settings work and UI callbacks.

One capability-driven creation dialog now covers ZIP, TAR, TAR.GZ, TAR.BZ2, GZIP, and BZIP2 with
format-specific presets, limits, directory support, raw-stream one-file semantics, and an explicit
final review. ZIP Rebuild Copy consumes the saved hint only after current-session capability
revalidation. Destinations must be distinct and absent; existing/raced objects are preserved and
sources/original archives remain untouched. No prohibited encryption/volume/overwrite/retry/in-place/
native/RAR control was added. System colors, access keys, focus order, automation names/help/live
status, bounded scrolling, and recycling virtualization are covered by STA tests without claiming
manual acceptance. The complete gate passes 456 unit/WPF, 29 adversarial, and 343 integration tests;
the exact M5C package flow covers settings, preset, fresh-capability create/rebuild, Rain/external
reopen, hashes, and zero residue.

### Completed checkpoint — M5B archive browser and navigation UX

The App now owns one reusable Contracts/Client-only archive browser model. It validates the bounded
complete protocol page sequence, entry metadata, unique IDs, parent graph, and negotiated persistent
keys before adoption. Root/folder navigation, breadcrumbs, Back/Forward/Up branching, ordinal Unicode
case-insensitive current-folder search, deterministic duplicate ordering/occurrence, selection, and
capability state are explicit. The complete index is capped at 500,000 entries, a page materializes at
most 512 rows, and navigation history retains only the newest 256 locations.

Reopen restoration requires persistent keys on both sessions and exact canonical source path,
length, last-write time, file identity, and prefix SHA-256 equality. Failure before old-session close
preserves the prior exact session; once authoritative close begins, any failed/nonauthoritative
replacement clears the old browser authority and closes the replacement. Changed, stale, downgrade,
offline, unsupported, malformed, and error states do not restore. No provider ID, provider/exception
text, temporary path, recovery ID, secret, filesystem path invented from an entry, or mutation
authority enters the browser surface.

Production XAML uses system-color resources, layout rounding, responsive wrapping, recycling row and
column virtualization, bounded paging, keyboard accelerators, deterministic focus restoration,
automation names/help, and live status. Unit/STA coverage proves navigation, branching, duplicate
keys, search/selection, stale-source invalidation, bounds, accessibility bindings, and 50,000-entry
materialization; real Worker and exact-package flows prove browse/preview/reopen plus existing
operations and Activity Center. The locked restore, vulnerability inventory, formatter, zero-warning
Release build, 429 unit/WPF, 29 adversarial, and 341 integration tests pass with zero failures/skips.
The scoped security scan found zero findings at its immutable pre-history-cap snapshot. Independent
verification then found one recursive App-side parent-walk stack-exhaustion blocker; the remediated
iterative O(n) validator and its exact-depth/deep-chain/large-cycle prior-state regressions passed a
separate security-fix review, complete gates, and independent acceptance. Manual visual and
assistive-technology acceptance remains pending for every later package.

### Completed checkpoint — M5A production Activity Center and lifecycle UX foundation

The WPF App now owns one connection-lifetime Activity Center model and one modeless window over
Contracts/Client only. Extraction, integrity, creation, and rebuild starts are named and tracked in a
single presentation. Validated event details provide escaped live current-item text; path-free
snapshots provide authoritative phase/progress/timestamps/counts; only typed error category is shown.
Warning/error messages, provider exception text, paths, recovery IDs, and secrets are never retained
or derived. Fixed-field protocol-1.22 diagnostics report policy and containment without recovery or
mutation authority.

One non-overlapping sequential refresh loop runs at a bounded interval, coalesces refresh signals and
UI work, rejects stale generations, and stops on downgrade/offline state. The model holds at most 96
rows and never blocks Client event receipt on dispatcher work. Closing the modeless window removes
only that presentation; the client-scoped model continues to authorize the main-window Cancel action.
App/client close cancels and drains the loop and queued UI callbacks deterministically. Cancel is hidden unless the latest snapshot says
`CanCancel`; pending and terminal state suppress stale authorization. Pause, retry, reorder, decision
continuations, durable queue, and restart persistence remain visibly unavailable.

Automated coverage includes five direct model cases, STA production-XAML/binding/accessibility checks,
malformed/downgrade/offline refusal, non-overlap/stale-response/disposal behavior, real Worker queued
cancellation with original-hash/residue proof, and packaged creation/integrity/extraction/rebuild
flows through the production model. The locked restore, vulnerability inventory, formatter,
zero-warning Release build, 412 unit/WPF, 29 adversarial, and 339 integration tests all pass with zero
failures/skips. A separate read-only security/reliability/accessibility pass found no source finding;
manual visual and assistive-technology acceptance remains pending.

### Completed checkpoint — M4-D lifecycle, diagnostics, containment, and convergence

Protocol 1.22 adds fixed-field `worker-support-diagnostics-v1` after the operation lifecycle and cleanup
policies were re-audited. Admission is 32, execution is one, terminal retention is 64, ordering is
unspecified, and cancel is the only supported control. A 33rd start is refused with secret settlement;
terminal cancellation is refused under the lifecycle lock. Pre-response start cancellation uses
bounded teardown of only the authenticated Client-owned Worker; post-response cancellation targets
the returned operation ID. Awaiting decision enum values remain forward-compatible validation only,
and no Worker emits them. Retry, pause, reorder, restart persistence, automatic reconciliation, and
generic cleanup remain unsupported.

Diagnostics disclose only fixed enums/numbers/booleans and explicitly report that the same-user
Worker is not a filesystem sandbox. The containment decision retains the unelevated exact-PID Worker
plus kill-on-close Job Object because a compatible AppContainer/brokered-handle deployment contract
is not proven. Cleanup convergence is bounded per owned artifact class; active, changed, malformed,
identity-unavailable, excessive, inaccessible, partial, or ambiguous state is preserved. Additive
minor-22 golden/downgrade/dependency/malformed tests, retained 1.18 binaries, the full 773-test gate,
zero-warning build, package flow, residue audit, and separate immutable security scan pass.

### Completed checkpoint — M4-C independent nested recovery reopen

Protocol 1.21 adds exact-evidence copy-only reopen for one source in a freshly inspected inactive
protocol-1.11 workspace. Security binds workspace/source ID, revision, count, aggregate bytes, source
length, stable identities, exact ordinary shape, one-link/no-reparse/no-ADS state, liveness, depth,
ancestry, and current content before copying through pinned handles. The Worker flushes a new owned
copy, redetects its format, and registers a fresh independent root at depth zero with zero ancestry
chain bytes. No temporary path crosses IPC and the original workspace remains byte-identical.

Nested discard deliberately retains no durable intent/completion receipt: cancellation is ignored
after its first exact delete, and any partial, smaller, missing, or ambiguous restart state is
preserved/not-found rather than inferred as success. Independent reopen is safe without a mutation
intent because it performs no recovery mutation. Exact-PID termination after the reopened copy is
flushed proves unchanged original evidence/hash, path-free restart warning, preserved copied source,
and successful fresh inspect/reopen/list at depth zero. Minor-20 downgrade, feature dependencies,
golden frames, unknown/missing/oversize fields, malformed responses, and retained-binary refusal pass.

### Completed checkpoint — M4-B extraction-staging inventory and read-only inspection

Protocol 1.20 adds only `extraction-staging-recovery-inspection-v1`, dependent on new-destination
extraction, source fingerprint validation, and request cancellation. Tracked extraction records five
durably flushed checksummed phases before and around the no-replace commit. Startup examines only the
bounded local journal root and returns path-free opaque summaries; it never contacts a recorded
destination or deletes unresolved state. Explicit inspection reparses one journal, pins destination
ancestry and direct artifacts without following reparses, validates exact-case ordinary shape,
identity, hard links, ADS, bounded counts/bytes/depth, and a content-hashed tree revision, then
returns a conservative read-only assessment with mutation/retry/cleanup flags fixed false.

Ordinary cancellation/failure removes only exact operation-owned staging and journal state. A
successfully delivered terminal removes its journal, while transport loss after publication and
every active, torn, changed, excessive, inaccessible, malformed, or ambiguous record remain for
review. Minor 19 refuses both new requests before send/deserialization/access; retained protocol
1.18 binaries keep prior operations and refuse the newer feature locally. Real Worker/Client tests
cover path-free listing, explicit inspection, bounded cancellation/owned-worker teardown, terminal
cleanup, exact-PID crash/preservation/restart inspection, and the immutable M3 package flow. This
slice grants no disposition, automatic retry, cleanup, restore, replacement, or WPF authority.
Actual second-machine SMB, blocked-UNC, DFS, no-file-ID, and non-ADS filesystem behavior remain
`manual_pending` rather than being inferred from local NTFS evidence.

The final remediation gate additionally proves that a torn final journal cannot become actionable,
unknown journal JSON and zero native identities fail closed, unexpected ordinary files/hard links/
ADS preserve both staging and journal state, and allowed provenance-stream bytes and content hashes
participate in the bounded tree evidence. Exact request/response JSON, unknown/missing-member and
oversize rejection, a mixed valid-plus-malformed local inventory, and malicious Client-response
validation are covered. Two separate read-only re-audits found no remaining M4-B protocol or
recovery-security blocker after those fixes.

### Completed checkpoint — M4-A bounded operation activity

Protocol 1.19 adds only `operation-activity-v1` over the existing authenticated event and request-
cancellation features. One query returns a path-free connection-lifetime snapshot with an exact
32-active/queued, one-concurrent, newest-64-terminal policy. The Worker records only successfully
sent event state; the Client validates timestamps, lifecycle, ordering, progress, unique IDs, and
each individual bound, aborting its owned transport on malformed output. Pause, retry, reordering,
and restart persistence are explicitly false. Minor 18 refuses before send/deserialization/access.
The retained 1.18 source/binary artifact and 17-case compatibility class preserve every prior managed
creation route in both directions. This is the queue/activity foundation, not queue controls, durable
replay, an App Activity Center, or Milestone 4 completion.

### Completed finite phase — Milestone 3 managed creation completion

The user explicitly authorized the remaining autonomously implementable Milestone 3 production,
test, compatibility, conformance, documentation, and local self-contained-package work on
2026-08-25. That finite phase completed on 2026-08-26. The authorization superseded the former manual-only next action, but did not relax
any architecture, dependency, security, recovery, evidence, or publication boundary in this file.
The verified starting checkpoint was protocol 1.16, 128 canonical production inputs at
`8E6E9D171C82841E804C03285D86B119122A61A339F6D00331054BE916AC6258`, and 599 automated tests
(307 unit/WPF, 29 adversarial, and 263 integration) with zero failures or skips.

The following finite manifest is closed. Managed items were implemented through every applicable
layer and policy-gated items were closed with negative capability/protocol/UI/CLI evidence rather
than fabricated support.

1. **Neutral single-output transaction and TAR.** Add an additive protocol feature and typed
   capability/request surface without changing any ZIP wire shape. Implement uncompressed TAR with
   provider-owned archive semantics and Security-owned source planning, pinned non-reparse leases,
   guarded sibling staging, durable five-phase recovery, complete staged readback, source/provenance
   revalidation, and handle-relative no-replace publication. Qualify regular files, explicit and
   empty directories, Unicode and zero-byte content, truthful TAR timestamps, cancellation, resource
   limits, mutation/race/failure/recovery paths, Rain reopen/test/extract hashes, and an owned pinned
   external implementation.
2. **Managed compressed TAR.** Add TAR.GZ and TAR.BZ2 over the qualified TAR foundation, explicitly
   finalize and validate each outer stream/footer, and repeat the changed-output security,
   conformance, compatibility, cleanup, and original-preservation gates.
3. **Managed raw streams.** Add distinct GZIP and BZIP2 single-member creation semantics: exactly one
   ordinary regular source file, no directory or multi-source interpretation, an explicit truthful
   synthetic member-name rule, and the same guarded transaction/readback/no-replace guarantees.
   Carry the capability, Client, CLI, and WPF-model routes through the real packaged Worker.
4. **Capability and preset parity.** Make supported managed formats and presets discoverable without
   hard-coded UI assumptions while keeping WPF limited to Contracts and Client. Preference storage
   must remain App-owned and may never contain secrets, source/destination paths, recovery IDs, or
   untrusted archive text.
5. **Closed capability decisions.** Preserve and regression-test the absence of unsafe write routes:
   ZIP content/name encryption remains disabled unless an approved managed dependency passes its
   security, license, package, interoperability, cancellation, and maintenance gates; TAR.XZ and raw
   XZ remain disabled without an approved compressor; 7z creation remains behind the unpassed native
   provider gate; RAR creation remains forbidden absent a newly verified official encoder licence
   and explicit product-owner approval; split output remains disabled without a bounded multi-output
   publication journal and recovery contract; and existing-destination replacement, overwrite, and
   in-place update remain explicit refusals. The qualified transaction therefore preserves an
   existing or raced destination and never needs rollback of an original it cannot mutate.
6. **Final qualification.** Run focused and complete automated gates after every production slice,
   retain binary/golden/downgrade evidence for each protocol change, run external conformance for
   every changed output, perform a separate read-only review, regenerate the canonical fingerprint,
   create and verify a current-source local self-contained package, audit cleanup/original hashes,
   and update every affected canonical document with exact evidence and limitations.

Final Milestone 3 automated evidence at phase exit: protocol 1.18; 132 production inputs at
`1C85F6D929DE5F940E1357C72C210A8647DFD2E9F38A0A2B0B0832127F5CE844`; 358 unit/WPF, 29
adversarial, and 293 integration tests (680 total), zero failures/skips; locked restore; fresh
no-known-vulnerability inventory; formatter; zero-warning Release build; Rain/external conformance;
14/14 current/retained compatibility boundaries; independent read-only review; and verified package
`artifacts/RainArchives.M3AutomatedPackage.1.18.1C85F6D9`. The earlier physical probes required
authoritative aggregate-error unwrapping for disk-full, RFC-1952 zero-MTIME-as-unknown handling for raw
GZIP on exFAT, and truthful format-neutral WPF creation labels. Final full-suite work then required
Worker secret settlement before extraction/integrity terminal publication and removal of a redundant
post-response Client discard that could turn successful Open cancellation into transport abort. The
focused lifecycle regressions passed ten and 30 rebuilt repeats respectively before the 293-test rerun.

### Retained Milestone 3 performance and package-bound physical tranche

The current `1C85F6D9` source ZIP operation probe and exact-package mixed-format probe retain 40 and 75
measured samples respectively; all 140 combined measured/warm-up/cancellation outputs pass the hash-
pinned official 7-Zip path and cleanup gates. Their JSON SHA-256 values are
`54F65B108FBA5F1336D5F8113D618C7C31CF6DA31F2D1E711BD6E6EF2A63CDA6` and
`31B8A5740FA54069AE476682D4C03E951BF26C5554A1E061F2EF9B2163225C73`.

The exact `1C85F6D9` package passes fresh offline self-contained Windows Sandbox startup/all-six-format
creation, non-elevated ACL denial, early/near-final actual Win32-112 NTFS exhaustion with explicit
recovery, all 15 exFAT preset/provenance cases, and real local 8.3/`subst`/alternate-loopback-UNC
probes. Evidence SHA-256 values are
`505B6504A5D67E9D114F6E1121FE547D860ACD07B0B56BCF419595E9FA4CC2A4`,
`9CFEC377120A3C1194EB1B72215062612C01475FB8C6BDB272A6E10A69B85924`, and
`8D1DAD376D864FBC7A3B1E760ED4DAA6576EDFA2C2B74E20E372808E01644852` respectively. The product
owner now supplies direct exact-package ZIP create/reopen/test and Rebuild Copy observation; predecessor
BC still supplies the only both-empty-Recovery-Center-tabs flow. The first controller attempt failed before
input delivery at SHA-256
`8C09561E7E470ED03F0E7C93FF0635F9DAE87EF494AF3D4B29FF412531A72CF5`. After full host access was
enabled, the second attempt opened the creation/native-picker surfaces and admitted a Unicode source,
but owned-modal text input still did not route; no operation started and cleanup was exact. Its
evidence SHA-256 is
`967C4CDD630D3498FE67EF2DA9E293ED02A24CC9A9D09029AC244AA6B60A91BC`. Exact evidence and limitations are in
[docs/milestone-3-manual-windows.md](docs/milestone-3-manual-windows.md) and
[docs/benchmarks.md](docs/benchmarks.md).

A third bounded direct-App retry moved the exact packaged main window fully onto positive primary-
display coordinates before opening the creation dialog. The owned modal was also fully on that
display; `DestinationInput` had accessibility focus and a visible caret. Two literal controller text
attempts, including an explicit refocus between them, left the destination blank. The dialog and App
closed through ordinary controls; no operation/output/staging or process residue remained. This rules
out negative/cross-display root or modal placement while preserving the exact blocker classification
`manual_pending_environment_owned_modal_text_input`. The evidence is 3,159 bytes at SHA-256
`849B5C8003D6BF30A8AC8A0DCF60D2752CB03EC2C7D33779A2A360131D42639E` in
[the third controller attempt](artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/wpf-direct-v3/gui-controller-attempt.json).

The product owner then completed the previously blocked exact-package interaction directly. Submitted
hashes matched the package manifest, App executable, and checked `plain.zip`; the App created and
reopened `manual-created.zip`, listed the nested 513-byte source, and completed integrity testing.
Rebuild Copy completed with the expected representable-metadata warning, and a post-close process
query returned no App/Worker rows. The two identical 385-byte manual outputs are preserved outside
the checked corpus at SHA-256
`C7047635B4E94697E5FFAFB72F2D2C0D6796C609EE0981065A47C990015BFE0C`. Exact screenshots and the
bounded statement are retained in
`artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/product-owner-gui-v1`; its evidence JSON
is 3,694 bytes at SHA-256
`A18B5DB00EF01A310B91E2E37FA41BE12CAD1952B8D00B96BDA8B0E6AC22092D`.
The submitted staging command used the literal placeholder `YOUR-DISPOSABLE-WORK-FOLDER`, so it
proves no residue state. No rebuilt-output manifest/hash, Recovery Center observation, accessibility,
remote/device, installation, signing, or publication claim is inferred.

The exact package also passes explicitly addressed installed GNU, WinRAR, and distinct libarchive
suites: GNU tar 1.35 lists/extracts TAR/TAR.GZ/TAR.BZ2; gzip 1.14/bzip2 1.0.8 integrity-test/
decompress the raw formats; WinRAR 7.20 integrity-tests/extracts all six; and `bsdtar`/libarchive
3.8.9 lists/extracts four simple containers plus three long-Unicode/empty-directory/zero-byte/
fractional-time PAX package layouts. Together with pinned official 7-Zip and Windows libarchive 3.8.4,
this closes the bounded M3 external-reader matrix. Every payload has its expected SHA-256; archives
remain immutable; all 36 exact launched external process IDs settle; Rain residue is zero. The public
SharpCompress descriptor rounds the exact staged PAX time upward by one 100 ns tick, which remains an
explicit read-metadata limitation. BCL PAX helper names include the Worker PID, so exact container
bytes differ across processes without changing a material entry. Evidence SHA-256 is
`8CAB9E6FA6AC7C169A14F0DE93CD3D0E4D9C7871A51AA6749EF0BC6AFEE1D64D`. A fixed-timestamp repeat
confirms deterministic material paths/content/metadata but not byte-reproducible PAX containers:
BCL helper header names include the Worker PID. Open-ended tool/version/
method/layout breadth remains Milestone 8 release hardening.

### Remaining Milestone 3 manual/remote/release acceptance

Direct exact-package ZIP create/reopen/integrity and Rebuild Copy observation is now complete as
bounded above. Direct Recovery Center state plus product-owner Narrator/keyboard-only/High-Contrast/
mixed-DPI observation of the current creation, rebuild, and recovery flows; physical blocked-
UNC cancellation; actual second-machine SMB creation source/destination/interruption/provenance/
recovery; DFS and no-file-ID filesystem behavior; any physical kill point later found without an
already qualified safe checkpoint; installation; signing; and publication remain
`manual_pending — autonomous managed implementation plus current automated,
performance, clean-machine, storage, and local-alias qualification complete; final product acceptance
remains open`. The current environment has no remote SMB mapping, the known
remote endpoint is unreachable, and it exposes no owned DFS namespace or no-file-ID filesystem.
Installation, signing, and publication require separate task-specific authority and actual release
inputs. A read-only availability inventory records zero SMB mappings/connections/network drives, no
owned DFS/no-file-ID surface, and an untouched warning-health FAT32 removable user volume; its SHA-256
is `F6F9DF62B678AD17C19A7E01C95D4DBD68DA4F1F1A45072E23A36AFFAF328DB1`. These gates must not be
fabricated or transferred from an older package. The earlier controller-specific destination-input
blocker is superseded by the product owner's successful direct creation/rebuild observation, but the
unsubmitted staging inventory, rebuilt-output manifest/hash, Recovery Center, accessibility, and
physical-device cases remain open rather than being inferred. Independent future
work may reopen a closed encryption/XZ/native-7z/split/replacement gate only with the exact new
authority and evidence prerequisites recorded above. Until then, Milestone 3 remains partial at the product exit gate
even though no autonomously implementable managed creation item remains.

### Completed autonomous phase record — M3-A Managed ZIP Rebuild Completion

**Complete the whole finite M3-A manifest below from the verified protocol-1.12 checkpoint. Do not
stop after protocol 1.13. This phase finishes the safe managed-only ZIP creation/rebuild surface for
eligible single-volume unencrypted ZIP archives under the existing distinct-new-destination policy.
It does not complete the much broader Milestone 3.**

This is a goal-scale production implementation, not another planning or verification-only task.
Each slice must change runnable production behavior across every affected layer, add or materially
expand automated evidence, and pass its own gate. After a slice passes, record its protocol, test
counts, fingerprint, evidence class, and remaining limitations in this document and immediately
continue to the next numbered slice. A final response is appropriate only after all four slices and
the phase exit gate pass, or after a genuine blocker listed below prevents further safe work.

The user has authorized a long unattended implementation run for this bounded phase. Noninteractive
build/test/benchmark processes, existing pinned conformance tools, local disposable fixtures, local
self-contained package creation, and read-only delegated reviews are in scope. Use `gpt-5.6-luna`
for subagents, keep their tasks bounded, and independently review their findings or patches before
integration. Default unattended fixtures must remain at or below 512 MiB each and 4 GiB aggregate
task-owned temporary data; one benchmark command may run for at most 30 minutes and must retain the
existing Worker process/resource limits. Opt-in multi-gigabyte probes, sustained fuzzing, true disk-
full, physical-network, destructive filesystem, and interactive-GUI tests are excluded unless the
user separately names them. Cleanup may target only resolved test-owned roots and exact test-owned
PIDs. Do not install software, initialize Git, commit, publish, sign, register, or open an interactive
GUI without separate authorization.

### Binding phase-wide behavior

1. Every rebuild/edit operation continues to use one already-open eligible ZIP session with the
   exact Worker-held source lease, fingerprint, volume identity, entry snapshot, and persistent
   keys. Encrypted, multipart/unknown, duplicate, unsafe, link, special, or incompletely described
   archives remain non-writable. Capability responses are advisory snapshots: only client-owned open
   sessions may be queried, foreign/closed IDs are refused, and every write revalidates exact source
   identity and eligibility.
2. Every edit resolves into one complete final namespace before staging. Rain writes a distinct
   previously absent ZIP; the original is never renamed, overwritten, deleted, opened as output, or
   used as staging. Existing-destination replacement and in-place update remain hard refusals.
3. Retained bytes continue through the checksum-validating provider path. Mapped source files and
   trees remain held by stable non-reparse identity leases and exact manifests. No whole-archive or
   whole-tree extraction may be used as an edit shortcut.
4. Preserve the global 4,096-material-entry and 64-GiB decoded/new-source budget, actual-byte and
   expansion-ratio enforcement, checked arithmetic, output/free-space checks, sanitized progress,
   cancellation, durable journal, guarded sibling staging, complete staged-manifest readback, final
   source revalidation, and handle-relative no-replace commit.
5. Every wire change is additive. Preserve every older request/response meaning and byte shape,
   update the protocol table and feature dependencies, reject unknown members, enforce the 1-MiB
   frame before writes/access, and never reuse an older message kind for new semantics. Freeze golden
   request/response/error frames for 1.12 through the new current minor. Before changing 1.12 source,
   retain a hash-identified protocol harness containing its exact Client/Worker binaries; retain each
   later slice checkpoint likewise. At every minor, test current Client against each retained 1.12-
   through-previous Worker and each retained Client against current Worker: common negotiated
   operations must work, and newer operations must refuse before frame/payload/filesystem access.
   Same-binary downgrade and golden-frame evidence must remain separately identified. If an exact
   historical binary genuinely cannot be retained or executed, record `binary_compat_pending` and
   do not claim actual N/N-1 binary compatibility from same-binary tests.
6. Contracts/Core/provider/Security/Worker/Client/CLI/WPF dependency rules remain binding. WPF keeps
   only Contracts and Client references and contains no codec, source-enumeration, staging, path-
   authority, or recovery logic.
7. A failing invariant, unexplained original hash change, uncertain publication result, unreviewed
   in-scope high/critical finding, or test that would need weakening/skipping is a phase blocker—not
   a reason to reduce protection or relabel evidence. Every validated in-scope finding requires an
   explicit disposition. A medium finding affecting an invariant, data integrity, protocol
   authorization, rollback/recovery, or secret handling also blocks; other medium/low findings may
   be deferred only with a written rationale, reproducible evidence where practical, and exact next
   ownership.

### Slice 1 — protocol 1.13 `zip-rebuild-rename-v1`

Implement retained regular-file rename/move through Contracts, the closed planner/provider seams,
Worker, Client, `rebuild-zip`, and WPF while keeping protocol 1.12 behavior unchanged.

1. Add a distinct minor-13 request/message/feature depending on `zip-rebuild-copy-v1` and all of its
   cancellation, persistent-key, volume-identity, and ZIP-creation dependencies. Minor-12 clients
   refuse locally without sending a frame; downgraded Workers refuse before deserialization or any
   source/destination access.
2. Each rename identifies one retained regular file by exact normalized persistent path plus
   duplicate ordinal and supplies one validated final archive-relative file path. One key may be
   renamed, deleted, or replaced, never more than one. Simultaneous swaps are allowed only when
   source keys are unique and the final namespace is unambiguous.
3. Resolve delete/add/replace/rename together. Reject traversal, rooted/UNC/device/drive-relative/
   ADS/reserved/trailing-dot-space names, case/NFC aliases, duplicates, depth/component/total-path
   excess, file-directory prefix collisions, and directory targets before staging.
4. Preserve representable timestamp/basic-attribute metadata, include final paths in escaped progress,
   and validate the exact renamed manifest after writer finalization.
5. Add explicit repeatable CLI rename arguments without changing old no-rename invocations, plus a
   WPF review surface that clearly distinguishes Rename from Replace and keeps invalid edits as
   inline validation results.

Slice-1 exit requires planner/adversarial/protocol tests; real Worker/Client/CLI/WPF-model mixed
rename/delete/add/replace round trips; cancellation/race/crash/low-space/original-hash evidence; and
official 7-Zip Extra 26.02 test/extraction across Store/Fast/Balanced/Maximum with exact manifests.

#### Slice 1 checkpoint — completed 2026-08-25

- Protocol 1.13 and `zip-rebuild-rename-v1` passed the complete Slice 1 gate: 263 unit/WPF, 29
  adversarial, and 217 integration checks, 509 total, with zero failures/skips; formatter and
  zero-warning Release build passed after locked restore and a fresh no-known-vulnerability inventory.
- Canonical production fingerprint: 121 inputs,
  `4A27632C1A32086663B972414A1F859BB40C9EBBAFEF53CB188AFA53B53FAD41`.
- Official 7-Zip Extra 26.02 tested/extracted exact mixed renamed output for every preset. Original
  hashes, failure cleanup, cancellation, race, low-space, crash/recovery, same-worker health, and
  authoritative Rain reopen/list/test/extract passed.
- Retained exact checkpoint: `artifacts/RainArchives.ProtocolCompatibility.1.13.4A27632C`; its
  self-contained Client harness passed common operations against its exact Worker. Manual GUI,
  accessibility, clean-machine, physical storage/SMB, signing, installation, and publication remain
  pending and do not block Slice 2.

### Slice 2 — protocol 1.14 `zip-rebuild-tree-edit-v1`

Add bounded directory-tree editing so Rebuild Copy can add a source folder and move or delete one
validated retained archive subtree without flattening it or enumerating through WPF.

1. Add a distinct minor-14 request/message/feature depending on minor 13 and
   `zip-create-directories-v1`. Preserve all minor-12 and minor-13 request shapes and no-tree routes.
2. Add a bounded read-only tree-plan query. For each non-root directory node it returns the normalized
   path, whether an explicit directory entry exists, bounded descendant/material counts, and an opaque
   lowercase SHA-256 subtree revision computed from the source fingerprint, node kind/path, and
   ordinal-sorted descendant kind/path/persistent-key membership. A subtree action must echo the path
   and exact revision and is either Move to one final normalized directory prefix or Delete. The
   Worker resolves and recomputes it against the exact leased snapshot immediately before planning.
   Reject missing, root, stale, duplicate/unsafe/special, overlapping, self-descendant, cyclic,
   ambiguous, or multiply transformed subtrees.
3. A source-folder addition reuses the existing pinned non-reparse directory enumeration and exact
   pre/post tree-manifest authority. It preserves the selected root and empty directories and joins
   the same global phase budget; providers receive only validated metadata and already-open streams.
4. Resolve subtree deletes, inherited subtree moves, exact file operations, and mapped file/tree
   additions into one final namespace. An entry under a deleted subtree cannot also be edited. A
   file-specific rename under a moved subtree supplies its explicit final path; otherwise it inherits
   the one parent-prefix transformation. A Move includes the explicit directory entry when present,
   moves every descendant, preserves explicit empty directories, and synthesizes only required
   ancestors. Delete removes that explicit node and every descendant, prunes now-unused synthetic
   ancestors, and preserves unrelated explicit ancestors. Final output order is ordinal by normalized
   final path, then kind, then original persistent key or mapped-source token. All collisions fail
   before staging.
5. Extend CLI and WPF with explicit Add folder, Move folder, and Delete folder review. UI selection
   never becomes filesystem authority, and folder operations must state their bounded descendant
   count before confirmation.
6. Admit at most 64 mapped source-folder roots, 4,096 subtree actions, and 4,096 total file/tree/
   metadata directives, all still within the global 4,096 material-entry limit. The combined UTF-8
   path/name payload is capped at 512 KiB before JSON framing, the serialized request remains below
   1 MiB, and planning must be linear in snapshot entries, directives, and total path bytes rather
   than repeatedly rescanning each subtree.

Slice-2 exit requires empty/synthetic/explicit directory cases, Unicode/deep trees, overlapping and
cyclic transforms, source-tree mutation/reparse/hard-link/ADS attacks, mixed file/tree edits, every
failure checkpoint, real packaged-Worker model routes, and official 7-Zip plus Windows `bsdtar`
independent manifest/hash validation for the qualified corpus.

#### Slice 2 checkpoint — completed 2026-08-25

- Protocol 1.14, `zip-rebuild-tree-edit-v1`, and the bounded Worker-owned source-folder review route
  passed the complete Slice 2 gate: 284 unit/WPF, 29 adversarial, and 243 integration checks, 556
  total, with zero failures/skips; formatter and zero-warning Release build passed after locked
  restore and a fresh no-known-vulnerability inventory.
- Canonical production fingerprint: 123 inputs,
  `4B5F3A57BFD10C6919EB34C8E35C2330D22A7643996EA4EA4F4AF27EA1B0F10A`.
- Official 7-Zip Extra 26.02 and Windows `bsdtar` independently validated the exact qualified
  directory-tree edit corpus. Original hashes, failure cleanup, bounded cancellation, source-tree
  mutation/reparse/hard-link/ADS refusal, race, low-space, exact-PID crash/recovery, same-worker
  health, and authoritative Rain reopen/list/test/extract passed.
- Retained exact checkpoint: `artifacts/RainArchives.ProtocolCompatibility.1.14.4B5F3A57`; its
  self-contained Client harness passed common operations against its exact Worker. Current/retained
  1.12 and 1.13 compatibility checks passed in both directions, including local refusal of tree and
  source-folder review routes before source access on older Workers.
- Separate read-only remediation review found no actionable issue. Manual GUI/accessibility,
  clean-machine, physical storage/SMB, signing, installation, and publication remain pending and do
  not block Slice 3.

### Slice 3 — protocol 1.15 `zip-rebuild-metadata-v1`

Add explicit, bounded metadata editing for final ZIP entries without pretending unsupported ZIP or
platform metadata can be preserved.

1. Add a distinct minor-15 request/message/feature depending on the preceding rebuild features.
   Metadata targets are typed as either an exact retained persistent key or an exact mapped final
   path/kind and are resolved only after the closed namespace plan. Missing, duplicated, deleted,
   ambiguous, or kind-mismatched targets fail before staging.
2. The only editable fields are one raw ZIP-local DOS last-write tuple and the low-word DOS/Windows
   attribute field. The timestamp is explicitly timezone-less, not UTC: year 1980-2107, valid
   month/day/hour/minute, and seconds 0-58 in exact two-second increments. CLI uses
   `YYYY-MM-DDTHH:mm:ss` with no offset; offset-bearing, fractional, odd-second, invalid, or out-of-
   range values are rejected rather than rounded. WPF labels it **Archive time (timezone unavailable)**.
   Readback compares the exact normalized DOS tuple; it must not claim UTC preservation. Existing
   source-preservation clamping remains a structured warning where already documented.
3. The allowed low-word attribute mask is ReadOnly (0x0001), Hidden (0x0002), System (0x0004), and
   Archive (0x0020), with Directory (0x0010) forced only from entry kind; zero means an ordinary file.
   Reject high-word bits, ReparsePoint, Device, Encrypted, SparseFile, Offline, Temporary, Normal,
   integrity, recall, pinned, virtual, and every unknown bit. Readback compares the exact allowed low
   word after the forced kind bit.
4. Comments, extra fields, ACLs, owners, ADS, creation/access times, POSIX modes, links, original
   compression methods, and arbitrary external attributes remain unsupported and truthfully reported.
5. Extend CLI and a compact WPF advanced panel with explicit current/final metadata review. No locale-
   ambiguous timestamp syntax may cross the CLI.
6. Include metadata in expected-output accounting and complete staged readback. Verify the values by
   reopening through Rain and an independent ZIP implementation where that implementation exposes
   them; never infer preservation from a successful extraction alone.
7. Before freezing the minor-15 DTO/message kind, add a focused provider proof that the BCL writer and
   reader round-trip the exact timezone-less DOS tuple and allowed attribute low word and that official
   7-Zip reports compatible values for the qualified corpus. If the active managed writer cannot
   satisfy those semantics, Slice 3 is a genuine blocker: do not publish protocol 1.15, invent private
   extra fields, silently quantize, or adopt a native dependency.

Slice-3 exit requires all valid masks and timestamp boundaries, unsupported-bit refusal, metadata
plus namespace edits, downgrade/frame tests, exact readback mismatch, cancellation/crash/original-
hash tests, and independent corpus evidence with unsupported-field limitations recorded.

#### Slice 3 checkpoint — completed 2026-08-25

- Protocol 1.15 and `zip-rebuild-metadata-v1` passed the complete Slice 3 gate: 293 unit/WPF, 29
  adversarial, and 251 integration checks, 573 total, with zero failures/skips; formatter and
  zero-warning Release build passed after locked restore and a fresh no-known-vulnerability inventory.
- Canonical production fingerprint: 125 inputs,
  `6F595F0D914E156685767181E063E455826B4621400395628D91C9948F1F4F78`.
- All 16 supported file masks, timestamp boundaries, strict invalid-value refusal, metadata combined
  with file/tree edits, authoritative Rain reopen/test/extract, exact BCL raw DOS readback, compatible
  official 7-Zip reporting, cancellation, readback corruption, exact-PID crash/recovery, and original
  hash preservation passed. Unsupported comments, extra fields, ACL/owner/ADS, other timestamps,
  POSIX/link metadata, original methods, and arbitrary external attributes remain explicitly absent.
- Retained exact checkpoint: `artifacts/RainArchives.ProtocolCompatibility.1.15.6F595F0D`; its
  self-contained Client harness passed common operations and explicit metadata editing against its
  exact Worker. Current/retained 1.12, 1.13, and 1.14 compatibility checks passed, including local
  pre-access refusal of metadata on older Workers.
- Separate read-only review and remediation re-review found no remaining actionable issue after six
  accepted product/compatibility/normalization findings were fixed and regression-tested. Manual
  GUI/accessibility, clean-machine, physical storage/SMB, signing, installation, and publication
  remain pending and do not block Slice 4.

### Slice 4 — managed ZIP capability, preset, and package productization

Finish the phase as a usable shared surface rather than leaving three protocol features discoverable
only through hard-coded UI assumptions.

1. Add protocol 1.16 `zip-write-capabilities-v1` as a separate bounded query. It reports global
   managed-writer creation support and, when supplied an opened-session ID, exact session-bound
   rebuild eligibility. Report supported add/replace/delete/file-rename/tree-edit/metadata operations,
   four built-in compression presets, current entry/byte bounds, and explicit false values for
   encryption, filename encryption, split output, existing-destination replacement, and in-place
   update. Older clients and all existing open/list shapes remain unchanged.
2. Make CLI and WPF consume the typed capability response for current-minor behavior. A missing or
   downgraded feature must produce honest disabled/refusal state before a picker, source read, or
   destination access; hard-coded library-wide write assumptions are forbidden.
3. Add reusable named ZIP presets over only the options supported in this phase. Keep the four built-
   ins immutable; user presets may select compression level and the supported metadata defaults but
   may never contain passwords, source paths, destination paths, opaque recovery IDs, or untrusted
   archive text. Metadata defaults apply only to newly mapped entries unless the user explicitly adds
   a per-entry metadata edit. `RainArchives.App` owns this UI preference store; it is not Worker,
   Security, provider, or archive-operation filesystem authority. Use exactly
   `%LocalAppData%\SailHub\Rain Archives\settings\zip-presets.v1.json`, at most 128 KiB, 64 user
   presets, 64 Unicode scalar values and 256 UTF-8 bytes per name, with NFC plus ordinal-ignore-case
   name uniqueness. Reject control/bidi/invalid Unicode in names. Refuse a reparse settings root/file,
   serialize concurrent writers with one per-user lock, write/flush a random CreateNew sibling, and
   atomically replace while retaining at most one last-known-good backup. A failed save preserves the
   previous file; malformed, oversized, future-version, missing, or interrupted state falls back to
   built-ins with a visible non-secret warning and never blocks engine use. Cleanup may remove only
   an exact operation-owned temporary sibling.
4. Share the preset surface between New ZIP and Rebuild Copy, add CLI capability/preset diagnostics,
   and cover keyboard/default/cancel/invalid-state bindings. This is functional productization, not a
   broad visual redesign.
5. Produce a new local self-contained `win-x64` App plus private Worker package from the final source,
   verify exact App/Worker/Contracts composition and forbidden-native-asset absence, and exercise the
   production packaged Worker through noninteractive Client/CLI/WPF-model create and all completed
   rebuild operations. Do not install, sign, publish, register, or relabel the retained 1.11 package.

#### Slice 4 checkpoint — completed 2026-08-25

- Protocol 1.16 and `zip-write-capabilities-v1` passed the complete Slice 4 gate. The typed
  capability query reports global managed-writer creation support, exact opened-session rebuild
  eligibility, supported operations, the four immutable built-in presets, current bounds, and
  explicit false values for encryption, filename encryption, split output, existing-destination
  replacement, and in-place update. CLI and WPF gate before picker/source/destination access and
  refuse missing, downgraded, or operation-incompatible capability state.
- `RainArchives.App` owns the reusable preset store at the exact LocalAppData settings path. The
  store keeps built-ins immutable, bounds and validates user names/defaults, rejects reparse roots
  and files, serializes writers with one per-user mutex, and uses random CreateNew sibling,
  Flush(true), atomic replacement, and one last-known-good backup. Failed saves preserve the prior
  document; malformed, oversized, future, missing, or interrupted state falls back to built-ins with
  a visible non-secret warning. Metadata defaults apply only to newly mapped entries, while explicit
  per-entry edits win; no preset contains secrets, paths, recovery IDs, or untrusted archive text.
- Final source verification passes 307 unit/WPF, 29 adversarial, and 263 integration checks: 599
  total, with zero failures or skips. Locked restore, the no-known-vulnerability inventory,
  formatter verification, and zero-warning Release build pass. The canonical production fingerprint
  is 128 inputs,
  `8E6E9D171C82841E804C03285D86B119122A61A339F6D00331054BE916AC6258`.
- The mandatory package flow passes against
  `artifacts/RainArchives.M3AAutomatedPackage.1.16.8E6E9D17`, a self-contained `win-x64` App with
  its private Worker subtree and byte-coherent Contracts assemblies and no forbidden native assets.
  The App tree is 609 files / 231,790,213 bytes at aggregate SHA-256
  `1507A0E6E9072B1A8A95948949E73E689ACF793235C3D88C6D5BB1D4826BDDB9`; the private Worker subtree
  is 205 files / 84,437,425 bytes at
  `BE8001F3453C63C2C63858E0DEE8128FC4465C35345AC89C35CBE5498FAEDDA0`, and both Contracts copies
  hash to `CDB5C66D11DC990B47B1E25AAB4BD0FE35710AA32562BBE71315D9FD82EB4714`.
  The production packaged Worker completes noninteractive Client/CLI/WPF-model create and all
  completed rebuild routes; Rain reopen/list/integrity/extract checks, original-byte preservation,
  and staging cleanup pass. The retained compatibility artifact
  `artifacts/RainArchives.ProtocolCompatibility.1.16.8E6E9D17` passes the current/retained 1.12
  through 1.16 matrix in both directions: 10/10 pair checks, with newer operations refused before
  source or destination access on older peers.
- Independent read-only review findings covering CLI global preflight, public WPF capability
  enforcement, and client validation of session-bound rebuild capability were fixed and regression-
  tested. No actionable issue remains. Manual GUI, accessibility, clean-machine, physical
  filesystem/SMB, installation, signing, and publication acceptance remain `manual_pending` and
  are not transferred from historical package evidence.
- The final residue audit shut down the build servers and found no Rain Archives App, Worker,
  testhost, new test-owned temporary child, or workspace staging item. Timestamped older retained
  temp evidence was identified and preserved.

### Per-slice verification cadence

After each slice, before advancing:

- Run focused unit, adversarial, and real Worker/Client/CLI integration tests; WPF-model and STA tests
  when UI changed; golden/downgrade/frame tests when IPC changed; and external ZIP conformance when
  archive output changed.
- Run all three complete test assemblies with zero failures and zero skips, formatter verification,
  and zero-warning Release build. Record the new totals and canonical production fingerprint as a
  new checkpoint; preserve protocol-1.12 totals/fingerprint as historical pre-phase evidence.
- Verify original archive byte identity for every successful and failed rebuild family, authoritative
  reopen/list/test/extract of outputs, bounded cancellation latency, same-worker health where promised,
  exact-PID crash/recovery behavior where state can become durable, and zero ordinary process/staging/
  workspace residue.
- Use a separate read-only review for the new slice's production paths. If delegation is unavailable,
  use a clean-context self-review and record that evidence class rather than stopping. Validate each
  finding before changing code, add a regression for every accepted defect, and do not call a scoped
  review a fresh repository-wide security audit.
- Update this file and relevant testing/architecture/milestone/format documents as a checkpoint, then
  continue to the next listed slice instead of returning a completion summary.

### M3-A automated phase exit

M3-A is complete only when:

- all four slices are implemented through Contracts/Core/Security/provider/Worker/Client/CLI/WPF as
  applicable, with old protocol meanings and dependency boundaries intact;
- the final full locked restore, vulnerability inventory, formatter, zero-warning Release build, all
  three complete test assemblies, external-tool corpus, package composition/flow gate, fingerprint,
  and residue audit pass from final source;
- no unresolved in-scope high/critical finding, unexplained output mismatch, original mutation,
  uncertain publication result, or weakened/skipped test remains;
- `CONTINUATION.md`, `docs/testing.md`, `docs/milestones.md`, `docs/architecture.md`, and
  `docs/format-capabilities.md` truthfully record final protocol/features, counts, fingerprint,
  package identity, evidence boundaries, limitations, and the next bounded phase; and
- the final report says **M3-A automated phase complete; Milestone 3 remains partial**. It must list
  manual/external gates separately and must not transfer historical acceptance to final source.

Reaching this automated exit completes the finite manifest, ends this autonomous run, and authorizes
replacing Section 13 with the next bounded implementation phase. Manual gates remain backlog items;
they do not leave M3-A as the active exact action.

At the M3-A exit, direct GUI interaction, Narrator/keyboard/contrast/mixed-DPI acceptance, clean-
machine execution, true physical disk-full/standard-user ACL/blocked UNC, actual second-machine SMB
creation/interruption, non-NTFS/SMB provenance, and alias/no-file-ID qualification were all
`manual_pending`. Later C400/BC tranches first supplied clean-machine, true NTFS disk-full,
standard-user ACL, exFAT provenance, direct ordinary GUI interaction, and local
8.3/mapped-drive/alternate-loopback-UNC predecessor evidence. The `1C85F6D9` tranche refreshes
package-bound local performance plus clean-machine, ACL, NTFS exhaustion, exFAT, and local-alias
evidence. The product owner later completed direct ZIP creation/reopen/integrity and Rebuild Copy
observation. Staging inventory, rebuilt-output manifest/hash, Recovery Center, product-owner
accessibility, blocked UNC, actual SMB, DFS/no-file-ID, and release gates remain pending and must not
be fabricated, silently skipped, or claimed from deterministic seams.

### Retained M3-A blocker rules and explicit phase exclusions

Stop rather than improvise only if an unexplained security/compatibility failure persists after at
most two reproducible diagnostic attempts plus one clean-process/test-owned-temp retry, a safe
required operation has no defined rollback/recovery outcome, the original might have changed, a
current result irreconcilably contradicts retained evidence, or completion requires new user
authority. Safety-critical operations are not retried after an uncertain mutation. A blocker record
must include the exact failing command/request, evidence, affected slice, preserved filesystem/
process state, attempts made, and safe resumption point. Do not stop merely because one slice was
large, the pre-phase suite passed, a manual-independent gate is pending, or more work remains in the
finite manifest.

This phase excludes existing-destination overwrite/replacement, in-place update, backup/rollback
policy, encrypted input/output, filename encryption, TAR/7z/RAR writing, native-provider enablement,
split output, multipart operations, new dependencies, Rain Explorer edits, shell integration,
installer/updater work, signing, installation, publication, Git, and broad visual redesign. Do not
weaken protocol-1.12 output readback, leases, path/resource limits, recovery, secret handling,
extraction, or Worker-containment invariants to complete M3-A.

## 14. Reproduce the current verification

Run from the repository root in ordinary Windows PowerShell. Native-handle, WPF, Worker-launch, and
process tests can fail under a restricted filesystem/process sandbox; when that happens, rerun the
same command in the ordinary Windows context rather than weakening the test.

```powershell
dotnet --info
dotnet restore RainArchives.slnx --locked-mode --force-evaluate
$projects = Get-ChildItem src,tests,benchmarks -Recurse -Filter *.csproj |
  Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
  Sort-Object FullName
foreach ($project in $projects) {
  dotnet list $project.FullName package --vulnerable --include-transitive --no-restore
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
dotnet format RainArchives.slnx --verify-no-changes --no-restore --verbosity minimal
dotnet build RainArchives.slnx --configuration Release --no-restore --nologo

dotnet test tests\RainArchives.UnitTests\RainArchives.UnitTests.csproj `
  --configuration Release --no-build --no-restore `
  --logger "console;verbosity=minimal"

dotnet test tests\RainArchives.AdversarialTests\RainArchives.AdversarialTests.csproj `
  --configuration Release --no-build --no-restore `
  --logger "console;verbosity=minimal"

dotnet test tests\RainArchives.IntegrationTests\RainArchives.IntegrationTests.csproj `
  --configuration Release --no-build --no-restore `
  --logger "console;verbosity=minimal"
```

Current protocol-1.23 expected result: 530 unit/WPF, 29 adversarial, 375 integration, 934 overall,
zero failures/skips. Immutable protocol-1.12 and historical protocol-1.16 results remain recorded as
their own checkpoints; changed counts must never be forced back to a historical baseline.

Current Rain Explorer verification uses its existing dirty worktree without staging or Git mutation:

```powershell
$rainExplorer = 'E:\Downloads\Rain\Code stuff\File Explorer'
$rainArchives = (Get-Location).Path
$env:RAINARCHIVES_TEST_WINRAR_723 =
  'C:\Users\aliss\AppData\Local\Temp\RainArchives-WinRar723-Probe-80c033a316494542a90d79493fa83079\payload\Rar.exe'

dotnet restore "$rainExplorer\Tests\RainExplorer.Git.Tests\RainExplorer.Git.Tests.csproj" --locked-mode
dotnet test "$rainExplorer\Tests\RainExplorer.Git.Tests\RainExplorer.Git.Tests.csproj" `
  --configuration Release --no-restore --nologo
dotnet build "$rainExplorer\Main\RainExplorer.csproj" `
  --configuration Release --no-restore --nologo

$env:RAINEXPLORER_TEST_WORKER_PATH =
  "$rainArchives\artifacts\RainExplorer.M7RarRemediationPackage.2.0.0.266A39F2.01\app\worker\RainArchives.Worker.exe"
dotnet test "$rainExplorer\Tests\RainExplorer.Git.Tests\RainExplorer.Git.Tests.csproj" `
  --configuration Release --no-restore --no-build --nologo
```

Both Rain Explorer runs expect 315/315 with zero failures/skips. Positive RAR-writing tests require the
same exact separately acquired, signature-checked official 7.23 test encoder or an equivalently qualified
test path; it must never be copied into source, package, or installer. The current machine's production
7.20 installation must continue to report `UpdateRequired`.

Canonical source fingerprint command:

```powershell
$root = (Get-Location).Path
$rootInputs = @(
  'Directory.Build.props',
  'Directory.Packages.props',
  'global.json',
  'NuGet.config',
  'RainArchives.slnx'
)
$extensions = @('.cs', '.csproj', '.xaml', '.manifest', '.ico')
$files = [System.Collections.Generic.List[string]]::new()

foreach ($name in $rootInputs) {
  $files.Add((Join-Path $root $name))
}

foreach ($scope in @('src', 'benchmarks')) {
  Get-ChildItem -LiteralPath (Join-Path $root $scope) -Recurse -File |
    Where-Object {
      $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
      $_.Extension -in $extensions
    } |
    ForEach-Object { $files.Add($_.FullName) }
}

$items = @($files | ForEach-Object {
  [pscustomobject]@{
    FullPath = $_
    RelativePath = [IO.Path]::GetRelativePath($root, $_).Replace('\', '/')
  }
})

[Array]::Sort(
  $items,
  [System.Collections.Generic.Comparer[object]]::Create(
    [System.Comparison[object]]{
      param($left, $right)
      [StringComparer]::Ordinal.Compare($left.RelativePath, $right.RelativePath)
    }))

$aggregate = [Security.Cryptography.IncrementalHash]::CreateHash(
  [Security.Cryptography.HashAlgorithmName]::SHA256)

foreach ($item in $items) {
  $aggregate.AppendData([Text.Encoding]::UTF8.GetBytes($item.RelativePath))
  $aggregate.AppendData([byte[]]@(0))
  $stream = [IO.File]::OpenRead($item.FullPath)
  try {
    $fileHash = [Security.Cryptography.SHA256]::HashData($stream)
  }
  finally {
    $stream.Dispose()
  }
  $aggregate.AppendData($fileHash)
}

[pscustomobject]@{
  InputCount = $items.Count
  Sha256 = [Convert]::ToHexString($aggregate.GetHashAndReset())
}
$aggregate.Dispose()
```

Current result: 164 inputs and
`33912C07420D5C87DA97DB200D168278E32054B6C718AB12789BF5E0434A29B9`.

Immutable protocol-1.12 pre-phase result: 121 inputs and
`D0DFC81FDD6829A8457B9DBF826CF7DE5AD6CC1EA21197C7650015EA649AD95A`. Recompute and record a new
fingerprint after each M3-A slice and at final phase exit; never relabel this retained value as a
later source identity.

After tests:

```powershell
dotnet build-server shutdown
Get-Process RainArchives.Worker, RainArchives, testhost, dotnet `
  -ErrorAction SilentlyContinue
```

An empty process query is expected except for unrelated `dotnet` processes owned by another task;
inspect PID, parent, executable path, and command line before stopping anything.

## 15. What the next agent must not undo

- Do not initialize Git or delete/rewrite the workspace because there is no commit safety net.
- Do not weaken a security invariant, resource bound, strict decoder, unknown-member rejection,
  downgrade refusal, test assertion, or error classification merely to make a new case pass.
- Do not move parsing/decompression into WPF or Rain Explorer.
- Do not introduce WPF/Core/Worker/provider coupling or send UI types over IPC.
- Do not give codec providers filesystem destination/source-enumeration authority.
- Do not replace content detection with extension-only selection.
- Do not make multipart/unknown sessions extractable, testable, streamable, or writable without the
  complete volume identity/lease/fingerprint protocol.
- Do not call SharpCompress directory extraction helpers.
- Do not enable SharpSevenZip or copy `7z.dll` merely because the package is centrally versioned.
- Do not search for native libraries through `PATH`, current directory, registry, or installed apps.
- Do not broaden or bypass the conditional RAR gate, bundle/download/update WinRAR, discover it through
  `PATH`/current directory/registry, accept an unqualified encoder, or use unRAR decoder code to recreate
  a compressor. Preserve concrete-provider re-resolution, executable leasing, bounded no-follow workspace
  ownership, exact child-exit proof, independent test/readback, and new-destination-only publication.
- Do not mutate a user archive in place. Modification must remain a complete guarded staged rebuild
  into a distinct copy unless a separately approved replacement/rollback contract proves otherwise.
- Do not change new-destination extraction into merge/overwrite semantics without a new contract and
  conflict/rollback/security design.
- Do not auto-open nested archives, recursively search them, or auto-delete nested workspaces.
- Do not expose Worker temporary paths in IPC, CLI, WPF, logs, or confirmation dialogs.
- Do not auto-retry creation disposition or nested discard after cancellation/disconnect/uncertainty.
- Do not treat a revision, name pattern, journal, receipt, or owner token alone as deletion authority.
- Do not put passwords in CLI arguments, JSON, logs, telemetry, or ordinary exceptions.
- Do not remove visible escaping of archive-controlled text.
- Do not claim process isolation is a sandbox against a malicious process under the same user.
- Do not claim current-source package/manual acceptance from historical M1 evidence.
- Do not relabel historical benchmark data with the current fingerprint or claim competitor wins.
- Rain Explorer edits must preserve the Contracts/Client-only boundary, ordinary folder behavior, the
  existing dirty state, and the M7 association/icon/navigation/Open With/Extract to/RAR capability work.
- Do not publish, sign, install, register associations, commit, push, or open a PR without explicit
  task-specific user authorization.

## 16. Everything still to implement or qualify

This is the remaining product backlog, grouped by the original milestone plan. An item is not done
merely because the current dependency can theoretically perform it.

### Milestone 2: secure read compatibility

- Broader fixtures generated by multiple versions/tools for every supported format and method.
- Sustained coverage-guided parser fuzzing and minimized regression corpus retention.
- Complete volume-set identity, lease, fingerprint, ordering, and mutation model for safe multipart
  ZIP/7z/RAR integrity/extraction/preview where realistically supportable.
- Multipart cancellation, missing/interchanged volume, network, and very-large-volume qualification.
- True bounded wire-range/seek reads if measurements justify the protocol complexity.
- Safe external/path-based preview handoff with provenance, lifetime, private-cache, and same-user
  isolation policy; additional preview types without active-content execution.
- Bounded skipped-work model for solid RAR random preview/nested open, or retain explicit refusal.
- Stable multi-level nested navigation restoration without uncontrolled recursion. Protocol 1.21's
  one-source recovery reopen is already complete and intentionally produces an independent root.
- Broader ZIP64 layout support, including the valid disk-start-sentinel fixture.
- Actual protocol N/N-1 binaries and golden v1.1-v1.12 frames, not only same-binary downgrade tests.
- Final managed-versus-native read authority decision based on correctness, warnings/errors,
  cancellation, deployment, security, and maintenance—not format-count advertising.

### Milestone 3: creation and modification

- **Completed autonomous managed/qualified scope:** ZIP creation/rebuild/capabilities; TAR, TAR.GZ, TAR.BZ2,
  raw GZIP, and raw BZIP2 creation; conditional protocol-1.23 RAR5 explicit/tree creation through the
  user's separately installed qualified WinRAR encoder; typed Client/CLI/WPF-model/capability routes; staged digest/
  readback/journal/no-replace recovery; external conformance; 1.17/1.18 compatibility; retained
  protocol-1.18 package.
- ZIP encryption remains disabled until an approved provider passes AES method/strength, filename
  encryption truthfulness, secret-slot lifecycle, interoperability, wrong-password, cancellation,
  dependency/licensing/package, and maintenance gates.
- TAR.XZ/raw XZ remains disabled until an approved compressor passes the same dependency/security/
  interoperability gate. SharpCompress 0.50.4 cannot compress XZ.
- 7z creation remains disabled until the corrected native adapter, loader, crash, corpus,
  cancellation, licensing/source-obligation, package, and maintenance-owner gates pass.
- Extend the completed managed-ZIP rebuild surface only through a separately designed contract for
  conflict decisions, drag/paste admission, or other future edit types. Preserve the original until
  the distinct copy is durably validated and committed; accurately label Rebuild.
- Existing-destination overwrite/replacement/in-place remains a closed refusal unless a separately
  approved typed conflict/backup/recovery contract proves alias checks, durable intent, atomic Windows
  replacement, a complete crash matrix, and no retry after uncertainty.
- Split-volume creation remains closed until a bounded multi-output contract, volume manifest,
  aggregate resource checks, durable partial-publication journal/recovery, complete crash/cancel
  matrix, missing/interchanged-volume detection, and official corpus exist.
- Package-bound physical creation tests cover early/near-final true NTFS disk-full, non-elevated
  standard-user ACL denial, all supported preset families on exFAT with truthful provenance warnings,
  and real local 8.3/mapped-drive/alternate-loopback-UNC paths. Product-owner direct ZIP creation/
  reopen/integrity and Rebuild Copy observation passes; staging inventory, rebuilt-output manifest/
  hash, and Recovery Center remain separate. Physical blocked-UNC cancellation, actual second-
  machine SMB destination/interruption/provenance, DFS/no-file-ID filesystems, and any later-identified
  staging/commit crash point without a qualified safe checkpoint stay open.
- Source-at-package-time ZIP and exact-package mixed-format benchmarks are complete. The bounded M3 external-
  reader matrix covers pinned official 7-Zip, Windows libarchive 3.8.4, GNU 1.35/1.14/1.0.8, WinRAR
  7.20, and distinct libarchive 3.8.9 simple/PAX layouts. Open-ended external versions/methods/layouts,
  cold-cache/cross-machine thresholds, and competitor-equivalent comparisons remain Milestone 8 work.
- Fresh offline exact-package clean-machine startup/all-six-format creation passes. Product-owner
  accessibility/High-Contrast/mixed-DPI remains open;
  installation, signing, and publication require separate task-specific authorization.
- Conditional RAR5 creation is implemented after the official encoder/licence path and product-owner approval.
  Remaining gates are a qualifying WinRAR 7.23+ production installation, direct GUI/interoperability,
  clean-machine/licensing, supported-version upgrade/rollback, signing, publication, and release acceptance.
  The current signed 7.20 installation truthfully reports `UpdateRequired`; unRAR/decoder availability is
  never compression authority.

### Milestone 4: Worker reliability and operation lifecycle

- **Completed autonomous scope:** protocol 1.19 bounded path-free activity with exact 32-admission/
  one-execution/64-terminal policy and cancel-only controls; protocol 1.20 local-only read-only
  extraction-staging inventory/inspection; protocol 1.21 exact-evidence copy-only nested recovery
  reopen as an independent root; protocol 1.22 fixed-field support diagnostics and truthful
  containment disclosure; exact-PID crash/restart coverage; retained 1.18 compatibility/downgrade/
  package refusal; cleanup/retention convergence; local self-contained package; and an immutable
  read-only security scan.
- **Closed unsupported controls:** `AwaitingPassword`, `AwaitingConflict`, and `AwaitingRiskApproval`
  remain forward-compatible enum values only. Password uses one-shot secret slots; conflicts and risk
  are local fail-closed preflight; `IsRetryable` is advisory. There is no decision, retry, pause,
  reorder, overwrite, automatic recovery, or restart-persistence endpoint without a future immutable
  operation descriptor and proof that no mutation ambiguity exists.
- **Closed extraction disposition gate:** protocol 1.20 lacks the exact per-entry identity/content
  manifest and deletion-progress ledger needed for safe mutation. A future contract must add bounded
  handle-relative evidence, durable intent/completion, fresh per-mutation identity checks, idempotent
  replay, and exact-PID crash proofs without weakening the read-only/no-auto-retry boundary.
- **Cleanup/retention policy:** parser/session streams own no durable temporary tree; preview is capped
  at eight leases/512 MiB and uses delete-on-close; operation activity retains 64 terminal records;
  extraction/creation/rewrite remove only exact ordinary owned state after normal settlement; creation
  `Completed` metadata retains newest 256 for at most 30 days; nested storage is capped at 16 sources/
  512 MiB and exact-cleaned on normal close. Startup inventory examines/publishes at most 1,024
  recovery records per family. Changed, malformed, identity-unavailable, excessive, inaccessible,
  active, partial, or ambiguous state has intentionally no age/count auto-delete authority.
  Active allocation and inspection work are bounded; unverified on-disk evidence is deliberately
  not time-deleted because that would contradict the required preservation boundary.
- **Manual/external gates:** authorized blocked-network cancellation and forced teardown; actual
  second-machine SMB/DFS/no-file-ID/non-ADS behavior; direct current-package Recovery Center/support-
  diagnostics GUI, Narrator, keyboard-only, High Contrast, and mixed-DPI acceptance; installer/update
  atomicity, signing, and release acceptance. Provider-message provenance review and an exact-PID
  pre-response operation-start cancellation fixture remain defense-in-depth test targets, not
  unimplemented Milestone 4 wire/product contracts; they do not authorize unsafe new controls or
  generic exception text.

### Milestone 5: standalone WPF application

- The bounded M5G-D control-design-system tranche is complete without a protocol, dependency, provider,
  filesystem, recovery, settings-schema, mutation, installer, signing, or publication change. One shared
  dynamic-resource layer owns ordinary WPF control templates between the active palette and product
  tokens. Runtime tests cover control states, open popups across Light/Dark/High Contrast, generated
  DataGrid checkbox/text editors, and compact Settings scrolling. Create default labels and post-create
  open fallbacks are archive-neutral without changing runtime format capability text. The `651729F4`
  package and 910-test gate are current; `B11E7A80` is preserved as superseded pre-final evidence after
  runtime proof exposed its generated text-editor fallback. Direct exact-package visual, Narrator,
  keyboard-only, actual High Contrast, DPI, and clean-machine acceptance remains `manual_pending`.
- The bounded M5G-C product-identity tranche is complete without a protocol, dependency, custom-title-
  bar, shell-integration, installer, signing, publication, or runtime-generation change. One original
  deterministic nine-frame alpha-capable icon follows the existing archive-glyph and semantic palette,
  is bound through `ApplicationIcon`, and is included in the canonical source identity. Focused tests
  parse all ICO frames/pixels, verify its exact hash, extract large/small icons from the Release and
  packaged executables, and prove the launched top-level window resolves both icon sizes without a
  `MainWindow.xaml` override. The `56FFA877` package and 905-test gate remain immutable historical
  product-identity evidence; M5G-D supersedes it as the current presentation candidate.
- The bounded M5G-B secondary-window reconciliation is complete without a protocol, dependency,
  provider, filesystem, recovery, settings-schema, or mutation change. Activity, Settings, Recovery,
  Create, Rebuild, and Password use shared dynamic semantic palettes; bounded layout/scrolling;
  collision-free access keys; explicit tab/default/cancel and Automation metadata; deliberate Activity
  and selected-Recovery live owners; and recycling grids. A weak/idempotent native-caption attachment
  follows source initialization and app-theme changes, detaches on close, and preserves every modal/
  nonmodal owner and cancellation/drain/session-handoff lifecycle. Full escaped password archive names
  remain accessible while visually bounded; passwords still clear on close. The `20FF2288` package and
  901-test gate remain immutable accepted predecessor evidence. M5G-A and `BE985F08` remain immutable
  accepted/rejected history.
- The bounded M5G-A main-shell visual tranche and its accessibility remediation are complete without a
  protocol, dependency, provider, filesystem, recovery, or mutation change. Three centralized palettes
  plus paired High Contrast surface/foreground roles and shared WPF tokens/styles/
  vector icons back a redesigned identity/search header, tiered command bar, navigation/breadcrumb
  strip, bounded virtualized entry list, adaptive on-demand inspector, wrapping welcome surface, and
  one coherent status live region. System mode follows Windows; explicit Light/Dark is app-local;
  High Contrast overrides both. Duplicate welcome access markers are removed while top access keys,
  M5F inspector drain/focus, and all existing authority remain unchanged.
- The bounded M5F existing-command context and performance tranche is complete: one canonical
  newest-state authority backs Enter/double-click, accelerators, and the row menu for navigation,
  validated properties, bounded safe preview, integrity, and selected extraction. Duplicate,
  substituted, stale, offline, downgraded, missing, and busy state fails closed; fixed presentation
  text and the Contracts/Client-only dependency boundary add no path, secret, provider, recovery,
  filesystem, mutation, overwrite, retry, restore, shell, or external-launch authority. Exact-package
  measurement pins the fixture/method/runtime/raw results, preserves the 500,000/512/256 and preview/
  Worker limits, and proves exact PID and residue convergence without a competitor claim.
- The M5F inspector remediation keeps Properties and Safe Preview hidden until explicitly requested,
  shares one fixed close lifecycle, cancels and joins preview before collapse, rejects late repaint, and
  restores current entry-list/selection focus without a global Escape binding. The 94811C7A package and
  measurement remain immutable predecessor evidence; its observed DPI scaling and broad keyboard
  navigation do not transfer to the remediated package.
- The bounded M5E Recovery Center/support-evidence foundation is complete: one four-surface
  Contracts/Client-only modal preserves creation disposition, nested exact-evidence independent
  reopen/discard, read-only extraction inspection, fixed diagnostics, deterministic invalidation,
  initialization/child cancellation and drain, recovered-session ownership, system-color/
  accessibility metadata, and real-Worker/exact-PID/exact-package hash/residue proof. Generic cleanup,
  retry, restore, replacement, extraction disposition, provider/exception/archive text, paths,
  secrets, and new mutation authority remain closed.
- The bounded M5D properties/safe-preview foundation is complete: fixed validated archive/entry
  projections, mathematically valid optional values, explicit stale/offline authority clearing, typed
  strict-decoding preview outcomes, inert binary/oversize/unsupported handling, exact cache/request
  bounds, deterministic selection/focus, cancellation/drain, system-color/accessibility metadata, and
  real-Worker/exact-package hash/residue proof. Active content, export/Open With, raw provider details,
  and any new mutation authority remain closed.
- The bounded M5B archive-browser foundation is complete: validated root/folder navigation,
  breadcrumbs, Back/Forward/Up, Unicode search, deterministic duplicate identity/selection, exact-
  fingerprint persistent-key reopen, explicit state surfaces, keyboard/focus/automation semantics,
  system colors, recycling virtualization, and 500,000-index/512-visible/256-history bounds. Broader
  application-wide visual refinement, localization, touch, and manual assistive-technology/DPI
  acceptance remain.
- The bounded M5C Settings/Presets and managed create/rebuild foundation is complete: strict fixed-field
  UI settings, hardened ZIP presets, explicit corrupt/unsupported/unsafe/reset states, serialized
  capability refresh and shutdown drain, all six managed creation formats, eligible ZIP Rebuild Copy,
  current-capability default clamping, raw one-file constraints, absent-destination/original-preserved
  review, and exact-package create/rebuild/reopen/hash/residue proof. Broader per-format methods,
  estimates, localization, touch, and manual assistive-technology/DPI acceptance remain.
- Encryption, volumes, overwrite/replacement, richer conflict decisions, retry, in-place update,
  drag-in/paste mutation, and rollback are closed evidence gates rather than simulated Settings or
  create/rebuild controls.
- Activity Center foundation is complete for protocol-1.22 connection-lifetime operations: unified
  details, warning counts, throughput/ratio where supplied, bounded retention, snapshot-authorized
  cancellation, fixed diagnostics, and explicit unsupported controls. Durable history, safe retry or
  decision continuation, and crash/recovery continuity remain closed until their separate evidence
  gates are satisfied.
- Exact-current broader application manual Narrator/keyboard/contrast/DPI/
  clean-machine acceptance remains package-bound and `manual_pending`.
- Self-contained packaging strategy, atomic App+Worker updater, installer UX, signing, provenance,
  rollback, and clean uninstall. No background service unless a future gate proves it necessary.

### Milestone 6: Rain Explorer integration

- Add a Rain-side archive-location/provider adapter over Contracts/Client; do not reuse WPF view
  models or provider implementation types.
- Archive root/nested location serialization, persistent history keys, reopen resolution, and source
  fingerprint invalidation.
- Tabs, breadcrumbs, back/forward history, hierarchy navigation, search, metadata, read-only/writable
  capability state, and nested-session ownership.
- Preview streams/caches, password events, warnings/errors, cancellation, and cleanup ownership.
- Copy/drag out and paste/export through Rain's normal file-operation/activity system.
- Drag/copy in only after staged-rewrite capabilities exist; unsupported formats remain visibly
  read-only.
- Underlying archive mutation detection, explicit reopen, no old/new session mixing.
- Recovery/diagnostic entry point and deduplicated Worker-lifetime attention warnings.
- Production tests for tabs/history/search/preview/drag/password/cancel/source mutation with no
  duplicated archive engine.

### Milestone 7: Windows associations and shell integration

- Explicit opt-in file associations by supported extension with default-app-safe registration.
- Open, Extract, Test, and Create verbs with correct quoting/long-path behavior.
- Icons, application identity, installer registration, upgrade/rollback, per-user/system policy,
  uninstall cleanup, and fallback when files are absent.
- Evaluate whether any shell extension is necessary; prefer ordinary verbs and no persistent service.
- Supported Windows-version and standard/elevated account install/upgrade/uninstall matrix.
- M7-A completes the first narrow item in source/package form: an owned HKCU ProgID and Registered
  Applications capability map, explicit Settings entry, Windows-owned default selection, direct archive
  activation, foreign-registration refusal, and tests proving no `UserChoice`, extension-default, or WinRAR
  mutation. It does not claim installed association/reversal, context-menu, uninstall-cleanup, or additional
  verb acceptance; see [docs/milestone-7.md](docs/milestone-7.md).

### Milestone 8: performance, compatibility, and release hardening

- Reproducible current-source startup, time-to-first-list, huge listing, single/full extraction, ZIP
  and approved 7z creation, memory, CPU, throughput, ratio, and cancellation-latency benchmarks.
- Repeatable competitor comparisons with identical corpus, method, level, hardware, cache state,
  destination, and correctness checks. Make no speed claim before data.
- Performance regression thresholds and CI/local baseline policy.
- Broader multi-tool/multi-version format corpus, very-large/solid/encrypted/multipart variants,
  malformed/truncated corpus, and sustained coverage-guided fuzzing.
- Final SBOM, third-party notices, native corresponding-source obligations, package manifest, and
  vulnerability review.
- Code signing, installer/updater signing, artifact hashes, release provenance, rollback, and release
  runbook.
- Telemetry-off/no-account/no-background-service verification.
- Clean-machine package tests and final manual accessibility, DPI, contrast, network, storage,
  standard-user, install/update/uninstall, association, and Rain Explorer acceptance.
- Resolve or explicitly accept every known limitation and decision gate above before release.

## 17. Authoritative supporting documents

- Architecture: [docs/architecture.md](docs/architecture.md)
- Security policy/invariants: [SECURITY.md](SECURITY.md)
- Format capability matrix: [docs/format-capabilities.md](docs/format-capabilities.md)
- Dependency and native gate evidence: [docs/dependency-evidence.md](docs/dependency-evidence.md)
- Test/adversarial strategy and current counts: [docs/testing.md](docs/testing.md)
- Milestone sequence: [docs/milestones.md](docs/milestones.md)
- Accepted M1 evidence: [docs/milestone-1.md](docs/milestone-1.md)
- Manual Windows matrix: [docs/milestone-1-manual-windows.md](docs/milestone-1-manual-windows.md)
- Clean-machine method: [docs/milestone-1-clean-machine.md](docs/milestone-1-clean-machine.md)
- Rain Explorer boundary: [docs/rain-explorer-integration.md](docs/rain-explorer-integration.md)
- Authorized Milestone 6 manifest: [docs/milestone-6.md](docs/milestone-6.md)
- Milestone 7 association/shell manifest: [docs/milestone-7.md](docs/milestone-7.md)
- Benchmark methodology/evidence: [docs/benchmarks.md](docs/benchmarks.md)
- Licenses/notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and [licenses/](licenses/)

Update this continuation file at the end of each future production slice so the next fresh task has
one accurate starting point. Never replace precise evidence with a percentage-only status claim.
