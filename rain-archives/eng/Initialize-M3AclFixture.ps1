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
    throw 'ACL fixture setup must run in the Sandbox system context.'
}

$root = 'R:\'
$drive = New-Object IO.DriveInfo($root)
if (-not $drive.IsReady -or
    -not [string]::Equals($drive.VolumeLabel, 'RAINM3NTFS', [StringComparison]::Ordinal) -or
    -not [string]::Equals($drive.DriveFormat, 'NTFS', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The exact disposable NTFS volume is not mounted at R:\.'
}

$volumeMarker = Join-Path $root '.rainarchives-m3-disposable'
if (-not [IO.File]::Exists($volumeMarker) -or
    -not [string]::Equals(
        [IO.File]::ReadAllText($volumeMarker, (New-Object Text.ASCIIEncoding)),
        $OwnershipToken,
        [StringComparison]::Ordinal)) {
    throw 'The disposable NTFS ownership marker does not match this run.'
}

$fixture = Join-Path $root "rain-m3-acl-denied-$OwnershipToken"
if ([IO.Directory]::Exists($fixture) -or [IO.File]::Exists($fixture)) {
    throw "The ACL fixture already exists: $fixture"
}

$user = Get-LocalUser -Name 'WDAGUtilityAccount' -ErrorAction Stop
$sid = [Security.Principal.SecurityIdentifier] $user.SID
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$fixtureMarker = Join-Path $fixture '.rainarchives-m3-acl-denied'
[IO.File]::WriteAllText($fixtureMarker, $OwnershipToken, (New-Object Text.ASCIIEncoding))
$acl = Get-Acl -LiteralPath $fixture
$acl.SetAccessRuleProtection($true, $true)
$deny = New-Object Security.AccessControl.FileSystemAccessRule(
    $sid,
    [Security.AccessControl.FileSystemRights]::Write,
    [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
    [Security.AccessControl.PropagationFlags]::None,
    [Security.AccessControl.AccessControlType]::Deny)
$acl.AddAccessRule($deny) | Out-Null
Set-Acl -LiteralPath $fixture -AclObject $acl

if ([IO.Directory]::Exists($EvidenceDirectory) -or [IO.File]::Exists($EvidenceDirectory)) {
    throw "The ACL setup evidence path already exists: $EvidenceDirectory"
}

[IO.Directory]::CreateDirectory($EvidenceDirectory) | Out-Null
$evidence = [ordered] @{
    schemaVersion = 1
    recordedUtc = [DateTime]::UtcNow.ToString('o')
    ownershipToken = $OwnershipToken
    fixture = $fixture
    fixtureMarkerSha256 = (Get-FileHash -LiteralPath $fixtureMarker -Algorithm SHA256).Hash
    deniedSid = $sid.Value
    deniedAccount = $user.Name
    setupPrincipal = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    rights = 'Write'
    inheritance = 'ContainerInherit,ObjectInherit'
    accessControlType = 'Deny'
}
[IO.File]::WriteAllText(
    (Join-Path $EvidenceDirectory 'acl-fixture-setup.json'),
    ($evidence | ConvertTo-Json -Depth 5),
    (New-Object Text.UTF8Encoding($false)))
