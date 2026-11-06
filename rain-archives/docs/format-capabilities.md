# Format capability roadmap

Current creation-tree budget: protocol 1.24 adds `creation-expanded-tree-budget-v1` for up to
65,536 expanded entries in ZIP/TAR/TAR.GZ/TAR.BZ2 and qualified RAR creation. Historical 4,096
tree counts below describe older peers; they remain the fallback without negotiation. Existing
`MaximumEntries` capability fields still describe the unchanged explicit/root limit; use
`IArchiveClient.MaximumCreationTreeEntries` separately for expansion. ZIP rebuild remains 4,096,
raw streams remain one file, byte ceilings remain 64 GiB/8 GiB RAR, and metadata remains 64 MiB.
See [the current verification checkpoint](creation-ui-remediation.md).

Legend: **M1-verified** means the current managed read path has checked-fixture, real-worker evidence for the stated case; it is not a claim that every variant is accepted. **M3 automated-verified** means the narrow write path has provider, Security, protocol, real-Worker/Client/CLI/WPF-model, Rain reopen/test/extract/hash, pinned official-tool, compatibility, and retained local package evidence. The protocol-1.18 package also passes source-at-package-time ZIP and exact-package mixed-format performance probes plus fresh offline Sandbox, standard-user ACL, true NTFS disk-full, exFAT creation/provenance, real local 8.3/mapped-drive/alternate-loopback-UNC, and the bounded official-7-Zip/Windows-libarchive/GNU/WinRAR/libarchive-3.8.9 simple and PAX conformance matrix. Product-owner direct observation covers exact-package ZIP creation/reopen/integrity and Rebuild Copy with the expected warning. Recovery Center, product-owner accessibility, actual SMB/blocked-UNC/DFS/no-file-ID, installation, signing, and publication remain separate. **Deferred M2/M3** is an intentional closed gate, not a provider promise. **Rebuild** means modification creates a complete staged replacement and commits it to a distinct absent destination; it is not in-place editing. **No (technical)** is inapplicable, while **No (policy/licensing)** is deliberately forbidden.

| Format | Phase | Detection | List / extract / test | Read encryption | Create | Create encryption | Add / replace / delete | Split volumes |
|---|---:|---|---|---|---|---|---|---|
| ZIP | M1 read, M3 write | M1-verified, content plus multipart signals | M1-verified ordinary and AES-256/ZipCrypto list/extract/test, CRC failure, truncation, empty/zero-byte entries, selected extraction, checked ZIP64 layouts, genuine classic split listing, and independently generated multi-disk ZIP64 listing; split test/extract intentionally refused and other variants deferred M2 | M1-verified AES-256/ZipCrypto read | M3 automated-verified: new unencrypted ZIP from protocol-1.7 explicit files or protocol-1.8 mixed explicit files and bounded non-reparse directory roots; 0–4,096 material entries, at most 64 GiB, root/empty-directory preservation, Store/Fast/Balanced/Maximum; absent destination only | Deferred: BCL/approved managed writers provide no qualified encryption route | M3 automated-verified at protocol 1.16: add/replace/delete, retained regular-file rename/move, bounded retained-subtree move/delete, mapped source-folder addition, and explicit ZIP-representable metadata editing by recompressing an eligible opened single-volume unencrypted ZIP into a distinct absent destination; original untouched | M1 list-only input proof; split creation deferred without multi-output journal/recovery authority |
| 7z | M1 managed read, M2 conditional native authority, M3 write decision closed | M1-verified, content | M1-verified solid list/extract/test and encrypted integrity reads; disabled SevenZip gate probes at confidence 0 and SharpCompress remains selected | M1-verified content and header encryption | Deferred and disabled; corrected official-native adapter, loader, crash, corpus, licensing/source-obligation, package, cancellation, and maintenance-owner gates are unmet | Deferred | Deferred, Rebuild only after a separately approved writer | M1 list-only proof; create deferred |
| RAR | M1 managed read, M2 conditional native read hardening, M3 conditional write | M1-verified, content | M1-verified checked solid and header-encrypted list/extract/test plus valid RAR5 directory-without-optional-CRC regression; broader method/version compatibility and corrected native reader adapter deferred M2 | M1-verified header-encryption read | Protocol-1.23 conditional: unencrypted RAR5 explicit/tree creation only when the exact separately installed signed x64 WinRAR 7.23–7.x encoder passes the Worker gate; 1–4,096 material entries, at most 8 GiB, Store/Fast/Balanced/Maximum, absent destination only; missing/old/untrusted encoder stays typed unavailable | Deferred; no password or header encryption | No rebuild/update/delete route; original untouched | M1 list-only input proof; split creation deferred |
| TAR | M1 read, M3 write | M1-verified, content/provider confirmation | M1-verified list/extract/test | No (technical) | M3 automated-verified at protocol 1.17: Store-only explicit/tree creation, 0–4,096 material entries, at most 64 GiB, regular files/directories, Unicode, zero-byte/empty directories, UTC PAX timestamps; pre-Unix and Windows attributes refused | No (technical) | No current rebuild route | No (technical) |
| TAR.GZ | M1 read, M3 write | M1-verified outer GZIP plus compound open | M1-verified list/extract/test and outer-checksum failure | No (technical) | M3 automated-verified at protocol 1.18: explicit/tree TAR creation with Store/Fast/Balanced/Maximum GZIP presets and validated outer trailer | No (technical) | No current rebuild route | No (technical) |
| TAR.BZ2 | M1 read, M3 write | M1-verified outer BZIP2 plus compound open | M1-verified list/extract/test and outer-checksum failure | No (technical) | M3 automated-verified at protocol 1.18: explicit/tree TAR creation with Balanced BZIP2 and single-member/trailing-data validation | No (technical) | No current rebuild route | No (technical) |
| TAR.XZ | M1 read, M3 write decision closed | M1-verified outer XZ plus compound open | M1-verified list/extract/test and outer-checksum failure | No (technical) | Deferred: SharpCompress 0.50.4 XZ provider is decompression-only and no compressor dependency/native gate is approved | No (technical) | Deferred | No (technical) |
| GZIP | M1 read, M3 write | M1-verified, content | M1-verified single-stream list/extract/test, truncation failure, and CRC32/ISIZE failure | No (technical) | M3 automated-verified at protocol 1.18: exactly one ordinary source file, Store/Fast/Balanced/Maximum, destination-stem synthetic member name, trailer validation | No (technical) | No current rebuild route | No (technical) |
| BZIP2 | M1 read, M3 write | M1-verified, content | M1-verified single-stream list/extract/test | No (technical) | M3 automated-verified at protocol 1.18: exactly one ordinary source file, Balanced only, destination-stem synthetic member name, single-member/trailing-data validation | No (technical) | No current rebuild route | No (technical) |
| XZ | M1 read, M3 write decision closed | M1-verified, content | M1-verified single-stream list/extract/test | No (technical) | Deferred: no approved managed compressor; native/new dependency gate required | No (technical) | Deferred | No (technical) |

Protocol 1.19 adds only bounded path-free operation activity. It changes no format, writer,
encryption, split, replacement, rebuild, preset, source-shape, or package capability above.

Protocol 1.20 adds only local extraction-staging recovery inventory and explicit one-record
read-only inspection. It changes no format, reader/writer, encryption, split, replacement, rebuild,
preset, source-shape, or package capability above, and it adds no extraction retry or cleanup
authority. The immutable protocol-1.18 M3 package remains bound to its named fingerprint.

Protocol 1.21 adds only exact-evidence copy-only reopen of one recovered nested source as a newly
detected independent depth-zero session. It does not add a format capability, reconstruct ancestry,
mutate the recovery tree, expose a temporary path, or authorize automatic restore/discard/retry.
Protocol 1.22 adds only fixed-field support diagnostics and changes no reader, writer, format,
encryption, split, replacement, rebuild, preset, or recovery-mutation capability. The immutable
protocol-1.18 M3 package and its format claims remain bound to their named fingerprint.

Protocol 1.23 adds distinct conditional `rar-create-v1`, `rar-create-directories-v1`, and
`rar-write-capabilities-v1` routes. The capability reports a closed availability enum and can create
only when the exact separately installed encoder passes its path, ordinary-file, AMD64, Authenticode
publisher, and 7.23–7.x version gate. Rain does not bundle or discover WinRAR through `PATH`, current
directory, or registry. The provider receives validated streams rather than original source paths,
materializes them under one bounded random private workspace, disables configuration injection,
requires encoder create/test success, and then performs complete SharpCompress path/kind/length/
timestamp/content-digest readback through Rain's existing staged no-replace transaction. Encryption,
split output, SFX, overwrite, update/rebuild, arbitrary switches, and password arguments remain
unavailable. `NotInstalled`, `UpdateRequired`, `UntrustedBinary`, `UnsupportedPlatform`, and
`RestartRequired` all keep creation disabled. The concrete provider is re-resolved at capability,
admission, and post-queue execution; its exact verified executable stays leased against write/delete
replacement throughout native use. Token-owned workspaces have an exclusive live claim. Post-handshake
maintenance runs outside request dispatch under one global candidate/object/time budget, opens stable root
ancestry and owner records no-follow, and deletes only from one complete bounded inventory of an exact inactive
ordinary non-reparse one-link remnant; suspicious state is preserved. Cancellation must prove the exact child
exited after kill, and cleanup failure is propagated. Exact-PID tests prove that contract, which remains crash
recovery rather than hostile same-user containment.
Protocol 1.22 peers negotiate no RAR creation feature and refuse it before access.

The TAR-family writer and Security staged validator preserve the qualified source timestamp exactly in
PAX output. SharpCompress 0.50.4's public read descriptor can differ by one 100 ns tick when converting
a fractional PAX timestamp; exact-package evidence retains that observed delta instead of claiming
bit-identical public metadata. BCL-generated `./PaxHeaders.<process-id>/` helper names also mean PAX
container bytes are not reproducible across Worker PIDs; deterministic namespace/order and exact
material content remain the qualified contract. Payload, staged-manifest, and publication validation
remain exact.

Current worker policy refuses multipart entry streaming, extraction, and integrity testing even if the managed provider can enumerate volumes. Protocol 1.4 distinguishes `Single`, `ConfirmedMultipart`, and `Unknown`. A confirmed multipart descriptor advertises only `List`, `ReadVolumes`, and `ReadEncrypted` where applicable; an unknown descriptor is list-only without `ReadVolumes`; client validation rejects a broader or incoherent descriptor, and operation handlers retain structured refusal as defense in depth. Complete checked split ZIP/7z/RAR sets list successfully. ZIP proofs include a deterministic 13-volume numeric set crossing `.009`/`.010`, a DotNetZip-generated classic `.z01` through terminal `.zip` set, and an official-7-Zip-generated 22-volume numeric multi-disk ZIP64 set with a 4,296,015,872-byte entry. Rain lists each complete set with exact list-only capabilities, refuses test/extract without output, and remains usable after a missing-volume refusal. Split ZIP is recognized from checked numeric/classic names and ZIP disk/spanning metadata rather than relying only on the provider's volume count. An interchanged middle volume can still expose metadata through the managed library, so shipping operations require identity-locking, fingerprinting, and validating every volume first.

Protocol 1.5 permits bounded complete-entry preview only for a `Single` session whose provider advertises `OpenEntryStream`. Current checked source-level evidence covers ordinary and AES-256 ZIP; solid, content-encrypted, and header-encrypted 7z; ordinary and header-encrypted non-solid RAR; TAR/TAR.GZ/TAR.BZ2/TAR.XZ; and raw GZIP/BZIP2/XZ. Tests compare complete decoded payload hashes, exercise wrong-password recovery, repeat out-of-order solid-7z reads, and retain the transport limits, cancellation, and cleanup checks. Solid RAR deliberately does not advertise `OpenEntryStream` under SharpCompress 0.50.4: its supported path is sequential, the checked direct random-entry path fails, and the current stream contract has no bounded accounting for decoding every preceding solid member. Integrity testing and selected extraction remain available for the checked solid-RAR fixture, and the descriptor carries structured warning `archive.preview_unavailable` so clients can explain the missing affordance. Multipart, unknown-volume, range/seek, and automatic nested-archive work remain deferred or forbidden, and no blanket claim is made for every encoder version or compression method.

Protocol 1.6 adds explicit bounded nested opening without adding a new format-wide capability flag. Eligibility is determined from the opened parent session: it must be `Single`, extraction-capable, and contain a material file entry that can be integrity-copied within the requested limits. The child is detected independently from content, so its filename extension does not choose the provider. Checked real-worker evidence opens nested ZIP payloads through three levels, refuses level four, and recovers from a malformed child; a header-encrypted nested 7z fixture proves wrong-password refusal followed by correct-password success. This evidence does not claim that every parent/child format or compression method is qualified. Confirmed multipart and unknown parents are refused. Solid RAR returns `nested.solid_source_deferred` because its safe sequential path does not yet expose a bound for skipped-member decode work; checked integrity testing and extraction remain unaffected. Every nested source is temporary and read-only, and nested opening never changes the creation/modification columns above.

ZIP64 support is layout-specific. The checked entry-count, large-offset, and per-entry size/offset-extra single-volume layouts support listing, integrity testing, and extraction. The independently generated multi-disk ZIP64 layout supports listing only under the multipart policy above. A standards-valid central-entry disk-start sentinel with ZIP64 disk number zero is currently refused with `UnsupportedFormat` / `archive.unsupported_zip64_disk_start` because SharpCompress 0.50.4 fails internally on that representation; a nonzero disk reference remains `InvalidArchive`. Missing/truncated ZIP64 extras and malformed terminal records are rejected as `archive.invalid_zip_central_directory`. A future provider revision may remove the explicit unsupported result only after the same fixtures pass listing, testing, extraction, and worker-recovery checks.

Protocol 1.7 exposes negotiated `zip-create-v1`: a session-independent request for a nonexistent destination, explicit regular source files, validated archive-relative names, and one of four presets. Protocol 1.8 adds separate `zip-create-directories-v1` rather than changing that request. It may combine explicit files with one or more nonempty archive-root/directory-source mappings; worker-owned handle-relative expansion includes each root and empty descendant directory, rejects reparses and collisions, and is finally compared with the exact planned identity manifest before commit. Real-worker tests create all four explicit modes plus an empty ZIP and mixed directory trees, verify bytes through .NET `ZipArchive`, reopen through Rain's managed reader, and exercise cancellation, unsafe names, source replacement/tree mutation, destination races, timestamp clamping, cleanup, downgrade refusal, same-worker recovery, and exact-worker death during copy and final tree revalidation. Retained current-source proofs use the hash-verified official 7-Zip Extra 26.02 `x64/7za.exe` to test and extract all four explicit-file outputs plus a mixed directory-tree output containing an empty directory and Unicode file; all extracted hashes match. Windows `bsdtar` 3.8.4/libarchive 3.8.4 independently lists and extracts the same tree output with both hashes and the empty directory preserved. These two external readers cover one bounded dataset/layout and are not package acceptance or broad version/layout conformance.

Protocol 1.12 adds negotiated `zip-rebuild-copy-v1` as a separate opened-session operation. One closed plan deletes exact persistent entry keys and maps explicit regular files to validated add/replace targets. Eligible retained entries are checksum-validating streamed into a new writer and all material is recompressed with Store/Fast/Balanced/Maximum under one checked 4,096-entry/64-GiB budget. Unique mapped files keep deny-write/delete identity leases through final validation and commit. The source ZIP is revalidated, and the durably flushed staged ZIP must exactly match the planned count/order/path/kind/length/attribute/timestamp manifest before the journaled no-replace commit. Unit, adversarial, real Worker/Client/CLI, WPF-model, crash, race, low-space, and cancellation tests prove no destination/staging residue on failure and byte-identical preservation of the original. A pinned owned copy of official 7-Zip Extra 26.02 tests and extracts representative mixed rebuilt output for every preset and matches the exact expected file/path manifest. This qualifies only that deterministic corpus; it is not broad layout, package, SMB, or manual UI acceptance. The ZIP rebuild route still refuses encryption, encrypted or multipart sources, overwrite, in-place replacement, split output, and 7z output; later TAR/raw creation does not expand opened-session rebuild authority. Opened-session capability flags describe that exact format/provider/session, not a library-wide superset; the UI must never imply an in-place update when a complete rebuild is required.

Protocol 1.13 adds negotiated `zip-rebuild-rename-v1` without changing the minor-12 request. Each directive identifies one retained regular file by exact normalized persistent path plus duplicate ordinal and supplies one final archive-relative file path. The closed planner resolves rename/delete/add/replace together, permits simultaneous swaps, rejects multiply edited keys and unsafe/case/NFC/directory/prefix collisions before staging, checksum-copies retained bytes, preserves representable timestamp/basic attributes, reports escaped final paths, and compares the finalized archive with the exact renamed manifest. Real Worker/Client/CLI/WPF-model tests cover mixed edits, cancellation, race, low space, exact-PID crash/recovery, Rain reopen/list/test/extract, and original-hash preservation; official 7-Zip Extra 26.02 tests/extracts every preset. The retained protocol-1.12 and 1.13 binary checkpoints prove the common negotiated surface and older-worker local refusal. This remains deterministic source/binary-corpus evidence, not a refreshed product package, broad layout/version conformance, clean-machine qualification, or manual GUI/accessibility acceptance.

Protocol 1.14 adds negotiated `zip-rebuild-tree-edit-v1` and a separate bounded Worker-owned source-folder review query without changing minor-12 or minor-13 messages. Retained subtree move/delete echoes an exact leased-snapshot revision; mapped folders preserve their selected root and empty directories through pinned non-reparse manifests; file and tree directives share one collision/resource budget and one final namespace. Worker review reports canonical roots plus bounded descendant/file counts before WPF confirmation, while WPF performs no source enumeration. Real Worker/Client/CLI/WPF-model tests cover empty/synthetic/explicit directories, mixed edits, mutation/reparse/hard-link/ADS refusal, cancellation, race, low space, exact-PID recovery, Rain reopen/list/test/extract, original hashes, and retained 1.12/1.13 compatibility. Official 7-Zip Extra 26.02 and Windows `bsdtar` independently validate the qualified tree corpus. Metadata editing, overwrite/in-place replacement, encrypted or multipart rebuild, and split output remain deferred.

Protocol 1.15 adds negotiated `zip-rebuild-metadata-v1` without changing the earlier rebuild routes. It may set one exact timezone-less raw ZIP DOS last-write tuple and the low-word ReadOnly/Hidden/System/Archive flags for a retained or mapped final entry; Directory is forced only by entry kind. Strict valid-calendar, 1980-2107, even-second, allowed-mask, typed-target, duplicate, count, path, and frame checks apply before staging, and the finalized archive must reproduce the exact normalized tuple/mask. All 16 allowed file masks and boundary times round-trip through the BCL writer/reader, and official 7-Zip Extra 26.02 reports compatible values for the qualified corpus. Comments, extra fields, ACLs, owners, ADS, creation/access times, POSIX modes, links, original compression methods, arbitrary external attributes, overwrite/in-place replacement, encrypted or multipart rebuild, and split output remain unsupported rather than silently preserved.

Protocol 1.16 adds negotiated `zip-write-capabilities-v1` as a separate bounded capability query; it
does not alter the ZIP create or rebuild request shapes. Global capability reports managed-writer
creation support, while an opened-session query reports exact rebuild eligibility. Responses enumerate
supported add/replace/delete/file-rename/tree-edit/metadata operations, Store/Fast/Balanced/Maximum,
current entry/decoded-byte bounds, and explicit false values for encryption, filename encryption,
split output, existing-destination replacement, and in-place update. Client and Worker revalidate
session ownership, negotiated dependencies, and requested operation before source/destination access;
CLI and WPF use the same typed response before pickers and write routes. The App-only preset store
persists validated compression and supported metadata defaults with immutable built-ins, strict
Unicode/name and size bounds, reparse refusal, one per-user mutex, atomic last-known-good replacement,
and visible non-secret fallback warnings. This remains M3 source/package evidence for the narrow
managed ZIP path, not encryption, overwrite, in-place, split, TAR/7z/RAR creation, installation,
signing, publication, or manual GUI acceptance.

Protocol 1.17 adds only `tar-create-v1`, `tar-create-directories-v1`, and
`tar-write-capabilities-v1`. TAR requests are distinct from every ZIP request and advertise only
`Store`; minor-16 peers negotiate no TAR feature and refuse all three routes before filesystem
access. The staged TAR manifest binds each regular file to its SHA-256 digest, and readback parses the
complete archive through the provider validator, compares path/kind/length/UTC timestamp/content,
rejects unsupported kinds and trailing content, and requires finalization exactly once. TAR output
estimation accounts conservatively for 512-byte headers, payload padding, EOF blocks, and bounded
PAX/extended-header overhead without changing the ZIP estimate.

Protocol 1.18 adds distinct TAR.GZ/TAR.BZ2 explicit/tree requests, raw GZIP/BZIP2 requests, and
`managed-write-capabilities-v1`. The raw routes accept exactly one ordinary source file and no
directory mapping; the synthetic member name is the normalized destination filename with the final
compression suffix removed. Every response explicitly reports `CanRebuild=false` and false values
for encryption, filename encryption, split output, existing-destination replacement, and in-place
update. The compressed validators prove inner manifests and payload hashes, finalize outer streams
once, validate canonical trailers/footers, and reject junk or concatenated BZIP2 members. Real Worker
outputs reopen, test, and extract through Rain and pass hash-pinned official 7-Zip Extra 26.02
test/extraction. The retained protocol-1.18 local package repeats all six managed creation formats collectively:
direct Client creation covers TAR/TAR.GZ/TAR.BZ2/GZIP/BZIP2, the in-process CLI covers
  ZIP/TAR.GZ/BZIP2, and representative WPF-model creation covers ZIP/TAR.GZ/GZIP. This remains one
  deterministic tool/corpus and local automated package gate. Its manifest has a 75-sample
  mixed-format package baseline with 90 pinned-7-Zip validations. Package-bound disposable evidence
  now covers all supported preset families on exFAT, typed standard-user ACL and true-disk-full failures
  on NTFS, fresh offline self-contained startup, and local 8.3/mapped-drive/alternate-loopback-UNC
  paths. The finite M3 external-reader matrix additionally covers pinned official 7-Zip, Windows
  libarchive 3.8.4, GNU tar/gzip/bzip2, WinRAR 7.20, and distinct libarchive 3.8.9 simple/PAX layouts.
  Product-owner direct exact-package ZIP creation/reopen/integrity and Rebuild Copy observation now
  passes with the expected warning. This is still not Recovery Center or product-owner current-flow
  accessibility, actual remote-SMB/blocked-UNC/DFS/no-file-ID,
  installation, signing, or publication acceptance. Open-ended version/method/layout breadth remains
  Milestone 8 release hardening.

Milestone 5C does not change this capability matrix or protocol 1.22. The production create surface
derives its selectable ZIP/TAR/TAR.GZ/TAR.BZ2/GZIP/BZIP2 rows, presets, directory support, and input
bounds only from the current authenticated capability responses; stored format/preset defaults are
bounded local hints and are clamped again before start. Raw GZIP/BZIP2 still require exactly one
ordinary file. Eligible ZIP Rebuild Copy repeats the opened-session ZIP capability query before using
its stored preset hint. Final review requires a distinct absent destination and states that every
source/original and any existing/raced destination are preserved. Encryption, filename encryption,
split output, overwrite/replacement, retry, pause, reorder, decision continuation, in-place update,
native 7z, and RAR creation remain unavailable. The exact M5C package
`RainArchives.M5CAutomatedPackage.1.22.6942C0B0` proves representative settings-backed ZIP create and
eligible rebuild with Rain/external reopen and unchanged hashes; it does not upgrade any broader
format/tool/filesystem/manual claim in the table.

Milestone 5D also leaves this capability matrix and protocol 1.22 unchanged. Archive/entry properties
are presentation-only projections of validated existing descriptors and capability responses; a
missing or unrepresentable optional value is omitted rather than inferred. Safe preview still requires
the negotiated protocol-1.5 `OpenEntryStream` route and the existing session/password/fingerprint
authority. The App reads at most 1 MiB plus one oversize-detection byte, accepts strict UTF-8 or
BOM-proven UTF-16/UTF-32 as inert text, and reports binary, oversize, unsupported, offline, stale,
cancelled, or fixed error states without provider text or a path. The exact M5D package
`RainArchives.M5DAutomatedPackage.1.22.AE156AB8` proves representative text, binary/NUL, and oversize
flows with source hashes and cache/Worker cleanup; it does not authorize active content, generic Open
With/export, external launching, new formats/providers, or mutation. The `49601147` package remains
rejected predecessor evidence because of its remediated one-based duplicate-occurrence display
overflow; that presentation fix does not change any format or wire capability.

The focused native dependency probe does not change this matrix. Unmodified SharpSevenZip 2.0.109 accepted the checked truncated ZIP in `Check()` while official 7-Zip reported an archive error. Native 7z/RAR authority therefore remains conditional on a newer audited upstream build or a separately approved minimal fork that exposes archive error/warning flags and the required metadata; managed support remains the shipping path until that gate passes.
