# Milestone 1 clean-machine package proof

This procedure defines the reproducible clean-machine transfer-and-run check. It does not claim that the development host is clean, and it does not replace the separate manual Windows matrix.

## Build the immutable bundle

Run from the repository on the development machine. The output path must not already exist.

```powershell
powershell.exe -NoProfile -File .\eng\New-Milestone1AcceptanceBundle.ps1 `
  -OutputDirectory 'C:\RainArchives-M1-Acceptance'
```

The builder is verified under Windows PowerShell 5.1 and may also be run with `pwsh`. Choose a healthy volume with enough space; do not use a removable or unhealthy volume merely because it has a convenient drive letter. The builder performs locked restore, publishes separate self-contained `win-x64` CLI and worker directories, copies the checked ordinary ZIP, AES-256 ZIP, and solid 7z fixtures plus their expected source files, and creates `bundle-manifest.json` with every payload length and SHA-256. It fails if `7z.dll` or `SharpSevenZip.dll` enters the bundle. It never deletes or overwrites an existing output directory.

Transfer the whole directory to an eligible x64 Windows environment that has never had modern .NET or 7-Zip installed. A fresh Windows Sandbox instance is acceptable for this dependency/package proof when the bundle is mapped read-only, only a separate evidence directory is writable, and the runner detects no disqualifying tools. Preserve the directory exactly; the runner rejects added, missing, or changed payload files. Windows Sandbox configuration and mapped-folder behavior are defined by the official [Microsoft configuration documentation](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file), and scripted start/share/execute/stop behavior is defined by the official [Windows Sandbox CLI documentation](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli).

## Run on the clean machine

Windows PowerShell 5.1 is sufficient and is part of supported Windows. Choose a new evidence directory outside the bundle:

```powershell
powershell.exe -NoProfile -File .\Invoke-Milestone1CleanMachineAcceptance.ps1 `
  -EvidenceDirectory 'C:\RainArchives-M1-Evidence'
```

The runner:

1. Verifies the bundle's exact file set, lengths, and SHA-256 values.
2. Refuses the clean-machine claim if a modern `dotnet` installation or 7-Zip command, standard install path, or product registry key is detected.
3. Confirms that no native 7-Zip or SharpSevenZip payload is packaged.
4. Runs content-detected listing, integrity testing, and full extraction from an extensionless checked ZIP while forcing invalid `DOTNET_ROOT` values and a System32-only `PATH`.
5. Lists, tests, and extracts a checked solid 7z archive.
6. Verifies the missing-password error, then tests and extracts a checked AES-256 ZIP using a `SecureString` supplied only through redirected standard input. Enter the fixture password from the checked fixture generator when prompted; it is never placed in a process argument.
7. Verifies a locked-source refusal and successful follow-up after releasing the lock.
8. Verifies canonical `ZoneId=3` propagation without copying the source URL, plus extraction to an output path longer than 260 characters.
9. Verifies exact output names, hashes, non-reparse file types, worker teardown, and both current and legacy Rain staging-name patterns.
10. Writes logs plus `evidence.json` without modifying or deleting the bundle.

Exit code `0` with `cleanMachineClaim: true` is the required package proof. Exit code `3` means the machine is ineligible because a developer runtime or 7-Zip was found. Any functional or integrity failure returns `1`.

`-AllowInstalledDeveloperTools` exists only to test the runner on a development machine. Its evidence always records the override and keeps `cleanMachineClaim` false, even if every functional check passes. Such a run does not satisfy the Milestone 1 clean-machine gate.

Copy the complete evidence directory back to the development machine for review. Do not edit `evidence.json` or its logs; retain the transferred bundle manifest alongside it.

## Verified current run

On 2026-08-23 UTC, a fresh AMD64 Windows Sandbox guest on Windows build 26100 and Windows PowerShell 5.1.26100.9168 completed the strict runner with no installed developer-tool evidence and no override. Networking, clipboard, printer, audio/video input, and GPU redirection were disabled; the immutable bundle and fixture-password reconstruction control directory were mapped read-only, and only the evidence mapping was writable.

- Bundle: `C:\Users\aliss\AppData\Local\Temp\RainArchives.M1FinalBundle.4651c78f811c4224b8f9e7270dbaa817`
- Payload: 410 files / 164,211,260 bytes
- Bundle manifest SHA-256: `40354B74DD36D728AC7C7A09961FEA7C04AD20817C084768A173087957F7403C`
- Source fingerprint: `95754497E21D10A57F99C37E8DC239DDEA20626A96A36694E558A1A763CFA859` across 79 canonical inputs
- Evidence: `C:\Users\aliss\AppData\Local\Temp\RainArchives.M1FinalCleanMachineEvidence.8cad551636ea4aa4adf3f0d5e970c7de\strict-final`
- Evidence JSON SHA-256: `3FA36A7C8FFB0CFA4BFADDF094B6AD0F0DFDDB99FD67CB351D5BF5B03F6764FC`
- Result: 20/20 checks, 13 packaged CLI operations, exit code 0, `cleanMachineClaim: true`

An independent host-side review rehashed the exact 410-file manifest, found no missing/changed/extra payload, matched the source and bundle identities in the evidence, found no fixture-password literal in the bundle/evidence/control files, and found no Rain process or staging residue. The Sandbox was then stopped and reports no running environment. This passes the self-contained clean-machine package gate for Milestone 1. Product-owner mixed-DPI, Narrator/keyboard, and actual CachyOS/Samba source/destination/interruption acceptance is recorded separately in `milestone-1-manual-windows.md`; none is inferred from the Sandbox run.
