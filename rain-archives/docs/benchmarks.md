# Benchmark methodology

No performance claim is valid until the exact build, fixture, machine state, and competitor settings are recorded. Benchmarks run from local fixed storage with real-time scanning state disclosed; warm and cold-cache results are separate and never averaged together.

## RAR preparation repair — 2026-09-05

The current protocol-1.24 [RAR performance checkpoint](rar-performance.md) records five operations per
build after separate-process warmups on one fixed 1,000-file/8,192,000-byte manifest: previous median
83.616 s (67.451–95.191 s), repaired median 11.603 s (11.218–12.305 s), **7.21x** by median. All ten
RAR5/Balanced/non-solid outputs have the same 385,245-byte length and SHA-256. Full source/package/provider
identities, raw reports, environment, cleanup follow-up and sampling limitations are linked there.
Cache was uncontrolled and these are ordinary Windows local observations, not cold-cache, competitor,
release-threshold or the user's private-folder measurements. A separate one-run 20,000-file/156.25 MiB
capacity check completed in 249.459 s and passed all correctness/cleanup checks; it is not a five-run
performance claim. Historical measurements below have not been transferred to this build.

## Required environment record

- Rain Archives commit/source fingerprint, configuration, runtime mode, package/native engine versions, and publish RID.
- Windows build, CPU, logical cores, RAM, storage model/filesystem/free space, power plan, and security scanner state.
- Archive SHA-256, format, codec, level, solid/block settings, encryption, entry count, original/compressed bytes, and producing tool/version.
- Five measured runs after at least one warm-up unless the metric is explicitly cold startup. Publish median, p95 where enough samples exist, range, and failures; retain raw CSV/JSON.

## Rain Archives measurements

- Cold process startup: client launch to successful protocol handshake. Use a fresh worker for every sample.
- Time to first listing: user request to first page received, separated into worker startup, archive open/index, and first-page transfer.
- Full listing: 1k, 100k, and limit-scale entry fixtures; record wall time, peak working set, allocated bytes where meaningful, and UI virtualization separately.
- Single-entry extraction: first/middle/last entry, with solid archives called out because random access is not comparable to non-solid ZIP.
- Full extraction: wall time, throughput from actual decoded bytes, CPU time, peak worker/client memory, output hash verification, and commit time.
- ZIP creation and approved 7z creation: identical source tree, explicit codec/level/thread/solid settings, output bytes, ratio, hashes after round-trip, CPU, memory, and commit time.
- Cancellation latency: issue cancellation during queued, open/index, first file, mid-file, between files, checksum finalization, and pre-commit stages; measure request-to-terminal and request-to-worker-exit fallback.
- Failure cost: malformed input, traversal refusal, ratio refusal, low-space refusal, worker crash, and cleanup duration/leftovers.

### Protocol-1.7/1.8 ZIP-creation procedure

Use one immutable source manifest containing separately reported empty-directory, tiny-file-heavy, mixed-size, compressible, and seeded-incompressible datasets. Record each relative path, object kind, byte length, timestamp, and regular-file SHA-256. Run `Store`, `Fast`, `Balanced`, and `Maximum` against the same manifest and a fresh nonexistent destination on the same recorded volume. Measure protocol 1.7 explicit-file input and protocol 1.8 directory-tree input separately; where both can represent the same material tree, report their planning overhead and resulting entry-order/layout differences rather than averaging them. Cold-cache and warm-cache series are separate; every measured output is durably completed by Rain, hashed, reopened/listed/integrity-tested through Rain, and independently tested by at least one external standards-conforming tool before its run counts as correctness evidence.

Measure request-to-authoritative-terminal wall time, directory-planning time, source entries/bytes, final ZIP entries/bytes, compression ratio, exact worker CPU, peak worker/client working set, commit duration where instrumented, and source/destination throughput. Cancellation cases must begin only after observed `Running` and should cover directory enumeration, early source copy, mid-large-file copy, between files, ZIP finalization, final tree-manifest revalidation, and pre-commit; report request-to-terminal latency and all final/staging leftovers. Also record destination filesystem/free space, Defender/scanner state, cache state, preset mapping, worker/package identity, whether the source and destination share a physical device, and whether the filesystem supplies stable enumeration file IDs.

The protocol-1.18 `1C85F6D9` source and package have the bounded local regression baselines below. Every
exact output is externally qualified, but the runs are not competitor comparisons, cold-cache
studies, separate-device results, or release thresholds. No comparison with 7-Zip, WinRAR, or another
product is valid until the full procedure above is run with comparable codec, level, thread,
encryption, and verification settings.

Protocol 1.22 source fingerprint
`41BB378A224046A505D54F955243BCD8570519FDF798018E09541425941D3C56` over 142 canonical inputs
and package `artifacts/RainArchives.M5BAutomatedPackage.1.22.41BB378A` passed functional package,
archive-browser, preview, Activity Center, creation/integrity/extraction/rebuild, cancellation, hash,
and residue verification on 2026-08-30. A focused model measurement indexed 50,000 synthetic
descriptors in 64.7 ms on the development host while materializing only the current 512-row page;
this is a bounded regression observation, not an end-user benchmark or release threshold. Browser
history is separately capped at 256 locations and the complete index refuses more than 500,000
entries. Iterative hierarchy regressions additionally accept 256 reverse-ordered levels, refuse 4,352
levels, and detect a 32,768-entry reverse-parent cycle; they are correctness cases, not performance
claims. The retained `93D944FC` pre-remediation M5B, `08A4C999` Milestone-5A, and `DCE2BC1E`
Milestone-4 packages preserve their own
Activity Center and lifecycle/recovery evidence. No archive-operation benchmark series was rerun for
these checkpoints. The measurements below remain bound
only to their named protocol-1.18 or earlier fingerprints and must not be relabelled as protocol-1.22
performance or release-threshold evidence.

## Current protocol-1.18 creation baselines — 2026-08-26

Both retained results bind canonical production fingerprint
`1C85F6D929DE5F940E1357C72C210A8647DFD2E9F38A0A2B0B0832127F5CE844` over 132 inputs. They ran on
Windows `10.0.26200.0`, .NET 10, an Intel Core i7-14700F host with 28 logical processors, local fixed
NTFS, and an uncontrolled warm cache. Worker startup is outside measured operation time. The external
validator is official 7-Zip Extra 26.02 `7za.exe`, 1,334,784 bytes, SHA-256
`35D4D69D7CD6CB44558F208C3B1334268013F9DAF82D2DDA848893A1C30C59C2`.

### Current-source ZIP operation probe

The rebuilt `CreationOperationProbe` ran one warm-up and five measured samples for each explicit-file
and tree preset. The explicit corpus contains 16 deterministic 512-KiB files (8 MiB); the tree corpus
contains five files, Unicode paths, and an empty directory (2.5 MiB). All 40 measured outputs and ten
warm-up/cancellation outputs passed Rain reopen/list/integrity/payload-SHA checks and pinned 7-Zip;
each case retained one stable output hash. Source cleanup took one attempt, with zero remaining owned
or staging artifacts.

Retained evidence:
[creation-operation-probe.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/performance-v1/creation-operation-probe.json),
201,308 bytes, SHA-256
`54F65B108FBA5F1336D5F8113D618C7C31CF6DA31F2D1E711BD6E6EF2A63CDA6`. The exact benchmark assembly
SHA-256 is `58B05E5565D241C15FF716FC372C8504944988CF71BC052DCEB4899AB67E7BCE`; its Worker SHA-256 is
`C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF`.

| Route | Preset | Median | p95 | Median throughput | Mean worker CPU | Mean peak worker memory | Output bytes |
|---|---|---:|---:|---:|---:|---:|---:|
| Explicit files | Store | 104.648 ms | 120.952 ms | 76.45 MiB/s | 90.625 ms | 70.41 MiB | 8,390,742 |
| Explicit files | Fast | 163.693 ms | 173.562 ms | 48.87 MiB/s | 181.250 ms | 74.92 MiB | 8,849,637 |
| Explicit files | Balanced | 251.490 ms | 263.518 ms | 31.81 MiB/s | 281.250 ms | 76.73 MiB | 8,393,348 |
| Explicit files | Maximum | 223.649 ms | 229.514 ms | 35.77 MiB/s | 206.250 ms | 73.31 MiB | 8,393,302 |
| Directory tree | Store | 56.294 ms | 58.514 ms | 44.41 MiB/s | 43.750 ms | 64.47 MiB | 2,622,748 |
| Directory tree | Fast | 74.942 ms | 81.772 ms | 33.36 MiB/s | 46.875 ms | 66.66 MiB | 2,766,175 |
| Directory tree | Balanced | 104.381 ms | 105.735 ms | 23.95 MiB/s | 81.250 ms | 66.33 MiB | 2,623,561 |
| Directory tree | Maximum | 97.168 ms | 111.833 ms | 25.73 MiB/s | 71.875 ms | 65.76 MiB | 2,623,548 |

Cancellation followed observed partial progress: 524,288/8,388,608 bytes for explicit files and
524,288/2,621,440 bytes for the tree. Authoritative `Cancelled` arrived in 14.8045 ms and 16.2112 ms;
neither operation published a destination or left staging.

### Exact-package managed mixed-corpus probe

[`eng/Invoke-Milestone3ManagedPerformance.ps1`](../eng/Invoke-Milestone3ManagedPerformance.ps1)
verifies the exact package manifest, principal assemblies, private Worker, source fingerprint, and
hash-pinned 7-Zip before running. Its owned work-root leaf must contain a caller-supplied GUID and be
absent. The script generates a deterministic 67-file/6-directory, 1,060,730-byte tree containing
compressible, seeded-incompressible, tiny, zero-byte, deep, long-name, and Unicode material, plus a
separate 1-MiB mixed raw source. Every output is reopened, listed, integrity-tested, extracted, and
hash-checked through Rain, then independently tested by 7-Zip. Cleanup refuses reparses and removes
only the exact owned work root after verifying zero recovery, staging, and process residue.

```powershell
pwsh -NoLogo -NoProfile -File eng\Invoke-Milestone3ManagedPerformance.ps1 `
  -PackageRoot artifacts\RainArchives.M3AutomatedPackage.1.18.1C85F6D9 `
  -ExternalSevenZip C:\path\to\hash-pinned\7za.exe `
  -WorkRoot C:\path\to\an-existing-parent\RainArchives.M3MixedPerformance.0123456789abcdef0123456789abcdef `
  -EvidencePath artifacts\RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9\performance-v1\managed-format-mixed-probe.json `
  -Runs 5
```

The accepted run completed 15 warm-ups, 75 measured operations, and 90 external validations. All
terminal, Rain integrity, Rain extraction, and external results succeeded. Creation and nested
recovery counts, remaining staging artifacts, and remaining Rain processes were zero; the work root
was removed. Retained evidence:
[managed-format-mixed-probe.json](../artifacts/RainArchives.M3PhysicalAcceptance.1.18.1C85F6D9/performance-v1/managed-format-mixed-probe.json),
66,695 bytes, SHA-256
`31B8A5740FA54069AE476682D4C03E951BF26C5554A1E061F2EF9B2163225C73`. It binds package manifest
SHA-256 `6A8CB71E3924E04451299E9C4C4AB2F022D9648FCFE8113186656C6F0D0FF47A` and package Client,
Contracts, and Worker hashes recorded in the JSON.

| Format | Preset | Route | Median | p95 | Median throughput | Mean worker CPU | Mean peak worker memory | First output bytes |
|---|---|---|---:|---:|---:|---:|---:|---:|
| ZIP | Store | Tree | 365.546 ms | 370.513 ms | 2.77 MiB/s | 631.250 ms | 63.26 MiB | 1,070,516 |
| ZIP | Fast | Tree | 371.043 ms | 374.972 ms | 2.73 MiB/s | 625.000 ms | 67.74 MiB | 577,475 |
| ZIP | Balanced | Tree | 378.754 ms | 382.476 ms | 2.67 MiB/s | 659.375 ms | 67.21 MiB | 545,961 |
| ZIP | Maximum | Tree | 377.070 ms | 379.042 ms | 2.68 MiB/s | 671.875 ms | 67.49 MiB | 544,151 |
| TAR | Store | Tree | 386.838 ms | 389.728 ms | 2.62 MiB/s | 640.625 ms | 55.86 MiB | 1,198,080 |
| TAR.GZ | Store | Tree | 398.896 ms | 407.997 ms | 2.54 MiB/s | 687.500 ms | 68.97 MiB | 1,198,283 |
| TAR.GZ | Fast | Tree | 405.989 ms | 411.631 ms | 2.49 MiB/s | 718.750 ms | 69.27 MiB | 564,284 |
| TAR.GZ | Balanced | Tree | 406.352 ms | 415.525 ms | 2.49 MiB/s | 656.250 ms | 68.98 MiB | 532,676 |
| TAR.GZ | Maximum | Tree | 412.503 ms | 423.172 ms | 2.45 MiB/s | 715.625 ms | 69.77 MiB | 530,186 |
| TAR.BZ2 | Balanced | Tree | 1,947.287 ms | 2,041.958 ms | 0.52 MiB/s | 3,737.500 ms | 121.20 MiB | 535,030 |
| GZIP | Store | Raw | 165.956 ms | 167.996 ms | 6.03 MiB/s | 321.875 ms | 50.24 MiB | 1,048,759 |
| GZIP | Fast | Raw | 170.126 ms | 171.262 ms | 5.88 MiB/s | 375.000 ms | 51.72 MiB | 559,178 |
| GZIP | Balanced | Raw | 172.847 ms | 175.732 ms | 5.79 MiB/s | 356.250 ms | 51.33 MiB | 528,779 |
| GZIP | Maximum | Raw | 173.838 ms | 174.913 ms | 5.75 MiB/s | 365.625 ms | 51.44 MiB | 526,994 |
| BZIP2 | Balanced | Raw | 1,468.080 ms | 1,664.139 ms | 0.68 MiB/s | 2,262.500 ms | 102.66 MiB | 530,488 |

ZIP and raw-stream output hashes were stable across each five-sample case. TAR-family archive-byte
hashes varied across fresh Worker processes, while every decoded entry manifest and payload hash
remained exact and every output passed both validators. This result records that variance rather than
claiming byte-reproducible TAR output.

## Current-source ZIP-creation operation baseline — 2026-08-23

`CreationOperationProbe` is a deterministic real-worker mode in the benchmark executable. The output leaf must be a new GUID beneath an existing parent. It creates its source fixture, an owned official-7-Zip validator copy, and every archive only inside that leaf. External 7-Zip validation is mandatory. The probe refuses reparses during cleanup, deletes each validated archive, the exact fixture it owns, and the owned validator, then retains one JSON result. A failed run retains a structured failure JSON and its exact GUID root for diagnosis.

```powershell
dotnet run --project benchmarks\RainArchives.Benchmarks\RainArchives.Benchmarks.csproj `
  -c Release --no-build --no-restore -- `
  --creation-operation-probe `
  --output C:\path\to\an-existing-parent\0123456789abcdef0123456789abcdef `
  --source-fingerprint CURRENT_CANONICAL_SOURCE_FINGERPRINT `
  --runs 5 `
  --external-seven-zip C:\path\to\official\7za.exe
```

The accepted run used canonical source fingerprint `96E0B4E5F0DFA19116D8B0BC2052F6416BB67C150E60848062FC28304830BC6C` over 100 production/benchmark inputs. Its retained JSON is `C:\Users\aliss\AppData\Local\Temp\RainArchives.CreationWpfFinal.2d0383eb3c124677ba0fc6608eb29fe5\52cbf0613cf24c7e9c5b5983a8526503\creation-operation-probe.json`, 207,240 bytes with SHA-256 `020017939F53EF46C2BD21F14FFAE4F8FA6405F79F86D8F15F9A740F78354239`. The exact worker SHA-256 is `C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF`; the benchmark assembly SHA-256 is `1862D9CA32655A13674B4A772246DCA665C4AA449E3CE5F641EF0C1183581F5B`. The later protocol-1.11 Recovery Center snapshot has fingerprint `11EA9BA7CA6A09D599B21ADB61BAF8EBCA376EC7793C2936496C738467F988AC` over 115 inputs. Current protocol-1.12 source has fingerprint `D0DFC81FDD6829A8457B9DBF826CF7DE5AD6CC1EA21197C7650015EA649AD95A` over 121 inputs. The operation probe has not been rerun against either later fingerprint, so the measurements below remain attached only to the earlier accepted fingerprint.

- Environment: `Release`, x64, .NET SDK 10.0.301/runtime 10.0.9, Windows build `10.0.26200.9168` / 25H2, Intel Core i7-14700F, 28 logical processors, 34,184,499,200 bytes RAM, and High performance power plan. Source and destination were on the same fixed NTFS `C:` volume on a CT1000P2SSD8 NVMe; 370,567,802,880 of 998,492,147,712 bytes were free immediately after the run. Microsoft Defender reported antivirus and real-time protection disabled. Cache state was not controlled; these are warm local measurements.
- Fixture: sixteen deterministic seeded-incompressible 512 KiB explicit files, 8 MiB total, producing sixteen files plus one material parent directory. The directory-tree route contains five such files, six material directories including an empty directory, and Unicode paths, 2.5 MiB total. All timestamps are fixed at `2026-01-01T00:00:00Z`; the 27-source-entry fixture manifest SHA-256 is `7FC5E4520E39DD18A0EB1550C6F3994A19EA5AA87EE7D0D77533B386A842A62D`.
- Correctness: every measured output was durably completed, hashed, reopened, listed, integrity-tested, and streamed entry-by-entry through Rain. Rain required exact path/kind, declared length, payload length, and per-file SHA-256. The verified content aggregate is `4D63316C0443B1D23AF60A150DCA3BF93E4067E9BC6397941A32DBF1143DFA13` for the 17-entry/16-file/8-MiB explicit output and `204E9F1C28BC05519180473C0FBC99C61D960B904748B781378E57AE25ACB5AB` for the 11-entry/5-file/2.5-MiB tree output.
- External qualification: official 7-Zip Extra 26.02 `x64/7za.exe`, SHA-256 `35D4D69D7CD6CB44558F208C3B1334268013F9DAF82D2DDA848893A1C30C59C2`, was copied through a stable read lease into the GUID root. The owned copy's hash and file identity were checked before and after every launch and once after the series, then the copy was deleted. All 50 exact outputs—40 measured, eight preset warm-ups, and two cancellation warm-ups—passed `7za t -bd -y -- <archive>` with exit code 0, no timeout, forced termination, output truncation, or output-cleanup failure. Every archive's length and SHA-256 also matched before and after 7-Zip.
- Job: one warm-up followed by five measured requests for each route/preset pair, 40 measured creations total. Timing starts immediately before the creation request and stops at the authoritative terminal event; Rain byte verification and 7-Zip qualification remain outside that creation timing. Each route/preset produced one stable output hash across all five samples. No measured operation or validator attempt failed.
- The table uses nearest-rank p95; with five samples that is the maximum. Throughput uses regular-file source bytes. Ratio is source bytes divided by final ZIP bytes. CPU is mean exact-worker processor time and memory is mean exact-worker peak working set; the Windows CPU timer's coarse resolution makes the smallest CPU samples unsuitable for fine comparisons.

| Route | Preset | Median | p95 | Range | Median throughput | Mean worker CPU | Mean peak worker memory | Output bytes | Ratio |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Explicit files | Store | 64.598 ms | 70.360 ms | 63.552–70.360 ms | 123.84 MiB/s | 68.750 ms | 65.76 MiB | 8,390,742 | 0.9997 |
| Explicit files | Fast | 141.792 ms | 144.542 ms | 135.187–144.542 ms | 56.42 MiB/s | 156.250 ms | 70.52 MiB | 8,849,637 | 0.9479 |
| Explicit files | Balanced | 231.392 ms | 249.002 ms | 229.250–249.002 ms | 34.57 MiB/s | 271.875 ms | 69.96 MiB | 8,393,348 | 0.9994 |
| Explicit files | Maximum | 211.817 ms | 225.553 ms | 202.384–225.553 ms | 37.77 MiB/s | 231.250 ms | 69.65 MiB | 8,393,302 | 0.9994 |
| Directory tree | Store | 31.345 ms | 31.600 ms | 29.165–31.600 ms | 79.76 MiB/s | 31.250 ms | 56.15 MiB | 2,622,748 | 0.9995 |
| Directory tree | Fast | 50.220 ms | 51.853 ms | 47.967–51.853 ms | 49.78 MiB/s | 37.500 ms | 62.13 MiB | 2,766,175 | 0.9477 |
| Directory tree | Balanced | 80.290 ms | 81.592 ms | 77.425–81.592 ms | 31.14 MiB/s | 78.125 ms | 62.32 MiB | 2,623,561 | 0.9992 |
| Directory tree | Maximum | 74.691 ms | 91.374 ms | 70.980–91.374 ms | 33.47 MiB/s | 81.250 ms | 57.95 MiB | 2,623,548 | 0.9992 |

Stable output hashes are explicit Store `A97BABF345D99D394BFD510B145F214BD1C585D7DA4D14201ECE4FB5A334ECBB`, Fast `993FC0BE3316B05E0D6130CB1CD7F1D8BE979406138C1C861DBA4AD7D64B9B91`, Balanced `14D7BCF80EB9C7C4803A1E4A46B048B47E38D977B6B6F1ABDA64E7FD9D03190D`, Maximum `A66BDF8380A0B4574830051F1409A46328BB80341D918825AF8094F9D2AF8978`; tree Store `1413C58BC37AB34F6F5D35798B132EBC90D048B6F4A46894392508C10A7F5D86`, Fast `69065C7945322BADABBE628E1403BF13CA39A3425C5C6E9913DAD91D7CA28308`, Balanced `C9607935C6B96952C53D017575BCFB2C4C6389769FAAFD70C35AF8824EDC7E2F`, and Maximum `CFAFF868324A7837918678862DAF511255E259A04338292781BF3BEF8FCB4FC8`.

Cancellation was requested after an observed 524,288 of 8,388,608 bytes on the explicit route and 524,288 of 2,621,440 bytes on the tree route. Authoritative `Cancelled` arrived in 16.216 ms and 14.781 ms respectively. Neither route published a destination or left a staging file/directory. Final cleanup removed the source fixture in one attempt (7.362 ms), removed the owned validator, and retained only the JSON evidence; no Rain or 7-Zip process remained.

This baseline now satisfies exact-output byte verification and official-7-Zip qualification for its deterministic seeded-incompressible local corpus. It is still not a competitor comparison, cold-cache study, broad mixed/compressible/tiny-file corpus, separate-device test, actual-SMB result, or full creation release gate. The predecessor C400 package separately passed physical NTFS disk-full, ACL-denial, and exFAT provenance gates; those runs are correctness evidence rather than benchmark data and are not transferred to `1C85F6D9`. SMB provenance, broader commit-phase instrumentation, and competitor-equivalent settings remain open.

## Foundation baseline — 2026-08-22

This is a local regression baseline, not a release target or competitor comparison. Source identity is `17906FD984DCCE8E16C75EB276D0F63DC462F9D285311BE452733C81537CAF75`, computed over 79 production/benchmark inputs as SHA-256 of each sorted relative UTF-8 path, a NUL byte, and that file's SHA-256. The folder has no Git commit.

- Build/runtime: framework-dependent `Release`, x64, .NET SDK 10.0.301, .NET runtime 10.0.9, BenchmarkDotNet 0.15.8, SharpCompress 0.50.4.
- Machine: Windows 11 build 10.0.26200.9168, Intel Core i7-14700F, 20 physical/28 logical cores, 34,184,499,200 bytes RAM. BenchmarkDotNet selected the High performance power plan for each run and reverted it afterward.
- Fixture storage: local fixed NTFS on CT1000P2SSD8 NVMe; 397,527,011,328 bytes free of 998,492,147,712 when recorded. Microsoft Defender antivirus and real-time protection both reported disabled; no claim is made about third-party scanners.
- Fixture: deterministic .NET 10.0.9 `ZipArchive`, 10,000 one-byte entries, `NoCompression`, fixed `2026-01-01T00:00:00Z` timestamps, 1,250,022 archive bytes, SHA-256 `B81B55082ACE042190A20322D6315946D0447A1377B65B8057D2E9823C48F7C9`.
- Job: one launch, one warm-up, five measured single-invocation iterations. First-page reporting removed one 378.696 ms outlier according to BenchmarkDotNet's default policy.

| Operation | Mean | StdDev | Managed allocation | Raw measured samples (ms) |
|---|---:|---:|---:|---|
| Fresh worker startup and protocol handshake | 142.1 ms | 2.68 ms | 52.75 KiB | 137.573, 144.347, 141.878, 143.508, 143.208 |
| Startup, open/index, first 512-entry page | 368.2 ms | 1.93 ms | 1,261.25 KiB | 371.041, 378.696, 367.856, 366.746, 367.273 |
| Startup, open/index, all 10,000 entries | 539.2 ms | 15.80 ms | 23,765.63 KiB | 563.353, 545.106, 536.068, 526.830, 524.453 |

This original foundation baseline did not measure extraction, peak working set, CPU time, cold-cache runs, or cancellation latency. The later operation probes below now cover warm extraction, exact-worker CPU/working set, cancellation-after-progress, and one physical 3 GiB case; cold-cache and broader format/storage measurements remain open.

## Post-hardening and malformed-archive verification baseline — 2026-08-22

This historical rerun used the same machine, runtime, job, and deterministic listing fixture described above after the sequential-format, protocol, selection, filesystem-budget, display, secret-lifecycle, shutdown, operation-probe, split-ZIP classification, sparse-ZIP64, production-WPF startup, isolated App/worker publish, ZIP central-directory preflight, RAR malformed-metadata translation, physical-large-file probe, and UNC disk-space support work. Its canonical source identity is `08B223C61691318A69E8E9449873E2150D82A237A076A906758A34C8040CC857`, computed over 78 production/benchmark inputs as one SHA-256 over each ordinal-sorted slash-normalized relative UTF-8 path, one NUL byte, then that file's raw SHA-256 bytes. The prior pre-UNC 78-input baseline remains identifiable as `43DBBB21237AD8766795B42460CA5017E3C23DC4C737113A6CF6752BB96BB778`. This run predates protocol 1.3 and the final Per-Monitor V2 manifest, so it remains a regression baseline for that source snapshot rather than current-source performance evidence. Raw BenchmarkDotNet artifacts for this run are retained under `C:\Users\aliss\AppData\Local\Temp\RainArchives.BenchmarkProof.bdb0d84f74ab402497aaf2c995b200e6`.

| Operation | Mean | StdDev | Managed allocation | Raw measured samples (ms) |
|---|---:|---:|---:|---|
| Fresh worker startup and protocol handshake | 151.0 ms | 9.31 ms | 48.23 KiB | 147.024, 164.973, 146.903, 140.984, 155.281 |
| Startup, open/index, first 512-entry page | 412.0 ms | 10.85 ms | 1,064.95 KiB | 412.590, 422.591, 397.789, 404.787, 422.196 |
| Startup, open/index, all 10,000 entries | 470.5 ms | 5.42 ms | 23,441.63 KiB | 473.795, 507.798 (removed), 466.229, 476.333, 465.491 |

BenchmarkDotNet collected five raw samples for all three cases. It removed the 507.798 ms full-listing sample under its default outlier policy and reported that mean/standard deviation over the remaining four; the table still preserves all five observations. It retained all five startup and first-page samples. The change from any earlier baseline is useful only as a local regression comparison; it is not evidence that Rain Archives is faster than another product.

## Extraction, worker-resource, and cancellation probe — 2026-08-22

`FoundationOperationProbe` is a deterministic real-worker diagnostic mode in the benchmark executable. Run it after a `Release` build with:

```powershell
dotnet run --project benchmarks\RainArchives.Benchmarks\RainArchives.Benchmarks.csproj `
  -c Release --no-build --no-restore -- `
  --foundation-operation-probe --output C:\path\to\a-new-proof-directory
```

The output directory must not exist. The probe creates a `NoCompression` ZIP containing 2,048 64 KiB files (128 MiB decoded), all with the fixed timestamp and deterministic payload recorded in JSON. It performs one warm-up and five measured samples per case. Operation timing starts immediately before the extraction request and ends at the authoritative terminal event, so worker startup, archive open/index, output hashing, and cleanup are excluded while secure staging/finalization/commit remain included. A 5 ms sampler records exact-worker CPU delta and peak working set through the internal diagnostic PID; this does not add PID exposure to the public client contract.

Artifact root: `C:\Users\aliss\AppData\Local\Temp\RainArchives.FoundationOperationProbe.932b4774391e4e588b61be64d061a794`. Fixture: 134,443,030 archive bytes, 134,217,728 decoded bytes, SHA-256 `7EC96B18B9E31B2518A0612C28EB30D604415A6CFD55DA214C408D3624E3AEE8`. Every completed output was byte-hashed before its exact run directory was removed; all five outputs in each extraction case had one stable aggregate hash. Every cancellation followed observed decoded progress and left zero final/staging directories.

| Operation | Mean | Median | p95 nearest-rank | Range | Worker CPU mean | Worker peak working set mean |
|---|---:|---:|---:|---:|---:|---:|
| One selected 64 KiB entry | 92.14 ms | 92.19 ms | 96.41 ms | 87.33–96.41 ms | 100.00 ms | 48.96 MiB |
| Full 128 MiB / 2,048-file extraction | 3,962.79 ms | 3,982.18 ms | 4,158.58 ms | 3,818.47–4,158.58 ms | 4,650.00 ms | 65.44 MiB |
| Cancel request after first 64 KiB progress to terminal | 9.37 ms | 9.31 ms | 10.15 ms | 8.77–10.15 ms | 103.13 ms | 51.21 MiB |

Mean full-extraction throughput was 32.33 MiB/s (30.78–33.52 MiB/s). Single-entry throughput is deliberately not treated as a codec-speed claim because fixed operation, staging, validation, and commit overhead dominates a 64 KiB output. Worker CPU can exceed wall time because decompression and security work may use more than one core. These are warm local regression results; cold-cache, other storage/filesystems, compressed/encrypted/solid formats, and competitor comparisons remain separate gates.

## Physical 3 GiB integrity, extraction, and cancellation probe — 2026-08-22

`PhysicalLargeFileProbe` creates a deterministic stored ZIP inside a new output root, requires enough free space for two payload copies plus 2 GiB headroom, flushes the archive to disk, and rejects sparse, filesystem-compressed, or less-than-99%-allocated source files. It hashes the archive, lists and integrity-tests it through the real worker, extracts and hashes the complete payload, then starts a second extraction and requests cancellation after at least 64 MiB of decoded progress. On success it deletes the generated archive and output through direct-child/reparse checks and retains only JSON evidence; on failure it retains the exact root for diagnosis.

```powershell
dotnet run --project benchmarks\RainArchives.Benchmarks\RainArchives.Benchmarks.csproj `
  -c Release --no-build --no-restore -- `
  --physical-large-file-probe `
  --output C:\path\to\a-new-proof-directory `
  --source-fingerprint CURRENT_CANONICAL_SOURCE_FINGERPRINT `
  --bytes 3221225472
```

Replace `CURRENT_CANONICAL_SOURCE_FINGERPRINT` with the fingerprint produced for the exact source under test. The retained historical run below used `08B223C61691318A69E8E9449873E2150D82A237A076A906758A34C8040CC857`.

Artifact root: `C:\Users\aliss\AppData\Local\Temp\RainArchives.PhysicalLargeProof.b20fbc7e276a4064be5ed42a9e2322d7`. The NTFS archive was 3,221,225,622 bytes with exactly 3,221,225,622 allocated bytes, `Archive` attributes only, and SHA-256 `428F076F859DFCAA67AD8255510CC42B2A2AFD1F6142CADFCF348F58D1DE567C`. Its 3,221,225,472-byte payload had SHA-256 `2AE54A2A7B7E43929ACCA6B10DCB654BC2124D67AB4D3C13DF4FFD42F8A42F7E` before and after extraction.

- Fixture generation with .NET `ZipArchive` plus disk flush: 3.333 s. This predates and does not measure Rain's protocol-1.7 ZIP-creation operation.
- Full integrity test: 6.541 s, terminal `Completed`.
- Full extraction and secure commit: 33.528 s, 91.62 MiB/s, 7.875 s worker CPU, 63.62 MiB peak worker working set, matching output hash.
- Cancellation: requested after 73,531,392 decoded bytes, accepted, terminal `Cancelled` after 23.09 ms, 63.01 MiB peak worker working set, no final destination and no staging directory.
- Cleanup: generated archive deleted, completed output deleted after validation, cancellation output absent, zero remaining staging directories, and only the JSON result retained.

This satisfies the Milestone 1 physical multi-gigabyte proof for one uncompressed ZIP entry on local NTFS. It does not establish equivalent behavior for compressed, encrypted, solid, multipart, network, or alternate-filesystem cases.

## Fixture families

- Tiny startup archive; 100k tiny entries; deep-but-valid hierarchy; one 10 GiB generated/sparse payload where the codec/tool treats it honestly; mixed media/source corpus; incompressible pseudorandom corpus with a recorded seed; solid and non-solid 7z/RAR reads; encrypted and multipart variants.
- External generators must include at least two independent tools where the format permits it. Empty archives, zero-byte files, Unicode/collision fixtures, and malformed/truncated fixtures are reported as correctness tests even when timed.

## Competitor comparison

Use the same source tree or archive bytes, destination volume, cache state, thread count, codec, compression level, dictionary/block/solid settings, encryption, overwrite/conflict policy, and verification policy. GUI launch time and engine operation time are separate. If a product does not expose an equivalent setting, label the comparison non-equivalent instead of selecting a favorable default.

Do not claim “faster than 7-Zip/WinRAR” from one archive, one warm run, different compression levels, different integrity checks, or measurements taken before Rain Archives produces byte-correct output.
