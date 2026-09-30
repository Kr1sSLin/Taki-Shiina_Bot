[CmdletBinding()]
param([switch]$SkipInstaller, [switch]$SkipTests, [switch]$NoSelfContained)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($NoSelfContained) { throw 'Release artifacts must be self-contained. Use dotnet build for development.' }
if (Get-Process TKSDesktop -ErrorAction SilentlyContinue) { throw 'Exit all TKSDesktop instances from the tray before packaging (single-instance smoke test).' }
$project = Join-Path $repo 'TKSDesktop\TKSDesktop.csproj'
$releaseNotesDir = Join-Path $repo 'docs'
$releaseNotes = @()
if (Test-Path -LiteralPath $releaseNotesDir) {
    $releaseNotes = @(Get-ChildItem -LiteralPath $releaseNotesDir -Filter 'M6_*.md' -File)
}
if ($releaseNotes.Count -gt 1) { throw 'At most one local M6 release-notes document is allowed.' }
$releaseNotesPath = Join-Path $repo 'README.md'
if ($releaseNotes.Count -eq 1) {
    $releaseNotesPath = $releaseNotes[0].FullName
} else {
    Write-Host 'Local M6 release notes unavailable; using the repository README.'
}
[xml]$metadata = Get-Content -LiteralPath $project -Raw
$version = @($metadata.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must come from csproj and use x.y.z.' }
$nsis = (Get-Command makensis -ErrorAction SilentlyContinue).Source
if (-not $nsis) { $nsis = "${env:ProgramFiles(x86)}\NSIS\makensis.exe" }
if (-not $SkipInstaller -and -not (Test-Path -LiteralPath $nsis)) { throw 'NSIS is required; explicitly use -SkipInstaller for a partial build.' }
$run = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stage = Join-Path $repo "artifacts\releases\$run"
$output = Join-Path $repo "dist\$run"
$publish = Join-Path $stage 'publish'
$portable = Join-Path $stage 'portable'
New-Item -ItemType Directory -Path $stage,$output -Force | Out-Null
# Every run uses fresh directories: never delete user data or reuse stale artifacts.
if (-not $SkipTests) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Verification gate failed.' }
}
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish --nologo -p:PublishReadyToRun=false -p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
$exe = Join-Path $publish 'TKSDesktop.exe'
foreach ($file in @('TKSDesktop.exe','TKSDesktop.dll','coreclr.dll','hostfxr.dll','PresentationFramework.dll','Resources\Assets\asset-manifest.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $file))) { throw "Missing release file: $file" }
}
$runtime = Get-Content -LiteralPath (Join-Path $publish 'TKSDesktop.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks) { throw 'Runtime config still requires an installed framework.' }
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $publish
Copy-Item -LiteralPath (Join-Path $repo 'installer\LICENSE.txt') -Destination $publish
$docs = Join-Path $publish 'docs'
New-Item -ItemType Directory -Path $docs | Out-Null
Copy-Item -LiteralPath $releaseNotesPath -Destination (Join-Path $docs 'M6-release.md')
$dll = Join-Path $publish 'TKSDesktop.dll'
$build = [ordered]@{ version=$version; buildId=$run; builtAtUtc=[DateTime]::UtcNow.ToString('o'); assemblyVersion=(Get-Item $dll).VersionInfo.ProductVersion; assemblySha256=(Get-FileHash $dll).Hash.ToLowerInvariant(); selfContained=$true; signed=$false }
$build | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'build-info.json') -Encoding UTF8
& (Join-Path $PSScriptRoot 'smoke-startup.ps1') -Executable $exe

# Validate the exact published binary, using local mock only; never inherit production selftest overrides.
$mock = $null
$probe = $null
try {
    try { $healthy = (Invoke-WebRequest 'http://127.0.0.1:8787/healthz' -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { $healthy = $false }
    if (-not $healthy) {
        $mockScript = [IO.Path]::GetFullPath((Join-Path $repo '..\TKS_Bot_Linux\mock-server\server.mjs'))
        $mock = Start-Process node -ArgumentList ('"' + $mockScript + '"') -WindowStyle Hidden -PassThru
        for ($attempt=0; $attempt -lt 30 -and -not $healthy; $attempt++) {
            Start-Sleep -Milliseconds 250
            try { $healthy = (Invoke-WebRequest 'http://127.0.0.1:8787/healthz' -UseBasicParsing -TimeoutSec 1).StatusCode -eq 200 } catch { $healthy = $false }
        }
    }
    if (-not $healthy) { throw 'Local mock unavailable.' }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$exe; $start.Arguments='--selftest'; $start.UseShellExecute=$false
    $start.EnvironmentVariables['TKS_SELFTEST_API']='http://127.0.0.1:8787/api/v1/'
    $start.EnvironmentVariables['TKS_SELFTEST_WS']='ws://127.0.0.1:8787/ws/chat'
    $start.EnvironmentVariables['TKS_SELFTEST_USER']='kris'; $start.EnvironmentVariables['TKS_SELFTEST_PASS']='taki'
    $start.EnvironmentVariables['TKS_SELFTEST_DIR']=$stage
    $probe=[Diagnostics.Process]::Start($start)
    if (-not $probe.WaitForExit(60000)) { throw 'Published selftest timed out.' }
    if ($probe.ExitCode -ne 0) { throw 'Published selftest failed.' }
} finally {
    if ($probe) { if (-not $probe.HasExited) { $probe.Kill() }; $probe.Dispose() }
    if ($mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id }
}
Copy-Item -LiteralPath $publish -Destination $portable -Recurse
Set-Content -LiteralPath (Join-Path $portable 'portable.flag') -Value 'portable' -Encoding ASCII
New-Item -ItemType Directory -Path (Join-Path $portable 'data\config'),(Join-Path $portable 'data\state'),(Join-Path $portable 'data\attachments') -Force | Out-Null
$bytes = (Get-ChildItem -LiteralPath $portable -Recurse -File | Measure-Object Length -Sum).Sum
if ($bytes -gt 300MB) { throw 'Portable uncompressed size exceeds 300 MiB.' }
$zip = Join-Path $output "TKS-Desktop-$version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip -CompressionLevel Optimal
$artifacts = @($zip)
if (-not $SkipInstaller) {
    $probeLink = Join-Path $stage 'aumid-probe.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($probeLink)
    $shortcut.TargetPath = $exe
    $shortcut.Save()
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'installer\Set-ShortcutAumid.ps1') -ShortcutPath $probeLink -Aumid 'TKSDesktop' -Verify
    if ($LASTEXITCODE -ne 0) { throw 'AUMID write/read probe failed.' }
    # Generate explicit uninstall operations for only the files shipped by this build.
    # Unknown/user-added files remain; never recursively delete the selected install directory.
    $deletes = @()
    foreach ($file in Get-ChildItem -LiteralPath $publish -Recurse -File) {
        $relative = $file.FullName.Substring($publish.Length + 1).Replace('$','$$')
        $deletes += 'Delete "$INSTDIR\' + $relative + '"'
    }
    $deletes += 'Delete "$INSTDIR\installer\Set-ShortcutAumid.ps1"','Delete "$INSTDIR\installer\Test-InstallTarget.ps1"','Delete "$INSTDIR\uninstall.exe"'
    foreach ($dir in (Get-ChildItem -LiteralPath $publish -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending)) {
        $deletes += 'RMDir "$INSTDIR\' + $dir.FullName.Substring($publish.Length+1).Replace('$','$$') + '"'
    }
    $deletes += 'RMDir "$INSTDIR\installer"','RMDir "$INSTDIR"'
    $uninstall = Join-Path $stage 'uninstall-files.nsh'
    $deletes | Set-Content -LiteralPath $uninstall -Encoding UTF8
    # NSIS Unicode license page requires a BOM-marked UTF-16LE text file.
    # Keep the repository and published license UTF-8; transcode only this build input.
    $licenseSource = Join-Path $repo 'installer\LICENSE.txt'
    $licenseForNsis = Join-Path $stage 'LICENSE-NSIS.txt'
    $licenseText = [IO.File]::ReadAllText($licenseSource, (New-Object Text.UTF8Encoding($false, $true)))
    [IO.File]::WriteAllText($licenseForNsis, $licenseText, (New-Object Text.UnicodeEncoding($false, $true)))
    $licenseBytes = [IO.File]::ReadAllBytes($licenseForNsis)
    if ($licenseBytes.Length -lt 4 -or $licenseBytes[0] -ne 0xff -or $licenseBytes[1] -ne 0xfe -or
        [IO.File]::ReadAllText($licenseForNsis, [Text.Encoding]::Unicode) -ne $licenseText) {
        throw 'NSIS license transcode verification failed.'
    }
    $setup=Join-Path $output "TKS-Desktop-$version-win-x64-setup.exe"
    & $nsis "/DAPP_VERSION=$version" "/DPUBLISH_DIR=$publish" "/DOUT_FILE=$setup" "/DNSI_DIR=$(Join-Path $repo 'installer')" "/DUNINSTALL_FILES=$uninstall" "/DLICENSE_FILE=$licenseForNsis" (Join-Path $repo 'installer\tks-desktop.nsi')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setup)) { throw 'Installer build failed.' }
    if ((Get-Item $setup).Length -gt 120MB) { throw 'Installer exceeds 120 MiB.' }
    $artifacts += $setup
}
$record=[ordered]@{ build=$build; unitAndStaticGates= $(if($SkipTests){'SKIPPED'}else{'PASS'}); publishedSelftest='PASS'; startupSmoke='PASS'; portableUncompressedBytes=$bytes; installerBuilt=(-not $SkipInstaller); releaseStatus='CANDIDATE_MANUAL_ACCEPTANCE_PENDING'; prerequisites=@{ healthLocations='UNVERIFIED'; uploadBodyLimit='UNVERIFIED'; pointsRouting8001='UNVERIFIED' }; manualAcceptance=@{ installUpgradeUninstall='PENDING'; toastActivation='PENDING'; portableScope='PENDING'; coldStartAndLatency='PENDING'; hiddenAndActiveMemory='PENDING' } }
$record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'release-record.json') -Encoding UTF8
Copy-Item -LiteralPath $releaseNotesPath -Destination (Join-Path $output 'RELEASE-NOTES.md')
$artifacts += (Join-Path $output 'release-record.json'),(Join-Path $output 'RELEASE-NOTES.md')
$artifacts | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_) } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII
& (Join-Path $PSScriptRoot 'Test-ReleaseArtifacts.ps1') -Directory $output
Write-Host "Release candidate (not final acceptance): $output"
