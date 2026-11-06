# Milestone 3 Windows physical and manual acceptance

Status date: 2026-08-26. The immutable protocol-1.18 M3 package has fresh offline Sandbox,
non-elevated ACL, true NTFS disk-full, exFAT, and local path-alias evidence. The product owner has now
directly observed package-bound ZIP creation/reopen/integrity and Rebuild Copy with the expected
warning, superseding the controller-specific destination-input blocker for those two flows. Recovery
Center, accessibility, staging-inventory, rebuilt-output-manifest/hash, remote/device, and release
claims remain separate. Earlier C400 physical results remain useful history but are superseded for
the rerun cases below; FBBBF558, D9E82567, and 67ED9164 are diagnostic or superseded.

## Bound Milestone 3 source and package

- Canonical production fingerprint: 132 inputs,
  `1C85F6D929DE5F940E1357C72C210A8647DFD2E9F38A0A2B0B0832127F5CE844`.
- Self-contained package: `artifacts/RainArchives.M3AutomatedPackage.1.18.1C85F6D9`.
- Package manifest SHA-256:
  `6A8CB71E3924E04451299E9C4C4AB2F022D9648FCFE8113186656C6F0D0FF47A`.
- App tree: 609 files / 232,216,977 bytes,
  `C61749B2CC94A4E66606D3615DF88C44A2D95F599EE1F2B78C812BAC1BCFCFB0`.
- Private Worker tree: 205 files / 84,699,121 bytes,
  `36C64503CC9EA9FD6CF0F6C390F75F8871BEF9BFCA5461456CE645CBACB2966F`.
- Exact forbidden packaged assets `7z.dll`, `7za.exe`, and `SharpSevenZip.dll`: zero.
- The package is self-contained application evidence, not an installer, signed artifact, or published
  release.
- Protocol 1.19 activity and protocol 1.20 read-only extraction-recovery source are later
  Worker/Client/Security slices with no new package. They do not relabel this protocol-1.18 package
  or any package-bound physical result as current-source evidence.

The non-shipping acceptance runner under `eng/RainArchives.M3AcceptanceProbe` is bound to the current
`1C85F6D9` identity. It refuses a mismatched source fingerprint, manifest, App/Worker tree, principal
binary, or forbidden asset before making a clean-machine/storage/path claim. Its retained self-contained
runtime is `artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/probe-runtime-v1`.

The final host-side runner smoke revalidated the manifest/App/Worker trees and all six Rain round trips
after the evidence-runner review. It used the explicit developer-tool override and is therefore not a
clean-machine claim:
[clean-machine-evidence.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/probe-host-smoke-v2/clean-machine-evidence.json),
5,441 bytes, SHA-256
`61B7A8BCB793DEFECEC43A17D1BB0709F659B24899B1AF60073A0E10D2098AF3`.

The Sandbox, ACL, disk-full, exFAT, and local-alias claims below bind only to source
`1C85F6D929DE5F940E1357C72C210A8647DFD2E9F38A0A2B0B0832127F5CE844`, package
`RainArchives.M3AutomatedPackage.1.18.1C85F6D9`, and manifest
`6A8CB71E3924E04451299E9C4C4AB2F022D9648FCFE8113186656C6F0D0FF47A`.

## Exact-current physical gates

### Fresh offline Windows Sandbox

A newly started x64 Windows Sandbox ran with networking disabled and without a developer-tool
override. The runner found no developer-tool dependency, verified every bound package identity,
started `RainArchives.exe`, observed the `Rain Archives` main window, requested an ordinary close,
and received exit code 0. It then created ZIP, TAR, TAR.GZ, TAR.BZ2, GZIP, and BZIP2 through the
packaged production Client/private Worker, reopened each output through Rain, integrity-tested and
extracted it, and matched the source SHA-256. No Rain process or staging artifact remained.

Evidence:

- [clean-machine-evidence.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/clean-machine-sandbox-v1/evidence-host/strict/clean-machine-evidence.json),
  5,155 bytes.
- Evidence SHA-256:
  `505B6504A5D67E9D114F6E1121FE547D860ACD07B0B56BCF419595E9FA4CC2A4`.

Windows Sandbox's default WDAG account was elevated in this run, so this result is not reused as the
standard-user permission proof. The separate ACL case below ran non-elevated. The Sandbox was stopped
by its exact instance ID and `wsb list --raw` returned an empty environment list afterward. Microsoft
documents the `.wsb` configuration surface, including networking and mapped-folder controls, and the
Sandbox command-line start/list/execute/stop lifecycle in the
[Windows Sandbox configuration reference](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)
and [Windows Sandbox CLI reference](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli).

### Standard-user ACL denial

The storage Sandbox used a non-elevated `WDAGUtilityAccount` against a test-owned denied destination.
A direct write raised `UnauthorizedAccessException`; the packaged Worker returned terminal `Failed`,
category `PermissionDenied`, code `filesystem.permission_denied`. The source remained unchanged, the
destination stayed absent, staging and recovery counts stayed zero, and the same Worker immediately
created/reopened/extracted a follow-up ZIP with the exact source hash.

### True NTFS disk exhaustion

A disposable 1,072,623,616-byte NTFS VHD labelled `RAINM3NTFS` was filled until the operating system
returned Win32 error 112. Two real packaged ZIP operations exercised separate states:

- Early failure requested the trigger after 1 MiB and observed it after 1,835,008 copied bytes.
- Near-final failure requested the trigger after 107,374,182 bytes and observed it after 108,003,328
  copied bytes.

Both operations returned terminal `Failed`, category `InsufficientSpace`, code
`resource.insufficient_space`. Each preserved the complete source, published no destination, and left
no staging artifact. Each retained one valid `Writing` recovery journal after the real low-level
failure; the runner inspected the exact record as `NoOwnedArtifactObserved`, confirmed absent staging
and final artifacts, explicitly applied `AcknowledgeNoOwnedArtifact`, and verified zero records
afterward. The same Worker then completed a hash-verified follow-up creation. No true-disk-full claim
is inferred from an injected stream or sparse allocation.

### exFAT creation and provenance

A disposable 1,609,236,480-byte exFAT VHD labelled `RAINM3EXFAT` ran every supported creation preset:
four ZIP, one TAR, four TAR.GZ, one TAR.BZ2, four raw GZIP, and one raw BZIP2 case. All 15 operations
completed with the truthful `provenance.not_applied` warning, published no `Zone.Identifier`, reopened
through Rain, passed integrity, extracted, and matched the source SHA-256. The source and its original
provenance stream remained unchanged. Staging and recovery counts were zero and all test-owned work
roots were removed.

The combined storage evidence is:

- [storage-evidence.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/storage-sandbox-v1/evidence-host/storage-2987690bd9d242629ca5103947a1aae4/storage-evidence.json),
  17,402 bytes.
- Evidence SHA-256:
  `9CFEC377120A3C1194EB1B72215062612C01475FB8C6BDB272A6E10A69B85924`.

The VHDs and work roots were disposable, ownership-token bound, and removed after evidence capture.
No removable or user-data volume was used.

## Findings closed before acceptance

The first physical passes exposed three current-source issues. None of their earlier package outputs
is counted as acceptance:

1. A real disk-full operation could aggregate its authoritative I/O failure with a cleanup failure;
   generic aggregate mapping hid `InsufficientSpace`. Worker error translation now unwraps the first
   authoritative inner failure, with regressions proving a secondary cleanup error cannot replace
   `InsufficientSpace` or `SourceChanged`.
2. RFC 1952 permits raw GZIP MTIME zero to mean that a timestamp is unavailable. The reader had
   projected it as the Unix epoch, which exFAT rejected during extraction. Raw GZIP now represents
   zero MTIME as unknown while preserving nonzero timestamps; the exFAT matrix then passed.
3. The live WPF shell still labelled its main action `Create ZIP`, and selected-format accessibility
   help retained ZIP wording. The action is now `Create archive`; the dialog updates source,
   destination, preset, cancel, and create labels/help from the selected negotiated format and keeps
   ZIP-only preset naming disabled elsewhere.

At the C400 physical checkpoint, the rebuilt source gate was 358 unit/WPF, 29 adversarial, and 293
integration tests: 680/680, zero failures or skips. A hard-kill cleanup test also exposed a transient
Windows delete-pending race in its own disposable-directory disposer. The helper now retries only
transient I/O/access failures for five seconds and preserves the final exception.

The later current `1C85F6D9` automated gate retains the same 680/680 counts after two lifecycle fixes:
extraction/integrity now settle and zero operation secrets before terminal publication, and Client no
longer queues a redundant slot discard after an authoritative cancelled Open response. The focused
slot case passed ten rebuilt repeats; the 200,000-entry Open-cancellation regression passed 30 rebuilt
repeats; the complete 293-test integration suite then passed. These changes are why predecessor
physical evidence is not relabelled as exact-current acceptance.

After the exact-current Sandbox/storage/alias/external-tool runs and acceptance-runner review, a fresh
Release solution build again passed with zero warnings/errors, formatter verification passed, and all
358 unit/WPF, 29 adversarial, and 293 integration tests passed with no skips. The production fingerprint
remained 132 inputs at `1C85F6D9`; non-shipping `eng`, documentation, and evidence artifacts do not
alter that identity.

## Direct WPF interaction

### Retained predecessor completed flow

After the host-access boundary was removed, the exact BC predecessor package completed a direct native-control
flow. `Create archive` opened the native source and destination pickers, created a ZIP from the checked
`tests/fixtures/corpus/archives/zip/plain.zip`, reopened it, listed the nested `plain.zip`, and completed
integrity testing. The original checked fixture remained 513 bytes with SHA-256
`EC2B18237115E295366F6DE410B0588D85F1FD274910244399554235B4EEB541`.

The same session used Rebuild Copy to publish a distinct absent `plain - rebuilt.zip`. WPF surfaced
the expected `Completed with warnings` metadata-preservation warning, reopened the rebuilt copy, and
completed integrity testing. Created and rebuilt outputs were each 445 bytes with SHA-256
`DA4AE3FA00447C24234EA58A6EEAA9601B958951BA993129144194D04C20589F`. The pinned Windows
`tar.exe` (`9B77D4C912F2EDAE8C241D0ECE1094D2AC068B084269CEAF85D7C7B085D2AE86`) independently listed and
extracted both outputs; each recovered the exact 513-byte source hash.

The directly opened Recovery Center showed zero pending, active, suspicious, or incomplete creation
records and no destination inspection. Its nested-workspace tab showed zero reviewable, active, or
suspicious records and no inspected workspace. Closing the App left zero Rain processes and zero
staging artifacts. The exact disposable GUI/output roots were checked for reparses and moved to the
Recycle Bin after validation, so those outputs are recoverable but are not retained release assets.

Retained evidence:
[gui-wpf-acceptance.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.BC1FB4A4/gui-v1/gui-wpf-acceptance.json),
2,943 bytes, SHA-256
`3CF1FB9DCD2C9F9855C96113711F84B863015EF8838341B8A9F3C201DB703D00`.

This qualifies direct predecessor-package create/rebuild/recovery interaction. It does not transfer
to `1C85F6D9`, and it does not transfer the historical product-owner accessibility acceptance to the
new creation flows: an exact-current direct rerun plus product-owner Narrator, keyboard-only, High
Contrast, and mixed-DPI observation remain separate.

### Exact-current environment-blocked attempts

The exact packaged `RainArchives.exe` opened normally under the current host account. The Windows
accessibility tree exposed `Open archive`, `Create archive`, `Open archive recovery center`, and the
correct disabled-with-no-archive states for Extract, Rebuild ZIP copy, Test, and Search. The approved
desktop controller then refused both an accessibility-targeted Create invocation and a keyboard Tab
before input delivery with `GetCursorPos failed: Access is denied. (0x80070005)`. The exact App process
accepted an ordinary main-window close and no Rain process remained. No create/rebuild/recovery claim
is inferred from this observation.

Retained attempt evidence:
[gui-controller-attempt.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/wpf-direct-v1/gui-controller-attempt.json),
1,436 bytes, SHA-256
`8C09561E7E470ED03F0E7C93FF0635F9DAE87EF494AF3D4B29FF412531A72CF5`.

After full host access was enabled, a second exact-current retry advanced beyond that first boundary.
The controller opened `Create archive`, opened the native `Add files to ZIP` picker, navigated it by
mouse, selected an 81-byte Unicode fixture named `gui-source-雨.txt`, and observed the WPF grid expose
the exact File kind, archive path, and selected source path. The fixture SHA-256 remained
`612181229B5C89078B75B23D5FAF118B6DC957D5E3867AE23B3FF0267F1CFE66`.

The destination edit received focus, but the controller could not address that owned-modal edit
through its value API; text input produced no visible value and Tab did not advance focus. Moving the
modal across displays did not restore text routing, and the later indexed Create action resolved
outside the root-window bounds. A bounded Windows On-Screen Keyboard fallback was also inaccessible
because it ran at higher Windows integrity. No creation or rebuild operation started, the Recovery
Center was not inspected, every intended/fallback output stayed absent, staging remained zero, the
fixture stayed unchanged, all exact package/Worker/temporary-keyboard processes were stopped, and the
temporary picker copies were removed from their external locations.

Retained second-attempt evidence:
[gui-controller-attempt.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/wpf-direct-v2/gui-controller-attempt.json),
3,859 bytes, SHA-256
`967C4CDD630D3498FE67EF2DA9E293ED02A24CC9A9D09029AC244AA6B60A91BC`.
The second attempt supersedes the claim that every controller click is denied, but it still does not
qualify destination entry, creation, reopen/test, Rebuild Copy, Recovery Center state, or accessibility.
At that checkpoint the direct exact-current flow remained
`manual_pending_environment_partial_interaction`.

A third bounded retry isolated the display-placement hypothesis. Before opening any modal, the exact
packaged App main window was moved from negative display coordinates to positive primary-display
coordinates (`224,245`, `1107x714`). The owned creation dialog then opened fully on that same display
at `412,308`, `807x614`. `DestinationInput` received accessibility focus and showed a visible caret.
Literal controller text input of `E:\a.zip` left the field blank; clicking the edit, verifying focus and
the caret again, and repeating the same literal input also left it blank. This rules out negative or
cross-display root/modal coordinates as the cause, but does not establish why this environment cannot
route controller text into the owned WPF modal.

The dialog was cancelled and the main window was closed through their ordinary controls. No operation
started, both intended short output paths remained absent, exact package process and staging counts
were zero, no forced termination was required, and the unused 81-byte fixture was retained inside the
evidence tree with no external picker copy left behind.

Retained third-attempt evidence:
[gui-controller-attempt.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/wpf-direct-v3/gui-controller-attempt.json),
3,159 bytes, SHA-256
`849B5C8003D6BF30A8AC8A0DCF60D2752CB03EC2C7D33779A2A360131D42639E`.
At that time the controller-only flow was classified
`manual_pending_environment_owned_modal_text_input`. The later product-owner observation below
supersedes that blocker for create/reopen/test and Rebuild Copy only; Recovery Center and
accessibility are not transferred from either controller attempt.

### Product-owner exact-package create and rebuild observation

The product owner submitted the following exact package checks:

- `package-manifest.json` SHA-256
  `6A8CB71E3924E04451299E9C4C4AB2F022D9648FCFE8113186656C6F0D0FF47A`;
- `RainArchives.exe` SHA-256
  `9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26`;
- checked `plain.zip` SHA-256
  `EC2B18237115E295366F6DE410B0588D85F1FD274910244399554235B4EEB541`.

The App displayed `manual-created.zip` as ZIP with one 513-byte `plain.zip` entry and completed its
integrity test successfully. Rebuild Copy then completed with the expected warning that comments,
extra fields, original methods, and metadata outside representable timestamps/basic Windows
attributes are not preserved. A post-close `Get-Process` query returned no `RainArchives` or
`RainArchives.Worker` rows. The two manual output names contained identical 385-byte archive bytes at
SHA-256 `C7047635B4E94697E5FFAFB72F2D2C0D6796C609EE0981065A47C990015BFE0C`; they were moved intact
from the checked corpus into the retained acceptance tree so the immutable checksum manifest remains
exact.

Retained evidence:
[product-owner-gui-evidence.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/product-owner-gui-v1/product-owner-gui-evidence.json),
3,694 bytes, SHA-256
`A18B5DB00EF01A310B91E2E37FA41BE12CAD1952B8D00B96BDA8B0E6AC22092D`;
[create-integrity-success.png](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/product-owner-gui-v1/create-integrity-success.png),
54,970 bytes, SHA-256
`FD070354FC9DC87FEF6B6EA2D6FD7E1C603FA4DE2F4CD8E917A004E6AA3B6AA3`;
and [rebuild-warning-success.png](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/product-owner-gui-v1/rebuild-warning-success.png),
73,339 bytes, SHA-256
`7C23167F301ED1952882930EB1ED2253908692F1A864F232AF9255B87DD63037`.

The submitted staging query used the literal placeholder `YOUR-DISPOSABLE-WORK-FOLDER` and failed
before enumeration, so it supplies no staging-residue evidence. No rebuilt-output manifest/hash,
Recovery Center state, Narrator/keyboard-only/High Contrast/mixed-DPI detail, remote/device behavior,
installation, signing, or publication claim is inferred. The product owner explicitly deferred the
unavailable second-PC, second-drive, cross-device, and remote checks for later; those deferrals do not
block independent automated development.

## Exact-current local path-alias conformance

Three exact-current probes exercise aliases available on this host. Each used the packaged Contracts,
Client, and private Worker, reopened/tested/extracted through Rain, matched source hashes, preserved
the source, left no recovery/staging/process residue, and removed only its exact reparse-free owned
root afterward.

- A real auto-generated 8.3 source path (`...\RA5305~1\D86828~1\EIGHTD~1.TXT`) created raw GZIP.
  Source/extraction SHA-256 was
  `5D1F5B3ACB176F40433D09D1A8F526BE028A6CB2FFC2BD883E3DEC86BF7E1BFC`; the 72-byte archive SHA-256
  was `01F591CFCB05F55592E696E53CB71CEB99540432EBB79F43310E93C665B88DB1`.
- A reversible `subst Z:` mapping of the exact owned root created raw BZIP2 through the mapped spelling.
  Source/extraction SHA-256 was
  `6FE5157E9F8D05572B8017DDF27E9D86687CAB09DA1030251A75570FC4DB66A2`; the 99-byte archive SHA-256
  was `D6A812F2EDA4DE0C3FE79562A25CC7D23BA39318D638187C5C621C6027233807`. The complete `subst`
  mapping set was byte-for-byte equivalent before and after the run.
- `\\localhost\C$` and `\\127.0.0.1\C$` addressed the same owned target through alternate loopback UNC
  spellings. TAR.GZ creation, reopen, listing, integrity, and extraction completed with
  `unc-payload.txt`. Source/extraction SHA-256 was
  `B750FB4011BC8070AB22227DB766FACE121A77546B872108771F60CE2DFF78AE`; the 276-byte archive
  SHA-256 was `F490839105202971935A11B15C71CCBA627278B9AED87F771DB017D19C12515F`.

The loopback case qualifies alternate local UNC spelling only. It is not an actual second-machine SMB
or DFS result. This host still does not expose an owned DFS namespace or a filesystem/server without
usable child file IDs.

Retained evidence:
[alias-evidence.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/alias-v2/alias-evidence.json),
3,671 bytes, SHA-256
`8D1DAD376D864FBC7A3B1E760ED4DAA6576EDFA2C2B74E20E372808E01644852`.
This final rerun records only the pre/post mapping count and SHA-256, not unrelated mapped-drive
targets. Both values were zero/count and the empty-set digest before and after. `alias-v1` is retained
only as superseded diagnostic evidence from before that evidence-hygiene correction.

## Current-source and package performance evidence

The current `1C85F6D9` source rerun of `CreationOperationProbe` produced 40 measured ZIP samples plus ten
warm-up/cancellation validations. Every output passed Rain reopen/integrity/payload-hash verification
and hash-pinned official 7-Zip Extra 26.02. Cancellation after partial progress settled in 14.8045 ms
for explicit files and 16.2112 ms for a tree, with no destination or staging residue. Retained JSON:

- [creation-operation-probe.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/performance-v1/creation-operation-probe.json),
  201,308 bytes, SHA-256
  `54F65B108FBA5F1336D5F8113D618C7C31CF6DA31F2D1E711BD6E6EF2A63CDA6`.

The exact `1C85F6D9` package then ran one warm-up plus five measured samples for every supported preset:
four ZIP, one TAR, four TAR.GZ, one TAR.BZ2, four raw GZIP, and one raw BZIP2 case. All 90 outputs
passed pinned 7-Zip validation as well as Rain reopen/list/integrity/extract/hash checks. The 75
measured samples left zero recovery records, staging artifacts, or Rain processes; the exact owned
work root was removed. Retained JSON:

- [managed-format-mixed-probe.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/performance-v1/managed-format-mixed-probe.json),
  66,695 bytes, SHA-256
  `31B8A5740FA54069AE476682D4C03E951BF26C5554A1E061F2EF9B2163225C73`.

The repeatable command, fixture shape, per-case measurements, and limitations are recorded in
`docs/benchmarks.md`. These are warm, uncontrolled-cache local baselines, not release thresholds or
competitor claims.

## Additional exact-current external-tool conformance

An explicitly addressed installed GNU suite, not ambient `PATH`, independently validated the package-
produced managed stream formats. The runner constrained child lookup to the exact tool directory plus
System32 and verified each executable hash before and after use:

- GNU tar 1.35: 511,486 bytes,
  `DF0582941D1F451A76337863BA1DC8D8986AD0FAF44F0E5F5C34AFEB43C36453`.
- GNU gzip 1.14: 126,581 bytes,
  `66B484C89583BD78F94FFCAE69D57D0BE05C9D682FBE265E55E5E95E9C0B89D5`.
- GNU bzip2 1.0.8: 92,176 bytes,
  `AC027E648F7D4BB8172D13A1BC27AC71784D193109AA48E76EFF703AEB0F520D`.
- WinRAR 7.20.0: 3,420,880 bytes,
  `B3A8469D2B132391D206D0799B34C563C0BDF6E7166A378E249F45848F5F1CB8`.
- MSYS2 `bsdtar` 3.8.9 / libarchive 3.8.9: 799,630 bytes,
  `F887AFE1B0102DAF146370B78B79EE3D070CBB38CCDD5333A155442E4F4C3AF9`.

GNU tar listed and extracted TAR, TAR.GZ, and TAR.BZ2; GNU gzip and bzip2 integrity-tested and
stream-decompressed raw GZIP/BZIP2. All five extracted payloads matched SHA-256
`612181229B5C89078B75B23D5FAF118B6DC957D5E3867AE23B3FF0267F1CFE66`; all tool exits were zero;
Rain had already reopened, integrity-tested, extracted, and hash-matched the six-format corpus. Every
archive remained byte-identical and recovery, staging, and Rain-process counts were zero.
WinRAR independently integrity-tested and extracted all six ZIP/TAR/TAR.GZ/TAR.BZ2/GZIP/BZIP2
outputs to that same source hash. The final evidence records every exact launched external process ID
and proves none remained. These installed executables are evidence tools only; none is packaged or
adopted as provider authority.

The distinct libarchive 3.8.9 build listed/extracted the four simple ZIP/TAR/TAR.GZ/TAR.BZ2 outputs
plus three exact-package PAX TAR-family layouts. Each PAX layout contains a long Unicode path, a
zero-byte file, an empty directory, and source timestamp
`2026-08-26T12:34:56.0001234+00:00`; Rain reopened/tested/extracted the exact hashes and libarchive
listed the five-entry manifest and extracted both payload hashes. The archives remained immutable.
SharpCompress's public Rain entry descriptor represented that fractional timestamp as
`2026-08-26T12:34:56.0001235+00:00`, a one-tick (100 ns) reader conversion difference. This does not
weaken creation validation: the BCL TAR staged validator compares the exact source timestamp before
publication. It is retained as a truthful managed-reader metadata limitation.

A second run fixed every file and directory source timestamp and repeated the three layouts through a
fresh Worker. Entry order, public metadata, extracted hashes, and the one-tick reader delta repeated.
The uncompressed TAR bytes differed at 45 positions confined to the five BCL-generated
`./PaxHeaders.<process-id>/` helper header names and their checksums; no material entry or PAX payload
field differed. The first fixed-timestamp TAR was
`B69B1666F9D4819D07122CCB3180864DE3EC5B62AB33DA62D143732173EAB6F0`; its complete 20,186-byte
`external-tools-v6` JSON has SHA-256
`8423525ADF415C31E359955936D1CCC79DEBCD18AFEE95E69B5D1A83B8EAAA92`. The final
TAR/TAR.GZ/TAR.BZ2 archives are respectively 9,216/405/456 bytes at
`DBBF2B34C6C721F75F04DF3AD185C1C88D04A4F148B4CE4A1F41CBCB0BF56261`,
`F064FC343C10FAD6568EFB5B156A9621E626D52F4E8C6503035FFBF01741F02D`, and
`E9D45E2F39D60AC58E0DBE5851B3CB31BC1BB3F7202AE0FF4616DE7AE97299C9`. Rain therefore promises a
deterministic planned namespace/order and exact content/qualified metadata, not reproducible PAX
container bytes across Worker PIDs. The raw helper header discloses an ephemeral local process ID;
standard readers consume it as metadata rather than exposing it as a material entry.

Evidence:
[gnu-tool-conformance.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/external-tools-v7/gnu-tool-conformance.json),
20,175 bytes, SHA-256
`8CAB9E6FA6AC7C169A14F0DE93CD3D0E4D9C7871A51AA6749EF0BC6AFEE1D64D`.
The identical executable hashes also existed under the local MSYS2 installation, so that duplicate
location is not counted as a second implementation/version. `gnu-tools-v1` and `gnu-tools-v2` retain
only diagnostic failures from absolute MSYS `-C` translation and an initially over-restricted child
`PATH`; `gnu-tools-v3` and `external-tools-v1` are superseded successful checkpoints before WinRAR and
exact launched-process retention were combined. `external-tools-v2` is the superseded simple-corpus
checkpoint; `external-tools-v3` and `external-tools-v4` retain the one-tick timestamp diagnostic that
led to the explicit reader boundary above. `external-tools-v5` is the first accepted expanded corpus;
`external-tools-v6` and `external-tools-v7` are the fixed-source-timestamp repeat pair used for the
BCL PAX helper-header comparison. Only `external-tools-v7` is the final consolidated claim.
Together with pinned official 7-Zip and Windows libarchive 3.8.4, the GNU, WinRAR, and distinct
libarchive 3.8.9 simple/PAX corpora close the finite Milestone 3 external-reader gate for the declared
creation shapes. An open-ended multi-version/method/layout matrix remains Milestone 8 release
hardening rather than a claim that every encoder is covered.

## Remaining Milestone 3 gates

The following remain `manual_pending`; they are not implementation claims and are not transferred
from Milestone 1 or loopback UNC evidence:

The final read-only availability inventory is
[remaining-gates-environment.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/environment-v1/remaining-gates-environment.json),
2,141 bytes, SHA-256
`F6F9DF62B678AD17C19A7E01C95D4DBD68DA4F1F1A45072E23A36AFFAF328DB1`.

- Product-owner exact-package ZIP creation/reopen/integrity and Rebuild Copy observation is complete.
  A valid staging inventory, rebuilt-output manifest/hash, and direct current Recovery Center state
  were not submitted. Sandbox, ACL, true disk-full, exFAT, and local alias reruns are complete.
- Product-owner Narrator, keyboard-only, High Contrast, and mixed-DPI observation of the current
  creation/rebuild/recovery flows.
- Physical blocked-UNC cancellation and actual second-machine SMB creation with source, destination,
  interruption after real progress, provenance, partial-write/recovery, and post-reconnect evidence.
  No SMB mapping was present and the previously used remote host's TCP 445 endpoint was unreachable.
- DFS and a filesystem/server without usable child file IDs. Local 8.3, mapped-drive, and alternate-
  loopback-UNC spellings are complete above; they do not substitute for remote alias behavior.
  The inventory found zero network drives/mappings/connections and no owned DFS surface. A real FAT32
  removable volume was present with warning health; it was explicitly left untouched as user data and
  cannot be relabelled as an owned no-file-ID fixture.
- A final source/test audit found no current single-output staging/commit/recovery transition lacking
  an exact-PID or deterministic checkpoint proof: writing, post-finalization validation, commit intent,
  post-commit reconciliation, recovery-disposition intent/commit, and partial nested discard are
  covered. Any future new transition without that evidence reopens this gate; no additional safe local
  physical kill point is currently identified.
- Installation, signing, and publication. These require separate task-specific authority and an
  actual installer/signing/release destination; none was performed.

ZIP encryption, TAR.XZ/raw XZ, native 7z creation, RAR creation, split output, and existing-
destination replacement/overwrite/in-place update remain the separately documented closed capability
decisions. They are not manual acceptance items and must not be enabled to make this matrix appear
complete.
