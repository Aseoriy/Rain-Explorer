[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string] $SevenZipPath,

    [Parameter(Mandatory = $true)]
    [string] $DotNetZipPath,

    [string] $RarPath
)

$ErrorActionPreference = 'Stop'
$fixturePassword = 'RainArchives-Fixture-2026!'
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$sevenZip = [System.IO.Path]::GetFullPath($SevenZipPath)
$dotNetZip = [System.IO.Path]::GetFullPath($DotNetZipPath)

if (-not (Test-Path -LiteralPath $sevenZip -PathType Leaf)) {
    throw "7-Zip fixture generator was not found: $sevenZip"
}

if (-not (Test-Path -LiteralPath $dotNetZip -PathType Leaf)) {
    throw "DotNetZip fixture generator was not found: $dotNetZip"
}

if (Test-Path -LiteralPath $outputRoot) {
    throw "Fixture output already exists. Refusing to overwrite: $outputRoot"
}

$sourceRoot = Join-Path $outputRoot 'source'
$archiveRoot = Join-Path $outputRoot 'archives'
$zipRoot = Join-Path $archiveRoot 'zip'
$sevenZipRoot = Join-Path $archiveRoot '7z'
$streamRoot = Join-Path $archiveRoot 'streams'
$rarRoot = Join-Path $archiveRoot 'rar'

foreach ($directory in @($sourceRoot, $zipRoot, $sevenZipRoot, $streamRoot, $rarRoot)) {
    New-Item -ItemType Directory -Path $directory | Out-Null
}

function Invoke-CheckedTool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    & $Path @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture generator exited with code ${LASTEXITCODE}: $Path"
    }
}

function Find-ByteSequence {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]] $Haystack,

        [Parameter(Mandatory = $true)]
        [byte[]] $Needle
    )

    for ($offset = 0; $offset -le $Haystack.Length - $Needle.Length; $offset++) {
        $matched = $true
        for ($index = 0; $index -lt $Needle.Length; $index++) {
            if ($Haystack[$offset + $index] -ne $Needle[$index]) {
                $matched = $false
                break
            }
        }

        if ($matched) {
            return $offset
        }
    }

    return -1
}

function Write-TruncatedFixture {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,

        [Parameter(Mandatory = $true)]
        [string] $Destination,

        [Parameter(Mandatory = $true)]
        [int] $BytesToKeep
    )

    $bytes = [System.IO.File]::ReadAllBytes($Source)
    if ($BytesToKeep -le 0 -or $BytesToKeep -ge $bytes.Length) {
        throw "Truncation length must be inside the source: $Source"
    }

    [System.IO.File]::WriteAllBytes($Destination, $bytes[0..($BytesToKeep - 1)])
}

$utf8 = [System.Text.UTF8Encoding]::new($false)
$alphaPayload = 'Rain Archives deterministic CRC fixture payload: 0123456789ABCDEF'
[System.IO.File]::WriteAllText((Join-Path $sourceRoot 'alpha.txt'), $alphaPayload, $utf8)
[System.IO.File]::WriteAllText((Join-Path $sourceRoot 'beta.txt'), "beta`nsecond line`n", $utf8)
[System.IO.File]::WriteAllText((Join-Path $sourceRoot 'unicode-雪.txt'), "snow 雪`n", $utf8)
New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'empty-directory') | Out-Null

$binaryPayload = [byte[]]::new(16384)
for ($index = 0; $index -lt $binaryPayload.Length; $index++) {
    $binaryPayload[$index] = [byte](($index * 31 + 17) % 251)
}
[System.IO.File]::WriteAllBytes((Join-Path $sourceRoot 'payload.bin'), $binaryPayload)

$classicSplitPayload = [byte[]]::new(384 * 1024)
$classicSplitCounter = [byte[]]::new(8)
for ($block = 0; $block -lt ($classicSplitPayload.Length / 32); $block++) {
    [System.Buffers.Binary.BinaryPrimitives]::WriteInt64LittleEndian(
        $classicSplitCounter,
        [long]$block)
    $digest = [System.Security.Cryptography.SHA256]::HashData($classicSplitCounter)
    [System.Buffer]::BlockCopy($digest, 0, $classicSplitPayload, $block * 32, 32)
}
[System.IO.File]::WriteAllBytes(
    (Join-Path $sourceRoot 'classic-payload.bin'),
    $classicSplitPayload)

$fixedTime = [DateTime]::SpecifyKind([DateTime]::new(2026, 1, 2, 3, 4, 6), [DateTimeKind]::Utc)
Get-ChildItem -LiteralPath $sourceRoot -File | ForEach-Object {
    $_.CreationTimeUtc = $fixedTime
    $_.LastAccessTimeUtc = $fixedTime
    $_.LastWriteTimeUtc = $fixedTime
}

$common = @('-bd', '-y', '-mmt=1')
Push-Location $sourceRoot
try {
    Invoke-CheckedTool $sevenZip (@('a', '-tzip', (Join-Path $zipRoot 'plain.zip'), 'alpha.txt', 'beta.txt', 'unicode-雪.txt', '-mx=7') + $common)
    Invoke-CheckedTool $sevenZip (@('a', '-tzip', (Join-Path $zipRoot 'aes256.zip'), 'alpha.txt', 'unicode-雪.txt', '-mx=7', '-mem=AES256', "-p$fixturePassword") + $common)
    Invoke-CheckedTool $sevenZip (@('a', '-tzip', (Join-Path $zipRoot 'zipcrypto.zip'), 'alpha.txt', '-mx=7', '-mem=ZipCrypto', "-p$fixturePassword") + $common)
    Invoke-CheckedTool $sevenZip (@('a', '-tzip', (Join-Path $zipRoot 'stored-valid.zip'), 'alpha.txt', '-mx=0') + $common)
    Invoke-CheckedTool $sevenZip (@('a', '-tzip', (Join-Path $zipRoot 'split.zip'), 'payload.bin', '-mx=0', '-v2k') + $common)

    $classicSplitTerminal = Join-Path $zipRoot 'classic-split.zip'
    Invoke-CheckedTool $dotNetZip @(
        $classicSplitTerminal,
        '-split',
        '65536',
        '-L',
        '0',
        '-utf8',
        '-Tw-',
        '-Tu-',
        '-UT',
        '2026-01-02T03:04:06Z',
        'classic-payload.bin',
        'unicode-雪.txt')
    $expectedClassicVolumes = 1..6 | ForEach-Object {
        Join-Path $zipRoot ("classic-split.z{0:D2}" -f $_)
    }
    $missingClassicVolumeCount = @($expectedClassicVolumes | Where-Object {
        -not (Test-Path -LiteralPath $_ -PathType Leaf)
    }).Count
    $actualClassicVolumeCount = @(
        Get-ChildItem -LiteralPath $zipRoot -File -Filter 'classic-split.z*' |
            Where-Object { $_.Extension -match '^\.z[0-9]+$' }
    ).Count
    $classicFixtureComplete = (Test-Path -LiteralPath $classicSplitTerminal -PathType Leaf) `
        -and $missingClassicVolumeCount -eq 0 `
        -and $actualClassicVolumeCount -eq 6
    if (-not $classicFixtureComplete) {
        throw 'DotNetZip did not produce the expected six classic split volumes and terminal .zip file.'
    }
    Invoke-CheckedTool $sevenZip @('t', $classicSplitTerminal, '-bd', '-y')

    $validStoredZip = Join-Path $zipRoot 'stored-valid.zip'
    $corruptStoredZip = Join-Path $zipRoot 'stored-corrupt-crc.zip'
    $corruptBytes = [System.IO.File]::ReadAllBytes($validStoredZip)
    $payloadBytes = $utf8.GetBytes($alphaPayload)
    $payloadOffset = Find-ByteSequence -Haystack $corruptBytes -Needle $payloadBytes
    if ($payloadOffset -lt 0) {
        throw 'Could not locate the stored ZIP payload for deterministic corruption.'
    }
    $corruptBytes[$payloadOffset] = $corruptBytes[$payloadOffset] -bxor 0x01
    [System.IO.File]::WriteAllBytes($corruptStoredZip, $corruptBytes)

    Invoke-CheckedTool $sevenZip (@('a', '-t7z', (Join-Path $sevenZipRoot 'solid.7z'), 'alpha.txt', 'beta.txt', 'unicode-雪.txt', '-mx=7', '-ms=on') + $common)
    Invoke-CheckedTool $sevenZip (@('a', '-t7z', (Join-Path $sevenZipRoot 'encrypted.7z'), 'alpha.txt', 'unicode-雪.txt', '-mx=7', '-ms=on', "-p$fixturePassword") + $common)
    Invoke-CheckedTool $sevenZip (@('a', '-t7z', (Join-Path $sevenZipRoot 'header-encrypted.7z'), 'alpha.txt', 'unicode-雪.txt', '-mx=7', '-ms=on', '-mhe=on', "-p$fixturePassword") + $common)
    Invoke-CheckedTool $sevenZip (@('a', '-t7z', (Join-Path $sevenZipRoot 'split.7z'), 'payload.bin', '-mx=0', '-v2k') + $common)

    Invoke-CheckedTool $sevenZip (@('a', '-ttar', (Join-Path $streamRoot 'sample.tar'), 'alpha.txt', 'unicode-雪.txt') + $common)
    Invoke-CheckedTool $sevenZip (@('a', '-tgzip', (Join-Path $streamRoot 'sample.tar.gz'), (Join-Path $streamRoot 'sample.tar'), '-mx=7') + $common)
    Invoke-CheckedTool $sevenZip (@('a', '-tbzip2', (Join-Path $streamRoot 'sample.tar.bz2'), (Join-Path $streamRoot 'sample.tar'), '-mx=7') + $common)
    Invoke-CheckedTool $sevenZip (@('a', '-txz', (Join-Path $streamRoot 'sample.tar.xz'), (Join-Path $streamRoot 'sample.tar'), '-mx=7') + $common)
    Invoke-CheckedTool $sevenZip (@('a', '-tgzip', (Join-Path $streamRoot 'alpha.txt.gz'), 'alpha.txt', '-mx=7') + $common)
    Invoke-CheckedTool $sevenZip (@('a', '-tbzip2', (Join-Path $streamRoot 'alpha.txt.bz2'), 'alpha.txt', '-mx=7') + $common)
    Invoke-CheckedTool $sevenZip (@('a', '-txz', (Join-Path $streamRoot 'alpha.txt.xz'), 'alpha.txt', '-mx=7') + $common)

    if (-not [string]::IsNullOrWhiteSpace($RarPath)) {
        $rar = [System.IO.Path]::GetFullPath($RarPath)
        if (-not (Test-Path -LiteralPath $rar -PathType Leaf)) {
            throw "RAR fixture generator was not found: $rar"
        }

        $rarCommon = @('-cfg-', '-idq', '-y', '-ma5')
        Invoke-CheckedTool $rar (@('a', (Join-Path $rarRoot 'plain.rar'), 'alpha.txt', 'unicode-雪.txt') + $rarCommon)
        Invoke-CheckedTool $rar (@('a', (Join-Path $rarRoot 'directory-no-crc.rar'), 'empty-directory') + $rarCommon)
        Invoke-CheckedTool $rar (@('a', '-s', (Join-Path $rarRoot 'solid.rar'), 'alpha.txt', 'beta.txt', 'unicode-雪.txt') + $rarCommon)
        Invoke-CheckedTool $rar (@('a', "-hp$fixturePassword", (Join-Path $rarRoot 'header-encrypted.rar'), 'alpha.txt', 'unicode-雪.txt') + $rarCommon)
        Invoke-CheckedTool $rar (@('a', '-m0', '-s', '-v2k', (Join-Path $rarRoot 'split.rar'), 'payload.bin') + $rarCommon)
    }
}
finally {
    Pop-Location
}

Write-TruncatedFixture `
    -Source (Join-Path $zipRoot 'plain.zip') `
    -Destination (Join-Path $zipRoot 'plain-truncated.zip') `
    -BytesToKeep 256
Write-TruncatedFixture `
    -Source (Join-Path $streamRoot 'alpha.txt.gz') `
    -Destination (Join-Path $streamRoot 'alpha-truncated.gz') `
    -BytesToKeep 87
if (Test-Path -LiteralPath (Join-Path $rarRoot 'plain.rar') -PathType Leaf) {
    Write-TruncatedFixture `
        -Source (Join-Path $rarRoot 'plain.rar') `
        -Destination (Join-Path $rarRoot 'plain-truncated.rar') `
        -BytesToKeep 120
}

Get-ChildItem -LiteralPath $archiveRoot -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
        [pscustomobject]@{
            Path = [System.IO.Path]::GetRelativePath($outputRoot, $_.FullName).Replace('\', '/')
            Length = $_.Length
            Sha256 = $hash.Hash
        }
    } |
    ConvertTo-Json -Depth 3 |
    Set-Content -LiteralPath (Join-Path $outputRoot 'generated-hashes.json') -Encoding utf8NoBOM
