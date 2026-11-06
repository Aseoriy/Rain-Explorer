#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{32}$')]
    [string] $OwnershipToken,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $EvidenceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Disposable VHD setup must run in the Sandbox system context.'
}

foreach ($letter in @('R', 'X')) {
    if ([IO.Directory]::Exists("${letter}:\")) {
        throw "Drive ${letter}: already exists; refusing to select or modify it."
    }
}

$storageRoot = 'C:\RainM3Storage'
if ([IO.Directory]::Exists($storageRoot)) {
    throw "The guest-local storage root already exists: $storageRoot"
}

[IO.Directory]::CreateDirectory($storageRoot) | Out-Null
$diskpartPath = Join-Path $storageRoot 'initialize-disposable-volumes.txt'
$diskpart = @"
create vdisk file="C:\RainM3Storage\rain-m3-ntfs.vhdx" maximum=1024 type=expandable
select vdisk file="C:\RainM3Storage\rain-m3-ntfs.vhdx"
attach vdisk
create partition primary
format fs=ntfs quick label=RAINM3NTFS
assign letter=R
create vdisk file="C:\RainM3Storage\rain-m3-exfat.vhdx" maximum=1536 type=expandable
select vdisk file="C:\RainM3Storage\rain-m3-exfat.vhdx"
attach vdisk
create partition primary
format fs=exfat quick label=RAINM3EXFAT
assign letter=X
exit
"@
[IO.File]::WriteAllText(
    $diskpartPath,
    $diskpart,
    (New-Object Text.ASCIIEncoding))

$process = Start-Process `
    -FilePath (Join-Path $env:SystemRoot 'System32\diskpart.exe') `
    -ArgumentList @('/s', $diskpartPath) `
    -Wait `
    -PassThru `
    -NoNewWindow
if ($process.ExitCode -ne 0) {
    throw "DiskPart failed with exit code $($process.ExitCode)."
}

$volumes = New-Object Collections.Generic.List[object]
foreach ($expected in @(
    [pscustomobject] @{ Letter = 'R'; Label = 'RAINM3NTFS'; Format = 'NTFS' },
    [pscustomobject] @{ Letter = 'X'; Label = 'RAINM3EXFAT'; Format = 'exFAT' }
)) {
    $root = "$($expected.Letter):\"
    if (-not [IO.Directory]::Exists($root)) {
        throw "The disposable volume was not mounted at $root"
    }

    $drive = New-Object IO.DriveInfo($root)
    if (-not $drive.IsReady -or
        -not [string]::Equals($drive.VolumeLabel, $expected.Label, [StringComparison]::Ordinal) -or
        -not [string]::Equals($drive.DriveFormat, $expected.Format, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The mounted disposable volume identity is wrong for $root"
    }

    $marker = Join-Path $root '.rainarchives-m3-disposable'
    [IO.File]::WriteAllText($marker, $OwnershipToken, (New-Object Text.ASCIIEncoding))
    $volumes.Add([pscustomobject] @{
        root = $root
        label = $drive.VolumeLabel
        format = $drive.DriveFormat
        driveType = $drive.DriveType.ToString()
        totalBytes = $drive.TotalSize
        availableBytes = $drive.AvailableFreeSpace
        markerSha256 = (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash
    }) | Out-Null
}

if ([IO.Directory]::Exists($EvidenceDirectory) -or [IO.File]::Exists($EvidenceDirectory)) {
    throw "The setup evidence path already exists: $EvidenceDirectory"
}

[IO.Directory]::CreateDirectory($EvidenceDirectory) | Out-Null
$evidence = [ordered] @{
    schemaVersion = 1
    recordedUtc = [DateTime]::UtcNow.ToString('o')
    ownershipToken = $OwnershipToken
    userName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    storageRoot = $storageRoot
    volumes = $volumes.ToArray()
}
[IO.File]::WriteAllText(
    (Join-Path $EvidenceDirectory 'storage-setup.json'),
    ($evidence | ConvertTo-Json -Depth 6),
    (New-Object Text.UTF8Encoding($false)))
