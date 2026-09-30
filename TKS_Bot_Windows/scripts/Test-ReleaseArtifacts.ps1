[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Directory)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $Directory).Path
$record=Get-Content -LiteralPath (Join-Path $root 'release-record.json') -Raw | ConvertFrom-Json
$sums=Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt')
$names=@()
foreach ($line in $sums) {
    if ($line -notmatch '^([a-f0-9]{64})  ([^\\/:]+)$') { throw 'Invalid checksum line.' }
    $expected=$Matches[1]; $name=$Matches[2]
    if ($names -contains $name) { throw 'Duplicate checksum entry.' }
    $names += $name
    if ((Get-FileHash -LiteralPath (Join-Path $root $name)).Hash.ToLowerInvariant() -ne $expected) { throw "Checksum mismatch: $name" }
}
$zipName="TKS-Desktop-$($record.build.version)-win-x64-portable.zip"
if ($names -notcontains $zipName) { throw 'Portable package missing from checksums.' }
$setupName="TKS-Desktop-$($record.build.version)-win-x64-setup.exe"
if ($record.installerBuilt -and $names -notcontains $setupName) { throw 'Installer missing from checksums.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead((Join-Path $root $zipName))
try {
    $entries=@{}
    $size=0L
    foreach ($entry in $zip.Entries) {
        $name=$entry.FullName.Replace('\','/')
        if ($name -match '(^/|(^|/)\.\.(/|$)|:)' -or $entries.ContainsKey($name)) { throw 'Unsafe or duplicate archive path.' }
        $entries[$name]=$entry
        $size += $entry.Length
        if ($name -match '(?i)(credentials\.bin|tks\.db|\.jsonl$)' -or ($name.StartsWith('data/') -and -not $name.EndsWith('/'))) { throw "User data in package: $name" }
    }
    foreach ($name in @('TKSDesktop.exe','TKSDesktop.dll','portable.flag','coreclr.dll','hostfxr.dll','PresentationFramework.dll','build-info.json','Resources/Assets/asset-manifest.json','README.md')) {
        if (-not $entries.ContainsKey($name)) { throw "Missing archive entry: $name" }
    }
    if ($size -gt 300MB) { throw 'Unpacked archive exceeds 300 MiB.' }
    $stream=$entries['TKSDesktop.dll'].Open()
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($hash -ne $record.build.assemblySha256) { throw 'Archive assembly identity mismatch.' }
} finally { $zip.Dispose() }
Write-Output "PASS: checksums, self-contained payload, assembly identity, clean portable archive ($size bytes)."
