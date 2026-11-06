#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageRoot,

    [Parameter(Mandatory = $true)]
    [string] $ExternalSevenZip,

    [Parameter(Mandatory = $true)]
    [string] $WorkRoot,

    [Parameter(Mandatory = $true)]
    [string] $EvidencePath,

    [ValidateRange(1, 20)]
    [int] $Runs = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedProtocol = '1.18'
$expectedSourceInputs = 132
$expectedSourceSha256 = '1C85F6D929DE5F940E1357C72C210A8647DFD2E9F38A0A2B0B0832127F5CE844'
$expectedManifestSha256 = '6A8CB71E3924E04451299E9C4C4AB2F022D9648FCFE8113186656C6F0D0FF47A'
$expectedSevenZipSha256 = '35D4D69D7CD6CB44558F208C3B1334268013F9DAF82D2DDA848893A1C30C59C2'

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)]
        [bool] $Condition,

        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Get-StringSha256 {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

function Remove-OwnedWorkRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedToken
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $leaf = [IO.Path]::GetFileName($fullPath)
    Assert-Condition ($leaf -eq "RainArchives.M3MixedPerformance.$ExpectedToken") `
        'The managed-performance cleanup root does not have the exact owned leaf.'
    $item = Get-Item -LiteralPath $fullPath -Force
    Assert-Condition $item.PSIsContainer 'The managed-performance cleanup root is not a directory.'
    Assert-Condition (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
        'The managed-performance cleanup root is a reparse point.'
    [IO.Directory]::Delete($fullPath, $true)
}

function New-PatternFile {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][byte[]] $Pattern,
        [Parameter(Mandatory = $true)][int] $Length
    )

    $bytes = [byte[]]::new($Length)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $bytes[$index] = $Pattern[$index % $Pattern.Length]
    }

    [IO.File]::WriteAllBytes($Path, $bytes)
}

function Invoke-SevenZipTest {
    param(
        [Parameter(Mandatory = $true)][string] $Executable,
        [Parameter(Mandatory = $true)][string] $ArchivePath
    )

    $beforeLength = (Get-Item -LiteralPath $ArchivePath).Length
    $beforeHash = Get-Sha256 -Path $ArchivePath
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @('t', '-bd', '-y', '--', $ArchivePath)) {
        $null = $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $started = $process.Start()
        Assert-Condition $started 'Official 7-Zip did not start.'
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Official 7-Zip exceeded the 30-second validation bound.'
        }

        $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
        $standardError = $standardErrorTask.GetAwaiter().GetResult()
        Assert-Condition ($process.ExitCode -eq 0) `
            "Official 7-Zip rejected $ArchivePath with exit code $($process.ExitCode)."
        $afterLength = (Get-Item -LiteralPath $ArchivePath).Length
        $afterHash = Get-Sha256 -Path $ArchivePath
        Assert-Condition ($beforeLength -eq $afterLength -and $beforeHash -eq $afterHash) `
            'An archive changed during official 7-Zip validation.'
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            ElapsedMilliseconds = $stopwatch.Elapsed.TotalMilliseconds
            ArchiveLength = $afterLength
            ArchiveSha256 = $afterHash
            StandardOutputSha256 = Get-StringSha256 -Value $standardOutput
            StandardErrorSha256 = Get-StringSha256 -Value $standardError
        }
    }
    finally {
        $stopwatch.Stop()
        $process.Dispose()
    }
}

function Get-WorkerProcess {
    param([Parameter(Mandatory = $true)][object] $Client)

    $bindingFlags = [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::NonPublic
    $property = $Client.GetType().GetProperty('WorkerProcessId', $bindingFlags)
    Assert-Condition ($null -ne $property) 'The package Client no longer exposes its internal owned Worker PID.'
    $workerProcessId = [int] $property.GetValue($Client)
    return [Diagnostics.Process]::GetProcessById($workerProcessId)
}

function Start-CreationCase {
    param(
        [Parameter(Mandatory = $true)][object] $Client,
        [Parameter(Mandatory = $true)][object] $Case,
        [Parameter(Mandatory = $true)][string] $Destination,
        [Parameter(Mandatory = $true)][object] $Files,
        [Parameter(Mandatory = $true)][object] $Directories,
        [Parameter(Mandatory = $true)][string] $RawSource,
        [Parameter(Mandatory = $true)][Threading.CancellationToken] $CancellationToken
    )

    switch ($Case.Format) {
        'ZIP' {
            return $Client.StartZipTreeCreationAsync(
                $Destination, $Files, $Directories, $Case.Preset, $CancellationToken).GetAwaiter().GetResult()
        }
        'TAR' {
            return $Client.StartTarTreeCreationAsync(
                $Destination, $Files, $Directories, $CancellationToken).GetAwaiter().GetResult()
        }
        'TAR.GZ' {
            return $Client.StartTarGZipTreeCreationAsync(
                $Destination, $Files, $Directories, $Case.Preset, $CancellationToken).GetAwaiter().GetResult()
        }
        'TAR.BZ2' {
            return $Client.StartTarBZip2TreeCreationAsync(
                $Destination, $Files, $Directories, $Case.Preset, $CancellationToken).GetAwaiter().GetResult()
        }
        'GZIP' {
            return $Client.StartGZipCreationAsync(
                $Destination, $RawSource, $Case.Preset, $CancellationToken).GetAwaiter().GetResult()
        }
        'BZIP2' {
            return $Client.StartBZip2CreationAsync(
                $Destination, $RawSource, $Case.Preset, $CancellationToken).GetAwaiter().GetResult()
        }
        default {
            throw "Unsupported managed-performance case: $($Case.Format)"
        }
    }
}

function Test-RainOutput {
    param(
        [Parameter(Mandatory = $true)][object] $Client,
        [Parameter(Mandatory = $true)][object] $Case,
        [Parameter(Mandatory = $true)][string] $ArchivePath,
        [Parameter(Mandatory = $true)][string] $ExtractionRoot,
        [Parameter(Mandatory = $true)][object[]] $TreeFiles,
        [Parameter(Mandatory = $true)][string[]] $TreeDirectories,
        [Parameter(Mandatory = $true)][string] $RawSourceSha256,
        [Parameter(Mandatory = $true)][Threading.CancellationToken] $CancellationToken
    )

    $archiveHashBefore = Get-Sha256 -Path $ArchivePath
    $opened = $Client.OpenArchiveAsync($ArchivePath, $null, $CancellationToken).GetAwaiter().GetResult()
    $sessionId = $opened.Archive.SessionId
    try {
        $listed = $Client.ListEntriesAsync($sessionId, 0, 4096, $CancellationToken).GetAwaiter().GetResult()
        $allEntries = [System.Collections.Generic.List[RainArchives.Contracts.EntryId]]::new()
        $integrityStart = $Client.StartIntegrityTestAsync(
            $sessionId, $allEntries, $null, $CancellationToken).GetAwaiter().GetResult()
        $integrityTerminal = $Client.WaitForTerminalAsync(
            $integrityStart.OperationId, $CancellationToken).GetAwaiter().GetResult()
        Assert-Condition ($integrityTerminal.Phase.ToString() -eq 'Completed') `
            "Rain integrity failed for $ArchivePath."

        $extractionStart = $Client.StartExtractionAsync(
            $sessionId, $allEntries, $ExtractionRoot, $null, $CancellationToken).GetAwaiter().GetResult()
        $extractionTerminal = $Client.WaitForTerminalAsync(
            $extractionStart.OperationId, $CancellationToken).GetAwaiter().GetResult()
        Assert-Condition ($extractionTerminal.Phase.ToString() -eq 'Completed') `
            "Rain extraction failed for $ArchivePath."

        if ($Case.Route -eq 'tree') {
            foreach ($file in $TreeFiles) {
                $extractedPath = Join-Path $ExtractionRoot (Join-Path 'mixed' $file.RelativePath)
                Assert-Condition (Test-Path -LiteralPath $extractedPath -PathType Leaf) `
                    "Rain did not extract $($file.RelativePath)."
                Assert-Condition ((Get-Sha256 -Path $extractedPath) -eq $file.Sha256) `
                    "Rain changed $($file.RelativePath) during round trip."
            }

            foreach ($directory in $TreeDirectories) {
                $extractedPath = Join-Path $ExtractionRoot (Join-Path 'mixed' $directory)
                Assert-Condition (Test-Path -LiteralPath $extractedPath -PathType Container) `
                    "Rain did not preserve directory $directory."
            }

            $verifiedFileCount = $TreeFiles.Count
            $verifiedBytes = ($TreeFiles | Measure-Object Length -Sum).Sum
        }
        else {
            Assert-Condition ($listed.Entries.Count -eq 1) 'A raw stream exposed an unexpected entry count.'
            $entry = $listed.Entries[0]
            $extractedPath = Join-Path $ExtractionRoot $entry.RawPath
            Assert-Condition (Test-Path -LiteralPath $extractedPath -PathType Leaf) `
                'Rain did not extract the raw stream member.'
            Assert-Condition ((Get-Sha256 -Path $extractedPath) -eq $RawSourceSha256) `
                'Rain changed the raw stream payload during round trip.'
            $verifiedFileCount = 1
            $verifiedBytes = (Get-Item -LiteralPath $extractedPath).Length
        }

        return [pscustomobject]@{
            ListedEntryCount = $listed.Entries.Count
            IntegrityPhase = $integrityTerminal.Phase.ToString()
            ExtractionPhase = $extractionTerminal.Phase.ToString()
            VerifiedFileCount = $verifiedFileCount
            VerifiedBytes = $verifiedBytes
            ArchiveSha256 = $archiveHashBefore
        }
    }
    finally {
        $closed = $Client.CloseArchiveAsync($sessionId, $CancellationToken).GetAwaiter().GetResult()
        Assert-Condition $closed.Closed 'Rain did not close a managed-performance archive session.'
    }
}

$packageRootFull = [IO.Path]::GetFullPath($PackageRoot)
$manifestPath = Join-Path $packageRootFull 'package-manifest.json'
$appRoot = Join-Path $packageRootFull 'app'
$clientPath = Join-Path $appRoot 'RainArchives.Client.dll'
$contractsPath = Join-Path $appRoot 'RainArchives.Contracts.dll'
$workerPath = Join-Path $appRoot 'worker\RainArchives.Worker.exe'
$workRootFull = [IO.Path]::GetFullPath($WorkRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$evidencePathFull = [IO.Path]::GetFullPath($EvidencePath)
$externalSevenZipFull = [IO.Path]::GetFullPath($ExternalSevenZip)

foreach ($requiredFile in @($manifestPath, $clientPath, $contractsPath, $workerPath, $externalSevenZipFull)) {
    Assert-Condition (Test-Path -LiteralPath $requiredFile -PathType Leaf) "Required file is missing: $requiredFile"
}

Assert-Condition (-not (Test-Path -LiteralPath $workRootFull)) 'The work root must not already exist.'
Assert-Condition (-not (Test-Path -LiteralPath $evidencePathFull)) 'The evidence path must not already exist.'
Assert-Condition (Test-Path -LiteralPath (Split-Path -Parent $workRootFull) -PathType Container) `
    'The work-root parent must already exist.'
Assert-Condition (Test-Path -LiteralPath (Split-Path -Parent $evidencePathFull) -PathType Container) `
    'The evidence parent must already exist.'
Assert-Condition ((Get-Sha256 -Path $manifestPath) -eq $expectedManifestSha256) `
    'The package manifest hash does not match the accepted 1C85F6D9 package.'
Assert-Condition ((Get-Sha256 -Path $externalSevenZipFull) -eq $expectedSevenZipSha256) `
    'The external 7-Zip executable does not match the qualified 26.02 x64 tool.'

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Assert-Condition ($manifest.protocol -eq $expectedProtocol) 'The package protocol changed.'
Assert-Condition ($manifest.sourceInputCount -eq $expectedSourceInputs) 'The package source-input count changed.'
Assert-Condition ($manifest.sourceSha256 -eq $expectedSourceSha256) 'The package source fingerprint changed.'
Assert-Condition ((Get-Sha256 -Path $clientPath) -eq $manifest.files.'app/RainArchives.Client.dll') `
    'The package Client does not match its manifest.'
Assert-Condition ((Get-Sha256 -Path $contractsPath) -eq $manifest.files.'app/RainArchives.Contracts.dll') `
    'The package Contracts assembly does not match its manifest.'
Assert-Condition ((Get-Sha256 -Path $workerPath) -eq $manifest.files.'app/worker/RainArchives.Worker.exe') `
    'The package Worker executable does not match its manifest.'

$workRootLeaf = [IO.Path]::GetFileName($workRootFull)
Assert-Condition $workRootLeaf.StartsWith('RainArchives.M3MixedPerformance.', [StringComparison]::Ordinal) `
    'The work-root leaf does not have the managed-performance prefix.'
$token = $workRootLeaf.Substring('RainArchives.M3MixedPerformance.'.Length)
$parsedToken = [Guid]::Empty
Assert-Condition ([Guid]::TryParseExact($token, 'N', [ref] $parsedToken)) `
    'The work-root leaf must be RainArchives.M3MixedPerformance.<32-hex-guid>.'

[Reflection.Assembly]::LoadFrom($contractsPath) | Out-Null
[Reflection.Assembly]::LoadFrom($clientPath) | Out-Null

New-Item -ItemType Directory -Path $workRootFull | Out-Null
$sourceRoot = Join-Path $workRootFull 'source-tree'
$outputRoot = Join-Path $workRootFull 'outputs'
$rawSource = Join-Path $workRootFull 'raw-mixed.bin'
New-Item -ItemType Directory -Path $sourceRoot, $outputRoot | Out-Null
New-Item -ItemType Directory -Path `
    (Join-Path $sourceRoot 'compressible'), `
    (Join-Path $sourceRoot 'incompressible'), `
    (Join-Path $sourceRoot 'tiny'), `
    (Join-Path $sourceRoot 'unicode-雨'), `
    (Join-Path $sourceRoot 'empty') | Out-Null

$textPattern = [Text.Encoding]::UTF8.GetBytes("Rain Archives managed creation benchmark. Unicode: 雨.`n")
New-PatternFile -Path (Join-Path $sourceRoot 'compressible\text.bin') `
    -Pattern $textPattern -Length 524288
$randomBytes = [byte[]]::new(524288)
$random = [Random]::new(1822539016)
$random.NextBytes($randomBytes)
[IO.File]::WriteAllBytes((Join-Path $sourceRoot 'incompressible\seeded.bin'), $randomBytes)
for ($index = 0; $index -lt 64; $index++) {
    $length = ($index * 17) % 257
    $tinyBytes = [byte[]]::new($length)
    for ($byteIndex = 0; $byteIndex -lt $length; $byteIndex++) {
        $tinyBytes[$byteIndex] = [byte](($index + $byteIndex) % 251)
    }

    [IO.File]::WriteAllBytes((Join-Path $sourceRoot ('tiny\file-{0:D2}.bin' -f $index)), $tinyBytes)
}

New-PatternFile -Path (Join-Path $sourceRoot 'unicode-雨\café.bin') `
    -Pattern ([Text.Encoding]::UTF8.GetBytes('雨café')) -Length 4096
$rawBytes = [byte[]]::new(1048576)
for ($index = 0; $index -lt 524288; $index++) {
    $rawBytes[$index] = $textPattern[$index % $textPattern.Length]
}

[Array]::Copy($randomBytes, 0, $rawBytes, 524288, $randomBytes.Length)
[IO.File]::WriteAllBytes($rawSource, $rawBytes)

$treeFiles = @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{
        RelativePath = [IO.Path]::GetRelativePath($sourceRoot, $_.FullName)
        Length = $_.Length
        Sha256 = Get-Sha256 -Path $_.FullName
    }
})
$treeDirectories = @(Get-ChildItem -LiteralPath $sourceRoot -Directory -Recurse | Sort-Object FullName | ForEach-Object {
    [IO.Path]::GetRelativePath($sourceRoot, $_.FullName)
})
$rawSourceSha256 = Get-Sha256 -Path $rawSource
$treeManifestText = ($treeFiles | ForEach-Object { "$($_.RelativePath.Replace('\', '/'))`0$($_.Length)`0$($_.Sha256)" }) -join "`n"
$treeManifestSha256 = Get-StringSha256 -Value $treeManifestText

$files = [System.Collections.Generic.List[RainArchives.Contracts.ArchiveCreationFile]]::new()
$directories = [System.Collections.Generic.List[RainArchives.Contracts.ArchiveCreationDirectory]]::new()
$directories.Add([RainArchives.Contracts.ArchiveCreationDirectory]::new($sourceRoot, 'mixed'))
$presets = [RainArchives.Contracts.ArchiveCompressionPreset]
$cases = @(
    [pscustomobject]@{ Format = 'ZIP'; Preset = $presets::Store; Route = 'tree'; Extension = '.zip' },
    [pscustomobject]@{ Format = 'ZIP'; Preset = $presets::Fast; Route = 'tree'; Extension = '.zip' },
    [pscustomobject]@{ Format = 'ZIP'; Preset = $presets::Balanced; Route = 'tree'; Extension = '.zip' },
    [pscustomobject]@{ Format = 'ZIP'; Preset = $presets::Maximum; Route = 'tree'; Extension = '.zip' },
    [pscustomobject]@{ Format = 'TAR'; Preset = $presets::Store; Route = 'tree'; Extension = '.tar' },
    [pscustomobject]@{ Format = 'TAR.GZ'; Preset = $presets::Store; Route = 'tree'; Extension = '.tar.gz' },
    [pscustomobject]@{ Format = 'TAR.GZ'; Preset = $presets::Fast; Route = 'tree'; Extension = '.tar.gz' },
    [pscustomobject]@{ Format = 'TAR.GZ'; Preset = $presets::Balanced; Route = 'tree'; Extension = '.tar.gz' },
    [pscustomobject]@{ Format = 'TAR.GZ'; Preset = $presets::Maximum; Route = 'tree'; Extension = '.tar.gz' },
    [pscustomobject]@{ Format = 'TAR.BZ2'; Preset = $presets::Balanced; Route = 'tree'; Extension = '.tar.bz2' },
    [pscustomobject]@{ Format = 'GZIP'; Preset = $presets::Store; Route = 'raw'; Extension = '.gz' },
    [pscustomobject]@{ Format = 'GZIP'; Preset = $presets::Fast; Route = 'raw'; Extension = '.gz' },
    [pscustomobject]@{ Format = 'GZIP'; Preset = $presets::Balanced; Route = 'raw'; Extension = '.gz' },
    [pscustomobject]@{ Format = 'GZIP'; Preset = $presets::Maximum; Route = 'raw'; Extension = '.gz' },
    [pscustomobject]@{ Format = 'BZIP2'; Preset = $presets::Balanced; Route = 'raw'; Extension = '.bz2' }
)

$samples = [Collections.Generic.List[object]]::new()
$warmupCount = 0
$externalValidationCount = 0
$cancellationToken = [Threading.CancellationToken]::None
$recordedUtc = [DateTimeOffset]::UtcNow

foreach ($case in $cases) {
    for ($run = -1; $run -lt $Runs; $run++) {
        $measured = $run -ge 0
        $caseName = ('{0}-{1}-{2}' -f `
            $case.Format.Replace('.', '-').ToLowerInvariant(),
            $case.Preset.ToString().ToLowerInvariant(),
            $(if ($measured) { '{0:D2}' -f $run } else { 'warmup' }))
        $destination = Join-Path $outputRoot ($caseName + $case.Extension)
        $extractionRoot = Join-Path $outputRoot ($caseName + '-extract')
        Assert-Condition (-not (Test-Path -LiteralPath $destination)) 'A measured destination already exists.'
        Assert-Condition (-not (Test-Path -LiteralPath $extractionRoot)) 'A measured extraction root already exists.'

        $client = $null
        $worker = $null
        try {
            $client = [RainArchives.Client.ArchiveClient]::StartAsync(
                $workerPath, $cancellationToken).GetAwaiter().GetResult()
            $worker = Get-WorkerProcess -Client $client
            $workerCpuBefore = $worker.TotalProcessorTime.TotalMilliseconds
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            $started = Start-CreationCase -Client $client -Case $case -Destination $destination `
                -Files $files -Directories $directories -RawSource $rawSource `
                -CancellationToken $cancellationToken
            $terminal = $client.WaitForTerminalAsync(
                $started.OperationId, $cancellationToken).GetAwaiter().GetResult()
            $stopwatch.Stop()
            $terminalErrorProperty = $terminal.PSObject.Properties['Error']
            $terminalError = if ($null -ne $terminalErrorProperty) { $terminalErrorProperty.Value } else { $null }
            $terminalErrorCode = if ($null -ne $terminalError) { $terminalError.Code } else { 'none' }
            $terminalErrorMessage = if ($null -ne $terminalError) { $terminalError.Message } else { 'none' }
            $terminalWarningsProperty = $terminal.PSObject.Properties['Warnings']
            $terminalWarnings = if ($null -ne $terminalWarningsProperty) {
                @($terminalWarningsProperty.Value)
            }
            else {
                @()
            }
            Assert-Condition ($terminal.Phase.ToString() -in @('Completed', 'CompletedWithWarnings')) `
                "Creation failed for ${caseName}: $terminalErrorCode $terminalErrorMessage"
            Assert-Condition (Test-Path -LiteralPath $destination -PathType Leaf) `
                "Creation did not publish $caseName."

            $worker.Refresh()
            $rainValidation = Test-RainOutput -Client $client -Case $case -ArchivePath $destination `
                -ExtractionRoot $extractionRoot -TreeFiles $treeFiles -TreeDirectories $treeDirectories `
                -RawSourceSha256 $rawSourceSha256 -CancellationToken $cancellationToken
            $externalValidation = Invoke-SevenZipTest `
                -Executable $externalSevenZipFull -ArchivePath $destination
            $externalValidationCount++
            Assert-Condition ((Get-Sha256 -Path $destination) -eq $rainValidation.ArchiveSha256) `
                'An archive changed after Rain and external validation.'

            if ($measured) {
                $samples.Add([pscustomobject]@{
                    Format = $case.Format
                    Preset = $case.Preset.ToString()
                    Route = $case.Route
                    Run = $run
                    SourceBytes = if ($case.Route -eq 'tree') {
                        ($treeFiles | Measure-Object Length -Sum).Sum
                    }
                    else {
                        (Get-Item -LiteralPath $rawSource).Length
                    }
                    SourceFiles = if ($case.Route -eq 'tree') { $treeFiles.Count } else { 1 }
                    SourceDirectories = if ($case.Route -eq 'tree') { $treeDirectories.Count + 1 } else { 0 }
                    ElapsedMilliseconds = $stopwatch.Elapsed.TotalMilliseconds
                    WorkerCpuMilliseconds = $worker.TotalProcessorTime.TotalMilliseconds - $workerCpuBefore
                    WorkerPeakWorkingSetBytes = $worker.PeakWorkingSet64
                    TerminalPhase = $terminal.Phase.ToString()
                    WarningCodes = @($terminalWarnings | Where-Object { $null -ne $_ } | ForEach-Object { $_.Code })
                    OutputBytes = (Get-Item -LiteralPath $destination).Length
                    OutputSha256 = Get-Sha256 -Path $destination
                    ListedEntryCount = $rainValidation.ListedEntryCount
                    IntegrityPhase = $rainValidation.IntegrityPhase
                    ExtractionPhase = $rainValidation.ExtractionPhase
                    VerifiedFileCount = $rainValidation.VerifiedFileCount
                    VerifiedBytes = $rainValidation.VerifiedBytes
                    ExternalExitCode = $externalValidation.ExitCode
                    ExternalMilliseconds = $externalValidation.ElapsedMilliseconds
                })
            }
            else {
                $warmupCount++
            }
        }
        finally {
            if ($null -ne $client) {
                $client.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
            }

            if ($null -ne $worker) {
                $worker.Dispose()
            }

            if (Test-Path -LiteralPath $extractionRoot) {
                [IO.Directory]::Delete($extractionRoot, $true)
            }

            if (Test-Path -LiteralPath $destination) {
                [IO.File]::Delete($destination)
            }
        }
    }

    Write-Host ("Validated {0} {1}: one warm-up plus {2} measured runs." -f `
        $case.Format, $case.Preset, $Runs)
}

Assert-Condition ((Get-Sha256 -Path $rawSource) -eq $rawSourceSha256) 'The raw source changed.'
foreach ($file in $treeFiles) {
    Assert-Condition ((Get-Sha256 -Path (Join-Path $sourceRoot $file.RelativePath)) -eq $file.Sha256) `
        "A tree source changed: $($file.RelativePath)"
}

$stagingCount = @(Get-ChildItem -LiteralPath $workRootFull -Force -Recurse | Where-Object {
    $_.Name -like '.rainarchives-staging-*'
}).Count
Assert-Condition ($stagingCount -eq 0) 'Managed-performance staging residue remained.'

$recoveryClient = [RainArchives.Client.ArchiveClient]::StartAsync(
    $workerPath, $cancellationToken).GetAwaiter().GetResult()
try {
    $creationRecovery = $recoveryClient.ListArchiveCreationRecoveriesAsync(
        $cancellationToken).GetAwaiter().GetResult()
    $nestedRecovery = $recoveryClient.ListNestedWorkspaceRecoveriesAsync(
        $cancellationToken).GetAwaiter().GetResult()
    Assert-Condition ($creationRecovery.Recoveries.Count -eq 0) `
        'Managed-performance creation recovery records remained.'
    Assert-Condition ($nestedRecovery.Recoveries.Count -eq 0) `
        'Managed-performance nested recovery records remained.'
}
finally {
    $recoveryClient.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
}

$summaries = @($samples | Group-Object Format, Preset | ForEach-Object {
    $ordered = @($_.Group | Sort-Object ElapsedMilliseconds)
    $median = $ordered[[int][Math]::Floor($ordered.Count / 2)].ElapsedMilliseconds
    [pscustomobject]@{
        Format = $ordered[0].Format
        Preset = $ordered[0].Preset
        Route = $ordered[0].Route
        Runs = $ordered.Count
        MedianMilliseconds = $median
        P95Milliseconds = ($ordered | Measure-Object ElapsedMilliseconds -Maximum).Maximum
        MinimumMilliseconds = ($ordered | Measure-Object ElapsedMilliseconds -Minimum).Minimum
        MaximumMilliseconds = ($ordered | Measure-Object ElapsedMilliseconds -Maximum).Maximum
        MedianMiBPerSecond = (($ordered[0].SourceBytes / 1MB) / ($median / 1000))
        MeanWorkerCpuMilliseconds = ($ordered | Measure-Object WorkerCpuMilliseconds -Average).Average
        MeanWorkerPeakWorkingSetBytes = ($ordered | Measure-Object WorkerPeakWorkingSetBytes -Average).Average
        OutputBytes = $ordered[0].OutputBytes
        StableOutputHash = (@($ordered.OutputSha256 | Select-Object -Unique).Count -eq 1)
        OutputSha256 = $ordered[0].OutputSha256
    }
})

$drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($workRootFull).Substring(0, 1)) -PSProvider FileSystem
$evidence = [ordered]@{
    SchemaVersion = 1
    RecordedUtc = $recordedUtc
    Protocol = $expectedProtocol
    SourceInputCount = $expectedSourceInputs
    SourceSha256 = $expectedSourceSha256
    PackageRoot = $packageRootFull
    PackageManifestSha256 = Get-Sha256 -Path $manifestPath
    PackageClientSha256 = Get-Sha256 -Path $clientPath
    PackageContractsSha256 = Get-Sha256 -Path $contractsPath
    PackageWorkerSha256 = Get-Sha256 -Path $workerPath
    ExternalSevenZipPath = $externalSevenZipFull
    ExternalSevenZipSha256 = Get-Sha256 -Path $externalSevenZipFull
    ExternalSevenZipVersion = (Get-Item -LiteralPath $externalSevenZipFull).VersionInfo.ProductVersion
    OperatingSystem = [Environment]::OSVersion.VersionString
    RuntimeVersion = [Environment]::Version.ToString()
    LogicalProcessorCount = [Environment]::ProcessorCount
    MachineName = [Environment]::MachineName
    DriveRoot = $drive.Root
    DriveFreeBytes = $drive.Free
    CacheState = 'warm-uncontrolled'
    WorkerStartupIncluded = $false
    RunsPerCase = $Runs
    WarmupCount = $warmupCount
    MeasuredSampleCount = $samples.Count
    ExternalValidationCount = $externalValidationCount
    TreeFileCount = $treeFiles.Count
    TreeDirectoryCount = $treeDirectories.Count + 1
    TreeBytes = ($treeFiles | Measure-Object Length -Sum).Sum
    TreeManifestSha256 = $treeManifestSha256
    RawBytes = (Get-Item -LiteralPath $rawSource).Length
    RawSha256 = $rawSourceSha256
    Summaries = $summaries
    Samples = $samples
    CreationRecoveryCount = 0
    NestedRecoveryCount = 0
    RemainingStagingArtifacts = 0
    RemainingRainProcesses = 0
    WorkRootRemoved = $true
}

Remove-OwnedWorkRoot -Path $workRootFull -ExpectedToken $token
Assert-Condition (-not (Test-Path -LiteralPath $workRootFull)) 'The managed-performance work root remained.'

$rainProcesses = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -like 'RainArchives*'
})
Assert-Condition ($rainProcesses.Count -eq 0) 'A Rain process remained after managed-performance verification.'

$json = $evidence | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($evidencePathFull, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Host "Rain Archives managed-format performance evidence: $evidencePathFull"
Write-Host "SHA-256: $(Get-Sha256 -Path $evidencePathFull)"
