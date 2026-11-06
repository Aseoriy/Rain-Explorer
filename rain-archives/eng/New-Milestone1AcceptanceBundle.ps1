#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    & $script:DotNetPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-RelativePathInsideRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $rootWithSeparator = $Root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $Path.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the expected root: $Path"
    }

    return $Path.Substring($rootWithSeparator.Length).Replace('\', '/')
}

function Convert-BytesToHex {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]] $Bytes
    )

    return ([System.BitConverter]::ToString($Bytes)).Replace('-', '')
}

function Get-SourceFingerprint {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot
    )

    $inputMap = New-Object 'System.Collections.Generic.SortedDictionary[string,string]' ([System.StringComparer]::Ordinal)
    $sourceRoots = @(
        (Join-Path $RepositoryRoot 'src'),
        (Join-Path $RepositoryRoot 'benchmarks')
    )

    foreach ($file in Get-ChildItem -LiteralPath $sourceRoots -Recurse -File) {
        if ($file.FullName -match '[\\/](bin|obj)[\\/]') {
            continue
        }

        if ($file.Extension -notin @('.cs', '.csproj', '.manifest', '.xaml')) {
            continue
        }

        $relative = Get-RelativePathInsideRoot -Root $RepositoryRoot -Path $file.FullName
        $inputMap.Add($relative, $file.FullName)
    }

    foreach ($name in @('Directory.Build.props', 'Directory.Packages.props', 'global.json', 'NuGet.config', 'RainArchives.slnx')) {
        $fullPath = Join-Path $RepositoryRoot $name
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Required source-fingerprint input is missing: $fullPath"
        }

        $inputMap.Add($name, $fullPath)
    }

    $aggregate = [System.Security.Cryptography.SHA256]::Create()
    $fileHasher = [System.Security.Cryptography.SHA256]::Create()
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    try {
        foreach ($entry in $inputMap.GetEnumerator()) {
            $pathBytes = $utf8.GetBytes($entry.Key)
            $null = $aggregate.TransformBlock($pathBytes, 0, $pathBytes.Length, $pathBytes, 0)
            $separator = [byte[]] @(0)
            $null = $aggregate.TransformBlock($separator, 0, 1, $separator, 0)

            $stream = [System.IO.File]::OpenRead($entry.Value)
            try {
                $fileHash = $fileHasher.ComputeHash($stream)
            }
            finally {
                $stream.Dispose()
            }

            $null = $aggregate.TransformBlock($fileHash, 0, $fileHash.Length, $fileHash, 0)
        }

        $empty = New-Object byte[] 0
        $null = $aggregate.TransformFinalBlock($empty, 0, 0)
        return [pscustomobject] @{
            InputCount = $inputMap.Count
            Sha256 = Convert-BytesToHex -Bytes $aggregate.Hash
        }
    }
    finally {
        $fileHasher.Dispose()
        $aggregate.Dispose()
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $resolvedOutput) {
    throw "The output path already exists. Choose a new directory: $resolvedOutput"
}

$dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1
$script:DotNetPath = $dotnetCommand.Source
$parent = Split-Path -Parent $resolvedOutput
if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
    $null = New-Item -ItemType Directory -Path $parent
}

$null = New-Item -ItemType Directory -Path $resolvedOutput
$cliDirectory = Join-Path $resolvedOutput 'cli'
$workerDirectory = Join-Path $resolvedOutput 'worker'
$fixtureDirectory = Join-Path $resolvedOutput 'fixtures'
$complianceDirectory = Join-Path $resolvedOutput 'notices'

Push-Location $repositoryRoot
try {
    Invoke-DotNet -Arguments @('restore', 'RainArchives.slnx', '--locked-mode', '--runtime', 'win-x64')
    Invoke-DotNet -Arguments @(
        'publish',
        'src/RainArchives.Cli/RainArchives.Cli.csproj',
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--no-restore',
        '--output', $cliDirectory
    )
    Invoke-DotNet -Arguments @(
        'publish',
        'src/RainArchives.Worker/RainArchives.Worker.csproj',
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--no-restore',
        '--output', $workerDirectory
    )
}
finally {
    Pop-Location
}

$requiredFiles = @(
    (Join-Path $cliDirectory 'rain-archives.exe'),
    (Join-Path $workerDirectory 'RainArchives.Worker.exe')
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Publish output is incomplete: $requiredFile"
    }
}

$forbiddenPayloads = Get-ChildItem -LiteralPath @($cliDirectory, $workerDirectory) -Recurse -File |
    Where-Object { $_.Name -in @('7z.dll', 'SharpSevenZip.dll') }
if ($forbiddenPayloads) {
    throw "The disabled native dependency unexpectedly entered the bundle: $($forbiddenPayloads.FullName -join ', ')"
}

$null = New-Item -ItemType Directory -Path $fixtureDirectory
$null = New-Item -ItemType Directory -Path (Join-Path $fixtureDirectory 'source')
Copy-Item -LiteralPath 'tests\fixtures\corpus\archives\zip\plain.zip' -Destination $fixtureDirectory
Copy-Item -LiteralPath 'tests\fixtures\corpus\archives\zip\aes256.zip' -Destination $fixtureDirectory
Copy-Item -LiteralPath 'tests\fixtures\corpus\archives\7z\solid.7z' -Destination $fixtureDirectory
Copy-Item -LiteralPath 'tests\fixtures\corpus\source\alpha.txt' -Destination (Join-Path $fixtureDirectory 'source')
Copy-Item -LiteralPath 'tests\fixtures\corpus\source\beta.txt' -Destination (Join-Path $fixtureDirectory 'source')
$unicodeFileName = 'unicode-' + [char] 0x96EA + '.txt'
Copy-Item -LiteralPath (Join-Path 'tests\fixtures\corpus\source' $unicodeFileName) -Destination (Join-Path $fixtureDirectory 'source')
Copy-Item -LiteralPath 'tests\fixtures\corpus\manifest.json' -Destination $fixtureDirectory
Copy-Item -LiteralPath 'tests\fixtures\corpus\generated-hashes.json' -Destination $fixtureDirectory

$null = New-Item -ItemType Directory -Path $complianceDirectory
Copy-Item -LiteralPath 'THIRD-PARTY-NOTICES.md' -Destination $complianceDirectory
Copy-Item -LiteralPath 'licenses\SharpCompress-LICENSE.txt' -Destination $complianceDirectory
Copy-Item -LiteralPath 'eng\Invoke-Milestone1CleanMachineAcceptance.ps1' -Destination $resolvedOutput

$sourceFingerprint = Get-SourceFingerprint -RepositoryRoot $repositoryRoot
$bundleFiles = @(
    Get-ChildItem -LiteralPath $resolvedOutput -Recurse -File |
        Sort-Object -Property FullName |
        ForEach-Object {
            [ordered] @{
                path = Get-RelativePathInsideRoot -Root $resolvedOutput -Path $_.FullName
                length = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
)

$sdkVersion = (& $script:DotNetPath --version | Select-Object -First 1).Trim()
$manifest = [ordered] @{
    schemaVersion = 1
    createdUtc = [DateTime]::UtcNow.ToString('o')
    runtimeIdentifier = 'win-x64'
    selfContained = $true
    sourceFingerprint = [ordered] @{
        inputCount = $sourceFingerprint.InputCount
        sha256 = $sourceFingerprint.Sha256
    }
    builder = [ordered] @{
        dotnetSdk = $sdkVersion
        osVersion = [Environment]::OSVersion.VersionString
    }
    forbiddenRuntimeFiles = @('7z.dll', 'SharpSevenZip.dll')
    files = $bundleFiles
}

$manifestPath = Join-Path $resolvedOutput 'bundle-manifest.json'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 8),
    $utf8NoBom)
$manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash

[long] $totalBytes = 0
foreach ($bundleFile in $bundleFiles) {
    $totalBytes += [long] $bundleFile['length']
}
Write-Host "Milestone 1 acceptance bundle created."
Write-Host "Path: $resolvedOutput"
Write-Host "Files: $($bundleFiles.Count)"
Write-Host "Bytes: $totalBytes"
Write-Host "Source fingerprint: $($sourceFingerprint.Sha256) ($($sourceFingerprint.InputCount) inputs)"
Write-Host "Bundle manifest SHA-256: $manifestSha256"
Write-Host "No existing path was overwritten or removed."
