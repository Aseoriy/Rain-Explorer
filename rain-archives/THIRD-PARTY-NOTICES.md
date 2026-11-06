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
- This remains a candidate adapter, not a current build dependency. The signed `.nupkg`, exact source commit, packaged LGPLv3 text, complete asset list, and official-7-Zip payload identity were audited on 2026-08-22. The package README's imprecise GPL wording is retained as a notice discrepancy; the `.nuspec`, packaged license, and exact source license consistently declare LGPL-3.0-or-later. Unmodified 2.0.109 is not approved as Rain's authoritative reader because it drops native archive warning/error flags and accepted the checked truncated ZIP during integrity testing.
- If adopted, the release must preserve the wrapper as a replaceable separate assembly, permit license-required debugging/reverse engineering, include the applicable LGPLv3 and GPLv3 texts and notices, and make the exact corresponding wrapper source available. No SharpSevenZip asset is currently shipped.

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
- The exact official x64 binary/source hashes and package-identity comparison are recorded in `docs/dependency-evidence.md`. No native 7-Zip binary is currently stored in this repository or shipped by a build.

## .NET 10

Self-contained Windows distributions are subject to Microsoft's .NET product-distribution licensing and third-party notices. Packaging must preserve the notices from the exact runtime pack used for publication.

## Development-only packages

MSTest, Microsoft.NET.Test.Sdk, and BenchmarkDotNet are development dependencies. They remain part of the dependency/SBOM inventory even though they are not intended to ship in the product runtime.
