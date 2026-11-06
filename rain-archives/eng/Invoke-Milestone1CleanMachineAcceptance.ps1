#requires -Version 5.1

[CmdletBinding()]
param(
    [string] $BundleRoot = $PSScriptRoot,

    [string] $EvidenceDirectory = (Join-Path $env:TEMP ("RainArchives.M1CleanMachineEvidence.{0}" -f [Guid]::NewGuid().ToString('N'))),

    [Security.SecureString] $FixturePassword,

    [switch] $AllowInstalledDeveloperTools
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FullPathWithSeparator {
    param([Parameter(Mandatory = $true)][string] $Path)

    return [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
}

function Get-RelativePathInsideRoot {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $rootWithSeparator = Get-FullPathWithSeparator -Path $Root
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the expected root: $fullPath"
    }

    return $fullPath.Substring($rootWithSeparator.Length).Replace('\', '/')
}

function ConvertTo-NativeArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    if ($Value.IndexOf([char]0) -ge 0) {
        throw 'A process argument contains a NUL character.'
    }

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = New-Object System.Text.StringBuilder
    $null = $builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            if ($backslashes -gt 0) {
                $null = $builder.Append('\', ($backslashes * 2))
            }

            $null = $builder.Append('\"')
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) {
            $null = $builder.Append('\', $backslashes)
            $backslashes = 0
        }

        $null = $builder.Append($character)
    }

    if ($backslashes -gt 0) {
        $null = $builder.Append('\', ($backslashes * 2))
    }

    $null = $builder.Append('"')
    return $builder.ToString()
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [AllowEmptyString()][string] $Content
    )

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

function Get-ExtendedLengthPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($fullPath.StartsWith('\\', [System.StringComparison]::Ordinal)) {
        return '\\?\UNC\' + $fullPath.Substring(2)
    }

    return '\\?\' + $fullPath
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    $stream = [System.IO.File]::OpenRead((Get-ExtendedLengthPath -Path $Path))
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $hasher.ComputeHash($stream)
        return ([System.BitConverter]::ToString($bytes)).Replace('-', '')
    }
    finally {
        $hasher.Dispose()
        $stream.Dispose()
    }
}

function Add-Check {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][bool] $Passed,
        [Parameter(Mandatory = $true)][string] $Detail
    )

    $script:Checks.Add([pscustomobject] @{
        name = $Name
        passed = $Passed
        detail = $Detail
    }) | Out-Null

    $label = if ($Passed) { 'PASS' } else { 'FAIL' }
    Write-Host "[$label] $Name - $Detail"
}

function Get-InstalledDeveloperToolEvidence {
    $findings = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($commandName in @('dotnet.exe', '7z.exe', '7zz.exe', '7za.exe')) {
        foreach ($command in @(Get-Command $commandName -CommandType Application -ErrorAction SilentlyContinue)) {
            $null = $findings.Add("command:$($command.Source)")
        }
    }

    $candidatePaths = @(
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
        (Join-Path $env:ProgramFiles '7-Zip\7z.exe')
    )
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $candidatePaths += Join-Path $programFilesX86 'dotnet\dotnet.exe'
        $candidatePaths += Join-Path $programFilesX86 '7-Zip\7z.exe'
    }

    foreach ($candidate in $candidatePaths) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $null = $findings.Add("file:$candidate")
        }
    }

    foreach ($registryPath in @(
        'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions',
        'HKLM:\SOFTWARE\7-Zip',
        'HKCU:\SOFTWARE\7-Zip'
    )) {
        if (Test-Path -LiteralPath $registryPath) {
            $null = $findings.Add("registry:$registryPath")
        }
    }

    return @($findings | Sort-Object)
}

function Assert-BundleManifest {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $ManifestPath
    )

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false, $true)
    $manifest = [System.IO.File]::ReadAllText($ManifestPath, $utf8NoBom) | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or -not $manifest.selfContained -or $manifest.runtimeIdentifier -ne 'win-x64') {
        throw 'The bundle manifest has an unsupported schema or runtime declaration.'
    }

    $expected = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::Ordinal)
    foreach ($entry in @($manifest.files)) {
        if ([string]::IsNullOrWhiteSpace($entry.path) -or $expected.ContainsKey([string] $entry.path)) {
            throw 'The bundle manifest contains an empty or duplicate path.'
        }

        $expected.Add([string] $entry.path, $entry)
    }

    $actualFiles = @(
        Get-ChildItem -LiteralPath $Root -Recurse -File |
            Where-Object { $_.FullName -ne $ManifestPath }
    )
    if ($actualFiles.Count -ne $expected.Count) {
        throw "Bundle file count mismatch. Manifest: $($expected.Count); actual: $($actualFiles.Count)."
    }

    foreach ($file in $actualFiles) {
        $relative = Get-RelativePathInsideRoot -Root $Root -Path $file.FullName
        if (-not $expected.ContainsKey($relative)) {
            throw "Bundle contains an unlisted file: $relative"
        }

        $entry = $expected[$relative]
        if ([long] $entry.length -ne $file.Length) {
            throw "Bundle length mismatch: $relative"
        }

        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        if (-not [string]::Equals($hash, [string] $entry.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Bundle hash mismatch: $relative"
        }
    }

    return $manifest
}

function Invoke-RainCli {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Security.SecureString] $StandardInputSecret,
        [int] $TimeoutSeconds = 120
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $script:CliPath
    $startInfo.Arguments = (($Arguments | ForEach-Object { ConvertTo-NativeArgument -Value $_ }) -join ' ')
    $startInfo.WorkingDirectory = $script:WorkDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.RedirectStandardInput = $null -ne $StandardInputSecret
    $startInfo.EnvironmentVariables['DOTNET_ROOT'] = (Join-Path $script:WorkDirectory 'missing-dotnet-root')
    $startInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = (Join-Path $script:WorkDirectory 'missing-dotnet-root')
    $startInfo.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $startInfo.EnvironmentVariables['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $standardInput = $null
    if ($null -ne $StandardInputSecret) {
        $originalInputEncoding = [Console]::InputEncoding
        try {
            [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false, $true)
            if (-not $process.Start()) {
                throw "Could not start the CLI for check: $Name"
            }

            $standardInput = $process.StandardInput
        }
        finally {
            [Console]::InputEncoding = $originalInputEncoding
        }
    }
    elseif (-not $process.Start()) {
        throw "Could not start the CLI for check: $Name"
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if ($null -ne $StandardInputSecret) {
        $secretBstr = [IntPtr]::Zero
        $secretCharacters = New-Object char[] $StandardInputSecret.Length
        $secretBytes = $null
        try {
            $secretBstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($StandardInputSecret)
            for ($index = 0; $index -lt $StandardInputSecret.Length; $index++) {
                $secretCharacters[$index] = [char] [Runtime.InteropServices.Marshal]::ReadInt16($secretBstr, $index * 2)
            }

            $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
            $byteCount = $utf8.GetByteCount($secretCharacters)
            $secretBytes = New-Object byte[] ($byteCount + 1)
            $written = $utf8.GetBytes($secretCharacters, 0, $secretCharacters.Length, $secretBytes, 0)
            $secretBytes[$written] = 0x0A
            $standardInput.BaseStream.Write($secretBytes, 0, $written + 1)
            $standardInput.BaseStream.Flush()
        }
        finally {
            $standardInput.Dispose()
            [Array]::Clear($secretCharacters, 0, $secretCharacters.Length)
            if ($null -ne $secretBytes) {
                [Array]::Clear($secretBytes, 0, $secretBytes.Length)
            }

            if ($secretBstr -ne [IntPtr]::Zero) {
                [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretBstr)
            }
        }
    }

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "CLI check timed out after $TimeoutSeconds seconds: $Name"
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    $process.Dispose()

    $safeName = $Name -replace '[^A-Za-z0-9_.-]', '-'
    Write-Utf8File -Path (Join-Path $script:LogDirectory "$safeName.stdout.txt") -Content $stdout
    Write-Utf8File -Path (Join-Path $script:LogDirectory "$safeName.stderr.txt") -Content $stderr

    return [pscustomobject] @{
        name = $Name
        exitCode = $exitCode
        stdout = $stdout
        stderr = $stderr
    }
}

$resolvedBundle = [System.IO.Path]::GetFullPath($BundleRoot)
$resolvedEvidence = [System.IO.Path]::GetFullPath($EvidenceDirectory)
if (-not (Test-Path -LiteralPath $resolvedBundle -PathType Container)) {
    throw "Bundle root does not exist: $resolvedBundle"
}

if ($resolvedEvidence.StartsWith((Get-FullPathWithSeparator -Path $resolvedBundle), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'EvidenceDirectory must be outside the immutable bundle root.'
}

if (Test-Path -LiteralPath $resolvedEvidence) {
    throw "EvidenceDirectory already exists. Choose a new path: $resolvedEvidence"
}

$evidenceParent = Split-Path -Parent $resolvedEvidence
if (-not (Test-Path -LiteralPath $evidenceParent -PathType Container)) {
    $null = New-Item -ItemType Directory -Path $evidenceParent
}

$null = New-Item -ItemType Directory -Path $resolvedEvidence
$script:LogDirectory = Join-Path $resolvedEvidence 'logs'
$script:WorkDirectory = Join-Path $resolvedEvidence 'work'
$null = New-Item -ItemType Directory -Path $script:LogDirectory
$null = New-Item -ItemType Directory -Path $script:WorkDirectory
$script:Checks = New-Object 'System.Collections.Generic.List[object]'
$runResults = New-Object 'System.Collections.Generic.List[object]'
$exitCode = 1
$manifest = $null
$toolEvidence = @()
$ownsFixturePassword = $false

try {
    $manifestPath = Join-Path $resolvedBundle 'bundle-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Bundle manifest is missing: $manifestPath"
    }

    $manifest = Assert-BundleManifest -Root $resolvedBundle -ManifestPath $manifestPath
    Add-Check -Name 'bundle-manifest' -Passed $true -Detail "$(@($manifest.files).Count) payload files matched length and SHA-256."

    $isSupportedPlatform = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT -and
        [Environment]::Is64BitOperatingSystem
    Add-Check -Name 'x64-windows' -Passed $isSupportedPlatform -Detail $(
        if ($isSupportedPlatform) { 'The runner is executing on 64-bit Windows.' }
        else { "Platform=$([Environment]::OSVersion.Platform); Is64BitOperatingSystem=$([Environment]::Is64BitOperatingSystem)" })
    if (-not $isSupportedPlatform) {
        throw 'The win-x64 acceptance bundle requires 64-bit Windows.'
    }

    $forbidden = Get-ChildItem -LiteralPath $resolvedBundle -Recurse -File |
        Where-Object { $_.Name -in @('7z.dll', 'SharpSevenZip.dll') }
    Add-Check -Name 'native-gate-disabled' -Passed (-not [bool] $forbidden) -Detail $(
        if ($forbidden) { $forbidden.FullName -join ', ' } else { 'No 7z.dll or SharpSevenZip.dll is present.' })
    if ($forbidden) {
        throw 'A forbidden disabled-provider payload is present.'
    }

    $toolEvidence = @(Get-InstalledDeveloperToolEvidence)
    $environmentClean = $toolEvidence.Count -eq 0
    Add-Check -Name 'clean-machine-environment' -Passed $environmentClean -Detail $(
        if ($environmentClean) { 'No modern .NET or 7-Zip command, standard install, or product registry key was detected.' }
        else { $toolEvidence -join '; ' })

    if (-not $environmentClean -and -not $AllowInstalledDeveloperTools) {
        $exitCode = 3
        throw 'This machine is not eligible for the clean-machine claim. Use a genuinely clean machine; the override is only for local runner validation.'
    }

    $script:CliPath = Join-Path $resolvedBundle 'cli\rain-archives.exe'
    $workerPath = Join-Path $resolvedBundle 'worker\RainArchives.Worker.exe'
    foreach ($required in @($script:CliPath, $workerPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Required executable is missing: $required"
        }
    }

    $extensionlessArchive = Join-Path $script:WorkDirectory 'extensionless-archive'
    Copy-Item -LiteralPath (Join-Path $resolvedBundle 'fixtures\plain.zip') -Destination $extensionlessArchive

    $commonArguments = @('--worker', $workerPath)
    $listResult = Invoke-RainCli -Name 'extensionless-list' -Arguments (@('list', $extensionlessArchive) + $commonArguments)
    $runResults.Add($listResult) | Out-Null
    $unicodeFileName = 'unicode-' + [char] 0x96EA + '.txt'
    $listPassed = $listResult.exitCode -eq 0 -and
        $listResult.stdout -match '(?m)^Format: zip\s*$' -and
        $listResult.stdout -match '(?m)^Entries: 3\s*$' -and
        $listResult.stdout.Contains($unicodeFileName)
    Add-Check -Name 'extensionless-list' -Passed $listPassed -Detail "CLI exit code $($listResult.exitCode); content detection and Unicode listing checked."

    $testResult = Invoke-RainCli -Name 'extensionless-integrity' -Arguments (@('test', $extensionlessArchive) + $commonArguments)
    $runResults.Add($testResult) | Out-Null
    $testPassed = $testResult.exitCode -eq 0 -and $testResult.stdout -match '(?m)^Completed\.\s*$'
    Add-Check -Name 'extensionless-integrity' -Passed $testPassed -Detail "CLI exit code $($testResult.exitCode); terminal completion checked."

    $destination = Join-Path $script:WorkDirectory 'extracted'
    $extractResult = Invoke-RainCli -Name 'extensionless-extract' -Arguments (@('extract', $extensionlessArchive, $destination) + $commonArguments)
    $runResults.Add($extractResult) | Out-Null
    $extractPassed = $extractResult.exitCode -eq 0 -and $extractResult.stdout -match '(?m)^Completed\.\s*$'
    Add-Check -Name 'extensionless-extract' -Passed $extractPassed -Detail "CLI exit code $($extractResult.exitCode); terminal completion checked."

    $expectedSource = Join-Path $resolvedBundle 'fixtures\source'
    $expectedFiles = @('alpha.txt', 'beta.txt', $unicodeFileName)
    $hashesMatch = $true
    foreach ($name in $expectedFiles) {
        $expectedPath = Join-Path $expectedSource $name
        $actualPath = Join-Path $destination $name
        if (-not (Test-Path -LiteralPath $actualPath -PathType Leaf)) {
            $hashesMatch = $false
            continue
        }

        $expectedHash = (Get-FileHash -LiteralPath $expectedPath -Algorithm SHA256).Hash
        $actualHash = (Get-FileHash -LiteralPath $actualPath -Algorithm SHA256).Hash
        if (-not [string]::Equals($expectedHash, $actualHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            $hashesMatch = $false
        }

        $actualItem = Get-Item -LiteralPath $actualPath -Force
        if ($actualItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            $hashesMatch = $false
        }
    }

    $actualRelativeFiles = @(
        Get-ChildItem -LiteralPath $destination -Recurse -File |
            ForEach-Object { Get-RelativePathInsideRoot -Root $destination -Path $_.FullName } |
            Sort-Object
    )
    $expectedRelativeFiles = @($expectedFiles | Sort-Object)
    $exactManifest = ($actualRelativeFiles.Count -eq $expectedRelativeFiles.Count) -and
        -not (Compare-Object -ReferenceObject $expectedRelativeFiles -DifferenceObject $actualRelativeFiles)
    Add-Check -Name 'extracted-content' -Passed ($hashesMatch -and $exactManifest) -Detail 'Exact filenames, hashes, and non-reparse file types checked.'

    $solidArchive = Join-Path $resolvedBundle 'fixtures\solid.7z'
    $solidList = Invoke-RainCli -Name 'solid-7z-list' -Arguments (@('list', $solidArchive) + $commonArguments)
    $runResults.Add($solidList) | Out-Null
    $solidListPassed = $solidList.exitCode -eq 0 -and
        $solidList.stdout -match '(?m)^Format: 7z\s*$' -and
        $solidList.stdout.Contains('alpha.txt')
    Add-Check -Name 'solid-7z-list' -Passed $solidListPassed -Detail "CLI exit code $($solidList.exitCode); solid fixture content listed."

    $solidTest = Invoke-RainCli -Name 'solid-7z-integrity' -Arguments (@('test', $solidArchive) + $commonArguments)
    $runResults.Add($solidTest) | Out-Null
    $solidTestPassed = $solidTest.exitCode -eq 0 -and $solidTest.stdout -match '(?m)^Completed\.\s*$'
    Add-Check -Name 'solid-7z-integrity' -Passed $solidTestPassed -Detail "CLI exit code $($solidTest.exitCode); terminal completion checked."

    $solidDestination = Join-Path $script:WorkDirectory 'solid-7z-extracted'
    $solidExtract = Invoke-RainCli -Name 'solid-7z-extract' -Arguments (@('extract', $solidArchive, $solidDestination) + $commonArguments)
    $runResults.Add($solidExtract) | Out-Null
    $solidOutput = Join-Path $solidDestination 'alpha.txt'
    $solidExtractPassed = $solidExtract.exitCode -eq 0 -and
        $solidExtract.stdout -match '(?m)^Completed\.\s*$' -and
        [System.IO.File]::Exists((Get-ExtendedLengthPath -Path $solidOutput)) -and
        [string]::Equals(
            (Get-FileSha256 -Path $solidOutput),
            (Get-FileSha256 -Path (Join-Path $expectedSource 'alpha.txt')),
            [System.StringComparison]::OrdinalIgnoreCase)
    Add-Check -Name 'solid-7z-extract' -Passed $solidExtractPassed -Detail "CLI exit code $($solidExtract.exitCode); alpha.txt hash checked."

    $encryptedArchive = Join-Path $resolvedBundle 'fixtures\aes256.zip'
    $passwordRequired = Invoke-RainCli -Name 'aes256-password-required' -Arguments (@('test', $encryptedArchive) + $commonArguments)
    $runResults.Add($passwordRequired) | Out-Null
    $passwordRequiredPassed = $passwordRequired.exitCode -eq 1 -and
        $passwordRequired.stderr -match '(?m)^archive\.password_required:'
    Add-Check -Name 'aes256-password-required' -Passed $passwordRequiredPassed -Detail "CLI exit code $($passwordRequired.exitCode); structured password request checked."

    if ($null -eq $FixturePassword) {
        Write-Host 'Enter the checked-fixture password. It will be sent only through redirected standard input.'
        $FixturePassword = Read-Host -Prompt 'Fixture password' -AsSecureString
        $ownsFixturePassword = $true
    }

    if ($FixturePassword.Length -eq 0) {
        throw 'The checked-fixture password cannot be empty.'
    }

    $encryptedTest = Invoke-RainCli `
        -Name 'aes256-integrity' `
        -Arguments (@('test', $encryptedArchive, '--password-stdin') + $commonArguments) `
        -StandardInputSecret $FixturePassword
    $runResults.Add($encryptedTest) | Out-Null
    $encryptedTestPassed = $encryptedTest.exitCode -eq 0 -and $encryptedTest.stdout -match '(?m)^Completed\.\s*$'
    Add-Check -Name 'aes256-integrity' -Passed $encryptedTestPassed -Detail "CLI exit code $($encryptedTest.exitCode); protected fixture completed without an argument secret."

    $encryptedDestination = Join-Path $script:WorkDirectory 'aes256-extracted'
    $encryptedExtract = Invoke-RainCli `
        -Name 'aes256-extract' `
        -Arguments (@('extract', $encryptedArchive, $encryptedDestination, '--password-stdin') + $commonArguments) `
        -StandardInputSecret $FixturePassword
    $runResults.Add($encryptedExtract) | Out-Null
    $encryptedOutput = Join-Path $encryptedDestination 'alpha.txt'
    $encryptedExtractPassed = $encryptedExtract.exitCode -eq 0 -and
        $encryptedExtract.stdout -match '(?m)^Completed\.\s*$' -and
        [System.IO.File]::Exists((Get-ExtendedLengthPath -Path $encryptedOutput)) -and
        [string]::Equals(
            (Get-FileSha256 -Path $encryptedOutput),
            (Get-FileSha256 -Path (Join-Path $expectedSource 'alpha.txt')),
            [System.StringComparison]::OrdinalIgnoreCase)
    Add-Check -Name 'aes256-extract' -Passed $encryptedExtractPassed -Detail "CLI exit code $($encryptedExtract.exitCode); alpha.txt hash checked."

    $lockedArchive = Join-Path $script:WorkDirectory 'locked-source.zip'
    Copy-Item -LiteralPath (Join-Path $resolvedBundle 'fixtures\plain.zip') -Destination $lockedArchive
    $lockStream = [System.IO.File]::Open(
        $lockedArchive,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        $lockedResult = Invoke-RainCli -Name 'locked-source-refusal' -Arguments (@('list', $lockedArchive) + $commonArguments)
    }
    finally {
        $lockStream.Dispose()
    }

    $runResults.Add($lockedResult) | Out-Null
    $lockedPassed = $lockedResult.exitCode -eq 1 -and
        $lockedResult.stderr -match '(?m)^filesystem\.(io_failure|permission_denied):'
    Add-Check -Name 'locked-source-refusal' -Passed $lockedPassed -Detail "CLI exit code $($lockedResult.exitCode); structured sharing refusal checked."

    $unlockedResult = Invoke-RainCli -Name 'locked-source-follow-up' -Arguments (@('list', $lockedArchive) + $commonArguments)
    $runResults.Add($unlockedResult) | Out-Null
    Add-Check -Name 'locked-source-follow-up' -Passed ($unlockedResult.exitCode -eq 0) -Detail "CLI exit code $($unlockedResult.exitCode); source remained usable after lock release."

    $provenanceArchive = Join-Path $script:WorkDirectory 'downloaded-source.zip'
    Copy-Item -LiteralPath (Join-Path $resolvedBundle 'fixtures\plain.zip') -Destination $provenanceArchive
    $zoneInput = "[ZoneTransfer]`r`nZoneId=3`r`nHostUrl=https://untrusted.invalid/archive.zip`r`n"
    Set-Content `
        -LiteralPath $provenanceArchive `
        -Stream 'Zone.Identifier' `
        -Value $zoneInput `
        -Encoding UTF8 `
        -NoNewline
    $provenanceDestination = Join-Path $script:WorkDirectory 'provenance-extracted'
    $provenanceResult = Invoke-RainCli -Name 'download-provenance' -Arguments (@('extract', $provenanceArchive, $provenanceDestination) + $commonArguments)
    $runResults.Add($provenanceResult) | Out-Null
    $zoneOutputFile = Join-Path $provenanceDestination 'alpha.txt'
    $zoneOutput = try {
        Get-Content -LiteralPath $zoneOutputFile -Stream 'Zone.Identifier' -Raw -Encoding ASCII
    }
    catch {
        $null
    }
    $provenancePassed = $provenanceResult.exitCode -eq 0 -and
        $zoneOutput -eq "[ZoneTransfer]`r`nZoneId=3`r`n"
    Add-Check -Name 'download-provenance' -Passed $provenancePassed -Detail 'Canonical ZoneId=3 propagation was checked; source URL was not copied.'

    $longParent = Join-Path $script:WorkDirectory 'long-path'
    [System.IO.Directory]::CreateDirectory((Get-ExtendedLengthPath -Path $longParent)) | Out-Null
    $segmentIndex = 0
    while ((Join-Path (Join-Path $longParent 'archive-output') $unicodeFileName).Length -le 260) {
        $segmentIndex++
        $longParent = Join-Path $longParent ("segment-{0:D2}-abcdefgh" -f $segmentIndex)
        [System.IO.Directory]::CreateDirectory((Get-ExtendedLengthPath -Path $longParent)) | Out-Null
    }

    $longDestination = Join-Path $longParent 'archive-output'
    $longResult = Invoke-RainCli -Name 'long-path-extract' -Arguments (@('extract', $extensionlessArchive, $longDestination) + $commonArguments)
    $runResults.Add($longResult) | Out-Null
    $longOutput = Join-Path $longDestination $unicodeFileName
    $longPassed = $longOutput.Length -gt 260 -and
        $longResult.exitCode -eq 0 -and
        [System.IO.File]::Exists((Get-ExtendedLengthPath -Path $longOutput)) -and
        [string]::Equals(
            (Get-FileSha256 -Path $longOutput),
            (Get-FileSha256 -Path (Join-Path $expectedSource $unicodeFileName)),
            [System.StringComparison]::OrdinalIgnoreCase)
    Add-Check -Name 'long-path-extract' -Passed $longPassed -Detail "Output path length $($longOutput.Length); Unicode file hash checked."

    Start-Sleep -Milliseconds 250
    $ownedWorkers = @(
        Get-CimInstance Win32_Process -Filter "Name='RainArchives.Worker.exe'" -ErrorAction SilentlyContinue |
            Where-Object {
                $_.ExecutablePath -and
                [string]::Equals([System.IO.Path]::GetFullPath($_.ExecutablePath), $workerPath, [System.StringComparison]::OrdinalIgnoreCase)
            }
    )
    Add-Check -Name 'worker-cleanup' -Passed ($ownedWorkers.Count -eq 0) -Detail $(
        if ($ownedWorkers.Count -eq 0) { 'No worker from this bundle remained.' }
        else { "Remaining process IDs: $($ownedWorkers.ProcessId -join ', ')" })

    $stagingResidue = @(Get-ChildItem -LiteralPath $script:WorkDirectory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -like '*.rainarchives-*.staging' -or
            $_.Name -like '*.rain-stage-*'
        })
    Add-Check -Name 'staging-cleanup' -Passed ($stagingResidue.Count -eq 0) -Detail $(
        if ($stagingResidue.Count -eq 0) { 'No Rain staging path remained in the proof root.' }
        else { $stagingResidue.FullName -join ', ' })

    $allChecksPassed = @($script:Checks | Where-Object { -not $_.passed }).Count -eq 0
    if ($AllowInstalledDeveloperTools -and -not $environmentClean) {
        $allFunctionalChecks = @($script:Checks | Where-Object { $_.name -ne 'clean-machine-environment' -and -not $_.passed }).Count -eq 0
        $exitCode = if ($allFunctionalChecks) { 0 } else { 1 }
    }
    else {
        $exitCode = if ($allChecksPassed) { 0 } else { 1 }
    }
}
catch {
    Add-Check -Name 'runner-completion' -Passed $false -Detail $_.Exception.Message
    if ($exitCode -ne 3) {
        $exitCode = 1
    }
}
finally {
    if ($ownsFixturePassword -and $null -ne $FixturePassword) {
        $FixturePassword.Dispose()
    }

    $environmentCleanCheck = @($script:Checks | Where-Object { $_.name -eq 'clean-machine-environment' } | Select-Object -Last 1)
    $environmentClean = $environmentCleanCheck.Count -eq 1 -and $environmentCleanCheck[0].passed
    $failedChecks = @($script:Checks | Where-Object { -not $_.passed -and -not ($AllowInstalledDeveloperTools -and $_.name -eq 'clean-machine-environment') })
    $cleanMachineClaim = $environmentClean -and $failedChecks.Count -eq 0 -and $exitCode -eq 0

    $evidence = [ordered] @{
        schemaVersion = 1
        completedUtc = [DateTime]::UtcNow.ToString('o')
        bundleRoot = $resolvedBundle
        bundleManifestSha256 = if (Test-Path -LiteralPath (Join-Path $resolvedBundle 'bundle-manifest.json')) {
            (Get-FileHash -LiteralPath (Join-Path $resolvedBundle 'bundle-manifest.json') -Algorithm SHA256).Hash
        } else {
            $null
        }
        bundleSourceFingerprint = if ($manifest) { $manifest.sourceFingerprint } else { $null }
        evidenceDirectory = $resolvedEvidence
        machine = [ordered] @{
            osVersion = [Environment]::OSVersion.VersionString
            osArchitecture = [Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE')
            powershellVersion = $PSVersionTable.PSVersion.ToString()
            installedDeveloperToolEvidence = $toolEvidence
            overrideUsed = [bool] $AllowInstalledDeveloperTools
        }
        checks = $script:Checks.ToArray()
        commands = @($runResults.ToArray() | ForEach-Object {
            [ordered] @{
                name = $_.name
                exitCode = $_.exitCode
            }
        })
        cleanMachineClaim = $cleanMachineClaim
        exitCode = $exitCode
    }

    Write-Utf8File -Path (Join-Path $resolvedEvidence 'evidence.json') -Content ($evidence | ConvertTo-Json -Depth 8)
    Write-Host "Evidence: $(Join-Path $resolvedEvidence 'evidence.json')"
    Write-Host "Clean-machine claim: $cleanMachineClaim"
}

exit $exitCode
