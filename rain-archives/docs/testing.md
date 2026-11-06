# Test and adversarial fixture strategy

Latest 2026-09-05 checkpoint: [extraction sync-conflict diagnosis/UI repair](extraction-sync-conflict.md),
protocol **1.24**. Current scoped runs passed **350 Explorer + 41 extraction/recovery/provenance =
391 distinct cases**, zero failures/skips. The 350 Explorer cases were repeated with the exact
packaged Worker. The user's copied ZIP extracted outside the synced tree, with all **773 files /
240 directories / 2,931,086 payload bytes** and every SHA-256 checked; provenance matched as well.
Live synced-folder probes found real hard links into Drive's upload cache and failed the unchanged
single-link check. This is a verified diagnostic improvement, **not** a cloud-sync extraction fix,
full Archives suite rerun, installed GUI acceptance, or a zero-residue claim.

Retained 2026-09-05 checkpoint: [protocol-1.24 RAR long-path repair](rar-long-path-fix.md).
Final scoped runs passed **536 unit/WPF + 29 adversarial + 32 RAR integration + 336 Explorer = 933**,
with zero failures/skips. Both real-Worker long-path regressions and all 336 Explorer cases were
repeated against the exact self-contained Worker. The pre-fix physical Worker failed both path
fixtures with `filesystem.native_failure`, although the long-path-aware in-process testhost passed.
The new test therefore verifies the actual Worker executable, RAR create/reopen/list/integrity/
extraction, payload and source equality, and cleanup. The full integration matrix was not rerun.
Earlier [performance results](rar-performance.md) remain tied to their own predecessor package.
Earlier package evidence below remains bound to its own source and package.

Checked fixtures are immutable inputs with a manifest containing SHA-256, byte length, format, generator/version/command, password identifier (never the password itself), expected entries, expected integrity result, and redistribution license. The fixture generator creates only missing checked-corpus files and refuses overwrite; runtime stress fixtures live under GUID test temp roots and never rewrite the checked corpus.

## Deterministic fixture tiers

1. Tiny in-repo fixtures: empty archives, empty directories, zero-byte entries, simple hierarchy, duplicate entries, Unicode names, encrypted samples, one solid sample, one multipart set, and minimal malformed/truncated inputs. Keep them small enough for every test run.
2. Generated fixtures: seeded incompressible data, highly compressible data, large entry counts, deep valid paths, sparse/large files, and mutation/cancellation timing cases. Record seeds and generation commands; do not check multi-gigabyte output into source control.
3. Compatibility corpus: archives produced by at least two independent implementations where practical, including official 7-Zip and .NET/other standards-conforming ZIP/TAR tools. Keep licenses and versions in the manifest.
4. Private security corpus: crashing/fuzz findings and potentially dangerous archives stored outside public source when redistribution is unsafe. Tests consume them by hash through an opt-in path.

The current checked corpus includes ordinary/AES-256/ZipCrypto/corrupt/truncated ZIP plus an independently generated classic `.z01`/terminal-`.zip` set; solid/content-encrypted/header-encrypted/split 7z; plain/solid/header-encrypted/truncated/split RAR; TAR/TAR.GZ/TAR.BZ2/TAR.XZ; and raw/truncated GZIP plus raw BZIP2/XZ. Hash coverage is exact: the integration test requires the checksum manifest to equal every archive file under the corpus, verifies every byte length/SHA-256, and confirms that the public manifest contains only the password identifier.

## Rain Explorer Phase-6 integration checkpoint (2026-09-02)

- M6-A through M6-exit are cumulative, uncommitted Rain Explorer working-tree checkpoints over
  `0bc3fb090bef4d93e0db5bb6d66813b80e8385f7`. M6-A retains identity 15 inputs / 541,661 bytes /
  `D4543C0ACB433EF596EEF8AB13204E6E59CB4B45708C08CD0A3B93EB58E27FAF`; M6-B is 18 inputs /
  618,457 bytes / `DAF3D191214F30C177C198E375239A0A9203F3B8E60767923769ABA64B657A7E` under the documented
  path/NUL/length/NUL/raw-byte digest; M6-C is 27 inputs / 817,265 bytes /
  `9F3672F88A01983FB8A49A9AC4959F2141347C502057FED3B4C884207A964709`; and M6-D is 28 inputs /
  907,670 bytes / `93EFF0EA30F531A4995DDFDBBB93BD714E7C7CB0C938DC62050C4D13415061B7`; and M6-E is 34 inputs /
  1,078,510 bytes / `104F6C3B5D5DFD6088C1394564C0744B36F1D96532BDD319B2B4E21C2345562A`; and M6-F is 44 inputs /
  1,326,298 bytes / `E637C248717555533151C3959B7A5847F4DFD5E4489AD262643263B4DEAF3108`; and M6 exit is 47 inputs /
  1,408,763 bytes / `CB0DC84FC3A19D43F73E9ECB7E527F7094A968F54787635F816DF9BD0240AD65` under the same algorithm.
- Independent M6-D verification runs
  `dotnet test Tests\RainExplorer.Git.Tests\RainExplorer.Git.Tests.csproj -c Release --no-restore`
  and requires 160/160 with zero failures/skips. It then runs the Release application build with zero
  warnings/errors and the direct/transitive NuGet advisory query with no known vulnerable package.
  The 11 focused M6-D tests must also pass independently. Scoped
  `dotnet format --verify-no-changes` covers every touched M6-D production/test C# file;
  the broader repository has pre-existing formatting debt and is not falsely reported as a clean gate.
- Preview tests require strict UTF-8/BOM Unicode classification, exact 1 MiB plus one-byte accounting,
  fixed/redacted state text, one-active/one-newest-pending ordering, replacement exactly at the
  decode-to-completion boundary, cancellation-source lifetime safety, lease cleanup before successor
  admission, navigation/tab-close/app-close drain, exact-client Worker invalidation, `SourceChanged`
  whole-tab stale transition, and explicit fresh-client reopen after transport loss.
- One real M6-C protocol-1.22 Worker test opens a spaces-and-Unicode ZIP, browses to a material entry,
  completes an authenticated inert text preview and integrity test, extracts an exact selected entry
  into a new destination, compares output hashes, preserves the source and a conflict sentinel, closes
  the exact operation/lease/session/client, and leaves no test-created Worker, testhost, fixture,
  recovery, cache, nested-workspace, or staging residue. App/Worker Contracts must remain byte-identical
  and no `7z.dll`, `7za.exe`, `7zz.exe`, or `SharpSevenZip.dll` may appear.
- One real M6-D protocol-1.22 Worker test opens a spaces-and-Unicode extension-independent root,
  explicitly materializes three nested archive levels, navigates/searches/previews inside the chain,
  integrity-tests and extracts at nested depth, compares output hashes, persists and reopens all three
  segments with fresh session and entry IDs, preserves the root bytes, and leaves no Worker, fixture,
  recovery, Preview Cache, nested-workspace, or staging residue.
- M6-D regressions require depth-three success/depth-four refusal; per-entry, ancestry-chain, and
  connection-global active-byte ceilings; feature/capability/solid/multipart/encrypted-parent refusal
  before secret use; wrong/correct child-password handling and immediate caller-buffer zeroization;
  cancellation after child ownership but before index adoption; malformed response and source-change
  compensation; descendant-before-ancestor close ordering; version-1 root compatibility plus strict
  bounded version-2 nested persistence; fresh-ID duplicate-ordinal replay; child fingerprint mismatch
  without ancestor/root fallback; and no implicit nested decode from listing or search.
- Independent M6-E verification runs the 65 focused transfer/OLE/adapter cases and all 192 Rain Explorer
  tests with zero failures/skips, then requires a zero-warning/zero-error Release build, an empty current
  direct/transitive NuGet advisory result, `git diff --check`, and scoped formatter verification for every
  new M6-E C# input. The repository-wide formatter still reports unrelated historical files and is not
  falsely relabelled as a clean full-repository gate.
- M6-E's Windows STA/OLE test proves WPF performs lazy custom `IDataObject.GetData` delegation but does
  not expose the required `IDataObjectAsyncCapability` lifetime; protocol 1.22 also lacks a committed
  destination-identity receipt. Production capability must therefore remain false and ordinary format
  probes must start zero extraction. Deterministic coordinator/storage
  tests cover first-render ownership, re-entrancy, replacement serialization, post-drain cancellation,
  password settlement, exact nested publication, aggregate path ceilings, object/ADS/reparse/link/
  identity refusal, destination races, normal and late cleanup exceptions, exact-handle cleanup, app
  drain, and preservation on uncertainty.
- One real M6-E protocol-1.22 Worker test extracts an exact nested selected object through the internal
  seam, verifies output and unchanged-source hashes, maps fixed Activity state, removes the exact owned
  tree, and independently asserts that Rain Explorer exposes no copy/drag-out capability.
- Independent M6-F verification runs 47 focused capability/creation/rebuild/source-guard/real-Worker
  cases and all 239 Rain Explorer tests with zero failures/skips. It also requires a zero-warning/
  zero-error Release build, no known vulnerable package in the current direct/transitive NuGet advisory
  query, no `git diff --check` error, and clean scoped formatting for every cumulative changed C# input.
  Repository-wide unrelated historical formatting debt is not relabelled as part of that gate.
- The M6-F real protocol-1.22 Worker flow creates ZIP, TAR, TAR.GZ, TAR.BZ2, GZIP, and BZIP2 from an
  ordinary spaces-and-Unicode file. It separately creates all four directory-capable formats from a
  Unicode tree containing a nested payload, zero-byte file, and explicit empty directory; reopens/lists
  exact file and directory manifests; integrity-tests; extracts and hash-compares every payload; and
  preserves all source hashes. It then rebuilds one eligible ZIP to a distinct absent copy, proves the
  original hash unchanged, and leaves no Worker, testhost, fixture, staging, or actionable recovery
  record. Deterministic tests additionally cover downgrade/unsafe capability refusal, exact dispatch
  shapes, raw-stream one-file constraints, source kind/reparse/change checks, destination/self/race
  refusal, exact-operation cancellation, unexpected interactive-phase cancellation, bounded escaped
  Activity state, fixed errors, stale rebuild authority, and proof that the deleted in-process ZIP writer
  and path-based partial-output cleanup cannot return.
- Independent M6-exit verification runs 31 focused physical-extraction/authority/real-Worker cases and all
  262 Rain Explorer tests with zero failures/skips. Release build is zero-warning/zero-error; locked-mode
  restore commands succeed; the current direct/transitive advisory query has no known vulnerable package;
  cumulative formatting verification and `git diff --check` pass. Exact before/after recovery inventories,
  source/output hashes, scoped temp inventory, and Worker/testhost inventory prove zero new scoped residue.
- The M6-exit real-Worker and exact-package matrix covers content detection without a useful extension and
  with a misleading extension, nonarchive/corrupt/multipart refusal, full ZIP/7z/RAR/TAR.GZ/GZIP/BZIP2
  extraction, AES plus header-encrypted 7z/RAR, one wrong-password attempt without retry, bounded ordered
  multi-selection, deterministic sibling destinations, source immutability, full directory expansion,
  cancellation/drain, package recovery listing, and process return. Dedicated regressions prove the first
  and later sequential password callbacks return to the originating WPF dispatcher after asynchronous
  Worker-like work; header-encrypted extraction reuses the password-opened session; wrong-password handling
  consumes one slot without retry or extraction; and foreign operation events cannot update current-item
  Activity state. The physical context-menu `Extract here` command is available for any bounded ordinary-file
  selection, including extensionless files; normal Open/double-click/Enter remain extension-hinted. The
  current self-contained package is
  `artifacts/RainExplorer.M6ExitIntegratedPackage.1.2.3.CB0DC84F`: 455 files / 250,820,035 bytes /
  `038671F5F5EC4EF7B91241E29571874ADCC167C98549604335A7321C88D23285`, with private Worker tree
  `D1A666333CD9588F8C71C439F729BB66886CDED5805DD90E852A8A9055CE7F49` and byte-equal App/Worker
  Contracts `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`. Ten package-bound flows pass
  with no app-root SharpCompress/Worker-only assembly or forbidden native 7-Zip asset. The earlier
  `C243D42E` package remains untouched as superseded pre-remediation evidence. Manual direct GUI,
  keyboard/Narrator, Light/High Contrast (including the password dialog), DPI, ordinary-folder,
  physical-network, and clean-machine checks remain pending and are not inferred from automation.
- M7-A adds 24 deterministic association/activation/resource/version cases. Fake-registry tests seed WinRAR-owned
  defaults and commands, then require byte/value preservation while Rain Explorer writes only its owned
  HKCU ProgID, Registered Applications capability map, and OpenWithProgids values. They reject foreign
  ownership/name collisions before the first write, verify the exact quoted `"RainExplorer.exe" "%1"`
  command, ensure `UserChoice` and extension defaults are absent, cover every claimed terminal/compound
  suffix, refuse misleading `.zip.exe` and generic `.001`, and distinguish direct archive activation from
  ordinary-file reveal/selection. A resource regression also proves every production `Ic.*` XAML reference
  resolves. Two source-surface regressions require one Rain button plus the required Windows confirmation
  copy and exact 2.0.0 project/assembly/file/manifest/installer/About/update-fallback versions. The full
  unchanged-source rerun passes 286/286.
- The retained M7-A predecessor candidate is
  `artifacts/RainExplorer.M7ArchiveAssociationsPackage.2.0.0.E6C6A372`, with 455 files /
  250,832,467 bytes / tree SHA-256
  `9E9B37B3D5EA6F3106840F3103CD013F6D2B9437EC729DFE9A7DD7D519169C52` and private Worker tree
  `D1A666333CD9588F8C71C439F729BB66886CDED5805DD90E852A8A9055CE7F49`. Its manifest is 4,547 bytes /
  `37C4167A2D8DBFF298CD2D583166D491C2ED7DED3654F136916A4E31F9B1E51C`. Exact archive-argument launch
  creates the visible window and private authenticated Worker without changing live registry defaults.
  Direct Windows-default selection, format double-clicks, WinRAR context-menu preservation, reversal,
  installed behavior, and accessibility/clean-machine/release gates remain manual.
- M7-B adds seven deterministic cases on top of M7-A: three virtual Windows file-type/folder icon variants,
  folder-origin archive Back/Forward, direct archive-start Back seeding, missing archive-icon refusal before
  any registry write, and exact nine-frame ICO/package metadata validation. The focused combined navigation/
  icon/association suite passes 33/33 and the complete final-source Rain Explorer suite passes 293/293 with
  zero failures/skips. Release build, scoped format verification, `git diff --check`, and Main/test direct
  plus transitive advisory queries pass. Archive-row icon lookup is extension/attribute-only and never gives
  shell APIs an archive path, including for EXE/LNK/ICO-style per-file icon types.
- The retained M7-B predecessor candidate is
  `artifacts/RainExplorer.M7ArchiveNavigationIconsPackage.2.0.0.35A10DC0`: 456 files /
  250,875,089 bytes / tree SHA-256
  `B77CBD98E71836D910AC53969F9D9C9518F1E4EC70842784DCF78FEF8873C6CF`, with unchanged private Worker tree
  `D1A666333CD9588F8C71C439F729BB66886CDED5805DD90E852A8A9055CE7F49` and byte-equal App/Worker Contracts
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`. Its 5,756-byte manifest is
  `FEE42BEF45596275FFA04E7BA62A89415C81234EC4F92F40AAF184A8A6C30A4F`; source/package icon SHA-256 is
  `FC2DF9F1DE6125043308BB91A575D3B6F95E9752B5453E088ECB9B9151326CDD`. The matching uninstalled,
  unsigned v2.0.0 installer is
  `artifacts/RainExplorer.M7ArchiveNavigationIconsInstaller.2.0.0.35A10DC0/RainExplorer-M7-Archive-Navigation-Icons-Setup-2.0.0.exe`,
  76,124,799 bytes / `FD8A64082F3C06EC717F6BE51574AA4913F8AA3322357AA584B002FD460D3699`.
  Direct packaged GUI navigation/icon-cache, WinRAR preservation/reversal, install, accessibility,
  clean-machine, signing, publication, and release acceptance remain manual; the running installed app was
  deliberately not interrupted.
- M7-C adds deterministic coverage for source-name preservation and fixed structured-error mapping after an
  initial archive-open refusal; the complete six-format writer-qualified creation list and raw one-file
  constraint; explicit existing-parent extraction planning with distinct collision-free no-overwrite
  children; the documented `SHOpenWithDialog` export and Unicode/space path validation; and precise shell
  menu projection. Menu cases suppress only Rain-owned canonical commands and the duplicate Open With
  surface, collapse only exact repeated `WinRAR` labels, and retain unrelated same-label commands plus Edit,
  Explore, Find, Copy to, and Move to extensions. The focused archive-usability suite passes 108/108, the
  complete final-source suite passes 311/311, and 19/19 exact-packaged-Worker archive flows pass, all with
  zero failures/skips. Release build, scoped formatting, `git diff --check`, and Main/test direct plus
  transitive advisory queries pass.
- The retained M7-C predecessor candidate is
  `artifacts/RainExplorer.M7ArchiveUsabilityPackage.2.0.0.834E5F4A`: 456 files / 250,885,777 bytes / tree
  SHA-256 `D463B46E69EF4CBF6B50C0D781A5EB1CE2B7DA6FB45D0D72C4BD23BE9F14B2C5`, with unchanged 205-file /
  85,041,541-byte private Worker tree
  `D1A666333CD9588F8C71C439F729BB66886CDED5805DD90E852A8A9055CE7F49` and byte-equal App/Worker Contracts
  `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`. Its 6,728-byte manifest is
  `60CD60D923D640E95B249BDA989168C659846057E8F65706CE5CA5842F4437D1`. The matching uninstalled, unsigned
  v2.0.0 installer is
  `artifacts/RainExplorer.M7ArchiveUsabilityInstaller.2.0.0.834E5F4A/RainExplorer-M7-Archive-Usability-Setup-2.0.0.exe`,
  76,116,013 bytes / `246F7053C582FDDF08C792A33293EE1D439A3BD1EBC083B5CE2AE434BDA9FF7D`.
  Direct packaged Open With/context-menu/extraction/creation/error-state behavior, format opening, WinRAR
  preservation/reversal, install, accessibility, clean-machine, signing, publication, and release
  acceptance remain manual; the running installed app was deliberately not interrupted. The `8F24FD12`
  pair is an unsealed pre-final intermediate and must not be promoted. Final build-server shutdown leaves
  only the intentionally preserved installed Rain Explorer/private Worker pair; test/build processes are
  absent, and default creation recovery, extraction recovery, Preview Cache, and nested-workspace
  inventories are empty.
- M7-D adds the RAR5 directory-without-optional-CRC reader regression, fixed Rain Explorer error mapping,
  protocol-1.23 typed RAR capability/start/downgrade paths, seven-row conditional creation presentation,
  concrete-provider/post-queue authority, signed exact-path/version qualification, executable leasing,
  bounded private workspace/crash cleanup, encoder test plus exact digest readback, and no-replace publication.
  Rain Archives passes 934/934 and Rain Explorer passes 315/315 both normally and against the exact packaged
  Worker. All 18 current project advisory inventories, locked restores, scoped format checks,
  `git diff --check`, and zero-warning/error Release builds pass.
- The current M7-D Rain Explorer candidate is
  `artifacts/RainExplorer.M7RarRemediationPackage.2.0.0.266A39F2.01`: 458 files / 251,080,369 bytes /
  `37B03FE1E36B2BF2D647831F3AEB43C6598FA0599D7692E46FFDE45F1F948628`, with 207-file /
  85,185,437-byte private Worker tree
  `B523EBE613581D176389C0C9EF6E6C6D77DDFC866E3D5485845E8555DE5AF1C8` and byte-equal App/Worker Contracts
  `E71B945555031A9933EB0631BE260CFCA5CF9AA0257BEEF658BAD9319CB398EF`. Its 6,749-byte manifest is
  `6E9AE928C36F9E2F7C2FA718D24D6C6337B7054A825D168603553A409ECCF23E`. The matching uninstalled,
  unsigned v2.0.0 installer is
  `artifacts/RainExplorer.M7RarRemediationInstaller.2.0.0.266A39F2.01/RainExplorer-M7-RAR-Remediation-Setup-2.0.0.exe`,
  76,160,790 bytes / `AB7E8BE476990B2CC0EC0CFB86ED6353196AF2DD14B019D5231370B99CCE19CB`.
  Direct exact-package UI/shell/association behavior, qualifying-installed-WinRAR creation, install/upgrade/
  uninstall, accessibility, clean-machine, signing, publication, and release acceptance remain manual; the
  installed single-instance app was deliberately not interrupted.
- M6-C operation regressions require fresh visible-selection resolution, material-directory Worker
  expansion, bounded implicit-directory expansion, exact-frame preflight before secret allocation,
  capability/volume/downgrade refusal, distinct absent destination and destination-race preservation,
  exact-ID cancellation/terminal settlement, five-second client-teardown drain, bounded escaped
  activity text, fixed error mapping, source-change/Worker-loss invalidation, and immediate caller
  password-array zeroization. STA prompt tests cover accepted transfer, cancellation, unused capture,
  failed transfer, and explicit local-array ownership cleanup. The test that intentionally creates a
  raced password-refusal destination must remove only its own verified empty GUID directory and assert
  absence; unexpected contents remain evidence and fail the test.

## Mandatory automated cases

- Content detection with misleading/missing extensions; empty and zero-byte content; directory reconstruction; duplicate entry IDs; metadata bounds and unsupported getters.
- Ordinary/encrypted/wrong-password/header-encrypted behavior; concurrent password-slot create/store/consume/discard, expiry/reuse, cancellation between slot allocation and binary submission; no password in JSON, process arguments, environment, diagnostics, filenames, or test output.
- ZIP/7z/RAR/TAR-family reads as each phase enables them; solid sequential/random selection; every supported multipart naming pattern; missing/interchanged volume.
- CRC/checksum success and deliberate payload/checksum corruption. Raw GZIP fixtures mutate CRC32/ISIZE, compressed-TAR fixtures mutate the outer GZIP/BZIP2/XZ checksum/footer, and failed extraction must leave no destination or staging tree. A full decode without provider checksum validation is not a passing integrity test.
- Traversal using both separators; rooted, drive-relative, UNC, device, ADS, reserved, control, trailing-dot/space, case, Unicode-normalization, duplicate, file/directory prefix, and 8.3-alias collision attempts.
- Symbolic link, hard link, junction/reparse metadata, escaping link targets, destination-parent reparses, dangling reparses, parent/staging identity replacement, and destination appearance before commit. The automated Windows suite includes a between-files swap of an operation-owned directory for an outside-pointing junction and asserts that the second file is not written outside staging. A direct handle-commit test also proves that a target already present relative to the pinned parent is preserved and the staging tree is not renamed over it.
- Declared-size under/overrun, integer overflow, huge entry count, deep hierarchy, metadata expansion, unknown-size output, extreme expansion ratio, malformed header, truncated stream, and decoder exception translation. Preview tests must independently cap actual decoded bytes when provider metadata understates size and must reject inconsistent terminal totals.
- Explicit nested opening from content rather than extension; depth-three success/depth-four refusal; per-entry, ancestry-chain, and global-active-byte limits; declared and actual byte overruns; wrong/correct nested password recovery; malformed child payload; inherited download provenance; cancellation inside provider copy; parent/client/worker shutdown cleanup; and cleanup refusal when unexpected same-user content appears. No listing/search test may trigger nested decoding implicitly.
- Cancellation while queued, opening, reading metadata, inside a blocked provider preview read, between files, mid-copy, checksum finalization, provenance write, and immediately before commit. Verify no final destination and identity-safe staging cleanup; preview disposal must receive bounded control acknowledgement, release its session reservation, and leave the same worker responsive.
- ZIP creation across Store/Fast/Balanced/Maximum and the zero-file case; mixed explicit-file/directory-root input; root and empty-directory preservation; deterministic order; Unicode and zero-byte sources; exact round-trip contents; the exact 4,096 expanded-entry boundary; unsafe, duplicate, case/NFC, parent-spelling, and file/directory-prefix archive names; invalid/relative/device/drive-relative/ADS/reserved/trailing-dot-or-space sources and destinations; overlapping/aliased source roots and destination-inside-tree attempts; nested reparses and enumerated-child identity substitution; accepted ordinary fully qualified UNC syntax with actual remote behavior kept as a separate gate; source replacement/tree mutation during copy and after provider finalization; destination appearance/existing destination; provider failure; timestamp conversion/clamping; cancellation after admission and during copy; staging cleanup; no-replace commit; and same-worker recovery after every refusal/failure.
- Disk reserve crossing, true disk-full, denied parent/file/ADS writes, locked source, source replacement/mutation, archive self-overwrite, worker kill, pipe loss, client kill, and graceful close.
- Creation-recovery journal admission before staging; exact phase ordering and immutable operation/destination/parent/staging identity; checksum mismatch, missing required timestamp, torn-tail fallback, invalid transition, unknown content, bounded inventory, exclusive liveness, handle-relative non-reparse local-root/operation/journal access, exact ordinary cleanup, preservation of unexpected content, and real worker-kill/restart reporting without destination access or automatic unresolved-record deletion. Protocol-1.9 cases cover path-free sorted listing, explicit exact/missing/replaced/length-mismatched/wrong-kind/hard-linked/multiple/torn evidence, byte-preserving inspection, omitted-field rejection, feature/downgrade dependencies, queued cancellation, and owned-worker teardown for a synthetic stuck response. Protocol-1.10 cases cover the four closed disposition actions, exact phase/sequence/timestamp concurrency, stale and conflicting intent refusal before mutation, handle-relative hard-link and parent-rename defenses, durable intent/completion receipts, same-action replay after destination-path loss, torn first-receipt restart, malformed-receipt preservation, request/response wire validation, minor-9 pre-frame/pre-destination refusal, queued cancellation, and bounded owned-worker teardown. Retention cases require completed receipts not to consume the actionable listing bound, newest-count and age expiry, retained replay, path-free not-found after expiry, complete-scan authority for count pruning, pre-mutation cancellation, and preservation of malformed, unexpected, reparse, active, or hard-linked state. A real Worker/IPC startup case removes the destination parent before pruning an expired receipt. Two exact-PID hard-kill cases wrap the production `WorkerProgram`, negotiate the authenticated Client/Worker protocol, issue the real disposition request, block only at the injected durable-intent or artifact-mutation callback, kill the authenticated Worker PID, require `worker.disconnected`, and prove that a fresh ordinary Worker commits exactly once or reconciles the already-published final before path-independent replay. WPF-model cases additionally require path-free first listing, explicit inspection before destination disclosure, exact evidence-to-action mapping, inspection-only capability behavior, default-No confirmation with zero request on refusal, exact inspected revision forwarding, no retry after an uncertain/stale result, escaped bidi/control presentation, and cancellation/drain when the dialog closes.
- Protocol-1.11 nested-workspace recovery cases cover path-free sorted/unique/bounded listing, exact verified and empty-ownerless inspection, discard plus absent replay, active-owner refusal, source mutation after review, unexpected content, named streams, workspace/source reparses, hard-linked source files, excessive source count, an oversized sparse source, identity-unavailable or incomplete assessment, minor-10 pre-frame/pre-root refusal, required cancellation dependency, malformed request/response evidence, and queued pre-mutation cancellation. A packaged Worker and authenticated `ArchiveClient` complete a real list/inspect/discard round trip. A non-shipping wrapper then pauses the production Security path only after its first exact source deletion; the test externally kills that authenticated Worker PID, requires `worker.disconnected`, and proves a fresh ordinary Worker reports the smaller tree as suspicious and preserves every remaining object. No test treats a name pattern, owner token, revision alone, or partial deletion as automatic cleanup authority.
- Protocol oversize/empty/unknown frames, single-pass serialization overflow before any frame bytes are written, encoded-byte-budgeted listing pagination, unknown JSON members, malformed payloads, unknown message, missing feature, major/minor mismatch, wrong request ID/kind, noncontiguous request/response/event sequences, unauthorized operation IDs, cancellation before and during frame admission, duplicate/post-terminal events, late cancelled response, slow/re-entrant client callbacks, and concurrent/idempotent disposal. Entry-stream cases additionally cover minor-4 refusal, canonical pipe/nonce validation, fixed-time hello proof, exact worker-PID rejection of a same-user impostor, malformed/oversized frames, structured terminal errors, non-reused IDs, decoded-byte underdeclaration, eight-lease admission/ninth refusal, password retry/secrecy, blocked-read cancellation, archive-close ownership, and client/worker shutdown.
- Protocol-1.19 operation activity cases cover exact event/cancellation dependencies, minor-18 local and
  pre-deserialization refusal, stable path-free JSON, unknown/missing-member and oversize rejection,
  active-to-terminal lifecycle, one-concurrent/32-active-or-queued/64-retained/96-total bounds,
  newest-first terminal retention with truncation, event/snapshot ordering, malformed unsafe flags,
  duplicate IDs, invalid sorting/progress/timestamps, a real out-of-process Worker creation, original
  hash preservation, and transport teardown for an invalid authenticated response.
- Protocol-1.20 extraction-recovery cases cover all five durable phases, local-only path-free startup,
  explicit one-record destination access, stable identity/type/link/reparse/ADS/count/byte/depth/
  content-revision validation, torn/change/excess/inaccessible/ambiguous preservation, false
  mutation/retry/cleanup flags, unknown/missing/oversize/dependency/downgrade rejection, ordinary
  terminal cleanup, and exact-PID loss with unchanged restart evidence. No fixture treats inspection
  as disposition or retries an uncertain extraction.
- Protocol-1.21 independent nested reopen cases cover fresh workspace/source/revision/count/byte
  evidence, liveness/ancestry/depth/identity/type/link/reparse/ADS refusal, exact-case selected source,
  content copy/flush/redetection, a new depth-zero session with zero chain bytes, no temporary-path
  disclosure, unchanged recovery source hash/evidence, cancellation compensation, CLI routing, real
  Worker listing, minor-20 local/pre-deserialization refusal, unknown/missing/oversize/dependency/
  malformed response handling, retained-1.18 refusal, and exact-PID kill immediately after copied-
  source flush followed by path-free restart and fresh reopen/list.
- Protocol-1.22 support-diagnostics cases require the exact fixed enum/numeric/boolean shape, truthful
  32/one/64/96 activity policy, cancel-only controls, false retry/pause/reorder/restart persistence,
  false automatic-recovery disposition, same-user Job containment with `IsSameUserSecuritySandbox=false`, and
  zero free-form/path/error/secret/recovery-authority fields. Golden JSON, unknown/missing/oversize,
  invalid enum/bounds/coherence, dependency, minor-21 downgrade, retained-binary, CLI, real Worker,
  mixed-package refusal, and local self-contained package-flow cases are mandatory.
- Lifecycle/cleanup cases cover terminal cancel refusal under the lifecycle lock, 33rd-operation
  admission refusal with password-slot settlement, queued cancellation, pre-response start bounded
  owned-Worker abort, post-response ID-targeted cancel, graceful shutdown, exact-PID crash points,
  original/output hashes, and zero ordinary residue. Every recovery family must preserve active,
  changed, malformed, identity-unavailable, excessive, inaccessible, partial, hard-linked, reparsed,
  streamed, or ambiguous state; only exact verified creation `Completed` metadata may use the bounded
  newest-256/30-day maintenance rule.

## Windows manual matrix

- Supported Windows builds on NTFS plus one filesystem without ADS; local fixed disk, removable/network path where supported, long paths on/off, standard user, high contrast, 100/150/200% scaling, and x64 clean machine without installed .NET or 7-Zip.
- Verify extensionless open, encrypted prompt/retry, solid and multipart warnings/refusals, low-space messaging, locked/mutated source, worker crash recovery, Mark-of-the-Web propagation, no elevation, and no persistent worker after all clients close.
- For creation, verify every supported ZIP/TAR/TAR.GZ/TAR.BZ2/GZIP/BZIP2 preset and input shape, empty archive output where applicable, exact-one-source raw refusal, mixed file/folder trees with empty directories, Unicode/zero-byte files, local/UNC/8.3/mapped-drive root aliases, a filesystem or server without usable child file IDs where available, source mutation, cancellation, destination ACL denial, low/disk-full behavior, an actual SMB source/destination/interruption, no staging residue after ordinary failure, and honest post-commit state after a worker crash. Do not infer these manual results from extraction coverage, loopback UNC tests, or the local automated package.
- Use a disposable test account/VM for reparse/device/path-race tests. Never aim destructive fixtures at real user folders.

## Protocol 1.17/1.18 managed creation completion gate (2026-08-26)

- Protocol 1.17 adds bounded Store-only TAR explicit/tree creation and TAR capabilities through new
  DTOs, message kinds, serializer registrations, and features. Protocol 1.18 adds separate TAR.GZ/
  TAR.BZ2 explicit/tree routes, raw GZIP/BZIP2 exact-one-source routes, and managed capabilities.
  All minor-16 ZIP shapes remain unchanged; retained/downlevel peers refuse newer requests before a
  frame or filesystem access. Unknown members, oversize frames, missing dependencies, and malformed
  capabilities fail closed.
- Provider/Security coverage proves Unicode, zero-byte and empty directories, UTC/PAX timestamp
  precision, unsupported TAR metadata/kinds, exact short/long source refusal, cancellation, one-shot
  finalization failure, same-length staged-payload corruption, trailing/junk/concatenated-member
  refusal, format-specific TAR/PAX maximum-output estimates, 4,096-entry and 64-GiB policy, raw
  one-file/directory/multiple refusal, source/provenance mutation, destination race, low-space,
  recovery checkpoints, original/existing-destination preservation, and cleanup.
- Real Worker/Client/CLI/WPF-model output reopens through Rain for exact list/integrity/extraction and
  payload SHA-256 checks. Hash-pinned official 7-Zip Extra 26.02 independently tests/extracts every
  qualified TAR.GZ/TAR.BZ2/GZIP/BZIP2 preset corpus, and the qualified external TAR path validates
  uncompressed TAR. Archive hashes are unchanged before/after external and Rain validation.
- `artifacts/RainArchives.ProtocolCompatibility.1.17.64081B70` retains genuine minor-17 Contracts,
  Client, Worker, harness, and fixture binaries. Reflection/probe evidence proves every 1.18 type/
  feature is absent. The current↔retained matrix passes 14/14 directions/boundaries, including real
  TAR creation through the older Worker and pre-access refusal of all 1.18 routes. This artifact is
  intentionally binaries-only: it contains no exact 1.17 source snapshot/fingerprint and makes no
  source-checkpoint claim.
- The retained Milestone 3 local self-contained `win-x64` package is
  `artifacts/RainArchives.M3AutomatedPackage.1.18.1C85F6D9`. Its 1,083-byte
  `package-manifest.json` has SHA-256
  `6A8CB71E3924E04451299E9C4C4AB2F022D9648FCFE8113186656C6F0D0FF47A` and binds protocol 1.18,
  132 source inputs, App/Worker tree identities, and seven principal files. App tree: 609 files,
  232,216,977 bytes,
  `C61749B2CC94A4E66606D3615DF88C44A2D95F599EE1F2B78C812BAC1BCFCFB0`. Private Worker: 205 files,
  84,699,121 bytes,
  `36C64503CC9EA9FD6CF0F6C390F75F8871BEF9BFCA5461456CE645CBACB2966F`. App/Worker Contracts are
  byte-coherent at `BFDC6FB719D71BF4B85CC7471F6B95116F5B66E5A26C5EFCFADB7D9B848E2E91`, and forbidden
  `7z.dll`, `7za.exe`, and `SharpSevenZip.dll` count is zero.
- The mandatory package flow covers ZIP, TAR, TAR.GZ, TAR.BZ2, GZIP, and BZIP2 creation collectively
  through the private packaged Worker. Production Client covers all five new managed formats, the
  in-process CLI covers ZIP/TAR.GZ/BZIP2, and representative WPF-model routes cover ZIP/TAR.GZ/GZIP,
  alongside existing ZIP rebuild coverage. It verifies exact capabilities/presets/unsafe false flags,
  Rain reopen/test/extract hashes, immutable archive hashes, original preservation, and zero ordinary
  staging residue.
- Final source verification passes locked restore, a nuget.org direct/transitive no-known-
  vulnerability inventory for all 15 projects, formatter verification, a zero-warning/error Release
  build, 358/358 unit/WPF, 29/29 adversarial, and 293/293 integration tests: 680/680, zero skips.
  Canonical production identity is 132 inputs and
  `1C85F6D929DE5F940E1357C72C210A8647DFD2E9F38A0A2B0B0832127F5CE844`.
- Build servers shut down cleanly. A read-only ordinary-Windows process query found zero workspace-
  attributable Rain App/Worker/CLI/testhost/dotnet processes; workspace staging count was zero; the
  superseded pre-fix package candidate was removed; retained historical evidence was preserved.
- Codex's restricted filesystem context can deny native ancestor-handle opens and MSBuild Up2Date
  timestamp writes with access denied. The identical accepted gates were rerun in ordinary Windows
  context without weakening code or tests. A separate read-only audit and targeted re-review passed
  after requiring full BZIP2 member consumption and correcting the format-specific folder-picker
  title. Final lifecycle regressions also prove extraction/integrity slots are settled before terminal
  publication and that an authoritative cancelled Open response cannot be converted into a transport
  abort by a redundant queued discard. The focused slot case passed ten rebuilt repeats; the cancelled
  200,000-entry Open case passed 30 rebuilt repeats before the complete suite.
- ZIP encryption, TAR.XZ/raw XZ, native 7z creation, split output, and overwrite/replacement/in-place
  update remain explicit negative capabilities at that protocol-1.18 checkpoint. Protocol 1.23 later adds
  only conditionally gated unencrypted single-volume RAR5 creation through a separately installed encoder.
  Current-source ZIP and exact-
  package mixed-format performance probes pass against `1C85F6D9`. Exact-current evidence now covers
  fresh offline Sandbox startup, standard-user ACL denial, early/near-final true NTFS disk-full, all
  supported exFAT preset families, and local 8.3/mapped-drive/alternate-loopback-UNC paths. Direct
  product-owner direct exact-package ZIP creation/reopen/integrity and Rebuild Copy observation now
  passes with the expected warning. The submitted staging placeholder proves no residue state.
  Recovery Center, DFS/no-file-ID/second-
  machine-SMB/blocked-UNC, product-owner current-flow accessibility, installer, signing, and
  publication checks remain `manual_pending`.

## Protocol 1.20 extraction staging recovery gate (2026-08-27)

- Minor 20 adds only `extraction-staging-recovery-inspection-v1`, with distinct bounded list and
  inspect request/response kinds. The feature depends on new-destination extraction, source
  fingerprint validation, and request cancellation. Minor 19 refuses both requests locally before
  sending and in the Worker before payload deserialization or filesystem access.
- Tracked extraction durably appends checksummed `Admitted`, `Writing`, `Finalized`, `CommitIntent`,
  and `Committed` records. Startup scans only the bounded local fixed-drive journal root and returns
  path-free opaque summaries. Missing or malformed children do not suppress valid siblings, and no
  startup scan contacts a recorded destination or mutates unresolved state.
- Explicit one-record inspection reparses the journal, pins exact-case destination ancestry and
  direct artifacts without following reparses, and requires ordinary types, nonzero native
  identities, one-link files, exact allowed-ADS shape, and bounded object/byte/depth totals. Its
  deterministic revision includes default-stream and allowed provenance-stream content hashes;
  same-length file or ADS mutation therefore invalidates reviewed evidence.
- Torn tails, unknown JSON members, zero/unavailable identities, unexpected ordinary files, hard
  links, ADS, changed shape/content, excessive trees, inaccessible objects, and ambiguous commit
  evidence remain incomplete or suspicious and preserve both staging and journal state. Ordinary
  terminal cancellation/failure removes only exact operation-owned state; transport loss never
  authorizes retry or cleanup.
- Client validation independently enforces list bounds, phase/assessment/evidence consistency,
  nonzero identities, stable exact JSON shapes, and false mutation/retry/cleanup flags. Tests cover
  unknown and missing members, both oversize responses, malicious torn-tail actionability, queued
  cancellation, terminal cleanup, and owned transport teardown.
- Real authenticated Worker/Client tests cover path-free inventory, explicit read-only inspection,
  exact-PID process death during `Writing` and after publication, fresh-Worker restart inspection,
  original/destination preservation, and the immutable Milestone 3 package flow. The retained exact
  protocol-1.18 matrix passes 17/17 while refusing both minor-20 requests before access.
- Final verification passes locked restore, a direct/transitive no-known-vulnerability inventory,
  formatter verification, a zero-warning/error Release build, 398 unit/WPF, 29 adversarial, and 320
  integration tests: 747 total, zero failures/skips. Canonical production identity is 135 inputs at
  `4806DA8CD71AA4C4C42BF9BA8DF3A2DBCE3F817DB9CD1D5CC29154F8DD12B591`.
- This slice changes no archive bytes or format capability and creates no replacement package. The
  immutable Milestone 3 package remains bound to protocol 1.18 and `1C85F6D9`. Actual second-machine
  SMB, blocked UNC, DFS, no-file-ID, and non-ADS filesystem checks remain `manual_pending`; no local
  or loopback evidence is promoted into those physical claims.

## Protocol 1.19 bounded operation activity gate (2026-08-26)

- Minor 19 adds one distinct `operation.activity.get` request and `operation-activity-v1` feature;
  all 1.18 DTOs, messages, features, capabilities, and managed writers are unchanged. The feature
  depends exactly on operation events and request cancellation. Minor 18 refuses locally before a
  frame and the Worker refuses before payload deserialization or filesystem access.
- The response truthfully reports 32 active/queued admissions, one concurrent execution, newest-64
  terminal retention, unspecified non-reorderable queue presentation, and false pause/retry/
  reordering/restart-persistence. The combined ceiling is 96 records. Client validation enforces
  the active and terminal limits separately in addition to the frame and combined bounds.
- Snapshots contain only typed operation ID/kind/phase, successfully sent event sequence, canonical
  UTC lifecycle timestamps, numeric progress, warning count, and cancelability. Reflection and JSON
  regressions prove that source/destination/temp paths, entry IDs/names, warning/error text, secrets,
  recovery IDs, provider details, and archive-controlled text are absent.
- Worker tests prove an active blocked creation becomes one retained terminal record, 65 completed
  operations retain only the newest 64 and set `HistoryTruncated`, and malformed downgraded payloads
  do not reach parsing. A real out-of-process Client/Worker test creates a ZIP, waits for terminal,
  queries retained activity, preserves the source hash, and leaves the destination authoritative.
- `artifacts/RainArchives.ProtocolCompatibility.1.18.1C85F6D9` retains the exact 132-input
  protocol-1.18 source snapshot, 205-file private Worker, captured Client/Contracts binaries,
  rebuildable retained-client recipe, fixture, and managed pair proof. The full compatibility class
  passes 17/17 cases: current Client/retained Worker and retained Client/current Worker preserve
  prior ZIP/TAR/managed operations, while 1.18 refuses activity before access.
- Fresh Release verification passes formatter, a zero-warning/error solution build, 365 unit/WPF,
  29 adversarial, and 307 integration tests: 701 total, zero failures/skips. Canonical protocol-1.19
  production identity is 132 inputs and
  `F800A94D031F999F74B9D5AB157855560507B42EBBA62997E3DC71B41CF48FC2`.
- This slice changes no archive bytes or format capability and adds no dependency. External archive
  conformance and a new self-contained product package are therefore not relabelled; the immutable
  M3 package remains bound to protocol 1.18 and fingerprint `1C85F6D9`.

## Current Milestone 3 physical Windows evidence (2026-08-26)

- A package-bound non-shipping runner verifies the current `1C85F6D9` source, manifest, App/Worker tree, principal
  binaries, and forbidden-asset absence before every claim. Its retained self-contained runtime and
  evidence live under `artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9`.
- Fresh offline Windows Sandbox evidence SHA-256
  `505B6504A5D67E9D114F6E1121FE547D860ACD07B0B56BCF419595E9FA4CC2A4` proves self-contained App
  startup/ordinary close and packaged ZIP/TAR/TAR.GZ/TAR.BZ2/GZIP/BZIP2 creation followed by Rain
  reopen/integrity/extraction/hash checks, with zero Rain process or staging residue.
- Combined disposable-storage evidence SHA-256
  `9CFEC377120A3C1194EB1B72215062612C01475FB8C6BDB272A6E10A69B85924` proves a non-elevated ACL
  denial as `filesystem.permission_denied`, early and near-final actual Win32-112 NTFS exhaustion as
  `resource.insufficient_space`, explicit path-free resolution of the two resulting absent-artifact
  journals, same-worker follow-up health, and 15/15 supported preset cases on exFAT with
  `provenance.not_applied`, no ADS, exact Rain round trips, and zero ordinary residue.
- The C400 physical runs found and closed authoritative aggregate-error classification and RFC-1952 zero-
  MTIME handling defects before the accepted reruns. An initial live UI Automation attempt verified
  the generic `Create archive` labels/help but hit a host input boundary at `GetCursorPos`. After that
  boundary was removed, the later BC predecessor package completed native-picker ZIP creation, reopen/integrity,
  Rebuild Copy, rebuilt reopen/integrity, and direct empty-state inspection of both Recovery Center
  tabs. Pinned Windows `tar.exe` independently extracted both GUI outputs to the original checked hash.
  The exact-current App/window tree was observed, but this host again rejected both click and key input
  before delivery with `GetCursorPos` access denied; that attempt is evidence, not a completed GUI flow.
- Exact-current path probes complete raw GZIP from a real generated 8.3 source, raw BZIP2 through a
  reversible `subst` drive, and TAR.GZ across `\\localhost\C$`/`\\127.0.0.1\C$` aliases of one local
  target. Every Rain reopen/integrity/extract hash matches; sources remain unchanged; mappings, exact
  disposable roots, staging, recovery, and processes are absent afterward. DFS, a no-file-ID server,
  blocked UNC, and actual remote SMB are not inferred from these local aliases.
- An explicit-path GNU suite adds tar 1.35 list/extract coverage for TAR/TAR.GZ/TAR.BZ2 plus gzip
  1.14 and bzip2 1.0.8 integrity/decompression coverage for the raw formats. The three executable
  SHA-256 values are retained with the corpus; all five payload hashes match, every archive remains
  immutable, and recovery/staging/process counts are zero. WinRAR 7.20 separately integrity-tests and
  extracts all six formats with the same hashes; every exact launched process settles. A distinct
  `bsdtar`/libarchive 3.8.9 build additionally lists/extracts the four simple archive containers and
  three package-created PAX TAR-family layouts containing a long Unicode path, empty directory,
  zero-byte file, and fractional timestamp. All seven manifests/hashes pass and the archives remain
  immutable. The public SharpCompress descriptor differs from the exact staged PAX timestamp by one
  100 ns tick; the evidence records that reader conversion boundary while Security's BCL staged
  validator retains exact pre-publication comparison. A fixed-source-timestamp repeat differs only in
  the five BCL-generated PAX helper names/checksums that include the Worker PID; material paths,
  content, and metadata repeat, but byte-reproducible PAX containers are not claimed. Combined with
  pinned official 7-Zip and Windows
  libarchive 3.8.4, this closes the finite M3 external-reader matrix; broader release corpus work stays M8.
- The current `1C85F6D9` rebuilt ZIP probe retains 40 measured samples and ten warm-up/cancellation external
  checks. A separate exact-package mixed probe retains 75 measured samples across all 15 supported
  format/preset cases and 90 external checks. Both current probe JSON files live under
  `artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/performance-v1`; every output passes Rain
  and hash-pinned official 7-Zip;
  evidence hashes and methodology are in `benchmarks.md` and `milestone-3-manual-windows.md`.
- Exact fixture, storage geometry, operation results, evidence links/hashes, cleanup boundaries, and
  remaining remote/manual/release gates are in `milestone-3-manual-windows.md`.

## Protocol 1.16 ZIP write capabilities, presets, and package gate (2026-08-25)

- Protocol minor 16 adds the distinct `zip-write-capabilities-v1` query and
  `archive.zip.write.capabilities` message. A global query reports managed-writer creation support;
  an opened-session query reports exact rebuild eligibility. Both report the supported add/replace/
  delete/file-rename/tree-edit/metadata operations, four built-in compression presets, current
  entry/decoded-byte bounds, and explicit false values for encryption, filename encryption, split
  output, existing-destination replacement, and in-place update. Older request/response shapes remain
  unchanged, and Client/Worker validation refuses foreign, closed, incoherent, or downgraded state
  before source or destination access.
- CLI and WPF consume the typed response before picker, source, or destination work. Global and
  session checks repeat at the write boundary; missing, downgraded, unsupported, or operation-
  incompatible capability state is a refusal, never a hard-coded managed-writer assumption. The
  shared preset surface exposes immutable Store/Fast/Balanced/Maximum built-ins and user presets.
- The App-only `ZipPresetStore` persists only supported compression and metadata defaults in a
  128-KiB/64-user document at `%LocalAppData%\SailHub\Rain Archives\settings\zip-presets.v1.json`.
  Tests cover the accepted exact 128-KiB boundary, next-byte refusal, and exact name/
  Unicode bounds, safe defaults, malformed/future/oversized/interrupted fallback, reparse refusal,
  per-user locking, CreateNew plus Flush(true), atomic replacement, one backup, failed-save
  preservation, cleanup, and concurrent writers. Defaults synthesize metadata only for newly mapped
  entries; explicit per-entry metadata wins. No secret, source/destination path, recovery ID, or
  untrusted archive text enters the store.
- The mandatory package flow passes against
  `artifacts\RainArchives.M3AAutomatedPackage.1.16.8E6E9D17`: the self-contained `win-x64` App
  launches only its private packaged Worker, App/Worker/Contracts composition is exact and
  byte-coherent, forbidden native assets are absent, and packaged Client/CLI/WPF-model create plus
  every completed rebuild route pass authoritative Rain reopen/list/integrity/extraction, original
  byte preservation, and staging cleanup checks. This is local package evidence, not installation,
  signing, publication, clean-machine, or direct GUI acceptance.
- The exact App tree is 609 files / 231,790,213 bytes at aggregate SHA-256
  `1507A0E6E9072B1A8A95948949E73E689ACF793235C3D88C6D5BB1D4826BDDB9`. Its private Worker subtree
  is 205 files / 84,437,425 bytes at
  `BE8001F3453C63C2C63858E0DEE8128FC4465C35345AC89C35CBE5498FAEDDA0`; App and Worker Contracts
  copies are byte-identical at
  `CDB5C66D11DC990B47B1E25AAB4BD0FE35710AA32562BBE71315D9FD82EB4714`, and the exact forbidden
  native-asset count is zero. `package-manifest.json` pins these identities and the mandatory test
  recomputes them before any packaged flow.
- The retained binary compatibility artifact
  `artifacts\RainArchives.ProtocolCompatibility.1.16.8E6E9D17` passes the current/retained 1.12,
  1.13, 1.14, 1.15, and 1.16 matrix in both directions: 10/10 checks, including common operations
  and pre-access refusal of newer operations on older peers. A separate read-only review found and
  closed the CLI global-preflight, public-WPF-boundary, and client session-capability validation
  findings with regression tests; no actionable issue remains.
- The 1.16 compatibility checkpoint retains 306 read-only source files / 4,467,670 bytes at ordinal
  aggregate SHA-256 `36264AC45C6FFA8FBA31F6146C3A71606BF47E6844BE2FD675B515ABA110ED89`,
  plus its exact self-contained Client harness, Worker, and fixture. Its retained pair self-test reports
  `PASS protocol=1.16` with metadata and capability markers and byte-identical source preservation.
- Final source verification passes 307 unit/WPF, 29 adversarial, and 263 integration tests: 599/599
  with zero failures and zero skips. Locked restore, a fresh no-known-vulnerability inventory,
  formatter verification, and the zero-warning Release build pass. The canonical production
  fingerprint is 128 inputs and SHA-256
  `8E6E9D171C82841E804C03285D86B119122A61A339F6D00331054BE916AC6258`.
- Build servers shut down cleanly. The final audit found no Rain Archives App, Worker, testhost, new
  test-owned temporary child, or workspace staging item; older retained temp evidence was identified
  by timestamp and preserved.

## Protocol 1.15 ZIP Rebuild Metadata source gate (2026-08-25)

- Protocol minor 15 adds distinct `zip-rebuild-metadata-v1` request/message/feature contracts. A
  directive targets either one retained persistent key or one mapped final path/kind after the same
  closed file/tree namespace plan. Client and Worker both enforce exact target shape, valid
  timezone-less DOS calendar tuples, even seconds from 00 through 58, the 1980-2107 year range, the
  ReadOnly/Hidden/System/Archive low-word mask, duplicate-target refusal, 4,096 directives, the
  combined 512-KiB path budget, and the 1-MiB frame ceiling before source access.
- Provider proof covers every one of the 16 allowed file masks and timestamp boundaries through the
  BCL writer/reader, then compares compatible official 7-Zip Extra 26.02 listing. Real Worker tests
  combine metadata with delete/add/replace/file-rename/tree-move and verify the exact final raw DOS
  tuple and forced-kind low word by staged readback, Rain reopen/integrity/extraction, and independent
  readers. Corrupted metadata readback fails with `zip.rebuild_output_manifest_mismatch`.
- CLI accepts only exact invariant `YYYY-MM-DDTHH:mm:ss`; WPF uses **Archive time (timezone
  unavailable)** and never presents provider UTC projection as the unavailable raw current tuple.
  Mapped metadata rows follow edited file/folder targets and final retained-tree moves, while escaped
  display-only values keep raw matching, editing, and IPC authority unchanged.
- Cancellation, exact-PID crash/recovery, same-worker health, byte-identical original preservation,
  downgrade/unknown-member/oversize refusal, and retained 1.12/1.13/1.14 compatibility all pass. A
  separate read-only review plus remediation re-review closed six accepted compatibility, wording,
  escaping, lifecycle, and NFC-remapping findings with regressions; the final review found no
  remaining actionable issue.
- The complete checkpoint passes locked restore, a fresh no-known-vulnerability inventory, formatter
  verification, zero-warning Release build, 293/293 unit/WPF, 29/29 adversarial, and 251/251
  integration tests: 573/573 with no skips. Canonical production identity is 125 inputs and SHA-256
  `6F595F0D914E156685767181E063E455826B4621400395628D91C9948F1F4F78`.
- `artifacts\RainArchives.ProtocolCompatibility.1.15.6F595F0D` retains the exact self-contained
  `win-x64` Client harness, Worker, fixture, and source snapshot. Comments, extra fields, ACLs,
  owners, ADS, creation/access times, POSIX modes, links, original compression methods, and arbitrary
  external attributes remain unsupported. This is binary/source compatibility evidence, not an
  installable product package, clean-machine result, or manual GUI/accessibility acceptance.

## Protocol 1.14 ZIP Rebuild Tree Edit source gate (2026-08-25)

- Protocol minor 14 adds `zip-rebuild-tree-edit-v1` plan/start routes plus a distinct bounded `zip-rebuild-source-folder-review-v1` query. Retained subtree move/delete, exact file operations, and mapped file/tree additions resolve into one final namespace before staging. The Worker recomputes exact subtree revisions from the leased source, and source-folder review uses pinned non-reparse handles, single-link regular-file checks, exact root snapshots, aggregate 4,096-entry/64-GiB limits, and a 512-KiB path budget without WPF enumeration.
- Coverage includes empty/synthetic/explicit directories, Unicode/deep trees, stale/overlapping/cyclic/self-descendant actions, case/NFC and file/directory collisions, mixed file/tree edits, source mutation, reparse/hard-link/ADS attacks, cancellation, destination race, low space, exact-PID crash/recovery, same-worker health, byte-identical originals, authoritative Rain reopen/list/test/extract, official 7-Zip Extra 26.02, and Windows `bsdtar`. A separate read-only remediation review found no actionable issue after incremental planner limits, bounded trie/radix ordering, parent-file namespace regression, and Worker-owned folder-count fixes.
- The complete checkpoint passes locked restore, a fresh no-known-vulnerability inventory, formatter verification, zero-warning Release build, 284/284 unit/WPF, 29/29 adversarial, and 243/243 integration tests: 556/556 with no skips. Canonical production identity is 123 inputs and SHA-256 `4B5F3A57BFD10C6919EB34C8E35C2330D22A7643996EA4EA4F4AF27EA1B0F10A`.
- `artifacts\RainArchives.ProtocolCompatibility.1.14.4B5F3A57` retains the exact self-contained `win-x64` Client harness, Worker, fixture, and source snapshot. Its protocol-1.14 pair self-test passes, and current/retained-1.12/1.13 cross-version tests prove common negotiated operations plus local pre-access refusal for newer tree/review routes. This is binary/source compatibility evidence, not an installable product package, clean-machine result, or manual GUI/accessibility acceptance.

## Protocol 1.13 ZIP Rebuild Rename source gate (2026-08-25)

- Protocol minor 13 and negotiated feature `zip-rebuild-rename-v1` add one distinct retained-file rename/move operation without changing protocol-1.12 copy semantics. Exact normalized path plus duplicate ordinal identifies one retained regular file; delete, replace, and rename are mutually exclusive for that key, while simultaneous swaps are allowed only after the complete final namespace passes traversal, Windows alias, case/NFC, duplicate, directory, and prefix-collision validation.
- Real Worker/Client/CLI/WPF-model cases combine rename/delete/add/replace. Failure evidence covers cancellation after progress, destination race, runtime low space, exact-PID Worker death with reviewed recovery state, same-worker health, no ordinary staging residue, and byte-identical original preservation. Successful outputs reopen through Rain for exact list/integrity/extraction checks, and the pinned hash-verified official 7-Zip Extra 26.02 tests and extracts every preset with exact paths and payload hashes.
- The complete checkpoint passes locked restore, a fresh no-known-vulnerability inventory, formatter verification, zero-warning Release build, 263/263 unit/WPF, 29/29 adversarial, and 217/217 integration tests: 509/509 with no skips. Canonical production identity is 121 inputs and SHA-256 `4A27632C1A32086663B972414A1F859BB40C9EBBAFEF53CB188AFA53B53FAD41`.
- `artifacts\RainArchives.ProtocolCompatibility.1.13.4A27632C` retains the exact self-contained `win-x64` Client harness and Worker. The retained pair passes open/list/integrity/create/rebuild and source-hash checks; current/retained-1.12 cross-version tests pass the common negotiated surface and refuse rename locally against the older Worker. This is binary compatibility evidence, not a product package, clean-machine result, or manual GUI/accessibility acceptance.

## Protocol 1.12 ZIP Rebuild Copy source gate (2026-08-24)

- Protocol minor 12 and negotiated feature `zip-rebuild-copy-v1` now run through Contracts, the provider-neutral planner/producer seams, the BCL ZIP writer, Worker, typed Client, `rebuild-zip` CLI, and the standalone WPF Rebuild Copy workflow. The route accepts one closed add/replace/delete plan against an eligible opened single-volume unencrypted ZIP and publishes only a distinct absent destination; the original is never staging and remains byte-identical.
- Security and correctness coverage includes shared canonical Windows archive-path validation, bounded typed-frame preflight before any transport byte, exact persistent deletion keys, deterministic collision/resource planning, held mapped-source leases through commit, provider checksum-copy, streamed actual-byte/ratio/output/free-space limits, staged ZIP manifest readback, source and destination races, cancellation, low space, exact-PID crash/recovery behavior, and clean WPF validation of user-edited paths. The independent re-audit's four initial findings and final WPF validation finding were fixed and regression-tested.
- Final source verification passes forced locked restore, a fresh no-known-vulnerability NuGet inventory, formatter, a zero-warning/error Release build, 255/255 unit/WPF, 28/28 adversarial, and 203/203 integration tests: 486/486 with no skips. The canonical production/benchmark identity is 121 inputs and SHA-256 `D0DFC81FDD6829A8457B9DBF826CF7DE5AD6CC1EA21197C7650015EA649AD95A`. Build servers shut down, the documented Rain/testhost/dotnet process query was empty, and live workspace staging, Worker/nested temp, and default recovery residue counts were zero; older retained evidence was left untouched.
- The integration gate includes real Worker/Client/CLI and packaged-Worker WPF-model mixed edits, all four presets, original SHA-256 preservation, ordinary-failure residue checks, destination races, low-space seams, crash reconciliation, authoritative reopen/list/test/extract, and test-owned official 7-Zip Extra 26.02 test/extraction with exact expected paths and hashes. This is source and deterministic-corpus evidence only: no protocol-1.12 self-contained package, manual GUI/accessibility, clean-machine, physical SMB/filesystem, signing, installer, or publication acceptance is claimed. Rain Explorer was not changed.

## Protocol 1.11 retained local package gate (2026-08-24)

The isolated package under test is `artifacts\RainArchives.Protocol111RecoveryCenterPackage.20260824.01\app`. It was composed without overwriting or relabeling the accepted M1 package:

```powershell
dotnet publish src\RainArchives.App\RainArchives.App.csproj `
  --configuration Release --runtime win-x64 --self-contained true --no-restore `
  --output artifacts\RainArchives.Protocol111RecoveryCenterPackage.20260824.01\app `
  --nologo
```

- The package-time canonical source input set was 115 files and SHA-256 `11EA9BA7CA6A09D599B21ADB61BAF8EBCA376EC7793C2936496C738467F988AC` using the command retained in `CONTINUATION.md`.
- The package contains 609 files and 231,060,953 bytes. The App root contributes 404 files/146,997,656 bytes and the private `worker\` subtree contributes 205 files/84,063,297 bytes. Its aggregate SHA-256 is `A51087FD342AAFF8F3F252B68C05106FE3D4BB2E0352ED7A0490BD0166182D90`, computed over ordinal-sorted forward-slash relative paths by appending UTF-8 path, one NUL byte, and that file's SHA-256 for every package file.
- `RainArchives.exe` is SHA-256 `9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26`; private `worker\RainArchives.Worker.exe` is `C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF`. Both report file version 1.0.0.0. App-root and Worker-subtree `RainArchives.Contracts.dll` are byte-coherent at `6F52E6828C6EBA4F4D171153201E355F424DC9741A02785C767F22803CC5D39E`. The package includes both self-contained runtimes, contains zero exact `7z.dll`, `7za.exe`, or `SharpSevenZip.dll` assets, and keeps the disabled managed adoption-gate assembly inside the Worker boundary.
- The production App launched only `app\worker\RainArchives.Worker.exe` by absolute packaged path; the observed executable paths were the exact App root and private Worker subtree above. Neither the current directory, `PATH`, an installed executable, nor the retained M1 package supplied the Worker.
- Through the production packaged WPF window, the checked `plain.zip` listed exactly `alpha.txt` (65 B), `beta.txt` (17 B), and `unicode-雪.txt` (9 B); Unicode search narrowed to the one exact row, clearing search restored all three, integrity testing completed successfully, and the read-only UTF-8 preview matched the fixture. Selected extraction published only `Extracted files\alpha.txt`, whose SHA-256 `42C82805FB9599A724D099683BB0AD73BA6931A5A859E5B5DD2F2BD43FB00868` matched the source; the isolated sibling sentinel remained unchanged.
- The packaged WPF creation dialog exposed atomic file/folder admission, the default Balanced preset, honest unencrypted/new-destination limits, and admitted the checked source into its grid. Windows UI automation could inspect but could not reliably write either the owned native destination edit or the protected password edit, so those two controls are not claimed as direct GUI acceptance. An isolated non-shipping harness in the package evidence directory instead used the production `MainWindowViewModel`, isolated preview caches, and the exact hash-checked packaged Worker. It refused `wrong-fixture-password` without exposing it, accepted the checked AES-256 fixture password, matched the expected text hash `42C82805FB9599A724D099683BB0AD73BA6931A5A859E5B5DD2F2BD43FB00868`, zeroed both caller arrays, and reused validated session state without a third prompt. The same route created and reopened one Balanced ZIP containing only `created-via-package.txt`; its 52 source/entry bytes both hash to `5B3C7A25B367612069DBDA59752E6DB74D2806BD9273612807E87F0FE3C5EB92`, and no creation staging file remained.
- An isolated package-only temporary root contained one verified stale workspace with opaque ID `cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc`. Production Recovery Center listing reported one reviewable, zero active, and zero suspicious workspace. The original inspection and a fresh action-time inspection both reported `Verified inactive`, one source, 31 bytes, and revision `2767bd1fe247e87a782726c880ef3aacbd6d0558659a15fce9d8f830968fa354`. The UI and default-No confirmation disclosed no temporary path, the first refusal sent no mutation, and repeated inspection returned the same evidence. After separate user approval, the final confirmation repeated only that opaque ID/revision/count/bytes plus the partial-deletion warning and defaulted to No. Confirmed Yes discarded the exact workspace once, refreshed directly to zero reviewable/active/suspicious records, and did not prompt or retry again. Filesystem verification found the exact workspace and nested child absent while the package-test root contained only its 72-byte sibling sentinel, unchanged at SHA-256 `7F3015AC654D3CAC04E6DE9181508BEE888D115996534C047EF3FBC0186E7D92`. Closing Recovery Center and the App left no packaged App, Worker, testhost, dotnet, or staging residue.
- Package-snapshot regression verification after packaging was clean: `dotnet format RainArchives.slnx --verify-no-changes --no-restore`, a zero-warning/error Release build, 230/230 unit/WPF, 23/23 adversarial, and 181/181 integration tests, for 434/434 with no skips. Restricted-sandbox handle opens first returned expected access denials; the documented identical build/tests passed in ordinary Windows context without weakening a policy or assertion.

The exact packaged-Worker harness command was:

```powershell
dotnet run `
  --project artifacts\RainArchives.Protocol111RecoveryCenterPackage.20260824.01\package-test-harness\PackageFlowHarness.csproj `
  --configuration Release --no-restore -- `
  artifacts\RainArchives.Protocol111RecoveryCenterPackage.20260824.01\app\worker\RainArchives.Worker.exe `
  tests\fixtures\corpus\archives\zip\aes256.zip `
  tests\fixtures\corpus\source\alpha.txt `
  artifacts\RainArchives.Protocol111RecoveryCenterPackage.20260824.01\package-test-io\create-input\created-via-package.txt `
  artifacts\RainArchives.Protocol111RecoveryCenterPackage.20260824.01\package-test-io\output-parent\created-by-package.zip `
  artifacts\RainArchives.Protocol111RecoveryCenterPackage.20260824.01\package-test-harness-temp\preview-cache
```

This is local automated/source evidence only. It does not claim current-package Narrator, High Contrast, clean-machine, standard-user, signing, installer, update, or publication acceptance. Those checks must use this exact package separately; historical M1 manual evidence is not transferred to it.

## Current automated coverage snapshot

- Current protocol-1.23 source verification passes 530 unit/WPF tests, 29 adversarial tests, and 375 real-worker/protocol/process integration tests: **934/934** with zero failures and zero skipped. The canonical production fingerprint is 164 inputs, 3,260,519 bytes, and `0D84243CA1A09AE3BD77942EC66578E44E3EB5A7547411CCD40E224CD2460AB2`. Locked restore, formatter verification, the current direct/transitive inventory for all 16 solution projects with no known vulnerable package, and the zero-warning/zero-error Release build pass. The accepted native-handle and Worker process tests use the ordinary Windows process/filesystem context when the Codex sandbox cannot grant those exact handles; no policy or test was weakened. The immutable protocol-1.12 and historical protocol-1.16/1.18/1.19/1.20/1.22 baselines remain separately recorded above.
- M7-D adds exact typed RAR explicit/tree/capability protocol contracts, concrete-provider and post-queue qualification, the signed-encoder lease, bounded private materialization, independent encoder testing, complete SharpCompress manifest/digest readback, and durable crash-workspace ownership. The focused WinRAR security/reliability class passes 14/14, including post-handshake non-blocking maintenance, one global candidate/object/time budget, stable reparse-ancestry and owner-link refusal, deterministic unproven-kill escalation, cleanup-failure propagation, fake-provider rejection, removed/replaced encoder closure, and exact-PID hard-kill cleanup with unrelated-sibling preservation. The current machine's signed WinRAR 7.20 remains `UpdateRequired`; an isolated signature-checked official 7.23 encoder qualifies positive tests but is never copied into source or packages.
- The valid 550,475,088-byte reported RAR5, SHA-256 `28F6AAC54C43CE10A39C0E930861D658229D7359F3466503C30D956D3222F92B`, lists 18,418 entries through the exact packaged Worker and remains byte-identical. A 73-byte checked fixture with a directory lacking optional CRC metadata now opens/lists/extracts; required metadata failures remain structured. The misleading generic pre-read rejection is not used for this provider defect.
- The immutable Rain Archives package is `artifacts/RainArchives.M7DRarPackage.1.23.0D84243C`; its 1,199-byte manifest is `DF47FAD343B6DCBDE2B1FFB42489C7CF7D19C154CA9D2CB6398C07AE2F442ADB`, its 611-file App tree is `4995B32081A66E95A77A0B914876256146D094F9630ED22A4E3B64F4BEA34BCD`, and its 207-file Worker tree is `A4A83D8B51E704B95BB588FF6F21432E6E7A0B37F882A6E1193F7FCC12D2DACC`. `Rar.exe`, `WinRAR.exe`, `7z.dll`, `7za.exe`, and `SharpSevenZip.dll` are absent. Package composition and the exact RAR directory-without-CRC flow pass. Qualifying-installed-WinRAR GUI creation, exact-current clean-machine/install/accessibility, physical SMB/blocked-UNC/DFS/no-file-ID/non-ADS, signing, publication, and release acceptance remain `manual_pending`.
- Protocol 1.21 proves exact-evidence independent nested recovery reopen through unit, adversarial, real Worker/Client/CLI, golden/downgrade/retained-binary, original/copy hash, zero-normal-residue, and exact-PID post-copy-flush crash/restart cases. Protocol 1.22 proves truthful lifecycle/support fields and no string/path/secret/recovery-authority payload. Operation admission is 32, execution one, retained terminal history 64, response maximum 96, ordering unspecified, and cancel the only supported control; terminal cancel and 33rd-start/secret settlement regressions pass. No Worker emits forward-compatible Awaiting decision phases and no retry/pause/reorder endpoint exists.
- Milestone 5A adds five direct Activity Center model cases plus STA production-XAML/binding/accessibility coverage. They prove truthful policy/progress/diagnostics presentation, shared visible escaping without warning/error-message retention, latest-snapshot-only exact-ID cancellation, terminal no-retry behavior, non-overlapping refresh, late-response/disposal suppression, and malformed/downgrade/offline refusal. A real Worker case starts two 96 MiB creations, observes running plus queued state, cancels the exact queued operation through the production row model, waits for authoritative cancellation/completion, and proves unchanged source hash and zero owned residue. The current package case drives creation, integrity, extraction, and rebuild through the production Activity Center model and verifies exact output/original hashes and cleanup.
- Milestone 5B adds nine direct browser-model cases plus six main-window browser orchestration cases and STA production-XAML/binding/accessibility assertions. They prove deterministic root/folder/Back/Forward/Up branching, a newest-256 history bound, Unicode current-folder search, duplicate-key selection, same-fingerprint restoration, different/stale source reset, prepare-before-close adoption, fail-closed authority after an uncertain close, malformed count/page/hierarchy/cycle/key refusal, downgrade/offline/unsupported/error states, and provider-free capability presentation. A 50,000-descriptor model run materializes 512 rows and completed indexing in 64.7 ms on the verification host. The real Worker case previews the exact Unicode duplicate by `EntryId`, restores its persistent key only across an exact same-source reopen, then integrity-tests and extracts one collision-free selection with unchanged source and exact output hashes. The package case binds the production source identity and runs the same browser/preview/reopen boundary against the private packaged Worker.
- Milestone 5C adds 27 unit/WPF tests and two integration tests. Store/model cases cover strict version, required/unknown/duplicate member, invalid-enum/Unicode, truncation/oversize, backup/last-known-good, unavailable/unsafe state, reparse/hard-link/ADS refusal, exact atomic save/replace/backup/failure cleanup/reset, concurrent-writer serialization, cancellation/drain, sensitive-field absence, capability downgrade/offline/error/stale suppression, and deterministic preset clamping. The remediation adds a literal exact-128-KiB accepted/next-byte-refused assertion, ordinary-Save refusal with byte-preserving explicit Reset, read-only recovered App-settings hints, and Create/Rebuild malformed/future/hard-link plus post-open Save/Remove race checks. Valid Missing/Ready mutations still pass. STA production-XAML assertions cover system-color/access-key/focus/automation/live-state bindings, all six managed formats, raw one-file rules, ZIP rebuild review, and existing-destination preservation. A real Worker flow and the exact package flow persist defaults, query fresh capabilities, create and Rain-reopen ZIP, perform eligible Rebuild Copy to an absent destination, Rain/external-reopen the copy, preserve source/original hashes, and leave no staging/Worker residue. Focused remediation repeats pass 22 settings/preset/STA cases and the exact package flow.
- Milestone 5D adds 19 unit/WPF tests and two integration tests. Property-model cases cover fixed archive/capability projection, known and absent optional metadata, checked duplicate occurrence, valid-only compression ratio, UTC timestamp and attribute formatting, link/encryption/integrity-presence state, malformed response refusal with prior-state preservation, and stale/offline clearing. The remediation regression first reproduced `#-2,147,483,648` for an accepted `int.MaxValue` duplicate ordinal, then proves exact `#2,147,483,648`, the same positive Automation occurrence, and unchanged CR/LF/bidi escaping after `Int64` widening before addition. Preview-model/service cases cover strict UTF-8, BOM-marked UTF-16/UTF-32 disclosure, Unicode and exact 1 MiB text, binary/NUL, ambiguous encoding, oversize plus-one detection, unsupported entry/encoding, loading/offline/stale/cancel/fixed error states, malformed-result preservation, selection cancellation, stale response suppression, close-time drain, focus restoration, and fixed redacted diagnostics. STA production-XAML assertions cover system colors, access keys, tab order, automation names/help/live status, inert text, and the absence of the provider method column. The real Worker flow and exact package flow project archive/entry properties and prove UTF-8 text, binary/NUL, and oversize outcomes, unchanged source hashes, cache release, and zero Worker/staging residue. Focused M5D repeats pass 41 model/STA cases and two real-Worker/exact-package flows.
- Milestone 5E adds 19 unit/WPF tests and five integration tests. Model cases cover bounded path-free inventory, explicit creation/nested/extraction inspection, exact action invalidation, nested source selection and password-slot settlement, depth-zero reopen, read-only extraction capability refusal, malformed/downgraded/offline/fixed-error states, fixed support-field projection, stale selection/response suppression, cancellation, and full close-time drain. The ownership remediation adds deterministic pre-cancel, lifecycle-gate cancellation, local-validation, drain/callback failure, handoff-cancellation, preview-drain failure, notification-failure, compatibility-wrapper, ordinary-open, and successful-transfer cases. They prove supplied one-shot slots are discarded exactly once without an open/reopen frame and every untransferred session is closed exactly once without disturbing prior authoritative state. STA production-XAML assertions cover the four tabs, system colors, access keys, focus order, automation names/help/live status, recycling virtualization, and the absence of generic extraction action controls; a blocking initialization regression proves dialog close joins the top-level load plus every child owner. The real Worker flow exercises all three recovery families and fixed diagnostics with original-hash preservation. Five exact-PID recovery crash/restart tests retain creation, nested reopen/discard, and extraction evidence without inference or broad cleanup. The exact-package flow recomputes every identity, drives creation disposition, nested independent reopen/list/close/discard, read-only extraction inspection, and fixed diagnostics, then proves zero scoped residue.
- Milestone 5F adds eleven unit/WPF cases and four integration cases over the accepted M5E totals. The fixed command-context model proves canonical current-row resolution, duplicate visible-identity refusal, the 512-selection ceiling, ready/empty behavior, and complete busy/offline/stale/downgrade/missing-selection negative authority. Main-window and STA cases prove shared mouse/keyboard commands, stale-source invalidation, deterministic visible-order selected extraction, fixed path-free status, exact five-item menu/accelerator/Automation wiring, and rapid preview replacement plus close-time drain. The inspector remediation proves initial collapse, explicit Properties/Safe Preview reveal, fixed close Automation metadata, no global Escape binding, close from Properties, close during and after preview, cancellation-before-collapse, full pump drain, late-completion repaint refusal, preserved selection/focus restoration, and reopen. A real Worker, the immutable predecessor flow, and a fresh exact-package remediation flow exercise navigation, safe preview, validated properties, integrity, selected extraction, the inspector lifecycle, original hash preservation, exact Worker exit, and zero Worker/cache/staging residue.
- Milestone 5G-A plus its accessibility remediation adds six direct resource/theme/model cases, updates the STA production-XAML/startup and browser regressions, and retains two package identity/flow checks. The tests prove complete semantic Light/Dark/High Contrast resources; exact Window/WindowText, Control/ControlText, and Highlight/HighlightText mappings; synthetic arbitrary-color resolution for BrowserButton, header, muted shell, alternating row, selected row, and row-icon styles; app-local theme selection with authoritative High Contrast; and a live no-session check that active actions have no duplicated mnemonic while top Alt+C/Alt+R, welcome Tab stops, and Automation names/help remain. They also retain eight-column virtualization and unchanged 500,000/512/256 limits, 320-380-DIP inspector sizing below 40% at 1,000 DIPs, narrow-safe welcome wrapping, visual-order tab indices, one status live owner, vector icons, row Automation safety/encryption state, no global Escape binding, and the inspector drain/focus lifecycle. The current exact-package flow binds source/manifest/tree/principal identities and forbidden assets, then drives packaged-Worker open, navigation, safe preview, properties, drained close/focus, integrity, and selected extraction with an unchanged original and zero scoped residue; a separate check pins the rejected pre-remediation package without binding it to current source. The current-package accessibility tree exposed the expected named shell controls and marker-free welcome labels, but Windows.Graphics.Capture returned a uniform blank frame; no direct visual acceptance or visual artifact is claimed.
- Milestone 5G-B adds four unit/WPF regressions plus one package-identity regression. Static production-XAML checks cover the six secondary windows' dynamic semantic palette use, complete button names/help, contiguous explicit tab order, active-scope access-key uniqueness, default/cancel bindings, bounded scroll/name layouts, one deliberate Activity live owner, selected-tab-only Recovery status, and recycling DataGrids. STA coverage switches an already-open Create dialog between Light and Dark, verifies semantic window/grid colors refresh, exercises min-size reachability, proves modal Create and nonmodal Activity owner/attach/detach behavior, and retains the full escaped but visually bounded password archive name while clearing the password. Caption tests inject the DWM seam and prove source-initialized application, theme reapplication, idempotence, close/dispose detachment, and weak window collection. The Settings editor's already-correct five explicit rows are regression-locked without a redundant production change. Existing Activity/recovery lifecycle, create/rebuild authority, limits, redaction, originals, and no-overwrite tests remain green. A fresh read-only post-patch review found no actionable defect in the bounded production/test scope; real DWM/High Contrast appearance remains manual.
- Milestone 5G-C adds three unit/WPF product-identity regressions plus one integration identity/flow regression. They bind `ApplicationIcon` to the deterministic 381,038-byte `RainArchives.ico`, parse all nine alpha-capable 32-bit DIB frames at 16/20/24/32/40/48/64/128/256 pixels, verify exact structure, palette, pixel bounds, size, and SHA-256 `554013891F9E3F50F2993CE8ACF636CB80457A9CF8A81099F69A0C675A7720A0`, and extract both large and small icons from the Release executable. A launched top-level App window supplies large and small icons through application-resource/class fallback without a `MainWindow.xaml` override. Its historical exact-package flow pins the source-at-package-time identity, manifest, both trees, every principal, Contracts byte equality, forbidden assets, and packaged executable icons before running the unchanged Worker product flow with unchanged inputs and zero scoped residue; it no longer asserts that live source has the M5G-C identity. No custom title bar, shell integration, installer, signing, or runtime icon generation was added.
- Milestone 5G-D adds four unit/WPF tests and one current-package integration test. Flattened resource-dictionary checks prove palette → shared controls → product-token merge order, dynamic semantic lookup, required template parts, product-style composition, state triggers, and the absence of hard-coded/SystemColors palette ownership in the shared control layer. Real STA controls cover check-box states, determinate/indeterminate progress, tab selection/disablement, menus and separators, DataGrid row/cell selection and scrollbars, generated checkbox display/edit containers, generated text-cell editing, status items, Buttons, and editable ComboBox selection. Open ComboBox and ContextMenu popups are verified while an existing window switches Light → Dark → High Contrast. Settings is arranged at 760×560 and proves a positive scroll extent, end-offset reachability, transformed lower content, and a stationary footer. Static tests bind the exact four explicit Rebuild generated-column bridges, archive-neutral Create shell copy, and the three shared post-create open fallbacks. A same-task GUI run exercised the exact owned Release App's dark menu and Settings wheel scrolling without saving settings; this finite automation is not product-owner/manual accessibility acceptance.
- The deterministic exact-package performance case remains immutable predecessor evidence. It pins a 20,546-entry / 4,421,858-byte ZIP at SHA-256 `6CAF8851DF604339E577D63DB0D8545829C76DA790866112E6B54190A9258060`. On Microsoft Windows 10.0.26200, .NET 10.0.9, x64, 28 logical processors, it uses one warmup and three measured fresh packaged-Worker opens with uncontrolled filesystem cache, then all 64 page transitions, 320 folder/root round trips, 1,024 selection changes, 32 preview replacements plus drain, and five 100 ms steady Worker samples. Measured cold open/list was 625.7876/712.0909/772.6138 ms minimum/median/maximum; Unicode search returned 1,000 rows in 2.1403 ms; average page transition, folder round trip, selection, and preview replacement/drain were 0.20114375, 0.2348421875, 0.044965234375, and 0.994121875 ms. Steady Worker working set was 89,157,632-89,935,872 bytes, private bytes 55,873,536-56,336,384, and handles 320. All four exact PIDs exited and Worker/cache/staging counts were zero. Raw evidence is `artifacts/RainArchives.M5FMeasurement.1.22.94811C7A.02/measurement.json`, 6,302 bytes at SHA-256 `F5AEB7113900F8373001BEEB76021D118B8FA4BCC09C8E11EF47C514FB55016B`. The initial unpinned-timestamp `artifacts/RainArchives.M5FMeasurement.1.22.94811C7A/measurement.json` remains preserved as superseded 6,298-byte evidence at SHA-256 `666BC0CB75CFF9E7A532F8C9E0F19409CBF7D97EFE32361100E182B8F13DB2F8` and is not the canonical result. These are local observations, not a competitor-speed or manual UI claim, and they are not relabelled as a measurement of the remediated package.
- The current local self-contained package is `artifacts/RainArchives.M5GDDesignSystemPackage.1.22.651729F4`. Its 1,083-byte manifest SHA-256 is `01BF21F943C46B54C953E2A2BE3C5D037BE1FB26F738DBE67CA6C8C46AB30137`; the 609-file/234,037,929-byte App tree is `AD4D7D620C75771788083B29B7DEB92DE403D665336BBF5D96810FF921BC212A`, and the 205-file/85,041,541-byte private Worker tree is `1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F`. Principal hashes are App executable `A565C435B523F656416ADB7736A0D005BB75B3D733379D2D443A7F7794F2E5C9`, App DLL `9C60B64D4A678AA0D15B2DF3E5B73A77275331E6E479CE28259E770FEB251AF5`, Client `53B933055CC5043FE8C5B15DB0B61C783AFCBBE429DE1E6EF443CD317498292D`, byte-coherent App/Worker Contracts `E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8`, Worker executable `C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF`, and Worker DLL `3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8`. Forbidden `7z.dll`, `7za.exe`, and `SharpSevenZip.dll` assets are absent. The exact-package flow binds the current 160-input source including icon/control resources, every manifest/tree/principal identity, Contracts byte equality, executable icon resources, and forbidden-asset rule, then runs the packaged Worker product flow with unchanged source and zero scoped residue. `artifacts/RainArchives.M5GCProductIdentityPackage.1.22.56FFA877` remains immutable historical product-identity evidence and is pinned without binding it to current source. `artifacts/RainArchives.M5GDDesignSystemPackage.1.22.B11E7A80` is preserved as superseded pre-final evidence at source identity `B11E7A808ADF9FD4E0329613F5C69C9A5FC2C88ED9EED9A3ADAE7FEF24BB679F`, manifest `4C742CE334FF2D7C37C7606EBDAA5F6A8C9B66E5B97535C5A2F3D978316A6918`, and App tree `609 / 234,036,197 / 1C3A27D0314F97EBC4F262AD952DCF7CB6C307163A0CD97B3F7FB9C170A29EEF`; runtime proof subsequently exposed its generated DataGrid text-editor fallback. M5G-B, M5G-A, exact M5F, measured 94811C7A, accepted M5E/M5D/M5C/M5B, and earlier rejected packages remain historical evidence. This is local automated evidence, not current-package visual/Narrator/keyboard-only/actual High Contrast/DPI/clean-machine, physical network/filesystem, installer, signing, publication, or release acceptance.

- The M6-A Rain Explorer integration checkpoint is an uncommitted 15-file delta over Rain Explorer base
  `0bc3fb090bef4d93e0db5bb6d66813b80e8385f7`; the exact changed-input set is 541,661 raw bytes at
  `D4543C0ACB433EF596EEF8AB13204E6E59CB4B45708C08CD0A3B93EB58E27FAF`. Independent verification
  rebuilt the current source and passed 116/116 Rain Explorer tests with zero failures/skips plus the
  zero-warning/zero-error Release build. The current configured NuGet direct/transitive advisory query
  reports no known vulnerable package. Its real protocol-1.22 Worker case opens and navigates a
  spaces-and-Unicode ZIP, verifies strict persistent identity, closes the exact session/client, and
  leaves no Worker, testhost, M6-A temporary fixture, cache, or staging residue. The development output
  has byte-identical App/Worker Contracts and no `7z.dll`, `7za.exe`, `7zz.exe`, or
  `SharpSevenZip.dll`. This is automated source/runtime evidence only; no integrated installer/package,
  manual GUI/accessibility/DPI/clean-machine acceptance, commit, push, signing, publication, or release
  claim exists.
- The cumulative M6-C Rain Explorer checkpoint is an uncommitted 27-file delta over the same base,
  817,265 raw bytes at
  `9F3672F88A01983FB8A49A9AC4959F2141347C502057FED3B4C884207A964709`. Independent verification
  passes 149/149 tests with zero failures/skips, a zero-warning/zero-error Release build, a current
  no-known-vulnerability direct/transitive advisory query, scoped formatter verification, and
  `git diff --check`. Its real Worker flow proves browse, preview, integrity, selected
  new-destination extraction, exact hashes, source/conflict preservation, operation cancellation,
  password-slot settlement, and exact cleanup. A test-harness audit found and fixed a repeated empty
  `password-refused-*` directory leak; 17 verified empty non-reparse directories from prior runs were
  removed by exact path, and the final matching fixture/recovery/cache/staging inventories are zero.
  Development App/Worker Contracts are byte-identical at
  `A2D7D6A173637B0B91E47E80625EF362FF5577290D440A5BE2B4227F1B8CAE4D`; no gated native 7-Zip
  asset is present. This remains automated uncommitted development-output evidence, not an integrated
  package, install, manual GUI/accessibility/DPI/clean-machine, signing, publication, or release claim.
- A fresh bounded read-only post-patch review checked only the High Contrast pairings, selected-row icons, direct muted surfaces, welcome mnemonic ownership, and the narrow tests; it returned `FIXED` with no P0-P3 finding. This review does not substitute for product-owner visual, Narrator, keyboard-only, High Contrast, DPI, or clean-machine acceptance.
- A separate same-task read-only remediation review traced every production preset Save/Reset caller, verified the Settings Center's recovered-primary save refusal, confirmed both dialogs recheck exact state before mutation, checked fixed warning provenance and the Contracts/Client-only App references, and found no remaining blocker. Hard-link refusals compare the preserved primary and sibling by bytes, volume/file identity, and link count. This review does not substitute for independent acceptance or manual current-package accessibility/clean-machine evidence.
- A separate finite same-task M5D read-only review traced property and preview field provenance, malformed-result adoption, checked arithmetic, cancellation/generation/disposal ownership, selection/focus transitions, XAML bindings/automation metadata, App references, package flow, and forbidden provider/path/active-content sinks. It found zero blocking or non-blocking findings in that bounded scope. This is not an external workbench scan, independent acceptance, or manual accessibility evidence.
- Independent M5E verification found that the earlier same-task review missed the two session-gated Client slot-settlement gaps and the dialog/MainWindow pre-transfer ownership gaps. The remediation review traced every return/throw/cancel path, Worker-consumed success, discard compensation, dialog drain/callback ordering, ordinary and recovered state transition, notification failure, and compatibility wrapper. Focused tests prove the repaired invariants and the finite same-task post-fix review found no sibling bypass. It remains neither independent acceptance nor manual accessibility evidence.
- Scoped M5F security scan `bc166098-03ae-4b4a-a3a4-62b17929f304` used Daybreak Blue at max reasoning, TAC1, and the required sequential fallback because that task prohibited subagents. It sealed five command-authority, WPF/accessibility, preview-lifecycle, information-exposure, and package/performance surfaces with zero findings at the immutable 94811C7A predecessor. It is not relabelled as review of the remediated source or as an independent/manual result.
- A finite direct inspector-remediation review traced initial visibility, every reveal/hide caller, generation invalidation, active/pending cancellation, pump join, late completion, source/session/state invalidation, fixed Automation text, scoped focus restoration, App project references, package composition, and forbidden Escape/authority/dependency paths. It found zero blocking or non-blocking issue in that bounded scope. This same-task review is not independent acceptance or manual visual, Narrator, keyboard-only, High Contrast, mixed-DPI, or clean-machine evidence.
- Independent verification of the `93D944FC` checkpoint found that App-side hierarchy validation recursively reached an entire reverse-ordered parent chain before enforcing depth. The remediation uses one reusable iterative path plus visited/depth maps, visits each descriptor at most once, detects cycles explicitly, and checks `resolvedDepth >= 256` before its only increment. Focused tests accept exactly 256 reverse-ordered levels, reject 4,352 levels with the fixed depth error, reject a 32,768-entry reverse-parent cycle with the fixed cycle error, and prove both refusals leave the prior authoritative browser title, state, and selection unchanged. This closes the WPF stack-exhaustion route without relying on Worker ordering or changing protocol 1.22.
- The retained Milestone 4 package remains `artifacts/RainArchives.M4AutomatedPackage.1.22.DCE2BC1E`. Its test binds that package's historical 135-input fingerprint, exact manifest and assemblies, support diagnostics, recovered-source independent reopen, unchanged original/copy hashes, and cleanup without asserting that the live source still has the old identity.
- Immutable security scan `e255a57b-9dde-43bd-b44d-c426500b16fa` reports complete six-surface coverage and zero findings for snapshot `codex-security-snapshot/v1:sha256:8fafb141100e72fb2d5817dbe48d9ec0caad9313f96ce0220a87306fde1b214e`. Build/test output directories changed during the scan but production source did not; the report remains snapshot-bound. Provider-message provenance remains an explicit negative test target because `ArchiveProviderException.Message` crosses the Worker error seam and must stay fixed/redacted.
- A separate post-gate read-only Milestone 5A review found no security, reliability, or automated-accessibility source finding. It checked the Contracts/Client-only project boundary; absence of path/recovery/secret/provider-message presentation authority; fixed-field diagnostics; snapshot-authorized cancellation; 96-record state bound; non-overlapping refresh and stale-generation handling; UI coalescing; close-time cancellation/drain; system-color semantics; virtualization; access keys; focus order; automation names/help; and polite status announcements. This same-task clean review is not a substitute for product-owner visual, Narrator, keyboard-only, High Contrast, mixed-DPI, or clean-machine acceptance.
- The scoped M5B read-only security scan `48ec3d6f-ccd0-4a5a-86dc-259f00f60571` sealed six reviewed surfaces with zero findings at immutable snapshot `codex-security-snapshot/v1:sha256:4ec1097ced945eaa805ecf0eaa19830286f61c723b450e16304407948a14c935`. Independent verification later found the recursive hierarchy issue, then accepted the iterative remediation after reproducing its 4,352-level and 32,768-cycle regressions, full 799-test gate, exact package identity, and zero residue. The original scan remains truthfully snapshot-bound. A requested M5C workbench registration never returned a scan ID and was terminated without claiming a result; the subsequent finite direct settings/storage/capability/create/rebuild/XAML/package review found zero findings. Manual visual/assistive/device acceptance remains separate.
- Final build-server shutdown passed. One read-only sample observed a concurrent `testhost`; it exited independently, and the final scoped snapshot found no Rain App, Worker, recovery crash probe, CLI, or test host. The user-owned exact-package process was not signalled or stopped. The workspace has zero top-level package staging entries, and the live App-data Preview Cache plus ArchiveCreation and Extraction recovery leaves each have zero descendants. Of 213 preserved Rain Archives Temp parents, 56 were touched on 2026-09-02. Two older parents retain current-day residue: `RainArchives.FinalPackageProbe.10ce8a7a637b4f1bb669456bbe90413c` has 14 empty directories, and `RainArchives.ZipRebuildTreeEditEndToEndTests` has one empty directory. They contain zero files and were inventoried without deletion. No unrelated process, historical/manual/tooling root, retained package, or filesystem object was stopped or removed.
- Minor-5 preview and minor-6 nested-session coverage remains intact across the checked ZIP/7z/non-solid-RAR/TAR/compression-stream corpus, including authenticated transport, actual-byte ceilings, password retry, cancellation, cleanup, lease limits, nested depth/byte/disk limits, immutable worker-owned sources, malformed-child recovery, inherited provenance, descendant ownership, and solid-RAR bounded-work refusal. The recovery-inventory and seekable-preview-cache slices retain exact cleanup/admission, hard-killed-child persistence, bounded non-destructive restart reporting, path-hidden read-only publication, exclusive live-file sharing that rejects a second reader, and same-worker recovery evidence.
- Standalone text-preview coverage adds strict UTF-8 and BOM-marked UTF-16 decoding, invalid UTF-8/NUL/oversize refusal, multiline visible escaping, capability refusal, metadata refusal without false password validation, wrong-password retry with zeroed caller arrays and explicit slot settlement, active-plus-latest request serialization when prior cancellation is delayed, stale-result suppression, cancellable modal password-dialog closure, and direct Client discard acknowledgement. A real packaged-worker WPF-model test opens the checked AES-256 ZIP, refuses a wrong password without exposing either secret, accepts the correct password, previews the expected text, and reuses only the validated session state without a third prompt.
- Protocol-1.7 creation coverage retains client/worker downgrade refusal, all four real ZIP modes plus empty output, .NET and Rain-managed round trips, timestamp conversion/clamping, path/collision/resource policy, stable explicit-source handles, source replacement, existing/destination-race refusal, provider-failure cleanup, deterministic post-Running cancellation, raw trailing-dot/space and dot-segment refusal, CLI routing, UNC syntax, kernel-level no-replace commit, and same-worker recovery.
- Protocol-1.8 coverage adds an unchanged minor-7 wire-shape fixture; client-local and worker-preparse downgrade refusal; mixed explicit-file/directory-root creation; root, empty-directory, zero-byte, and Unicode preservation; deterministic global ordering across reordered requests; exact aggregate byte/entry progress; the exact 4,096-entry pass and 4,097-entry refusal; metadata/depth/path limits; same/overlapping roots, explicit-file/tree overlap, destination-inside-tree, and mixed file/directory archive collisions; nested reparse refusal; enumerated child file/directory ID substitution; source-tree additions and deep mutations; final manifest mutation after provider finalization; provider failure and cancellation cleanup; CLI directory routing; real-worker round trips; and same-worker recovery after a gated mutation failure and after both file-only/tree cancellation cases.
- Protocol-1.12 coverage adds bounded DTO/frame validation, minor-11 pre-frame/pre-access refusal, dependency-incomplete negotiation refusal, shared archive-path rules, closed add/replace/delete planning, exact entry/source identity, streamed checksum and actual-byte enforcement, all presets, complete staged-manifest readback, held mapped-source leases, cancellation at each phase, output/free-space failures, destination races, exact-PID crashes, same-worker recovery, CLI/WPF routing, and official-7-Zip conformance. Oversized typed requests fail locally without consuming sequence state or poisoning the transport, and invalid user-edited WPF paths remain a validation result rather than an exception.
- Protocol-1.13 coverage adds additive golden/unknown-member/oversize frames, feature-dependency and minor-12 downgrade refusal, exact retained-file rename keys, swap planning, mixed namespace edits, unsafe/case/NFC/prefix/directory refusal, escaped final-path progress, retained metadata checks, WPF inline validation, CLI routing, external conformance, actual 1.12 binary cross-version checks, and an independently reviewed mixed WPF regression.
- Protocol-1.14 coverage adds bounded tree-plan/revision frames, Worker-owned mapped-folder review, aggregate entry/byte and request path/frame caps, hard-link/reparse/ADS-safe enumeration, stale/overlap/cycle/self-descendant refusal, exact moved/deleted explicit and synthetic directory semantics, deterministic final namespace ordering, mixed file/tree operations, CLI/WPF count review, current/retained-1.12/1.13 binary checks, official 7-Zip plus `bsdtar` validation, and separate read-only remediation review.
- Retained source-snapshot proofs use hash-verified official 7-Zip Extra 26.02 to test/extract all four explicit-file preset outputs plus one mixed directory-tree output containing Unicode and an empty directory; every regular-file hash matches. Windows `bsdtar` 3.8.4/libarchive 3.8.4 independently lists and extracts that tree ZIP with both hashes and the empty directory preserved. Separate explicit-file and directory-tree loopback-UNC proofs also pass official 7-Zip. Automated failure evidence includes a deterministic free-space reserve drop with exact cleanup, exact-worker death during a real 128 MiB compressed copy with a prepared provenance stream, and exact-worker death after durable writer finalization during maximum-entry final manifest validation; both kills publish nothing and leave no delete-pending staging file. The copy-kill now additionally proves a local journal survives with the real operation ID, destination, staging identity, and `Writing` phase; the next worker reports the count without path disclosure, destination contact, or deletion. Unit coverage proves all five durable phases, exclusive liveness, checksum rejection, torn-tail recovery, bounded local-only inventory, strict transition/identity validation, handle-relative non-reparse reopening, exact cleanup, explicit read-only artifact classification, four closed disposition actions, exact-revision refusal, durable receipt/replay semantics, completed-receipt count/age retention, actionable-inventory fairness, truncated-scan refusal, cancellation, hard-link/unexpected-state preservation, exceptional-path stream cleanup, and fail-closed replay expiry. A real Worker/IPC startup test prunes an expired receipt after its destination parent is removed. Two exact-PID Worker/IPC crash-probe tests add authenticated request-path termination after disposition intent and after commit, `worker.disconnected` observation, byte-exact state verification, one-time reconciliation through a fresh ordinary Worker, and path-independent completed replay without adding any probe switch to the shipping Worker. Nested-workspace recovery coverage proves exact inactive and empty-ownerless inspect/discard/replay, active refusal, stale-revision refusal after source mutation, unexpected-object and ADS preservation, reparse/hard-link/excessive-count/oversized-sparse refusal, path-free authenticated Worker round trips, queued cancellation before mutation, and an exact-PID crash after first source deletion with suspicious restart preservation. Download-provenance regressions require one in-section zone, reject out-of-range and duplicate/conflicting values, preserve a UTF-8 BOM case, and discard unrelated URL/section data. Creation-specific tests cover unanimous explicit/tree propagation, canonical URL-free output, mixed and malformed warning policy, warning-then-authoritative-failure semantics, exact valid-payload and bounded oversized-prefix mutation refusal, destination rename preservation, delete-pending ordinary cleanup, and cancellation before ADS application. Current WPF creation tests cover capability gating, atomic file-batch admission and collision refusal, folder-route gating, keyboard/default/cancel behavior, post-commit cancellation wording, and real-worker explicit-file, directory-tree, and existing-destination flows. Creation-recovery model tests cover read-first disclosure, fresh evidence-to-action mapping, explicit confirmation, exact revision forwarding, escaped untrusted text, failure-without-retry, capability downgrade, and close-time request draining. Nested-recovery model tests add path-free evidence, exact discard forwarding, default-No refusal, non-disposable assessment gating, refresh/selection invalidation, downgrade, failure without retry, and cancellation/drain. STA WPF smoke opens the two-tab Recovery Center and validates converter/binding/accessibility state; a real packaged production Worker integration test drives its nested view model through list, inspect, exact-evidence confirmation, discard, and refresh. The earlier creation operation probe remains bound to fingerprint `96E0B4E5F0DFA19116D8B0BC2052F6416BB67C150E60848062FC28304830BC6C`. The rebuilt package-time probe binds the same checks to `1C85F6D9`: 40 measured ZIP creations, ten warm-up/cancellation external validations, stable per-case archive hashes, partial-progress cancellation, exact Rain payload hashes, and zero residue. A second exact-package probe covers all 15 ZIP/TAR/TAR.GZ/TAR.BZ2/GZIP/BZIP2 preset cases with 75 measured samples and 90 pinned-7-Zip validations. The actual second-machine Milestone 1 extraction source/destination/interruption gate is accepted in `docs/milestone-1-manual-windows.md`. Package-bound true disk-full, standard-user ACL, exFAT creation/provenance, clean-machine startup, local alias evidence, and the bounded external-reader matrix are recorded separately in `docs/milestone-3-manual-windows.md`; product-owner direct ZIP creation/reopen/integrity and Rebuild Copy observation now passes. Automatic nested-workspace cleanup, nested restore/reopen, physical blocked-UNC inspection/disposition cancellation, true wire-range reads, external path-based preview, DFS/no-file-ID qualification, actual-SMB creation provenance/interruption, product-owner current-flow accessibility qualification, Recovery Center observation, and polished creation/recovery UX beyond the initial dialogs remain open. Open-ended multi-version/method/layout coverage remains Milestone 8 release hardening.
- The protocol-1.21/1.22 checkpoint supersedes the older current-state tail of the preceding historical
  coverage summary: one-source independent nested recovery reopen is complete, while stable restored
  ancestry, automatic nested cleanup, extraction disposition, physical blocked-network/filesystem
  qualification, and direct current-package accessibility remain open. No historical benchmark or
  physical result is relabelled with the new fingerprint.
- The next three bullets retain the detailed protocol-1.4 baseline breakdown (81/19/59) so earlier evidence remains distinguishable; their counts are not the immutable protocol-1.12 baseline, historical protocol-1.18 totals, or current protocol-1.22 totals above.
- 81 unit tests cover provider-neutral detection/indexing, persistent duplicate-key stability, adjacency-based subtree selection with cancellation, protocol/secret semantics including minor-version feature selection and open/index cancellation checkpoints, bounded serialization and response paging, malformed-output transport teardown, operation-ID authorization, safe display text, filesystem-object-aware resource guards including injected low space and Windows drive/UNC disk-space query normalization, native staged output including deep entries and staging roots beyond the legacy 260-character limit, reparse replacement, exact-manifest mismatch, hard-link count, and no-replace commit. The STA regression constructs, shows, lays out, and closes the production WPF window; verifies the search field, entry grid, realized filename row, and required archive-password field accessibility metadata; then drives the real password dialog through its Continue binding, verifies the captured characters, verifies the `PasswordBox` is cleared, and clears the returned array. A separate app-host regression launches the exact application manifest, verifies process-level per-monitor awareness, and requires the top-level WPF window to use the Per-Monitor V2 context.
- 19 adversarial tests cover Windows path/traversal/ADS/device/reserved-name rules, case/Unicode/duplicate collisions, links, hierarchy amplification, integer/resource limits, and source invalidation.
- 59 real-worker/protocol integration tests cover package-backed listing, integrity, extraction, extensionless detection, encrypted extraction/error secrecy, solid and multipart policy, empty/zero-byte entries, selected extraction, a 65,536-entry ZIP64 threshold, a runtime-generated sparse ZIP64 with its central directory beyond 4 GiB, a standards-valid per-entry ZIP64 size/offset-extra layout, checked split-ZIP detection/listing and exact list-only capability flags, a deterministic 13-volume numeric ZIP crossing `.009`/`.010`, a genuine classic `.z01` through terminal `.zip` set, missing/interchanged volumes, truncation/corruption rollback, raw and outer-stream checksum enforcement, sequential-reader reuse/reset, deterministic malformed-input mutations, cancellation after real progress, negotiated cancellation of queued 200,000-entry opens with secret/session cleanup, bounded cancel-response and worker-close timeout teardown of the exact owned worker, pre-write and queued-operation slot settlement, native access-denied error classification, two independent 32-failure raw WorkerHost capacity proofs without client fallback, minor-version feature rejection, exact-PID worker crash behavior, source lease/mutation, traversal refusal, Mark-of-the-Web propagation, BOM-less UTF-8 CLI output for redirected Unicode archive names, and end-to-end extraction through a worker when the staging root exceeds 260 characters. The numeric and classic split cases prove full listing, exact list-only capabilities, no-output operation refusal, missing-volume failure, and same-worker recovery. ZIP64 cases also prove missing/truncated per-entry extras, invalid/nonzero disk references, missing terminal records, absent/misdirected locators, missing end records, and invalid ZIP64 lengths return stable structured results while the same worker remains usable. Protocol cases prove all 306 ordered events survive a deliberately slow presentation consumer, duplicate persistent keys remain stable across a real archive reopen while session IDs change, runtime minor-2 negotiation omits both minor-3 key fields, and runtime minor-3 negotiation omits the minor-4 volume identity from raw JSON. Current-minor client tests require a valid identity, reject unknown identities that advertise data operations, accept list-only unknown state, and reject a new field under legacy negotiation. Direct-worker provider fixtures prove unknown sessions are refused before operation admission and incoherent provider identity/capabilities fail the open. The checked ZIP/7z/RAR/TAR-family corpus now asserts exact `Single` or `ConfirmedMultipart` identities. The sparse ZIP64 case has a logical size of 4,294,971,546 bytes but consumes sparse allocation rather than physical multi-gigabyte fixture space.
- The deterministic malformed-archive test applies ten recorded truncation/bit-flip cases per round across ZIP, 7z, RAR, raw GZIP, and TAR.GZ. It enforces a four-second step bound, verifies structured failure and no partial output where a mutation is rejected, and reopens a known-good ZIP after every case. The default one-round smoke and an opt-in 500-round/5,000-case run pass on the current source snapshot. Focused regressions reject an invalid single-volume ZIP entry disk reference and an overflowing RAR variable integer as `InvalidArchive`, then prove that the same worker remains healthy. This deterministic coverage is not a substitute for coverage-guided parser testing.
- The deterministic operation probe performs one warm-up plus five measured selected-entry, 128 MiB/2,048-file full-extraction, and cancellation-after-progress runs. It hashes every completed output, samples exact-worker CPU/working set, and requires zero final/staging output after cancellation; raw JSON evidence and the fixture are retained outside source.
- The opt-in physical-large-file probe creates and flushes a fully allocated deterministic stored ZIP, hashes it, performs real-worker listing/integrity/extraction, verifies the complete extracted hash, requests cancellation after bounded decoded progress, and removes only its direct-child/reparse-free generated data on success. The current 3 GiB run cancelled after 73,531,392 decoded bytes and retained only its JSON evidence.
- A loopback SMB proof uses a content-detected archive source and fresh destination through `\\localhost\C$`. The retained self-contained package lists, integrity-tests, extracts, and commits through the UNC paths with all three output hashes matching the checked fixture and zero staging residue. This exercises the Windows network redirector and UNC disk-space lookup but did not itself substitute for a remote server. The separately recorded CachyOS/Samba 4.24.6 run now supplies physical remote extraction success, disconnect, and latency evidence.
- A protocol-1.7 creation proof uses both source and new ZIP destination through `\\localhost\C$`, retains the committed output under `C:\Users\aliss\AppData\Local\Temp\RainArchives.ZipLoopbackSmbProof.13cd2278a3e84fad8b663f6d8be8c16f`, and receives a successful official-7-Zip integrity result. The 449-byte archive SHA-256 is `803B8755E50B65C59259DD967AC6C1D6A4A2BDA41F47390F6F488AAA5A124366`. A separate protocol-1.8 directory-tree proof uses both its source tree and destination through `\\localhost\C$`, retains evidence under `C:\Users\aliss\AppData\Local\Temp\RainArchives.ZipTreeLoopbackSmbProof.2208f165abc44c149c2966b0cd4ae857`, preserves an empty directory, receives a successful official-7-Zip result, and produces a 403-byte archive with SHA-256 `C4889F79A39899BF44041ECB8A4EB91CD0BC448117CBC224CCB8DB485DA99ABB`. These prove current-source loopback UNC creation only; the actual second-machine ZIP-creation source/destination/interruption gate remains open and is not closed by the accepted M1 extraction run.
- A retained self-contained App-package proof composes the Worker in its own subtree, checks package/module provenance, and drives the production WPF open/list/search/integrity/selected-extraction/close path against a checked ZIP. The selected file must be the only output and must match the fixture SHA-256; both owned processes and all staging directories must be gone after close.
- A packaged WPF cancellation proof opens an official-7-Zip-generated 3 GiB stored ZIP, starts extraction through the production folder picker and Extract binding, observes the operation in `Running` with Cancel enabled, clicks the real Cancel control, and requires authoritative `Cancelled`. The expected final destination and every `*.rainarchives-*.staging` directory must be absent before App close, Cancel must settle back to disabled, and App close must leave no owned process.
- Packaged WPF accessibility proofs open the native picker, select checked archives, and expose `Search archive entries`, `Archive entries`, and actual filenames in the production accessibility tree rather than raw view-model type names. The encrypted-archive dialog is also a distinct `Password required` window whose focused protected field is named `Archive password`, marked required, and carries operation-specific help text. App close leaves no owned process or staging directory. Product-owner acceptance now covers contrast-theme behavior, manually typed wrong-password/correct-password/extraction interaction, mixed-DPI scaling, and the Narrator/keyboard flow. Final-package keyboard-control passes cover picker open, Unicode search, arrow-key entry navigation, integrity testing, complete three-file extraction through the native destination picker with exact hashes and cleanup, disabled multipart Test/Extract controls with Tab focus skipping, in-progress integrity-test cancellation on a valid single-volume 4,296,015,872-byte ZIP64 entry, exposed terminal states, and keyboard close. The multipart UI showed no explicit warning text, so the evidence is limited to capability refusal and focus behavior. On 2026-08-23 local time, the product owner reported that all keyboard controls worked and completed ZIP selection and extraction using only the keyboard with Narrator running; combined with the retained automated control matrix, this closes the human screen-reader usability gate without claiming a separate verbatim announcement transcript.
- A deterministic low-space seam is automated. Historical Milestone 1 disposable Windows Sandbox VHD runs prove read/extraction behavior. The package-bound `1C85F6D9` Milestone 3 runner separately proves early and near-final true NTFS exhaustion, non-elevated ACL denial, and 15/15 supported creation preset cases on exFAT; each binds the retained package identity, checks recovery/original/hash state, and leaves no Rain process or ordinary staging residue. Its fresh offline x64 Sandbox also starts/closes that App and completes all six creation families. The physical CachyOS/Samba source/destination/interruption case completes the separate remote-server extraction gate only; creation-specific SMB remains open.

The exact disposable-target, evidence, physical-DPI, High Contrast, Narrator, and protected-password procedure is retained in `milestone-1-manual-windows.md`; it deliberately avoids commands that could target a real user-data disk or folder.

## Disk-full and crash simulation

Prefer a bounded disposable VHD/VHDX or a fault-injecting stream/filesystem seam owned by the test. Resolve and assert the disposable root before cleanup. Kill only the worker PID launched by the test and verify its named pipe, final destination, random staging path, and source archive afterward.

## Protocol compatibility

Retain golden v1.1 frames and every additive request/response/error fixture through v1.22. Same-binary runtime downgrade tests retain the established omission and pre-access refusal boundaries through v1.22. A minor-16 peer cannot negotiate or send TAR creation/capabilities. A minor-17 peer may use the exact TAR explicit/tree/capability shapes but cannot negotiate or send TAR.GZ, TAR.BZ2, raw GZIP/BZIP2, or managed capabilities; client refusal happens before a frame and the downgraded Worker refuses the message kind before payload parsing or filesystem access. Minor 18 refuses operation activity and both extraction-recovery requests before access; minor 19 may query activity but refuses both extraction-recovery requests; minor 20 refuses independent nested recovery reopen and support diagnostics; minor 21 permits independent reopen but refuses support diagnostics. Current peers enforce every explicit feature dependency, reject omitted required fields and unknown members, and validate bounded requests before source/destination access. The retained 1.12-1.16 matrix, narrow genuine 1.17 binaries-only checkpoint, and exact retained 1.18 source/binary artifact prove current/older behavior in both directions; feature selection alone is not treated as binary evidence. Additive fields must not change existing meanings. Any semantic change to IDs, operation phases, errors, capabilities, stream framing, temporary-source ownership, creation transaction ownership, disposition receipts, rebuild plans, recovery evidence, diagnostics, or lease ownership requires a protocol decision and compatibility fixture, not merely a DTO edit.

## Pass evidence

Retain test result files, fixture manifest hash, package lock hashes, worker/client versions, OS/runtime details, and logs scrubbed of passwords/private content. A skipped test is listed with reason. Product-owner observation closes the manual encrypted/password, contrast-theme, mixed-DPI, Narrator/keyboard, and physical remote-extraction gates when combined with the retained package automation and recorded operation evidence. One CachyOS/Samba result does not stand in for broader SMB-server, Windows-hosted SMB, removable-device, or creation compatibility.
