# Third-party notices

This file describes dependencies selected for Rain Archives. Release packaging must include the complete applicable license texts and source/offer material, not only this summary.

## SharpCompress 0.50.4

- Copyright (c) 2014 Adam Hathcock.
- License: MIT.
- Source: <https://github.com/adamhathcock/sharpcompress/tree/0.50.4>
- The complete upstream license is included at `licenses/SharpCompress-LICENSE.txt`.

## SharpSevenZip 2.0.109

- License declared by the package: LGPL-3.0-or-later.
- Exact source: <https://github.com/JeremyAnsel/SharpSevenZip/tree/105964f1c060481a17d20d084aacaecace92ffab>
- **Shipped since protocol 1.25 (2026-11-19) for 7z / XZ / TAR.XZ creation only**, by `RainArchives.Providers.SevenZip`, inside the private Worker folder as the separate, replaceable `SharpSevenZip.dll`. The package's own build step is excluded so it cannot place 7z.dll elsewhere.
- The signed `.nupkg`, exact source commit, packaged LGPLv3 text, complete asset list, and official-7-Zip payload identity were audited on 2026-08-22. The package README's imprecise GPL wording is retained as a notice discrepancy; the `.nuspec`, packaged license, and exact source license consistently declare LGPL-3.0-or-later. Unmodified 2.0.109 is still not approved as Rain's authoritative *reader* (it drops native archive warning/error flags and accepted the checked truncated ZIP during integrity testing), so the native read gate stays closed; every written archive is instead checked by 7-Zip's own test plus an independent managed manifest/SHA-256 read-back.
- Obligations: keep it a replaceable separate assembly, permit license-required debugging/reverse engineering, ship the LGPLv3 text (`licenses/SharpSevenZip-LICENSE.txt`) and the GPLv3 text it incorporates (`licenses/GPL-3.0.txt`, from gnu.org), both copied to `worker\licenses\`, and point to the exact source above.

## User-installed WinRAR encoder

- Rain Archives does not distribute WinRAR, `Rar.exe`, a WinRAR installer, package component, licence key, or trial copy.
- Conditional RAR5 creation invokes only the user's separately installed, separately licensed, Authenticode-trusted x64 WinRAR 7.23-through-7.x encoder at `C:\Program Files\WinRAR\Rar.exe`.
- Official licence: <https://www.rarlab.com/license.htm>
- Official downloads and release notes: <https://www.rarlab.com/download.htm> and <https://www.rarlab.com/rarnew.htm>
- Decoder availability never grants compression authority, and Rain does not use unRAR code to recreate a compressor.

## 7-Zip 26.02

- Copyright (c) 1999-2026 Igor Pavlov in the audited 26.02 distribution.
- `7z.dll` uses LGPL-2.1-or-later for most code, BSD-2-Clause/BSD-3-Clause for identified components, and the unRAR restriction for some decoder code.
- Official license: <https://www.7-zip.org/license.txt>
- Official source/downloads: <https://www.7-zip.org/download.html>
- Rain Archives must not use the RAR decoder to recreate a RAR compressor.
- The exact official x64 binary/source hashes and package-identity comparison are recorded in `docs/dependency-evidence.md`.
- **Shipped since protocol 1.25 (2026-11-19):** the unmodified official x64 `7z.dll` 26.02 (SHA-256 `69FD4DF057985C40E510E2FAC182881C7F85E90AA13EC703F763A8FDB2CE61F8`, 1,906,688 bytes), taken from the pinned SharpSevenZip 2.0.109 package and placed only at the Worker's `native\win-x64\7z.dll`. `SevenZipNativeLibrary` loads it from there only, and only when length and SHA-256 match; it never probes the current directory, `PATH`, registry installations or user-chosen DLLs. Used only to *write* 7z, XZ and TAR.XZ; the native read gate (`SevenZipProviderGate`) stays closed. Not stored in this repository; it is restored from NuGet at build time.
- Shipped in `worker\licenses\`: `7-Zip-NOTICE.txt` (what/where/hash), `7-Zip-License.txt` and `LGPL-2.1.txt` (gnu.org). `7-Zip-License.txt` was fetched from <https://www.7-zip.org/license.txt> on 2026-11-19 (SHA-256 `3A184AA13DC8AD30734E28FF901B478CCBCB5B41BE52427A5E7609C8FD9E5DDB`); it reads "Copyright (C) 1999-2025", while the copy inside the 26.02 distribution (SHA-256 `39D2187B…`, see dependency-evidence) says 1999-2026. Same license terms; swap in the distribution copy if a release audit wants it byte-exact.

## .NET 10

Self-contained Windows distributions are subject to Microsoft's .NET product-distribution licensing and third-party notices. Packaging must preserve the notices from the exact runtime pack used for publication.

## Development-only packages

MSTest, Microsoft.NET.Test.Sdk, and BenchmarkDotNet are development dependencies. They remain part of the dependency/SBOM inventory even though they are not intended to ship in the product runtime.
